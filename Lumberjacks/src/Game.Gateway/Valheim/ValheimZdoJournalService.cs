using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game.Gateway.Valheim;

public sealed record ValheimZdoJournalObject
{
    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = "";

    [JsonPropertyName("world_epoch")]
    public string WorldEpoch { get; init; } = "";

    [JsonPropertyName("zone_epoch")]
    public long ZoneEpoch { get; init; }

    [JsonPropertyName("source_seq")]
    public long SourceSequence { get; init; }

    [JsonPropertyName("object_revision")]
    public long ObjectRevision { get; init; }

    [JsonPropertyName("uid_user")]
    public long UidUser { get; init; }

    [JsonPropertyName("uid_id")]
    public long UidId { get; init; }

    [JsonPropertyName("prefab")]
    public int Prefab { get; init; }

    [JsonPropertyName("owner")]
    public long Owner { get; init; }

    [JsonPropertyName("owner_rev")]
    public int OwnerRevision { get; init; }

    [JsonPropertyName("data_rev")]
    public long DataRevision { get; init; }

    [JsonPropertyName("position_x")]
    public float PositionX { get; init; }

    [JsonPropertyName("position_y")]
    public float PositionY { get; init; }

    [JsonPropertyName("position_z")]
    public float PositionZ { get; init; }

    [JsonPropertyName("zone_x")]
    public int ZoneX { get; init; }

    [JsonPropertyName("zone_y")]
    public int ZoneY { get; init; }

    [JsonPropertyName("body_b64")]
    public string BodyBase64 { get; init; } = "";

    [JsonPropertyName("tombstone")]
    public bool Tombstone { get; init; }

    // C3 failure injection only. A delivery-only row crosses the exact consumer boundary but
    // cannot replace durable journal state. It lets the real client prove stale/malformed
    // rejection followed by recovery without corrupting the snapshot used by a late joiner.
    [JsonPropertyName("delivery_only")]
    public bool DeliveryOnly { get; init; }
}

public sealed record ValheimZdoJournalInterest
{
    [JsonPropertyName("recipient_id")]
    public string RecipientId { get; init; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = "";

    [JsonPropertyName("world_epoch")]
    public string WorldEpoch { get; init; } = "";

    [JsonPropertyName("zone_epoch")]
    public long ZoneEpoch { get; init; }

    [JsonPropertyName("zone_x")]
    public int ZoneX { get; init; }

    [JsonPropertyName("zone_y")]
    public int ZoneY { get; init; }

    [JsonPropertyName("radius_zones")]
    public int RadiusZones { get; init; }

    [JsonPropertyName("refresh")]
    public bool Refresh { get; init; }
}

public sealed record ValheimZdoJournalAck
{
    [JsonPropertyName("recipient_id")]
    public string RecipientId { get; init; } = "";

    [JsonPropertyName("world_epoch")]
    public string WorldEpoch { get; init; } = "";

    [JsonPropertyName("sequences")]
    public long[] Sequences { get; init; } = [];
}

public sealed record ValheimZdoJournalDelivery
{
    [JsonPropertyName("seq")]
    public long Sequence { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("object")]
    public ValheimZdoJournalObject Object { get; init; } = new();
}

public sealed record ValheimZdoJournalMutationResult(
    bool Accepted,
    string Result,
    int RecipientCount,
    long DurableObjects);

public sealed record ValheimZdoJournalInterestResult(
    string RecipientId,
    int SnapshotCount,
    int PendingCount);

public sealed record ValheimZdoJournalAckResult(int Acknowledged, int Unknown);

public sealed record ValheimZdoJournalStatus(
    bool PersistenceEnabled,
    bool PersistenceHealthy,
    long WalBytes,
    long DurableObjects,
    long Mutations,
    long DeliveryOnly,
    long Snapshots,
    long Deltas,
    long Tombstones,
    long Acknowledged,
    long Pending,
    int Interests);

public sealed record ValheimZdoJournalRunStatus(
    string RunId,
    string WorldEpoch,
    long DurableObjects,
    int InterestedRecipients,
    long Pending);

/// <summary>
/// Lumberjacks-owned semantic ZDO journal. Mutation capture precedes Valheim's recipient-specific
/// CreateSyncList selection; recipient interest and delivery sequencing live here. Only durable
/// object state is replayed after restart. Clients re-register their explicit zone interest and
/// receive a reconstructed snapshot, then continue with deltas.
/// </summary>
public sealed class ValheimZdoJournalService
{
    readonly object _gate = new();
    readonly Dictionary<string, ValheimZdoJournalObject> _objects = new(StringComparer.Ordinal);
    readonly Dictionary<string, InterestState> _interests = new(StringComparer.Ordinal);
    readonly Dictionary<string, SortedDictionary<long, ValheimZdoJournalDelivery>> _pending =
        new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _nextSequence = new(StringComparer.Ordinal);
    readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    readonly string? _walPath;
    bool _persistenceHealthy = true;
    long _mutations;
    long _deliveryOnly;
    long _snapshots;
    long _deltas;
    long _tombstones;
    long _acknowledged;

    public ValheimZdoJournalService(string? walPath = null)
    {
        _walPath = string.IsNullOrWhiteSpace(walPath) ? null : Path.GetFullPath(walPath);
        Replay();
    }

    public bool PersistenceEnabled => _walPath is not null;

    public ValheimZdoJournalMutationResult Record(ValheimZdoJournalObject mutation)
    {
        lock (_gate)
        {
            var key = ObjectKey(mutation.WorldEpoch, mutation.UidUser, mutation.UidId);
            var hasCurrent = _objects.TryGetValue(key, out var current);
            var isNewer = !hasCurrent || mutation.ObjectRevision > current!.ObjectRevision;

            if (!mutation.DeliveryOnly && !isNewer)
            {
                return new(false, "stale_journal_revision", 0, _objects.Count);
            }

            if (mutation.DeliveryOnly)
            {
                _deliveryOnly++;
            }
            else
            {
                Append(mutation);
                _objects[key] = mutation with { DeliveryOnly = false };
                _mutations++;
            }

            var kind = mutation.Tombstone ? "tombstone" : "delta";
            var recipients = 0;
            foreach (var pair in _interests)
            {
                if (!Matches(pair.Value.Interest, mutation)) continue;
                Enqueue(pair.Key, kind, mutation);
                recipients++;
            }

            if (mutation.Tombstone) _tombstones += recipients;
            else _deltas += recipients;
            return new(true, mutation.DeliveryOnly ? "delivery_only" : kind,
                recipients, _objects.Count);
        }
    }

    public ValheimZdoJournalInterestResult RegisterInterest(
        string recipientId, ValheimZdoJournalInterest interest)
    {
        lock (_gate)
        {
            var changed = !_interests.TryGetValue(recipientId, out var previous) ||
                !SameInterest(previous.Interest, interest);
            _interests[recipientId] = new(interest, DateTimeOffset.UtcNow);

            var snapshots = 0;
            if (changed || interest.Refresh)
            {
                foreach (var state in _objects.Values)
                {
                    if (!Matches(interest, state)) continue;
                    if (HasPending(recipientId, state)) continue;
                    Enqueue(recipientId, state.Tombstone ? "tombstone" : "snapshot", state);
                    snapshots++;
                }
                _snapshots += snapshots;
            }

            return new(recipientId, snapshots, PendingCount(recipientId, interest.WorldEpoch));
        }
    }

    public IReadOnlyList<ValheimZdoJournalDelivery> Pending(
        string recipientId, string worldEpoch, int limit)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(recipientId, out var queue))
                return Array.Empty<ValheimZdoJournalDelivery>();
            return queue.Values
                .Where(value => string.Equals(value.Object.WorldEpoch, worldEpoch,
                    StringComparison.Ordinal))
                .Take(Math.Clamp(limit, 1, 1024))
                .ToArray();
        }
    }

    public ValheimZdoJournalAckResult Acknowledge(
        string recipientId, string worldEpoch, IReadOnlyList<long> sequences)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(recipientId, out var queue))
                return new(0, sequences.Count);
            var acknowledged = 0;
            foreach (var sequence in sequences.Distinct())
            {
                if (queue.TryGetValue(sequence, out var delivery) &&
                    string.Equals(delivery.Object.WorldEpoch, worldEpoch, StringComparison.Ordinal))
                {
                    queue.Remove(sequence);
                    acknowledged++;
                }
            }
            _acknowledged += acknowledged;
            return new(acknowledged, sequences.Count - acknowledged);
        }
    }

    public ValheimZdoJournalStatus Status()
    {
        lock (_gate)
        {
            return new(
                PersistenceEnabled,
                _persistenceHealthy,
                WalBytes(),
                _objects.Count,
                _mutations,
                _deliveryOnly,
                _snapshots,
                _deltas,
                _tombstones,
                _acknowledged,
                _pending.Values.Sum(queue => (long)queue.Count),
                _interests.Count);
        }
    }

    public ValheimZdoJournalRunStatus RunStatus(string runId, string worldEpoch)
    {
        lock (_gate)
        {
            return new(
                runId,
                worldEpoch,
                _objects.Values.LongCount(value =>
                    string.Equals(value.RunId, runId, StringComparison.Ordinal) &&
                    string.Equals(value.WorldEpoch, worldEpoch, StringComparison.Ordinal)),
                _interests.Values.Count(value =>
                    string.Equals(value.Interest.RunId, runId, StringComparison.Ordinal) &&
                    string.Equals(value.Interest.WorldEpoch, worldEpoch, StringComparison.Ordinal)),
                _pending.Values.Sum(queue => (long)queue.Values.Count(value =>
                    string.Equals(value.Object.RunId, runId, StringComparison.Ordinal) &&
                    string.Equals(value.Object.WorldEpoch, worldEpoch, StringComparison.Ordinal))));
        }
    }

    public int Reset(string? worldEpoch)
    {
        lock (_gate)
        {
            var keys = _objects
                .Where(pair => worldEpoch is null ||
                    string.Equals(pair.Value.WorldEpoch, worldEpoch, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys) _objects.Remove(key);
            if (worldEpoch is null)
            {
                _interests.Clear();
                _pending.Clear();
                _nextSequence.Clear();
            }
            else
            {
                var recipients = _interests
                    .Where(pair => string.Equals(
                        pair.Value.Interest.WorldEpoch, worldEpoch,
                        StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var recipient in recipients) _interests.Remove(recipient);

                var emptyQueues = new List<string>();
                foreach (var pair in _pending)
                {
                    foreach (var sequence in pair.Value
                        .Where(delivery => string.Equals(
                            delivery.Value.Object.WorldEpoch, worldEpoch,
                            StringComparison.Ordinal))
                        .Select(delivery => delivery.Key).ToArray())
                        pair.Value.Remove(sequence);
                    if (pair.Value.Count == 0) emptyQueues.Add(pair.Key);
                }
                foreach (var recipient in emptyQueues) _pending.Remove(recipient);
                foreach (var recipient in recipients)
                    if (!_pending.ContainsKey(recipient)) _nextSequence.Remove(recipient);
            }
            RewriteWal();
            return keys.Length;
        }
    }

    void Enqueue(string recipientId, string kind, ValheimZdoJournalObject state)
    {
        if (!_pending.TryGetValue(recipientId, out var queue))
        {
            queue = new();
            _pending[recipientId] = queue;
        }
        var next = _nextSequence.TryGetValue(recipientId, out var current) ? current + 1 : 1;
        _nextSequence[recipientId] = next;
        queue[next] = new()
        {
            Sequence = next,
            Kind = kind,
            Object = state,
        };
    }

    bool HasPending(string recipientId, ValheimZdoJournalObject state) =>
        _pending.TryGetValue(recipientId, out var queue) &&
        queue.Values.Any(value =>
            value.Object.UidUser == state.UidUser &&
            value.Object.UidId == state.UidId &&
            value.Object.ObjectRevision == state.ObjectRevision &&
            string.Equals(value.Object.WorldEpoch, state.WorldEpoch, StringComparison.Ordinal));

    int PendingCount(string recipientId, string worldEpoch) =>
        _pending.TryGetValue(recipientId, out var queue)
            ? queue.Values.Count(value => string.Equals(
                value.Object.WorldEpoch, worldEpoch, StringComparison.Ordinal))
            : 0;

    static bool SameInterest(
        ValheimZdoJournalInterest left, ValheimZdoJournalInterest right) =>
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
        string.Equals(left.WorldEpoch, right.WorldEpoch, StringComparison.Ordinal) &&
        left.ZoneEpoch == right.ZoneEpoch &&
        left.ZoneX == right.ZoneX &&
        left.ZoneY == right.ZoneY &&
        left.RadiusZones == right.RadiusZones;

    static bool Matches(ValheimZdoJournalInterest interest, ValheimZdoJournalObject state) =>
        string.Equals(interest.WorldEpoch, state.WorldEpoch, StringComparison.Ordinal) &&
        string.Equals(interest.RunId, state.RunId, StringComparison.Ordinal) &&
        Math.Abs(interest.ZoneX - state.ZoneX) <= interest.RadiusZones &&
        Math.Abs(interest.ZoneY - state.ZoneY) <= interest.RadiusZones;

    static string ObjectKey(string worldEpoch, long uidUser, long uidId) =>
        worldEpoch + "|" + uidUser + ":" + uidId;

    void Append(ValheimZdoJournalObject state)
    {
        if (_walPath is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_walPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(_walPath, JsonSerializer.Serialize(state, _json) + Environment.NewLine);
        }
        catch
        {
            _persistenceHealthy = false;
            throw;
        }
    }

    void Replay()
    {
        if (_walPath is null || !File.Exists(_walPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_walPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var state = JsonSerializer.Deserialize<ValheimZdoJournalObject>(line, _json);
                if (state is null || state.DeliveryOnly ||
                    string.IsNullOrWhiteSpace(state.WorldEpoch)) continue;
                var key = ObjectKey(state.WorldEpoch, state.UidUser, state.UidId);
                if (!_objects.TryGetValue(key, out var current) ||
                    state.ObjectRevision > current.ObjectRevision)
                    _objects[key] = state;
            }
        }
        catch
        {
            _persistenceHealthy = false;
        }
    }

    void RewriteWal()
    {
        if (_walPath is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_walPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = _walPath + ".new";
            using (var writer = new StreamWriter(temporary, false))
            {
                foreach (var state in _objects.Values.OrderBy(value => value.WorldEpoch)
                    .ThenBy(value => value.UidUser).ThenBy(value => value.UidId))
                    writer.WriteLine(JsonSerializer.Serialize(state, _json));
            }
            File.Move(temporary, _walPath, true);
            _persistenceHealthy = true;
        }
        catch
        {
            _persistenceHealthy = false;
            throw;
        }
    }

    long WalBytes()
    {
        if (_walPath is null || !File.Exists(_walPath)) return 0;
        try { return new FileInfo(_walPath).Length; }
        catch { return 0; }
    }

    sealed record InterestState(
        ValheimZdoJournalInterest Interest,
        DateTimeOffset UpdatedUtc);
}

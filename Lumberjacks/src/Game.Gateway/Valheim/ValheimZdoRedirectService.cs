using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Game.Gateway.Valheim;

/// <summary>
/// A single redirected ZDO envelope, as forwarded by the Harmony mod after it
/// suppressed the original Valheim send.
/// </summary>
public sealed record ValheimZdoRedirectEnvelope
{
    public string? CorrelationId { get; init; }
    public string? CreatedUtc { get; init; }
    public string? Recipient { get; init; }
    public string? ImportanceClass { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? RecipientId { get; init; }
    public long? Seq { get; init; }
    public long? UidUser { get; init; }
    public long? UidId { get; init; }
    public long? Owner { get; init; }
    public int? OwnerRev { get; init; }
    public int? DataRev { get; init; }
    public int? Prefab { get; init; }
    public double[]? Pos { get; init; }
    public string? PriorityTier { get; init; }
    public int? PriorityRank { get; init; }
    public string? PriorityReason { get; init; }
    public double? DistanceMeters { get; init; }
    public string? BodyB64 { get; init; }
}

/// <summary>
/// POST body for /valheim/zdo-redirect/receipts. window_id and a non-null
/// envelopes list (each carrying seq) are required; everything else is
/// best-effort bookkeeping.
/// </summary>
public sealed record ValheimZdoRedirectRequest
{
    public int? SchemaVersion { get; init; }
    public string? SourceInstance { get; init; }
    public string? ModRelease { get; init; }
    public string? Operation { get; init; }
    public List<ValheimZdoRedirectEnvelope>? Payload { get; init; }
    // Frozen schema-1 rollback fields.
    public string? Source { get; init; }
    public string? WindowId { get; init; }
    public List<ValheimZdoRedirectEnvelope>? Envelopes { get; init; }
}

public sealed record ValheimZdoRedirectRecordResult(int Received, long Total);
public sealed record ValheimZdoRedirectAckResult(int Acknowledged, int Unknown);

public sealed record ValheimZdoRedirectWindowStatus(
    string WindowId,
    string RecipientId,
    long Receipts,
    long DistinctSeq,
    long Acknowledged,
    long Pending,
    long Duplicates,
    long? MinSeq,
    long? MaxSeq,
    long MissingSeq,
    bool SeqTrackingSaturated,
    long EmptyBodyCount,
    DateTime? FirstUtc,
    DateTime? LastUtc,
    IReadOnlyDictionary<string, long> PerPrefab,
    IReadOnlyDictionary<string, long> PerSource)
{
    public static ValheimZdoRedirectWindowStatus Aggregate(
        string windowId, IReadOnlyList<ValheimZdoRedirectWindowStatus> statuses)
    {
        static long Sum(IEnumerable<ValheimZdoRedirectWindowStatus> values, Func<ValheimZdoRedirectWindowStatus, long> selector) =>
            values.Sum(selector);
        var first = statuses.MinBy(status => status.FirstUtc)!.FirstUtc;
        var last = statuses.MaxBy(status => status.LastUtc)!.LastUtc;
        var perPrefab = statuses.SelectMany(status => status.PerPrefab)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
        var perSource = statuses.SelectMany(status => status.PerSource)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
        return new(
            windowId,
            "aggregate",
            Sum(statuses, status => status.Receipts),
            statuses.Sum(status => status.DistinctSeq),
            Sum(statuses, status => status.Acknowledged),
            Sum(statuses, status => status.Pending),
            Sum(statuses, status => status.Duplicates),
            statuses.Select(status => status.MinSeq).Where(value => value is not null).DefaultIfEmpty().Min(),
            statuses.Select(status => status.MaxSeq).Where(value => value is not null).DefaultIfEmpty().Max(),
            Sum(statuses, status => status.MissingSeq),
            statuses.Any(status => status.SeqTrackingSaturated),
            Sum(statuses, status => status.EmptyBodyCount),
            first,
            last,
            perPrefab,
            perSource);
    }
}

/// <summary>
/// Durable queue for redirected Valheim ZDO payloads. The Harmony mod
/// suppresses a ZDO send and forwards it here instead. When a WAL path is
/// configured, records and acknowledgements are flushed before the in-memory
/// state changes so an interrupted Gateway can resume the same window.
/// </summary>
public sealed class ValheimZdoRedirectService
{
    private const int MaxWalEntryBytes = 64 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, ValheimZdoRedirectWindow> _windows =
        new(StringComparer.Ordinal);
    private readonly object _persistenceGate = new();
    private readonly string? _walPath;

    public bool PersistenceEnabled => _walPath is not null;
    public bool PersistenceHealthy { get; private set; } = true;
    public long WalBytes => _walPath is null || !File.Exists(_walPath) ? 0 : new FileInfo(_walPath).Length;

    public ValheimZdoRedirectService()
        : this(Environment.GetEnvironmentVariable("VALHEIM_ZDO_QUEUE_PATH"))
    {
    }

    public ValheimZdoRedirectService(string? walPath)
    {
        if (string.IsNullOrWhiteSpace(walPath)) return;
        _walPath = Path.GetFullPath(walPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_walPath)!);
        ReplayWal();
    }

    public ValheimZdoRedirectRecordResult RecordEnvelopes(
        string windowId,
        string source,
        IReadOnlyList<ValheimZdoRedirectEnvelope> envelopes)
    {
        return RecordEnvelopes(windowId, source, envelopes, recipientSelector: null);
    }

    public ValheimZdoRedirectRecordResult RecordEnvelopes(
        string windowId,
        string source,
        IReadOnlyList<ValheimZdoRedirectEnvelope> envelopes,
        string? recipientId)
    {
        return RecordEnvelopes(windowId, source, envelopes, _ => recipientId);
    }

    /// <summary>
    /// Public so ingest can resolve the partition per envelope. The producer stamps a Steam
    /// identity it can actually observe; only the Gateway knows the opaque recipient it maps to.
    /// </summary>
    public ValheimZdoRedirectRecordResult RecordEnvelopes(
        string windowId,
        string source,
        IReadOnlyList<ValheimZdoRedirectEnvelope> envelopes,
        Func<ValheimZdoRedirectEnvelope, string?>? recipientSelector)
    {
        lock (_persistenceGate)
        {
            var observedUtc = DateTime.UtcNow;
            var groups = envelopes.GroupBy(envelope => NormalizeRecipient(
                recipientSelector?.Invoke(envelope) ?? envelope.Recipient ?? envelope.RecipientId));
            long total = 0;
            foreach (var group in groups)
            {
                var recipientId = group.Key;
                var batch = group.ToList();
                AppendWal(new() { SchemaVersion = CurrentWalSchemaVersion, Op = "record",
                    WindowId = windowId, RecipientId = recipientId, Source = source,
                    Envelopes = batch, ObservedUtc = observedUtc });
                var window = GetOrAdd(windowId, recipientId);
                total += window.RecordBatch(source, batch, observedUtc);
            }
            return new ValheimZdoRedirectRecordResult(envelopes.Count, total);
        }
    }

    public ValheimZdoRedirectWindowStatus GetStatus(string windowId) =>
        AggregateStatuses(windowId);

    public ValheimZdoRedirectWindowStatus GetStatus(string windowId, string recipientId) =>
        _windows.TryGetValue(Key(windowId, NormalizeRecipient(recipientId)), out var window)
            ? window.ToStatus(windowId, NormalizeRecipient(recipientId))
            : ValheimZdoRedirectWindow.Empty(windowId, NormalizeRecipient(recipientId));

    public IReadOnlyList<ValheimZdoRedirectWindowStatus> GetAllStatuses() =>
        _windows.Keys
            .Select(ParseKey)
            .Select(pair => pair.WindowId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(AggregateStatuses)
            .ToList();

    public IReadOnlyList<ValheimZdoRedirectWindowStatus> GetAllRecipientStatuses() =>
        _windows
            .Select(kv =>
            {
                var key = ParseKey(kv.Key);
                return kv.Value.ToStatus(key.WindowId, key.RecipientId);
            })
            .OrderBy(status => status.WindowId, StringComparer.Ordinal)
            .ThenBy(status => status.RecipientId, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<ValheimZdoRedirectEnvelope> Pending(string windowId, int limit)
        => Pending(windowId, ValheimRecipient.Legacy, limit);

    public IReadOnlyList<ValheimZdoRedirectEnvelope> Pending(string windowId, string recipientId, int limit)
    {
        return _windows.TryGetValue(Key(windowId, NormalizeRecipient(recipientId)), out var window)
            ? window.Pending(Math.Clamp(limit, 1, 1024))
            : Array.Empty<ValheimZdoRedirectEnvelope>();
    }

    public ValheimZdoRedirectAckResult Acknowledge(string windowId, IReadOnlyList<long> sequences)
        => Acknowledge(windowId, ValheimRecipient.Legacy, sequences);

    public ValheimZdoRedirectAckResult Acknowledge(string windowId, string recipientId, IReadOnlyList<long> sequences)
    {
        lock (_persistenceGate)
        {
            var normalized = NormalizeRecipient(recipientId);
            AppendWal(new() { SchemaVersion = CurrentWalSchemaVersion, Op = "ack", WindowId = windowId,
                RecipientId = normalized, Sequences = sequences.ToArray() });
            return _windows.TryGetValue(Key(windowId, normalized), out var window)
                ? window.Acknowledge(sequences)
                : new(0, sequences.Count);
        }
    }

    /// <summary>
    /// Rewrites the WAL from the current in-memory state. The replacement is
    /// fsynced before an atomic rename, so a crash leaves either the old WAL or
    /// the complete compacted WAL. This is intentionally explicit; callers
    /// should run it only after measuring the source WAL and queue state.
    /// </summary>
    public long Compact()
    {
        if (_walPath is null) return 0;
        lock (_persistenceGate)
        {
            var tempPath = _walPath + ".compact-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    foreach (var pair in _windows.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        var key = ParseKey(pair.Key);
                        WriteWalEntry(stream, new RedirectWalEntry
                        {
                            SchemaVersion = CurrentWalSchemaVersion,
                            Op = "snapshot",
                            WindowId = key.WindowId,
                            RecipientId = key.RecipientId,
                            Snapshot = pair.Value.ToSnapshot(),
                        });
                    }
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _walPath, overwrite: true);
                return WalBytes;
            }
            catch
            {
                PersistenceHealthy = false;
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }
    }

    /// <summary>Clears a single window. Returns whether it existed.</summary>
    public bool Reset(string windowId)
    {
        lock (_persistenceGate)
        {
            AppendWal(new() { Op = "reset", WindowId = windowId });
            var removed = false;
            foreach (var key in _windows.Keys.Where(key => ParseKey(key).WindowId == windowId).ToList())
                removed |= _windows.TryRemove(key, out _);
            return removed;
        }
    }

    /// <summary>Clears all windows. Returns how many were cleared.</summary>
    public int ResetAll()
    {
        lock (_persistenceGate)
        {
            AppendWal(new() { Op = "reset_all" });
            var count = _windows.Count;
            _windows.Clear();
            return count;
        }
    }

    private void AppendWal(RedirectWalEntry entry)
    {
        if (_walPath is null) return;
        try
        {
            using var stream = new FileStream(_walPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.WriteThrough);
            WriteWalEntry(stream, entry);
        }
        catch
        {
            PersistenceHealthy = false;
            throw;
        }
    }

    private static void WriteWalEntry(FileStream stream, RedirectWalEntry entry)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (payload.Length > MaxWalEntryBytes)
            throw new InvalidOperationException($"ZDO WAL entry is too large ({payload.Length} bytes)");
        Span<byte> length = stackalloc byte[4];
        BitConverter.TryWriteBytes(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private void ReplayWal()
    {
        if (_walPath is null || !File.Exists(_walPath)) return;
        try
        {
            using var stream = new FileStream(_walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            while (stream.Position < stream.Length)
            {
                var entryStart = stream.Position;
                if (stream.Length - stream.Position < sizeof(int))
                {
                    stream.SetLength(entryStart);
                    break;
                }
                var length = reader.ReadInt32();
                if (length <= 0 || length > MaxWalEntryBytes)
                    throw new InvalidDataException($"Invalid ZDO WAL entry length {length} at {entryStart}");
                if (stream.Length - stream.Position < length)
                {
                    stream.SetLength(entryStart);
                    break;
                }
                var payload = reader.ReadBytes(length);
                var entry = JsonSerializer.Deserialize<RedirectWalEntry>(payload)
                    ?? throw new InvalidDataException($"Empty ZDO WAL entry at {entryStart}");
                ApplyWalEntry(entry);
            }
        }
        catch
        {
            PersistenceHealthy = false;
            throw;
        }
    }

    private void ApplyWalEntry(RedirectWalEntry entry)
    {
        var recipientId = entry.SchemaVersion.GetValueOrDefault(1) <= 1
            ? ValheimRecipient.Legacy
            : NormalizeRecipient(entry.RecipientId);
        switch (entry.Op)
        {
            case "record" when !string.IsNullOrWhiteSpace(entry.WindowId) && entry.Envelopes is not null:
                GetOrAdd(entry.WindowId, recipientId)
                    .RecordBatch(entry.Source ?? "unknown", entry.Envelopes, entry.ObservedUtc ?? DateTime.UtcNow);
                break;
            case "ack" when !string.IsNullOrWhiteSpace(entry.WindowId) && entry.Sequences is not null:
                if (_windows.TryGetValue(Key(entry.WindowId, recipientId), out var window)) window.Acknowledge(entry.Sequences);
                break;
            case "snapshot" when !string.IsNullOrWhiteSpace(entry.WindowId) && entry.Snapshot is not null:
                _windows[Key(entry.WindowId, recipientId)] = ValheimZdoRedirectWindow.FromSnapshot(entry.Snapshot);
                break;
            case "reset" when !string.IsNullOrWhiteSpace(entry.WindowId):
                foreach (var key in _windows.Keys.Where(key => ParseKey(key).WindowId == entry.WindowId).ToList())
                    _windows.TryRemove(key, out _);
                break;
            case "reset_all":
                _windows.Clear();
                break;
            default:
                throw new InvalidDataException($"Invalid ZDO WAL operation '{entry.Op}'");
        }
    }

    private sealed record RedirectWalEntry
    {
        public int? SchemaVersion { get; init; }
        public string? Op { get; init; }
        public string? WindowId { get; init; }
        public string? RecipientId { get; init; }
        public string? Source { get; init; }
        public List<ValheimZdoRedirectEnvelope>? Envelopes { get; init; }
        public long[]? Sequences { get; init; }
        public DateTime? ObservedUtc { get; init; }
        public RedirectWalSnapshot? Snapshot { get; init; }
    }

    private sealed record RedirectWalSnapshot(
        long Receipts,
        long Duplicates,
        long Acknowledged,
        long? MinSeq,
        long? MaxSeq,
        long EmptyBodyCount,
        bool SeqTrackingSaturated,
        DateTime? FirstUtc,
        DateTime? LastUtc,
        long[] DistinctSeq,
        List<ValheimZdoRedirectEnvelope> Pending,
        Dictionary<int, long> PerPrefab,
        Dictionary<string, long> PerSource);

    private const int CurrentWalSchemaVersion = 2;

    private static string NormalizeRecipient(string? recipientId) =>
        string.IsNullOrWhiteSpace(recipientId) ? ValheimRecipient.Legacy : recipientId.Trim();

    private static string Key(string windowId, string recipientId) =>
        windowId + "\u001f" + NormalizeRecipient(recipientId);

    private ValheimZdoRedirectWindow GetOrAdd(string windowId, string recipientId) =>
        _windows.GetOrAdd(Key(windowId, recipientId), static _ => new ValheimZdoRedirectWindow());

    private static (string WindowId, string RecipientId) ParseKey(string key)
    {
        var separator = key.IndexOf('\u001f');
        return separator < 0
            ? (key, ValheimRecipient.Legacy)
            : (key[..separator], NormalizeRecipient(key[(separator + 1)..]));
    }

    private ValheimZdoRedirectWindowStatus AggregateStatuses(string windowId)
    {
        var statuses = _windows
            .Where(pair => ParseKey(pair.Key).WindowId == windowId)
            .Select(pair =>
            {
                var key = ParseKey(pair.Key);
                return pair.Value.ToStatus(windowId, key.RecipientId);
            })
            .ToList();
        if (statuses.Count == 0) return ValheimZdoRedirectWindow.Empty(windowId, ValheimRecipient.Legacy);
        return ValheimZdoRedirectWindowStatus.Aggregate(windowId, statuses);
    }

    private sealed class ValheimZdoRedirectWindow
    {
        // Bounded so a runaway/misbehaving sender can't grow this without limit;
        // past the cap we keep counting totals but flag seq tracking as saturated.
        private const int MaxTrackedSeq = 1_000_000;

        private readonly object _gate = new();
        private readonly HashSet<long> _distinctSeq = new();
        private readonly Dictionary<long, ValheimZdoRedirectEnvelope> _pending = new();
        private readonly Dictionary<int, long> _perPrefab = new();
        private readonly Dictionary<string, long> _perSource = new(StringComparer.Ordinal);

        private long _receipts;
        private long _duplicates;
        private long _acknowledged;
        private long? _minSeq;
        private long? _maxSeq;
        private long _emptyBodyCount;
        private bool _seqTrackingSaturated;
        private DateTime? _firstUtc;
        private DateTime? _lastUtc;

        public long RecordBatch(string source, IReadOnlyList<ValheimZdoRedirectEnvelope> envelopes,
            DateTime? observedUtc = null)
        {
            lock (_gate)
            {
                var now = observedUtc ?? DateTime.UtcNow;

                foreach (var envelope in envelopes)
                {
                    var seq = envelope.Seq!.Value;

                    _receipts++;
                    _firstUtc ??= now;
                    _lastUtc = now;

                    if (_minSeq is null || seq < _minSeq)
                        _minSeq = seq;
                    if (_maxSeq is null || seq > _maxSeq)
                        _maxSeq = seq;

                    if (_distinctSeq.Contains(seq))
                    {
                        _duplicates++;
                    }

                    else if (_distinctSeq.Count < MaxTrackedSeq)
                    {
                        _distinctSeq.Add(seq);
                    }
                    else
                    {
                        // Cap reached: can no longer reliably tell new-vs-duplicate
                        // beyond this point. Totals below still keep counting.
                        _seqTrackingSaturated = true;
                    }

                    _pending.TryAdd(seq, envelope);

                    if (string.IsNullOrEmpty(envelope.BodyB64))
                        _emptyBodyCount++;

                    var prefabKey = envelope.Prefab.GetValueOrDefault(0);
                    _perPrefab[prefabKey] = _perPrefab.GetValueOrDefault(prefabKey) + 1;
                    _perSource[source] = _perSource.GetValueOrDefault(source) + 1;
                }

                return _receipts;
            }
        }

        public IReadOnlyList<ValheimZdoRedirectEnvelope> Pending(int limit)
        {
            lock (_gate)
            {
                // Every envelope remains in the durable reliable queue. Priority only
                // changes delivery order, carrying the proven FieldLab load-order tiers
                // into the authoritative path while seq remains the deterministic tie-break.
                return _pending.Values
                    .OrderBy(envelope => envelope.PriorityRank ?? int.MaxValue)
                    .ThenBy(envelope => envelope.DistanceMeters ?? double.MaxValue)
                    .ThenBy(envelope => envelope.Seq)
                    .Take(limit)
                    .ToList();
            }
        }

        public ValheimZdoRedirectAckResult Acknowledge(IReadOnlyList<long> sequences)
        {
            lock (_gate)
            {
                var acknowledged = 0;
                var unknown = 0;
                foreach (var sequence in sequences.Distinct())
                {
                    if (_pending.Remove(sequence))
                    {
                        acknowledged++;
                        _acknowledged++;
                    }
                    else unknown++;
                }
                return new(acknowledged, unknown);
            }
        }

        public ValheimZdoRedirectWindowStatus ToStatus(string windowId, string recipientId)
        {
            lock (_gate)
            {
                var distinctCount = (long)_distinctSeq.Count;
                var missing = _maxSeq is null ? 0 : Math.Max(0, _maxSeq.Value - distinctCount);

                return new ValheimZdoRedirectWindowStatus(
                    windowId,
                    recipientId,
                    _receipts,
                    distinctCount,
                    _acknowledged,
                    _pending.Count,
                    _duplicates,
                    _minSeq,
                    _maxSeq,
                    missing,
                    _seqTrackingSaturated,
                    _emptyBodyCount,
                    _firstUtc,
                    _lastUtc,
                    _perPrefab.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    new Dictionary<string, long>(_perSource));
            }
        }

        public RedirectWalSnapshot ToSnapshot()
        {
            lock (_gate)
            {
                return new(
                    _receipts,
                    _duplicates,
                    _acknowledged,
                    _minSeq,
                    _maxSeq,
                    _emptyBodyCount,
                    _seqTrackingSaturated,
                    _firstUtc,
                    _lastUtc,
                    _distinctSeq.ToArray(),
                    _pending.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList(),
                    new Dictionary<int, long>(_perPrefab),
                    new Dictionary<string, long>(_perSource));
            }
        }

        public static ValheimZdoRedirectWindow FromSnapshot(RedirectWalSnapshot snapshot)
        {
            var window = new ValheimZdoRedirectWindow
            {
                _receipts = snapshot.Receipts,
                _duplicates = snapshot.Duplicates,
                _acknowledged = snapshot.Acknowledged,
                _minSeq = snapshot.MinSeq,
                _maxSeq = snapshot.MaxSeq,
                _emptyBodyCount = snapshot.EmptyBodyCount,
                _seqTrackingSaturated = snapshot.SeqTrackingSaturated,
                _firstUtc = snapshot.FirstUtc,
                _lastUtc = snapshot.LastUtc,
            };
            foreach (var seq in snapshot.DistinctSeq) window._distinctSeq.Add(seq);
            foreach (var envelope in snapshot.Pending)
                if (envelope.Seq is long seq) window._pending[seq] = envelope;
            foreach (var pair in snapshot.PerPrefab) window._perPrefab[pair.Key] = pair.Value;
            foreach (var pair in snapshot.PerSource) window._perSource[pair.Key] = pair.Value;
            return window;
        }

        public static ValheimZdoRedirectWindowStatus Empty(string windowId, string recipientId) =>
            new(
                windowId,
                recipientId,
                Receipts: 0,
                DistinctSeq: 0,
                Acknowledged: 0,
                Pending: 0,
                Duplicates: 0,
                MinSeq: null,
                MaxSeq: null,
                MissingSeq: 0,
                SeqTrackingSaturated: false,
                EmptyBodyCount: 0,
                FirstUtc: null,
                LastUtc: null,
                PerPrefab: new Dictionary<string, long>(),
                PerSource: new Dictionary<string, long>());
    }
}

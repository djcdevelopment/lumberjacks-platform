using System.Text.Json.Serialization;

namespace Game.Gateway.Valheim;

/// <summary>
/// Process-authoritative Valheim connected-world descriptor for the native-network cutover.
/// The dedicated server publishes it only after loading the real world. Clients may ask before
/// it exists, but they never receive synthesized or default world fields.
/// </summary>
public sealed class ValheimWorldZoneService
{
    private readonly object _gate = new();
    private ValheimWorldDescriptor? _descriptor;
    private long _publishCount;
    private readonly Dictionary<string, ValheimZoneSnapshotState> _snapshots =
        new(StringComparer.Ordinal);
    // A process restart must not reuse an epoch that a still-running dedicated server
    // remembers from the previous Gateway incarnation.
    private long _nextSnapshotEpoch =
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public ValheimWorldDescriptor Publish(ValheimWorldDescriptor descriptor)
    {
        lock (_gate)
        {
            _descriptor = descriptor;
            _publishCount++;
            return descriptor;
        }
    }

    public ValheimWorldDescriptor? Current()
    {
        lock (_gate) return _descriptor;
    }

    public long PublishCount
    {
        get { lock (_gate) return _publishCount; }
    }

    public ValheimZoneSnapshotState Begin(
        string recipientId, ValheimZoneMembershipRequest request)
    {
        lock (_gate)
        {
            var state = new ValheimZoneSnapshotState(
                recipientId, request, ++_nextSnapshotEpoch);
            _snapshots[recipientId] = state;
            return state;
        }
    }

    public ValheimZoneSnapshotState Publish(ValheimZoneSnapshotPublication publication)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(publication.RecipientId, out var state) ||
                state.Request.RunId != publication.RunId ||
                state.Request.ActionId != publication.ActionId ||
                state.Request.WorldEpoch != publication.WorldEpoch ||
                state.SnapshotEpoch != publication.SnapshotEpoch ||
                state.Request.ZoneEpoch != publication.ZoneEpoch ||
                state.Request.ZoneX != publication.ZoneX ||
                state.Request.ZoneY != publication.ZoneY)
                throw new InvalidDataException("zone_snapshot_publication_scope_mismatch");
            if (state.Objects is not null)
                return state;
            state.Objects = publication.Objects;
            return state;
        }
    }

    public ValheimZoneSnapshotChunk? NextChunk(string recipientId)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(recipientId, out var state) ||
                state.Objects is null || state.AckedChunks >= state.Objects.Length)
                return null;
            var index = state.AckedChunks;
            return new(
                state.Request.RunId,
                state.Request.ActionId,
                state.Request.WorldEpoch,
                state.Request.ZoneEpoch,
                state.SnapshotEpoch,
                state.Request.ZoneX,
                state.Request.ZoneY,
                index + 1,
                state.Objects.Length,
                state.Objects[index]);
        }
    }

    public (ValheimZoneSnapshotState State, bool Complete, bool Duplicate) Ack(
        string recipientId, long snapshotEpoch, int chunkIndex)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(recipientId, out var state) ||
                state.Objects is null || state.SnapshotEpoch != snapshotEpoch ||
                chunkIndex > state.Objects.Length)
                throw new InvalidDataException("zone_snapshot_ack_out_of_order");
            if (chunkIndex == state.AckedChunks)
                return (state, state.AckedChunks == state.Objects.Length, true);
            if (chunkIndex != state.AckedChunks + 1)
                throw new InvalidDataException("zone_snapshot_ack_out_of_order");
            state.AckedChunks = chunkIndex;
            return (state, state.AckedChunks == state.Objects.Length, false);
        }
    }

    public bool TryMarkComplete(string recipientId, long snapshotEpoch)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(recipientId, out var state) ||
                state.Objects is null || state.SnapshotEpoch != snapshotEpoch ||
                state.AckedChunks != state.Objects.Length || state.CompleteSent)
                return false;
            state.CompleteSent = true;
            return true;
        }
    }

    public ValheimZoneSnapshotState Leave(
        string recipientId, string runId, string actionId, long snapshotEpoch)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(recipientId, out var state) ||
                state.Request.RunId != runId ||
                state.Request.ActionId != actionId ||
                state.SnapshotEpoch != snapshotEpoch ||
                state.Objects is null || state.AckedChunks != state.Objects.Length)
                throw new InvalidDataException("zone_membership_leave_scope_mismatch");
            _snapshots.Remove(recipientId);
            return state;
        }
    }
}

public sealed record ValheimZoneMembershipRequest
{
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("action_id")] public string ActionId { get; init; } = "";
    [JsonPropertyName("world_epoch")] public string WorldEpoch { get; init; } = "";
    [JsonPropertyName("zone_epoch")] public long ZoneEpoch { get; init; }
    [JsonPropertyName("zone_x")] public int ZoneX { get; init; }
    [JsonPropertyName("zone_y")] public int ZoneY { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "";
}

public sealed record ValheimZoneSnapshotPublication
{
    [JsonPropertyName("recipient_id")] public string RecipientId { get; init; } = "";
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("action_id")] public string ActionId { get; init; } = "";
    [JsonPropertyName("world_epoch")] public string WorldEpoch { get; init; } = "";
    [JsonPropertyName("zone_epoch")] public long ZoneEpoch { get; init; }
    [JsonPropertyName("snapshot_epoch")] public long SnapshotEpoch { get; init; }
    [JsonPropertyName("zone_x")] public int ZoneX { get; init; }
    [JsonPropertyName("zone_y")] public int ZoneY { get; init; }
    [JsonPropertyName("objects")] public ValheimZoneSnapshotObject[] Objects { get; init; } = [];
}

public sealed record ValheimZoneSnapshotObject
{
    [JsonPropertyName("uid_user")] public long UidUser { get; init; }
    [JsonPropertyName("uid_id")] public uint UidId { get; init; }
    [JsonPropertyName("prefab")] public int Prefab { get; init; }
    [JsonPropertyName("owner")] public long Owner { get; init; }
    [JsonPropertyName("owner_rev")] public int OwnerRevision { get; init; }
    [JsonPropertyName("data_rev")] public long DataRevision { get; init; }
    [JsonPropertyName("position_x")] public float PositionX { get; init; }
    [JsonPropertyName("position_y")] public float PositionY { get; init; }
    [JsonPropertyName("position_z")] public float PositionZ { get; init; }
    [JsonPropertyName("body_b64")] public string BodyBase64 { get; init; } = "";
}

public sealed record ValheimZoneSnapshotChunk(
    string RunId,
    string ActionId,
    string WorldEpoch,
    long ZoneEpoch,
    long SnapshotEpoch,
    int ZoneX,
    int ZoneY,
    int ChunkIndex,
    int ChunkCount,
    ValheimZoneSnapshotObject Object);

public sealed class ValheimZoneSnapshotState
{
    public ValheimZoneSnapshotState(
        string recipientId, ValheimZoneMembershipRequest request, long snapshotEpoch)
    {
        RecipientId = recipientId;
        Request = request;
        SnapshotEpoch = snapshotEpoch;
    }

    public string RecipientId { get; }
    public ValheimZoneMembershipRequest Request { get; }
    public long SnapshotEpoch { get; }
    public ValheimZoneSnapshotObject[]? Objects { get; set; }
    public int AckedChunks { get; set; }
    public bool CompleteSent { get; set; }
}

public sealed record ValheimWorldDescriptor
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("release_id")]
    public string ReleaseId { get; init; } = "";

    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = "";

    [JsonPropertyName("world_epoch")]
    public string WorldEpoch { get; init; } = "";

    [JsonPropertyName("world_name")]
    public string WorldName { get; init; } = "";

    [JsonPropertyName("seed")]
    public int Seed { get; init; }

    [JsonPropertyName("seed_name")]
    public string SeedName { get; init; } = "";

    [JsonPropertyName("world_uid")]
    public long WorldUid { get; init; }

    [JsonPropertyName("world_generation_version")]
    public int WorldGenerationVersion { get; init; }

    [JsonPropertyName("network_time")]
    public double NetworkTime { get; init; }

    [JsonPropertyName("save_epoch")]
    public long SaveEpoch { get; init; }

    [JsonPropertyName("initial_zone_x")]
    public int InitialZoneX { get; init; }

    [JsonPropertyName("initial_zone_y")]
    public int InitialZoneY { get; init; }
}

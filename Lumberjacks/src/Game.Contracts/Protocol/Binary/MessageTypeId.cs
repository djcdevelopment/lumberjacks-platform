namespace Game.Contracts.Protocol.Binary;

/// <summary>
/// Numeric message type identifiers for binary protocol.
/// Maps 1:1 with the string constants in <see cref="MessageType"/>.
///
/// Packed as 6 bits in the binary envelope header (supports up to 63 message types).
/// </summary>
public enum MessageTypeId : byte
{
    // Client → Server
    JoinRegion = 1,
    LeaveRegion = 2,
    PlayerMove = 3,
    PlayerInput = 6,
    PlaceStructure = 4,
    Interact = 5,

    // Server → Client
    SessionStarted = 10,
    WorldSnapshot = 11,
    EntityUpdate = 12,
    EntityRemoved = 13,
    EventEmitted = 14,
    Error = 15,
    PriorityManifest = 16,
    PriorityManifestObject = 17,
}

/// <summary>
/// Bidirectional mapping between string message types and numeric IDs.
/// </summary>
public static class MessageTypeMapping
{
    private static readonly Dictionary<string, MessageTypeId> StringToId = new()
    {
        [MessageType.JoinRegion] = MessageTypeId.JoinRegion,
        [MessageType.LeaveRegion] = MessageTypeId.LeaveRegion,
        [MessageType.PlayerMove] = MessageTypeId.PlayerMove,
        [MessageType.PlayerInput] = MessageTypeId.PlayerInput,
        [MessageType.PlaceStructure] = MessageTypeId.PlaceStructure,
        [MessageType.Interact] = MessageTypeId.Interact,
        [MessageType.SessionStarted] = MessageTypeId.SessionStarted,
        [MessageType.WorldSnapshot] = MessageTypeId.WorldSnapshot,
        [MessageType.EntityUpdate] = MessageTypeId.EntityUpdate,
        [MessageType.EntityRemoved] = MessageTypeId.EntityRemoved,
        [MessageType.PriorityManifest] = MessageTypeId.PriorityManifest,
        [MessageType.PriorityManifestObject] = MessageTypeId.PriorityManifestObject,
        [MessageType.EventEmitted] = MessageTypeId.EventEmitted,
        [MessageType.Error] = MessageTypeId.Error,
    };

    private static readonly Dictionary<MessageTypeId, string> IdToString = new()
    {
        [MessageTypeId.JoinRegion] = MessageType.JoinRegion,
        [MessageTypeId.LeaveRegion] = MessageType.LeaveRegion,
        [MessageTypeId.PlayerMove] = MessageType.PlayerMove,
        [MessageTypeId.PlayerInput] = MessageType.PlayerInput,
        [MessageTypeId.PlaceStructure] = MessageType.PlaceStructure,
        [MessageTypeId.Interact] = MessageType.Interact,
        [MessageTypeId.SessionStarted] = MessageType.SessionStarted,
        [MessageTypeId.WorldSnapshot] = MessageType.WorldSnapshot,
        [MessageTypeId.EntityUpdate] = MessageType.EntityUpdate,
        [MessageTypeId.EntityRemoved] = MessageType.EntityRemoved,
        [MessageTypeId.PriorityManifest] = MessageType.PriorityManifest,
        [MessageTypeId.PriorityManifestObject] = MessageType.PriorityManifestObject,
        [MessageTypeId.EventEmitted] = MessageType.EventEmitted,
        [MessageTypeId.Error] = MessageType.Error,
    };

    public static MessageTypeId ToId(string messageType)
        => StringToId.TryGetValue(messageType, out var id) ? id : throw new ArgumentException($"Unknown message type: {messageType}");

    public static string ToName(MessageTypeId id)
        => IdToString.TryGetValue(id, out var name) ? name : throw new ArgumentException($"Unknown message type ID: {id}");

    public static bool TryGetId(string messageType, out MessageTypeId id)
        => StringToId.TryGetValue(messageType, out id);

    public static bool TryGetName(MessageTypeId id, out string name)
        => IdToString.TryGetValue(id, out name!);
}

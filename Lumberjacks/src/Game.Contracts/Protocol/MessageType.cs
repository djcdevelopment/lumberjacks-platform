namespace Game.Contracts.Protocol;

public static class MessageType
{
    // Client → Server
    public const string JoinRegion = "join_region";
    public const string LeaveRegion = "leave_region";
    public const string PlayerMove = "player_move";
    public const string PlayerInput = "player_input";
    public const string PlaceStructure = "place_structure";
    public const string Interact = "interact";

    // Server → Client
    public const string SessionStarted = "session_started";
    public const string WorldSnapshot = "world_snapshot";
    public const string EntityUpdate = "entity_update";
    public const string EntityRemoved = "entity_removed";
    public const string PriorityManifest = "priority_manifest";
    public const string PriorityManifestObject = "priority_manifest_object";
    public const string EventEmitted = "event_emitted";
    public const string Error = "error";
}

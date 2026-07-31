namespace Game.Contracts.Protocol;

public static class MessageType
{
    // Client → Server
    public const string JoinRegion = "join_region";
    public const string LeaveRegion = "leave_region";
    public const string PlayerMove = "player_move";
    public const string PlayerInput = "player_input";
    public const string ValheimPlayerMotion = "valheim_player_motion";
    public const string PlaceStructure = "place_structure";
    public const string Interact = "interact";
    public const string ReliableAck = "reliable_ack";
    public const string ValheimSessionProbe = "valheim_session_probe";
    public const string ValheimControlResponse = "valheim_control_response";
    public const string ValheimDirectPulseProbe = "valheim_direct_pulse_probe";
    public const string ValheimPeerBind = "valheim_peer_bind";
    public const string ValheimRoutedRpcSend = "valheim_routed_rpc_send";
    public const string ValheimZdoMutation = "valheim_zdo_mutation";
    public const string ValheimZdoInterest = "valheim_zdo_interest";
    public const string ValheimZdoAck = "valheim_zdo_ack";
    public const string ValheimOwnershipLeaseRequest = "valheim_ownership_lease_request";
    public const string ValheimOwnershipLeaseIssue = "valheim_ownership_lease_issue";
    public const string ValheimOwnershipAction = "valheim_ownership_action";
    public const string ValheimOwnershipActionResult = "valheim_ownership_action_result";

    // Server → Client
    public const string SessionStarted = "session_started";
    public const string ValheimControlRequest = "valheim_control_request";
    public const string ValheimControlReceipt = "valheim_control_receipt";
    public const string ValheimDirectPulse = "valheim_direct_pulse";
    public const string ValheimRoutedRpc = "valheim_routed_rpc";
    public const string ValheimZdoDelivery = "valheim_zdo_delivery";
    public const string ValheimZdoMutationReceipt = "valheim_zdo_mutation_receipt";
    public const string ValheimZdoInterestReceipt = "valheim_zdo_interest_receipt";
    public const string ValheimZdoInterestStatus = "valheim_zdo_interest_status";
    public const string ValheimOwnershipLeaseCommand = "valheim_ownership_lease_command";
    public const string ValheimOwnershipLeaseGranted = "valheim_ownership_lease_granted";
    public const string ValheimOwnershipLeaseReceipt = "valheim_ownership_lease_receipt";
    public const string ValheimOwnershipActionRejected = "valheim_ownership_action_rejected";
    public const string ValheimOwnershipActionAuthorized = "valheim_ownership_action_authorized";
    public const string ValheimOwnershipActionCompleted = "valheim_ownership_action_completed";
    public const string ValheimOwnershipResultReceipt = "valheim_ownership_result_receipt";
    public const string WorldSnapshot = "world_snapshot";
    public const string EntityUpdate = "entity_update";
    public const string EntityRemoved = "entity_removed";
    public const string PriorityManifest = "priority_manifest";
    public const string PriorityManifestObject = "priority_manifest_object";
    public const string EventEmitted = "event_emitted";
    public const string Error = "error";
}

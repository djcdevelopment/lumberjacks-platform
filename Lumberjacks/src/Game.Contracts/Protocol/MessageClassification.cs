namespace Game.Contracts.Protocol;

/// <summary>
/// Maps every message type to its delivery lane (ADR 0003).
/// This classification must be defined before implementation sprawls.
/// </summary>
public static class MessageClassification
{
    private static readonly Dictionary<string, DeliveryLane> Map = new()
    {
        // Reliable: authoritative, ordered, auditable
        [MessageType.JoinRegion] = DeliveryLane.Reliable,
        [MessageType.LeaveRegion] = DeliveryLane.Reliable,
        [MessageType.PlaceStructure] = DeliveryLane.Reliable,
        [MessageType.Interact] = DeliveryLane.Reliable,
        [MessageType.SessionStarted] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoMutation] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoInterest] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoAck] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoDelivery] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoMutationReceipt] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoInterestReceipt] = DeliveryLane.Reliable,
        [MessageType.ValheimZdoInterestStatus] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipLeaseRequest] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipLeaseIssue] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipAction] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipActionResult] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipLeaseCommand] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipLeaseGranted] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipLeaseReceipt] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipActionRejected] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipActionAuthorized] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipActionCompleted] = DeliveryLane.Reliable,
        [MessageType.ValheimOwnershipResultReceipt] = DeliveryLane.Reliable,
        [MessageType.EventEmitted] = DeliveryLane.Reliable,
        [MessageType.Error] = DeliveryLane.Reliable,

        // Reliable: world_snapshot is full state transfer — must arrive
        [MessageType.WorldSnapshot] = DeliveryLane.Reliable,
        [MessageType.PriorityManifest] = DeliveryLane.Reliable,

        // Datagram: transient, supersedable, safely discardable
        [MessageType.PlayerMove] = DeliveryLane.Datagram,
        [MessageType.PlayerInput] = DeliveryLane.Datagram,
        [MessageType.ValheimPlayerMotion] = DeliveryLane.Datagram,
        [MessageType.EntityUpdate] = DeliveryLane.Datagram,
        [MessageType.EntityRemoved] = DeliveryLane.Datagram,
        [MessageType.PriorityManifestObject] = DeliveryLane.Datagram,
    };

    public static DeliveryLane GetLane(string messageType)
        => Map.TryGetValue(messageType, out var lane) ? lane : DeliveryLane.Reliable;

    public static bool IsDatagram(string messageType)
        => GetLane(messageType) == DeliveryLane.Datagram;
}

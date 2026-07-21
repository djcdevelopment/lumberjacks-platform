namespace Game.Contracts.Protocol;

/// <summary>
/// ADR 0003: Multi-Lane Transport Strategy.
/// Every protocol message is classified into a delivery lane.
/// </summary>
public enum DeliveryLane
{
    /// <summary>Ordered, retried, auditable. For authoritative state.</summary>
    Reliable,

    /// <summary>Best-effort, no ordering guarantee. For transient state that can be dropped.</summary>
    Datagram,
}

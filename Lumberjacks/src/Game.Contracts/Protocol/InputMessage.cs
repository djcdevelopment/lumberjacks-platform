using Game.Contracts.Entities;

namespace Game.Contracts.Protocol;

/// <summary>
/// Input-only message sent from client → server.
/// Contains raw player intent, NOT computed position.
/// The server's tick loop computes position from this input.
///
/// Wire size: 5 bytes binary (direction:8, speed:8, actions:8, inputSeq:16)
/// vs ~120 bytes for the old JSON PlayerMoveMessage with absolute positions.
/// </summary>
public record PlayerInputMessage
{
    /// <summary>
    /// Movement direction in degrees: 0-255 mapped to 0°-360°.
    /// 0 = North (+Z), 64 = East (+X), 128 = South (-Z), 192 = West (-X).
    /// Value of 255 with SpeedPercent=0 means "no movement / idle".
    /// </summary>
    public required byte Direction { get; init; }

    /// <summary>
    /// Movement speed as percentage: 0 = stopped, 100 = full speed.
    /// Values > 100 are clamped by the server.
    /// </summary>
    public required byte SpeedPercent { get; init; }

    /// <summary>
    /// Bitfield for discrete actions:
    ///   bit 0: Jump
    ///   bit 1: Crouch
    ///   bit 2: Interact
    ///   bit 3-7: Reserved
    /// </summary>
    public byte ActionFlags { get; init; }

    /// <summary>
    /// Client-side input sequence number. The server echoes this back
    /// in authoritative updates so the client knows which inputs are confirmed.
    /// Used for client-side prediction reconciliation.
    /// </summary>
    public required ushort InputSeq { get; init; }
}

/// <summary>
/// A timestamped input ready for the simulation's input queue.
/// </summary>
public record QueuedInput
{
    public required string PlayerId { get; init; }
    public required PlayerInputMessage Input { get; init; }

    /// <summary>The tick this input should be processed on (assigned by server).</summary>
    public required long TargetTick { get; init; }

    /// <summary>When the input was received (for latency diagnostics).</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

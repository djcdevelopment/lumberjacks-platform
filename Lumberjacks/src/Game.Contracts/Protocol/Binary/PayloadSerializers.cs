using Game.Contracts.Entities;

namespace Game.Contracts.Protocol.Binary;

/// <summary>
/// Binary payload serializers for hot-path messages.
/// Replaces JSON payloads inside binary envelopes for bandwidth-critical paths.
///
/// Wire sizes:
///   EntityUpdate:  ~19 bytes binary vs ~200+ bytes JSON
///   PlayerInput:    5 bytes binary vs ~120 bytes JSON
///   EntityRemoved:  varies (VarInt string length + string)
/// </summary>
public static class PayloadSerializers
{
    public const int ValheimPlayerMotionBytes = 36;

    // ──────────────────────────────────────────────
    //  EntityUpdate (server → client)
    //
    //  Layout:
    //    entityId:    VarInt-prefixed UTF-8 string
    //    position:    CompactVec3 (48 bits)
    //    velocity:    CompactVec3 (48 bits)
    //    heading:     uint16 (0-3600, 0.1° precision)
    //    lastInputSeq: uint16
    //    tick:        uint32
    //    stateHash:   uint32
    //
    //  Total: ~19 bytes for a typical 8-char entity ID
    //  vs ~200+ bytes JSON (entity_id, entity_type, nested data dict, tick, state_hash)
    // ──────────────────────────────────────────────

    public static int WriteEntityUpdate(
        Span<byte> buffer,
        string entityId,
        Vec3 position,
        Vec3 velocity,
        double heading,
        ushort lastInputSeq,
        uint tick,
        uint stateHash)
    {
        var writer = new BitWriter(buffer);

        writer.WriteString(entityId);
        CompactVec3.FromVec3(position).Write(ref writer);
        CompactVec3.FromVec3(velocity).Write(ref writer);

        // Heading: 0-360° → 0-3600 (0.1° precision) packed as uint16
        var headingPacked = (ushort)Math.Clamp(Math.Round(heading * 10.0), 0, 3600);
        writer.WriteUInt16(headingPacked);

        writer.WriteUInt16(lastInputSeq);
        writer.WriteUInt32(tick);
        writer.WriteUInt32(stateHash);

        return writer.ByteLength;
    }

    public static EntityUpdateBinary ReadEntityUpdate(ReadOnlySpan<byte> payload)
    {
        var reader = new BitReader(payload);

        var entityId = reader.ReadString();
        var position = CompactVec3.Read(ref reader).ToVec3();
        var velocity = CompactVec3.Read(ref reader).ToVec3();
        var heading = reader.ReadUInt16() / 10.0;
        var lastInputSeq = reader.ReadUInt16();
        var tick = reader.ReadUInt32();
        var stateHash = reader.ReadUInt32();

        return new EntityUpdateBinary(entityId, position, velocity, heading, lastInputSeq, tick, stateHash);
    }

    // ──────────────────────────────────────────────
    //  PlayerInput (client → server)
    //
    //  Layout:
    //    direction:    byte (0-255 → 0°-360°)
    //    speedPercent: byte (0-100)
    //    actionFlags:  byte (bitfield)
    //    inputSeq:     uint16
    //
    //  Total: 5 bytes (exactly matches the spec in InputMessage.cs)
    // ──────────────────────────────────────────────

    public static int WritePlayerInput(
        Span<byte> buffer,
        byte direction,
        byte speedPercent,
        byte actionFlags,
        ushort inputSeq)
    {
        var writer = new BitWriter(buffer);

        writer.WriteByte(direction);
        writer.WriteByte(speedPercent);
        writer.WriteByte(actionFlags);
        writer.WriteUInt16(inputSeq);

        return writer.ByteLength; // always 5
    }

    public static PlayerInputBinary ReadPlayerInput(ReadOnlySpan<byte> payload)
    {
        var reader = new BitReader(payload);

        var direction = reader.ReadByte();
        var speedPercent = reader.ReadByte();
        var actionFlags = reader.ReadByte();
        var inputSeq = reader.ReadUInt16();

        return new PlayerInputBinary(direction, speedPercent, actionFlags, inputSeq);
    }

    // Valheim player motion is an observed transform, not an input for Lumberjacks' generic
    // simulation. The ZDO id is the cross-client entity key already shared by Valheim. Position
    // uses signed centimetres (enough precision and world range for presentation), velocity uses
    // signed centimetres/second, and yaw uses 0.1-degree units. Sequence lives in BinaryEnvelope.
    public static int WriteValheimPlayerMotion(
        Span<byte> buffer,
        long zdoUserId,
        uint zdoId,
        Vec3 position,
        Vec3 velocity,
        double yaw,
        uint sentMilliseconds)
    {
        var writer = new BitWriter(buffer);
        var userBits = unchecked((ulong)zdoUserId);
        writer.WriteUInt32((uint)(userBits >> 32));
        writer.WriteUInt32((uint)userBits);
        writer.WriteUInt32(zdoId);
        WriteCentimetres(ref writer, position.X);
        WriteCentimetres(ref writer, position.Y);
        WriteCentimetres(ref writer, position.Z);
        writer.WriteInt16(ToCentimetresPerSecond(velocity.X));
        writer.WriteInt16(ToCentimetresPerSecond(velocity.Y));
        writer.WriteInt16(ToCentimetresPerSecond(velocity.Z));
        var normalizedYaw = ((yaw % 360.0) + 360.0) % 360.0;
        writer.WriteUInt16((ushort)Math.Round(normalizedYaw * 10.0));
        writer.WriteUInt32(sentMilliseconds);
        return writer.ByteLength;
    }

    public static ValheimPlayerMotionBinary ReadValheimPlayerMotion(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ValheimPlayerMotionBytes)
            throw new ArgumentException($"Valheim motion payload must be {ValheimPlayerMotionBytes} bytes, got {payload.Length}");

        var reader = new BitReader(payload);
        var userBits = ((ulong)reader.ReadUInt32() << 32) | reader.ReadUInt32();
        var zdoId = reader.ReadUInt32();
        var position = new Vec3(ReadCentimetres(ref reader), ReadCentimetres(ref reader), ReadCentimetres(ref reader));
        var velocity = new Vec3(
            reader.ReadInt16() / 100.0,
            reader.ReadInt16() / 100.0,
            reader.ReadInt16() / 100.0);
        var yaw = reader.ReadUInt16() / 10.0;
        var sentMilliseconds = reader.ReadUInt32();
        return new ValheimPlayerMotionBinary(unchecked((long)userBits), zdoId, position, velocity, yaw, sentMilliseconds);
    }

    private static void WriteCentimetres(ref BitWriter writer, double value)
    {
        var scaled = Math.Clamp(Math.Round(value * 100.0), int.MinValue, int.MaxValue);
        writer.WriteUInt32(unchecked((uint)(int)scaled));
    }

    private static double ReadCentimetres(ref BitReader reader) => unchecked((int)reader.ReadUInt32()) / 100.0;

    private static short ToCentimetresPerSecond(double value) =>
        (short)Math.Clamp(Math.Round(value * 100.0), short.MinValue, short.MaxValue);

    // ──────────────────────────────────────────────
    //  EntityRemoved (server → client)
    //
    //  Layout:
    //    entityId: VarInt-prefixed UTF-8 string
    //    tick:     uint32
    //
    //  Total: ~6 bytes for typical entity ID
    // ──────────────────────────────────────────────

    public static int WriteEntityRemoved(Span<byte> buffer, string entityId, uint tick)
    {
        var writer = new BitWriter(buffer);
        writer.WriteString(entityId);
        writer.WriteUInt32(tick);
        return writer.ByteLength;
    }

    public static EntityRemovedBinary ReadEntityRemoved(ReadOnlySpan<byte> payload)
    {
        var reader = new BitReader(payload);
        var entityId = reader.ReadString();
        var tick = reader.ReadUInt32();
        return new EntityRemovedBinary(entityId, tick);
    }
}

/// <summary>Deserialized binary entity update.</summary>
public readonly record struct EntityUpdateBinary(
    string EntityId,
    Vec3 Position,
    Vec3 Velocity,
    double Heading,
    ushort LastInputSeq,
    uint Tick,
    uint StateHash);

/// <summary>Deserialized binary player input.</summary>
public readonly record struct PlayerInputBinary(
    byte Direction,
    byte SpeedPercent,
    byte ActionFlags,
    ushort InputSeq);

public readonly record struct ValheimPlayerMotionBinary(
    long ZdoUserId,
    uint ZdoId,
    Vec3 Position,
    Vec3 Velocity,
    double Yaw,
    uint SentMilliseconds);

/// <summary>Deserialized binary entity removed.</summary>
public readonly record struct EntityRemovedBinary(
    string EntityId,
    uint Tick);

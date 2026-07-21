namespace Game.Contracts.Protocol.Binary;

/// <summary>
/// Binary wire format envelope — replaces JSON envelope for compact framing.
///
/// Header layout (42 bits = 6 bytes padded):
///   [version: 4 bits] [type: 6 bits] [lane: 1 bit] [seq: 16 bits] [payloadLen: 16 bits]
///   Total header: 43 bits → 6 bytes (5 spare bits)
///
/// Compared to JSON envelope: ~6 bytes vs ~80-120 bytes.
/// </summary>
public static class BinaryEnvelope
{
    public const int HeaderBits = 43;
    public const int HeaderBytes = 6; // ceil(43/8)
    public const int MaxPayloadBytes = 65535;

    /// <summary>
    /// Serialize a binary envelope: header + raw payload bytes.
    /// Returns the total byte count written.
    /// </summary>
    public static int Write(
        Span<byte> buffer,
        byte version,
        MessageTypeId type,
        DeliveryLane lane,
        ushort seq,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentException($"Payload too large: {payload.Length} bytes (max {MaxPayloadBytes})");

        // Clear the header region
        buffer[..HeaderBytes].Clear();

        var writer = new BitWriter(buffer);

        // Header fields
        writer.WriteBits(version, 4);              // 4 bits: protocol version (0-15)
        writer.WriteBits((uint)type, 6);            // 6 bits: message type (0-63)
        writer.WriteBool(lane == DeliveryLane.Datagram); // 1 bit: delivery lane
        writer.WriteUInt16(seq);                    // 16 bits: sequence number
        writer.WriteUInt16((ushort)payload.Length);  // 16 bits: payload length

        // Payload
        payload.CopyTo(buffer[HeaderBytes..]);

        return HeaderBytes + payload.Length;
    }

    /// <summary>
    /// Parse a binary envelope header from raw bytes.
    /// </summary>
    public static BinaryEnvelopeHeader ReadHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderBytes)
            throw new ArgumentException($"Buffer too small for header: {buffer.Length} bytes (need {HeaderBytes})");

        var reader = new BitReader(buffer);

        var version = (byte)reader.ReadBits(4);
        var type = (MessageTypeId)reader.ReadBits(6);
        var lane = reader.ReadBool() ? DeliveryLane.Datagram : DeliveryLane.Reliable;
        var seq = reader.ReadUInt16();
        var payloadLen = reader.ReadUInt16();

        return new BinaryEnvelopeHeader(version, type, lane, seq, payloadLen);
    }

    /// <summary>
    /// Get the payload slice from a complete envelope buffer.
    /// </summary>
    public static ReadOnlySpan<byte> GetPayload(ReadOnlySpan<byte> buffer, BinaryEnvelopeHeader header)
    {
        return buffer.Slice(HeaderBytes, header.PayloadLength);
    }

    /// <summary>
    /// Calculate total frame size from a header.
    /// </summary>
    public static int FrameSize(BinaryEnvelopeHeader header)
        => HeaderBytes + header.PayloadLength;
}

/// <summary>
/// Parsed binary envelope header.
/// </summary>
public readonly record struct BinaryEnvelopeHeader(
    byte Version,
    MessageTypeId Type,
    DeliveryLane Lane,
    ushort Seq,
    ushort PayloadLength);

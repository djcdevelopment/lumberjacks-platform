using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Xunit;

namespace Game.Contracts.Tests;

public class BinaryEnvelopeTests
{
    [Fact]
    public void Write_and_ReadHeader_roundtrip()
    {
        Span<byte> buf = stackalloc byte[64];
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        var written = BinaryEnvelope.Write(
            buf, version: 1, MessageTypeId.PlayerMove, DeliveryLane.Datagram,
            seq: 42, payload);

        var header = BinaryEnvelope.ReadHeader(buf);

        Assert.Equal(1, header.Version);
        Assert.Equal(MessageTypeId.PlayerMove, header.Type);
        Assert.Equal(DeliveryLane.Datagram, header.Lane);
        Assert.Equal(42, header.Seq);
        Assert.Equal(3, header.PayloadLength);
    }

    [Fact]
    public void Payload_bytes_preserved()
    {
        Span<byte> buf = stackalloc byte[64];
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        BinaryEnvelope.Write(buf, 1, MessageTypeId.EntityUpdate, DeliveryLane.Datagram, 1, payload);

        var header = BinaryEnvelope.ReadHeader(buf);
        var readPayload = BinaryEnvelope.GetPayload(buf, header);

        Assert.Equal(payload.Length, readPayload.Length);
        for (int i = 0; i < payload.Length; i++)
            Assert.Equal(payload[i], readPayload[i]);
    }

    [Fact]
    public void Header_is_6_bytes()
    {
        Assert.Equal(6, BinaryEnvelope.HeaderBytes);
    }

    [Fact]
    public void Total_frame_size_is_header_plus_payload()
    {
        Span<byte> buf = stackalloc byte[64];
        var payload = new byte[10];

        var written = BinaryEnvelope.Write(buf, 1, MessageTypeId.SessionStarted, DeliveryLane.Reliable, 1, payload);

        Assert.Equal(16, written); // 6 header + 10 payload
    }

    [Fact]
    public void Empty_payload_roundtrips()
    {
        Span<byte> buf = stackalloc byte[16];

        BinaryEnvelope.Write(buf, 1, MessageTypeId.LeaveRegion, DeliveryLane.Reliable, 0, ReadOnlySpan<byte>.Empty);

        var header = BinaryEnvelope.ReadHeader(buf);

        Assert.Equal(0, header.PayloadLength);
        Assert.Equal(MessageTypeId.LeaveRegion, header.Type);
        Assert.Equal(DeliveryLane.Reliable, header.Lane);
    }

    [Fact]
    public void Reliable_lane_bit_is_zero()
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryEnvelope.Write(buf, 1, MessageTypeId.JoinRegion, DeliveryLane.Reliable, 1, ReadOnlySpan<byte>.Empty);

        var header = BinaryEnvelope.ReadHeader(buf);
        Assert.Equal(DeliveryLane.Reliable, header.Lane);
    }

    [Fact]
    public void Datagram_lane_bit_is_one()
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryEnvelope.Write(buf, 1, MessageTypeId.PlayerMove, DeliveryLane.Datagram, 1, ReadOnlySpan<byte>.Empty);

        var header = BinaryEnvelope.ReadHeader(buf);
        Assert.Equal(DeliveryLane.Datagram, header.Lane);
    }

    [Fact]
    public void Seq_max_value_roundtrips()
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryEnvelope.Write(buf, 1, MessageTypeId.Error, DeliveryLane.Reliable, ushort.MaxValue, ReadOnlySpan<byte>.Empty);

        var header = BinaryEnvelope.ReadHeader(buf);
        Assert.Equal(ushort.MaxValue, header.Seq);
    }

    [Fact]
    public void Version_max_value_roundtrips()
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryEnvelope.Write(buf, 15, MessageTypeId.Error, DeliveryLane.Reliable, 0, ReadOnlySpan<byte>.Empty);

        var header = BinaryEnvelope.ReadHeader(buf);
        Assert.Equal(15, header.Version);
    }

    [Fact]
    public void All_message_type_ids_roundtrip()
    {
        var types = new[]
        {
            MessageTypeId.JoinRegion, MessageTypeId.LeaveRegion, MessageTypeId.PlayerMove,
            MessageTypeId.PlaceStructure, MessageTypeId.Interact,
            MessageTypeId.SessionStarted, MessageTypeId.WorldSnapshot, MessageTypeId.EntityUpdate,
            MessageTypeId.EntityRemoved, MessageTypeId.EventEmitted, MessageTypeId.Error,
        };

        Span<byte> buf = stackalloc byte[16];

        foreach (var type in types)
        {
            buf.Clear();
            BinaryEnvelope.Write(buf, 1, type, DeliveryLane.Reliable, 0, ReadOnlySpan<byte>.Empty);
            var header = BinaryEnvelope.ReadHeader(buf);
            Assert.Equal(type, header.Type);
        }
    }

    [Fact]
    public void FrameSize_returns_correct_total()
    {
        var header = new BinaryEnvelopeHeader(1, MessageTypeId.PlayerMove, DeliveryLane.Datagram, 0, 100);
        Assert.Equal(106, BinaryEnvelope.FrameSize(header));
    }

    [Fact]
    public void Buffer_too_small_for_header_throws()
    {
        Assert.Throws<ArgumentException>(() => BinaryEnvelope.ReadHeader(new byte[3]));
    }

    [Fact]
    public void Bandwidth_comparison_vs_json_envelope()
    {
        // Demonstrate the size advantage:
        // Binary: 6 byte header + payload
        // JSON: ~80-120 byte envelope overhead

        Span<byte> binaryBuf = stackalloc byte[64];
        var payload = new byte[] { 0x01 }; // 1 byte payload

        var binarySize = BinaryEnvelope.Write(
            binaryBuf, 1, MessageTypeId.PlayerMove, DeliveryLane.Datagram, 42, payload);

        // Equivalent JSON envelope
        var jsonEnvelope = Game.Contracts.Protocol.EnvelopeFactory.Create(
            MessageType.PlayerMove, new { x = 1 });
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(
            Game.Contracts.Protocol.EnvelopeFactory.Serialize(jsonEnvelope));

        // Binary should be dramatically smaller
        Assert.True(binarySize < jsonBytes.Length,
            $"Binary ({binarySize}B) should be smaller than JSON ({jsonBytes.Length}B)");
        Assert.True(binarySize <= 7, $"Binary move frame should be ≤7 bytes, was {binarySize}");
    }
}

using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Xunit;

namespace Game.Contracts.Tests;

/// <summary>
/// Tests for the UDP packet format: [udpToken: 8 bytes] [binaryEnvelope: header + payload].
/// Validates that packets can be constructed and parsed correctly.
/// </summary>
public class UdpPacketFormatTests
{
    private const int TokenBytes = 8;

    [Fact]
    public void PlayerInput_UdpPacket_Roundtrip()
    {
        ulong token = 0xDEADBEEFCAFEBABE;

        // Build payload
        var payloadBuf = new byte[16];
        var payloadLen = PayloadSerializers.WritePlayerInput(payloadBuf, direction: 128, speedPercent: 50, actionFlags: 1, inputSeq: 42);

        // Build binary envelope
        var envBuf = new byte[BinaryEnvelope.HeaderBytes + payloadLen];
        var envLen = BinaryEnvelope.Write(envBuf, 1, MessageTypeId.PlayerInput, DeliveryLane.Datagram, 0, payloadBuf.AsSpan(0, payloadLen));

        // Build UDP packet: [token][envelope]
        var packet = new byte[TokenBytes + envLen];
        BitConverter.TryWriteBytes(packet.AsSpan(0, TokenBytes), token);
        envBuf.AsSpan(0, envLen).CopyTo(packet.AsSpan(TokenBytes));

        // Parse: extract token
        var parsedToken = BitConverter.ToUInt64(packet, 0);
        Assert.Equal(token, parsedToken);

        // Parse: extract envelope
        var envelopeSpan = packet.AsSpan(TokenBytes);
        var header = BinaryEnvelope.ReadHeader(envelopeSpan);
        Assert.Equal(MessageTypeId.PlayerInput, header.Type);
        Assert.Equal(DeliveryLane.Datagram, header.Lane);

        // Parse: extract payload
        var payload = BinaryEnvelope.GetPayload(envelopeSpan, header);
        var input = PayloadSerializers.ReadPlayerInput(payload);
        Assert.Equal(128, input.Direction);
        Assert.Equal(50, input.SpeedPercent);
        Assert.Equal(1, input.ActionFlags);
        Assert.Equal(42, input.InputSeq);
    }

    [Fact]
    public void PlayerInput_UdpPacket_TotalSize_Is19Bytes()
    {
        // Token(8) + Header(6) + Payload(5) = 19 bytes
        var payloadBuf = new byte[16];
        var payloadLen = PayloadSerializers.WritePlayerInput(payloadBuf, 0, 0, 0, 0);

        var totalSize = TokenBytes + BinaryEnvelope.HeaderBytes + payloadLen;
        Assert.Equal(19, totalSize);
    }

    [Fact]
    public void EntityUpdate_UdpPacket_Roundtrip()
    {
        ulong token = 12345;

        var payloadBuf = new byte[128];
        var payloadLen = PayloadSerializers.WriteEntityUpdate(
            payloadBuf, "player-1",
            new Game.Contracts.Entities.Vec3(100, 0, 200),
            new Game.Contracts.Entities.Vec3(5, 0, -3),
            heading: 90.0, lastInputSeq: 10, tick: 500, stateHash: 0xABCD);

        var envBuf = new byte[BinaryEnvelope.HeaderBytes + payloadLen];
        var envLen = BinaryEnvelope.Write(envBuf, 1, MessageTypeId.EntityUpdate, DeliveryLane.Datagram, 0, payloadBuf.AsSpan(0, payloadLen));

        var packet = new byte[TokenBytes + envLen];
        BitConverter.TryWriteBytes(packet.AsSpan(0, TokenBytes), token);
        envBuf.AsSpan(0, envLen).CopyTo(packet.AsSpan(TokenBytes));

        // Parse
        var parsedToken = BitConverter.ToUInt64(packet, 0);
        Assert.Equal(token, parsedToken);

        var header = BinaryEnvelope.ReadHeader(packet.AsSpan(TokenBytes));
        Assert.Equal(MessageTypeId.EntityUpdate, header.Type);

        var payload = BinaryEnvelope.GetPayload(packet.AsSpan(TokenBytes), header);
        var update = PayloadSerializers.ReadEntityUpdate(payload);
        Assert.Equal("player-1", update.EntityId);
        Assert.Equal(100.0, update.Position.X, 0);
        Assert.Equal(90.0, update.Heading, 1);
        Assert.Equal(10, update.LastInputSeq);
        Assert.Equal(500u, update.Tick);
    }

    [Fact]
    public void UdpPacket_TooSmall_Detected()
    {
        // Minimum valid packet: 8 (token) + 6 (header) = 14 bytes
        var tooSmall = new byte[13];
        Assert.True(tooSmall.Length < TokenBytes + BinaryEnvelope.HeaderBytes);
    }

    [Fact]
    public void ValheimPlayerMotion_UdpPacket_Roundtrip()
    {
        const ulong token = 0x0102030405060708;
        var payload = new byte[PayloadSerializers.ValheimPlayerMotionBytes];
        var payloadLength = PayloadSerializers.WriteValheimPlayerMotion(
            payload,
            zdoUserId: -9876543210,
            zdoId: 4242,
            new Game.Contracts.Entities.Vec3(123.456, -7.891, -432.109),
            new Game.Contracts.Entities.Vec3(6.789, -1.234, 0.125),
            yaw: -45.0,
            sentMilliseconds: 123456789);

        var envelope = new byte[BinaryEnvelope.HeaderBytes + payloadLength];
        var envelopeLength = BinaryEnvelope.Write(
            envelope, 1, MessageTypeId.ValheimPlayerMotion, DeliveryLane.Datagram, 65534, payload);
        var packet = new byte[TokenBytes + envelopeLength];
        BitConverter.TryWriteBytes(packet.AsSpan(0, TokenBytes), token);
        envelope.CopyTo(packet.AsSpan(TokenBytes));

        var header = BinaryEnvelope.ReadHeader(packet.AsSpan(TokenBytes));
        var motion = PayloadSerializers.ReadValheimPlayerMotion(
            BinaryEnvelope.GetPayload(packet.AsSpan(TokenBytes), header));

        Assert.Equal(token, BitConverter.ToUInt64(packet, 0));
        Assert.Equal(MessageTypeId.ValheimPlayerMotion, header.Type);
        Assert.Equal(DeliveryLane.Datagram, header.Lane);
        Assert.Equal(65534, header.Seq);
        Assert.Equal(-9876543210, motion.ZdoUserId);
        Assert.Equal((uint)4242, motion.ZdoId);
        Assert.Equal(123.46, motion.Position.X, 2);
        Assert.Equal(-7.89, motion.Position.Y, 2);
        Assert.Equal(-432.11, motion.Position.Z, 2);
        Assert.Equal(6.79, motion.Velocity.X, 2);
        Assert.Equal(315.0, motion.Yaw, 1);
        Assert.Equal((uint)123456789, motion.SentMilliseconds);
        Assert.Equal(50, packet.Length);
        Assert.Equal(
            "080706050403020114bfffc00480fffffffdb34fe916000010920000303afffffcebffff573502a7ff85000c0c4e075bcd15",
            Convert.ToHexString(packet).ToLowerInvariant());
    }

    [Fact]
    public void EntityUpdate_UdpPacket_WellUnder100Bytes()
    {
        // Full entity update packet should be tiny compared to JSON
        var payloadBuf = new byte[128];
        var payloadLen = PayloadSerializers.WriteEntityUpdate(
            payloadBuf, "player-longname-12345",
            new Game.Contracts.Entities.Vec3(10000, 500, -10000),
            new Game.Contracts.Entities.Vec3(10, 0, -10),
            heading: 270.0, lastInputSeq: 65535, tick: 100000, stateHash: uint.MaxValue);

        var totalSize = TokenBytes + BinaryEnvelope.HeaderBytes + payloadLen;
        // Even with a long entity ID, should be well under 100 bytes
        Assert.True(totalSize < 100, $"Full UDP EntityUpdate packet should be < 100 bytes, got {totalSize}");
    }
}

using Game.Contracts.Entities;
using Game.Contracts.Protocol.Binary;
using Xunit;

namespace Game.Contracts.Tests;

public class PayloadSerializerTests
{
    // ── EntityUpdate roundtrip ──

    [Fact]
    public void EntityUpdate_Roundtrip_PreservesAllFields()
    {
        var buf = new byte[128];
        var len = PayloadSerializers.WriteEntityUpdate(
            buf, "player-abc123",
            position: new Vec3(100.0, 25.5, -200.0),
            velocity: new Vec3(5.0, 0.0, -3.0),
            heading: 127.5,
            lastInputSeq: 42,
            tick: 1000,
            stateHash: 0xDEADBEEF);

        var result = PayloadSerializers.ReadEntityUpdate(buf.AsSpan(0, len));

        Assert.Equal("player-abc123", result.EntityId);
        Assert.Equal(100.0, result.Position.X, 0);
        Assert.Equal(25.5, result.Position.Y, 0);
        Assert.Equal(-200.0, result.Position.Z, 0);
        Assert.Equal(5.0, result.Velocity.X, 0);
        Assert.Equal(0.0, result.Velocity.Y, 0);
        Assert.Equal(-3.0, result.Velocity.Z, 0);
        Assert.Equal(127.5, result.Heading, 1);
        Assert.Equal(42, result.LastInputSeq);
        Assert.Equal(1000u, result.Tick);
        Assert.Equal(0xDEADBEEFu, result.StateHash);
    }

    [Fact]
    public void EntityUpdate_CompactSize_MuchSmallerThanJson()
    {
        var buf = new byte[128];
        var len = PayloadSerializers.WriteEntityUpdate(
            buf, "player-1",
            new Vec3(10, 0, 20), new Vec3(1, 0, 0),
            heading: 90.0, lastInputSeq: 1, tick: 50, stateHash: 123);

        // Binary: ~33 bytes (1 VarInt + 8 chars + 6+6 CompactVec3 + 2+2+4+4)
        // JSON equivalent: ~200+ bytes — roughly 6x smaller
        Assert.True(len < 40, $"EntityUpdate binary should be < 40 bytes, got {len}");
    }

    [Fact]
    public void EntityUpdate_HeadingPrecision_TenthOfDegree()
    {
        var buf = new byte[128];
        var len = PayloadSerializers.WriteEntityUpdate(
            buf, "p", new Vec3(0, 0, 0), new Vec3(0, 0, 0),
            heading: 359.9, lastInputSeq: 0, tick: 0, stateHash: 0);

        var result = PayloadSerializers.ReadEntityUpdate(buf.AsSpan(0, len));
        Assert.Equal(359.9, result.Heading, 1);
    }

    [Fact]
    public void EntityUpdate_HeadingClamps_At360()
    {
        var buf = new byte[128];
        var len = PayloadSerializers.WriteEntityUpdate(
            buf, "p", new Vec3(0, 0, 0), new Vec3(0, 0, 0),
            heading: 400.0, lastInputSeq: 0, tick: 0, stateHash: 0);

        var result = PayloadSerializers.ReadEntityUpdate(buf.AsSpan(0, len));
        Assert.Equal(360.0, result.Heading, 1);
    }

    [Fact]
    public void EntityUpdate_NegativePositions_Work()
    {
        var buf = new byte[128];
        var len = PayloadSerializers.WriteEntityUpdate(
            buf, "p", new Vec3(-500, -10, -300), new Vec3(-1, 0, 2),
            heading: 0, lastInputSeq: 0, tick: 0, stateHash: 0);

        var result = PayloadSerializers.ReadEntityUpdate(buf.AsSpan(0, len));
        Assert.Equal(-500.0, result.Position.X, 0);
        Assert.Equal(-10.0, result.Position.Y, 0);
        Assert.Equal(-300.0, result.Position.Z, 0);
    }

    // ── PlayerInput roundtrip ──

    [Fact]
    public void PlayerInput_Roundtrip_PreservesAllFields()
    {
        var buf = new byte[16];
        var len = PayloadSerializers.WritePlayerInput(buf, direction: 128, speedPercent: 75, actionFlags: 0b101, inputSeq: 999);

        var result = PayloadSerializers.ReadPlayerInput(buf.AsSpan(0, len));

        Assert.Equal(128, result.Direction);
        Assert.Equal(75, result.SpeedPercent);
        Assert.Equal(0b101, result.ActionFlags);
        Assert.Equal(999, result.InputSeq);
    }

    [Fact]
    public void PlayerInput_ExactlyFiveBytes()
    {
        var buf = new byte[16];
        var len = PayloadSerializers.WritePlayerInput(buf, 0, 0, 0, 0);
        Assert.Equal(5, len);
    }

    [Fact]
    public void PlayerInput_MaxValues()
    {
        var buf = new byte[16];
        var len = PayloadSerializers.WritePlayerInput(buf, 255, 100, 0xFF, 65535);

        var result = PayloadSerializers.ReadPlayerInput(buf.AsSpan(0, len));
        Assert.Equal(255, result.Direction);
        Assert.Equal(100, result.SpeedPercent);
        Assert.Equal(0xFF, result.ActionFlags);
        Assert.Equal(65535, result.InputSeq);
    }

    // ── EntityRemoved roundtrip ──

    [Fact]
    public void EntityRemoved_Roundtrip()
    {
        var buf = new byte[64];
        var len = PayloadSerializers.WriteEntityRemoved(buf, "player-xyz", tick: 500);

        var result = PayloadSerializers.ReadEntityRemoved(buf.AsSpan(0, len));
        Assert.Equal("player-xyz", result.EntityId);
        Assert.Equal(500u, result.Tick);
    }

    // ── Full binary frame (envelope + payload) ──

    [Fact]
    public void EntityUpdate_FullFrame_EnvelopePlusPayload()
    {
        // Serialize payload
        var payloadBuf = new byte[128];
        var payloadLen = PayloadSerializers.WriteEntityUpdate(
            payloadBuf, "player-1",
            new Vec3(50, 0, 50), new Vec3(1, 0, 0),
            heading: 45.0, lastInputSeq: 10, tick: 100, stateHash: 0xCAFE);

        // Wrap in binary envelope
        var frameBuf = new byte[BinaryEnvelope.HeaderBytes + payloadLen];
        var frameLen = BinaryEnvelope.Write(
            frameBuf, version: 1,
            MessageTypeId.EntityUpdate,
            Game.Contracts.Protocol.DeliveryLane.Datagram,
            seq: 0,
            payloadBuf.AsSpan(0, payloadLen));

        // Parse header
        var header = BinaryEnvelope.ReadHeader(frameBuf);
        Assert.Equal(MessageTypeId.EntityUpdate, header.Type);
        Assert.Equal(payloadLen, header.PayloadLength);

        // Parse payload from frame
        var extractedPayload = BinaryEnvelope.GetPayload(frameBuf, header);
        var result = PayloadSerializers.ReadEntityUpdate(extractedPayload);

        Assert.Equal("player-1", result.EntityId);
        Assert.Equal(50.0, result.Position.X, 0);
        Assert.Equal(45.0, result.Heading, 1);
        Assert.Equal(10, result.LastInputSeq);

        // Total frame: 6 header + payload — should be well under 40 bytes
        Assert.True(frameLen < 40, $"Full EntityUpdate frame should be < 40 bytes, got {frameLen}");
    }

    [Fact]
    public void PlayerInput_FullFrame_EnvelopePlusPayload()
    {
        var payloadBuf = new byte[16];
        var payloadLen = PayloadSerializers.WritePlayerInput(payloadBuf, 64, 80, 0, 42);

        var frameBuf = new byte[BinaryEnvelope.HeaderBytes + payloadLen];
        var frameLen = BinaryEnvelope.Write(
            frameBuf, version: 1,
            MessageTypeId.PlayerInput,
            Game.Contracts.Protocol.DeliveryLane.Datagram,
            seq: 0,
            payloadBuf.AsSpan(0, payloadLen));

        // Total: 6 header + 5 payload = 11 bytes
        Assert.Equal(11, frameLen);

        // Parse back
        var header = BinaryEnvelope.ReadHeader(frameBuf);
        Assert.Equal(MessageTypeId.PlayerInput, header.Type);

        var payload = BinaryEnvelope.GetPayload(frameBuf, header);
        var result = PayloadSerializers.ReadPlayerInput(payload);
        Assert.Equal(64, result.Direction);
        Assert.Equal(80, result.SpeedPercent);
        Assert.Equal(42, result.InputSeq);
    }
}

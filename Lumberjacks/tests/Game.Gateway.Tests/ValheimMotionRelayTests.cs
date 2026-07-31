using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Game.Gateway.Valheim;
using Game.Gateway.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimMotionRelayTests
{
    [Fact]
    public async Task DistinctRecipientInSameRegion_RelaysMotionOverWebSocketFallback()
    {
        var fixture = new MotionRelayFixture();
        var source = fixture.CreateSession("region-spawn", "recipient-a");
        var target = fixture.CreateSession("region-spawn", "recipient-b");
        var (header, payload, frame) = BuildMotionFrame(seq: 10);

        await fixture.Transport.HandleValheimMotionFrameAsync(source, header, payload, frame, "websocket");

        var telemetry = fixture.MotionSnapshot();
        Assert.Equal(1, telemetry.GetProperty("received").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("received_websocket").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("relayed_websocket").GetInt64());
        Assert.Equal(0, telemetry.GetProperty("relayed_udp").GetInt64());
        var sentFrame = Assert.Single(target.Socket.SentFrames);
        Assert.Equal(Convert.ToHexString(frame), Convert.ToHexString(sentFrame));
    }

    [Fact]
    public async Task SameRecipientInSameRegion_IsSuppressed()
    {
        var fixture = new MotionRelayFixture();
        var source = fixture.CreateSession("region-spawn", "recipient-a");
        var target = fixture.CreateSession("region-spawn", "recipient-a");
        var (header, payload, frame) = BuildMotionFrame(seq: 10);

        await fixture.Transport.HandleValheimMotionFrameAsync(source, header, payload, frame, "websocket");

        var telemetry = fixture.MotionSnapshot();
        Assert.Equal(1, telemetry.GetProperty("received").GetInt64());
        Assert.Equal(0, telemetry.GetProperty("relayed_websocket").GetInt64());
        Assert.Empty(target.Socket.SentFrames);
    }

    [Fact]
    public async Task MissingRecipient_IsUnauthorized()
    {
        var fixture = new MotionRelayFixture();
        var source = fixture.CreateSession("region-spawn", recipient: null);
        var (header, payload, frame) = BuildMotionFrame(seq: 10);

        await fixture.Transport.HandleValheimMotionFrameAsync(source, header, payload, frame, "websocket");

        var telemetry = fixture.MotionSnapshot();
        Assert.Equal(0, telemetry.GetProperty("received").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("dropped_unauthorized").GetInt64());
        Assert.Equal(0, telemetry.GetProperty("relayed_websocket").GetInt64());
    }

    [Fact]
    public async Task DuplicateAndOldSequence_AreDroppedAsStale()
    {
        var fixture = new MotionRelayFixture();
        var source = fixture.CreateSession("region-spawn", "recipient-a");
        fixture.CreateSession("region-spawn", "recipient-b");
        var accepted = BuildMotionFrame(seq: 10);
        var duplicate = BuildMotionFrame(seq: 10);
        var older = BuildMotionFrame(seq: 9);

        await fixture.Transport.HandleValheimMotionFrameAsync(source, accepted.Header, accepted.Payload, accepted.Frame, "websocket");
        await fixture.Transport.HandleValheimMotionFrameAsync(source, duplicate.Header, duplicate.Payload, duplicate.Frame, "websocket");
        await fixture.Transport.HandleValheimMotionFrameAsync(source, older.Header, older.Payload, older.Frame, "websocket");

        var telemetry = fixture.MotionSnapshot();
        Assert.Equal(1, telemetry.GetProperty("received").GetInt64());
        Assert.Equal(2, telemetry.GetProperty("dropped_stale").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("relayed_websocket").GetInt64());
    }

    [Fact]
    public async Task DifferentSourceZdoAfterBind_IsDroppedAsStale()
    {
        var fixture = new MotionRelayFixture();
        var source = fixture.CreateSession("region-spawn", "recipient-a");
        fixture.CreateSession("region-spawn", "recipient-b");
        var accepted = BuildMotionFrame(seq: 10, zdoUserId: 100, zdoId: 200);
        var differentZdo = BuildMotionFrame(seq: 11, zdoUserId: 101, zdoId: 200);

        await fixture.Transport.HandleValheimMotionFrameAsync(source, accepted.Header, accepted.Payload, accepted.Frame, "websocket");
        await fixture.Transport.HandleValheimMotionFrameAsync(source, differentZdo.Header, differentZdo.Payload, differentZdo.Frame, "websocket");

        var telemetry = fixture.MotionSnapshot();
        Assert.Equal(1, telemetry.GetProperty("received").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("dropped_stale").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("relayed_websocket").GetInt64());
    }

    [Fact]
    public async Task MalformedFrame_IsDroppedInvalid()
    {
        var fixture = new MotionRelayFixture();
        var source = fixture.CreateSession("region-spawn", "recipient-a");
        var (header, payload, frame) = BuildMotionFrame(seq: 10);
        var badFrame = frame[..^1];

        await fixture.Transport.HandleValheimMotionFrameAsync(source, header, payload, badFrame, "websocket");

        var telemetry = fixture.MotionSnapshot();
        Assert.Equal(0, telemetry.GetProperty("received").GetInt64());
        Assert.Equal(1, telemetry.GetProperty("dropped_invalid").GetInt64());
    }

    private static MotionFrame BuildMotionFrame(ushort seq, long zdoUserId = 100, uint zdoId = 200)
    {
        var payload = new byte[PayloadSerializers.ValheimPlayerMotionBytes];
        var payloadLength = PayloadSerializers.WriteValheimPlayerMotion(
            payload,
            zdoUserId,
            zdoId,
            new Vec3(10, 20, 30),
            new Vec3(1, 0, 0),
            yaw: 90,
            sentMilliseconds: 1234);

        var frame = new byte[BinaryEnvelope.HeaderBytes + payloadLength];
        var frameLength = BinaryEnvelope.Write(
            frame,
            version: 1,
            MessageTypeId.ValheimPlayerMotion,
            DeliveryLane.Datagram,
            seq,
            payload.AsSpan(0, payloadLength));
        Array.Resize(ref frame, frameLength);

        return new MotionFrame(
            BinaryEnvelope.ReadHeader(frame),
            payload[..payloadLength],
            frame);
    }

    private sealed record MotionFrame(BinaryEnvelopeHeader Header, byte[] Payload, byte[] Frame);

    private sealed class MotionRelayFixture
    {
        private readonly SessionManager _sessions = new();
        private readonly ValheimMotionTelemetry _motionTelemetry = new();

        public MotionRelayFixture()
        {
            Transport = new UdpTransport(
                _sessions,
                router: null!,
                _motionTelemetry,
                new ConfigurationBuilder().Build(),
                NullLogger<UdpTransport>.Instance);
        }

        public UdpTransport Transport { get; }

        public CapturingSession CreateSession(string region, string? recipient)
        {
            var socket = new CapturingWebSocket();
            var session = _sessions.Create(socket);
            session.RegionId = region;
            session.ValheimRecipientId = recipient;
            if (recipient != null)
                session.AuthorizeValheimCharacter(100, 200);
            return new CapturingSession(session, socket);
        }

        public JsonElement MotionSnapshot()
        {
            var json = JsonSerializer.Serialize(_motionTelemetry.Snapshot());
            return JsonDocument.Parse(json).RootElement.Clone();
        }
    }

    private sealed record CapturingSession(GameSession Session, CapturingWebSocket Socket)
    {
        public static implicit operator GameSession(CapturingSession capturing) => capturing.Session;
    }

    private sealed class CapturingWebSocket : System.Net.WebSockets.WebSocket
    {
        public List<byte[]> SentFrames { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentFrames.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
    }
}

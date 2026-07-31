using System.Net.WebSockets;
using System.Text.Json;
using Game.Contracts.Protocol;
using Game.Gateway.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimLogicalPeerRouterTests
{
    [Fact]
    public async Task OppositeRoleBindings_AnnounceAuthenticatedLogicalPeersBothWays()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "durracktu");
        var server = fixture.Create("server", "server-logical", "");

        await fixture.Bind(client.Session, 101);
        Assert.Empty(client.Socket.Envelopes);

        await fixture.Bind(server.Session, 202);

        var toClient = Assert.Single(client.Socket.Envelopes);
        Assert.Equal(MessageType.ValheimLogicalPeerAttached, toClient.Type);
        Assert.Equal("server", toClient.Payload.GetProperty("role").GetString());
        Assert.Equal(202, toClient.Payload.GetProperty("peer_uid").GetInt64());
        Assert.Equal("server-logical",
            toClient.Payload.GetProperty("logical_peer_id").GetString());

        var toServer = Assert.Single(server.Socket.Envelopes);
        Assert.Equal(MessageType.ValheimLogicalPeerAttached, toServer.Type);
        Assert.Equal("client", toServer.Payload.GetProperty("role").GetString());
        Assert.Equal(101, toServer.Payload.GetProperty("peer_uid").GetInt64());
        Assert.Equal("durracktu",
            toServer.Payload.GetProperty("character").GetString());
    }

    [Fact]
    public async Task DuplicateBinding_ReannouncesCounterpartsForSocketResume()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");
        var server = fixture.Create("server", "server-logical", "");
        var clientBind = EnvelopeFactory.Create(
            MessageType.ValheimPeerBind, new { role = "client", peer_uid = 101L });

        await fixture.Router.RouteAsync(client.Session, clientBind);
        await fixture.Bind(server.Session, 202);
        client.Socket.Envelopes.Clear();
        server.Socket.Envelopes.Clear();

        await fixture.Router.RouteAsync(client.Session, clientBind);

        Assert.Equal(MessageType.ValheimLogicalPeerAttached,
            Assert.Single(client.Socket.Envelopes).Type);
        Assert.Equal(MessageType.ValheimLogicalPeerAttached,
            Assert.Single(server.Socket.Envelopes).Type);
    }

    [Fact]
    public async Task CharacterIdControl_IsForwardedAsTypedLogicalPeerControl()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");
        var server = fixture.Create("server", "server-logical", "");
        await fixture.Bind(client.Session, 101);
        await fixture.Bind(server.Session, 202);
        client.Socket.Envelopes.Clear();
        server.Socket.Envelopes.Clear();

        await fixture.Router.RouteAsync(
            client.Session,
            EnvelopeFactory.Create(
                MessageType.ValheimLogicalPeerControl,
                new
                {
                    target_logical_peer_id = "server-logical",
                    control = "character_id",
                    zdo_user_id = 777L,
                    zdo_id = 42U,
                }));

        var forwarded = Assert.Single(server.Socket.Envelopes);
        Assert.Equal(MessageType.ValheimLogicalPeerControl, forwarded.Type);
        Assert.Equal("client-logical",
            forwarded.Payload.GetProperty("source_logical_peer_id").GetString());
        Assert.Equal(101,
            forwarded.Payload.GetProperty("source_peer_uid").GetInt64());
        Assert.Equal(777,
            forwarded.Payload.GetProperty("zdo_user_id").GetInt64());
        Assert.Equal(42U,
            forwarded.Payload.GetProperty("zdo_id").GetUInt32());
    }

    sealed class LogicalPeerFixture
    {
        readonly SessionManager _sessions = new();

        public LogicalPeerFixture()
        {
            Router = new MessageRouter(
                _sessions,
                inputQueue: null!,
                world: null!,
                playerHandler: null!,
                placeStructureHandler: null!,
                inventoryHandler: null!,
                zdoJournal: null!,
                ownershipLeases: null!,
                worldZones: null!,
                NullLogger<MessageRouter>.Instance);
        }

        public MessageRouter Router { get; }

        public CapturingSession Create(
            string role, string logicalPeerId, string character)
        {
            var socket = new CapturingWebSocket();
            var session = _sessions.Create(socket);
            session.ValheimRole = role;
            session.ValheimLogicalPeerId = logicalPeerId;
            session.ValheimCharacter = character;
            return new CapturingSession(session, socket);
        }

        public Task Bind(GameSession session, long peerUid) =>
            Router.RouteAsync(
                session,
                EnvelopeFactory.Create(
                    MessageType.ValheimPeerBind,
                    new { role = session.ValheimRole, peer_uid = peerUid }));
    }

    sealed record CapturingSession(
        GameSession Session, CapturingWebSocket Socket);

    sealed class CapturingWebSocket : System.Net.WebSockets.WebSocket
    {
        public List<Envelope> Envelopes { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Assert.Equal(WebSocketMessageType.Text, messageType);
            Envelopes.Add(EnvelopeFactory.Parse(
                System.Text.Encoding.UTF8.GetString(buffer)));
            return Task.CompletedTask;
        }
    }
}

using System.Net.WebSockets;
using System.Text.Json;
using Game.Contracts.Protocol;
using Game.Gateway.WebSocket;
using Lumberjacks.Contracts.Valheim;
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

    [Fact]
    public async Task ClientBinding_WithReliableCharacterIdentity_AuthorizesMotionGeneration()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");

        await fixture.Router.RouteAsync(
            client.Session,
            EnvelopeFactory.Create(
                MessageType.ValheimPeerBind,
                new
                {
                    role = "client",
                    peer_uid = 101L,
                    character_zdo_user_id = 101L,
                    character_zdo_id = 42U,
                }));

        var authority = Assert.IsType<ValheimCharacterAuthority>(
            client.Session.ValheimCharacterAuthority);
        Assert.Equal(101L, authority.ZdoUserId);
        Assert.Equal(42U, authority.ZdoId);
        Assert.Equal(1L, authority.Generation);
    }

    [Fact]
    public async Task ClientBinding_RejectsCharacterIdentityFromAnotherPeer()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Router.RouteAsync(
                client.Session,
                EnvelopeFactory.Create(
                    MessageType.ValheimPeerBind,
                    new
                    {
                        role = "client",
                        peer_uid = 101L,
                        character_zdo_user_id = 999L,
                        character_zdo_id = 42U,
                    })));
    }

    [Fact]
    public async Task P1InstanceRoutedRpc_UsesSharedAdmissionAndReachesTargetPeer()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");
        var server = fixture.Create("server", "server-logical", "");
        await fixture.Bind(client.Session, 101);
        await fixture.Bind(server.Session, 202);
        client.Socket.Envelopes.Clear();
        server.Socket.Envelopes.Clear();
        var admission = Assert.Single(
            ValheimRoutedRpcAdmissions.Entries,
            entry => entry.Name == "UseDoor");

        await fixture.Router.RouteAsync(
            client.Session,
            EnvelopeFactory.Create(
                MessageType.ValheimRoutedRpcSend,
                new
                {
                    run_id = "c10-contract-test",
                    action_id = "p1-instance",
                    route_id = "route-p1-instance",
                    message_id = 1001L,
                    sender_peer_id = 101L,
                    target_peer_id = 202L,
                    target_zdo_user_id = 202L,
                    target_zdo_id = 42U,
                    method_name = admission.Name,
                    method_hash = admission.MethodHash,
                    parameters_base64 = Convert.ToBase64String([1]),
                    delivery_mode = "deliver",
                }));

        var forwarded = Assert.Single(server.Socket.Envelopes);
        Assert.Equal(MessageType.ValheimRoutedRpc, forwarded.Type);
        Assert.Equal("UseDoor",
            forwarded.Payload.GetProperty("method_name").GetString());
        Assert.Equal(admission.MethodHash,
            forwarded.Payload.GetProperty("method_hash").GetInt32());
        Assert.Equal(202L,
            forwarded.Payload.GetProperty("target_zdo_user_id").GetInt64());
        Assert.Empty(client.Socket.Envelopes);
    }

    [Fact]
    public async Task ObservedP2InstanceRoutedRpc_UsesExactSharedPayloadContract()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");
        var server = fixture.Create("server", "server-logical", "");
        await fixture.Bind(client.Session, 101);
        await fixture.Bind(server.Session, 202);
        client.Socket.Envelopes.Clear();
        server.Socket.Envelopes.Clear();
        var admission = Assert.Single(
            ValheimRoutedRpcAdmissions.Entries,
            entry => entry.Name == "RPC_HealthChanged");

        await fixture.Router.RouteAsync(
            client.Session,
            EnvelopeFactory.Create(
                MessageType.ValheimRoutedRpcSend,
                new
                {
                    run_id = "c10-contract-test",
                    action_id = "p2-instance",
                    route_id = "route-p2-health",
                    message_id = 1002L,
                    sender_peer_id = 101L,
                    target_peer_id = 202L,
                    target_zdo_user_id = 202L,
                    target_zdo_id = 43U,
                    method_name = admission.Name,
                    method_hash = admission.MethodHash,
                    parameters_base64 = Convert.ToBase64String([0, 0, 128, 63]),
                    delivery_mode = "deliver",
                }));

        var forwarded = Assert.Single(server.Socket.Envelopes);
        Assert.Equal(MessageType.ValheimRoutedRpc, forwarded.Type);
        Assert.Equal("RPC_HealthChanged",
            forwarded.Payload.GetProperty("method_name").GetString());
        Assert.Equal(43U,
            forwarded.Payload.GetProperty("target_zdo_id").GetUInt32());
        Assert.Empty(client.Socket.Envelopes);
    }

    [Fact]
    public async Task RoutedRpc_RejectsUnadmittedSupersededAndMissingInstanceTarget()
    {
        var fixture = new LogicalPeerFixture();
        var client = fixture.Create("client", "client-logical", "Tugcorp");
        var server = fixture.Create("server", "server-logical", "");
        await fixture.Bind(client.Session, 101);
        await fixture.Bind(server.Session, 202);
        client.Socket.Envelopes.Clear();
        server.Socket.Envelopes.Clear();
        var admitted = Assert.Single(
            ValheimRoutedRpcAdmissions.Entries,
            entry => entry.Name == "UseDoor");
        var superseded = Assert.Single(
            ValheimRoutedRpcAdmissions.Entries,
            entry => entry.Name == "DestroyZDO");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Router.RouteAsync(
                client.Session,
                EnvelopeFactory.Create(
                    MessageType.ValheimRoutedRpcSend,
                    new
                    {
                        run_id = "c10-contract-test",
                        action_id = "unknown-method",
                        route_id = "route-unknown-method",
                        message_id = 1002L,
                        sender_peer_id = 101L,
                        target_peer_id = 202L,
                        target_zdo_user_id = 202L,
                        target_zdo_id = 42U,
                        method_name = "RPC_NotAdmitted",
                        method_hash = ValheimRoutedRpcAdmissions.StableHash(
                            "RPC_NotAdmitted"),
                        parameters_base64 = Convert.ToBase64String([1]),
                        delivery_mode = "deliver",
                    })));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Router.RouteAsync(
                client.Session,
                EnvelopeFactory.Create(
                    MessageType.ValheimRoutedRpcSend,
                    new
                    {
                        run_id = "c10-contract-test",
                        action_id = "missing-target",
                        route_id = "route-missing-target",
                        message_id = 1003L,
                        sender_peer_id = 101L,
                        target_peer_id = 202L,
                        target_zdo_user_id = 0L,
                        target_zdo_id = 0U,
                        method_name = admitted.Name,
                        method_hash = admitted.MethodHash,
                        parameters_base64 = Convert.ToBase64String([1]),
                        delivery_mode = "deliver",
                    })));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Router.RouteAsync(
                client.Session,
                EnvelopeFactory.Create(
                    MessageType.ValheimRoutedRpcSend,
                    new
                    {
                        run_id = "c10-contract-test",
                        action_id = "superseded-method",
                        route_id = "route-superseded-method",
                        message_id = 1004L,
                        sender_peer_id = 101L,
                        target_peer_id = 202L,
                        target_zdo_user_id = 0L,
                        target_zdo_id = 0U,
                        method_name = superseded.Name,
                        method_hash = superseded.MethodHash,
                        parameters_base64 = Convert.ToBase64String([0, 0, 0, 0]),
                        delivery_mode = "deliver",
                    })));

        Assert.Empty(client.Socket.Envelopes);
        Assert.Empty(server.Socket.Envelopes);
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

using System.Net.WebSockets;
using System.Text;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Game.Gateway.Valheim;

namespace Game.Gateway.WebSocket;

public class GameWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SessionManager _sessions;
    private readonly ILogger<GameWebSocketMiddleware> _logger;
    private readonly IServiceProvider _services;

    public GameWebSocketMiddleware(RequestDelegate next, SessionManager sessions, ILogger<GameWebSocketMiddleware> logger, IServiceProvider services)
    {
        _next = next;
        _sessions = sessions;
        _logger = logger;
        _services = services;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync();

        // Check for resume token in query string: ws://host:4000?resume=TOKEN
        var resumeToken = context.Request.Query["resume"].FirstOrDefault();
        // Check for binary protocol: ws://host:4000?protocol=binary
        var useBinary = string.Equals(
            context.Request.Query["protocol"].FirstOrDefault(), "binary",
            StringComparison.OrdinalIgnoreCase);

        GameSession session;
        bool resumed = false;

        if (!string.IsNullOrEmpty(resumeToken))
        {
            var existing = _sessions.TryResume(resumeToken, ws);
            if (existing != null)
            {
                session = existing;
                resumed = true;
                _logger.LogInformation(
                    "Session {SessionId} resumed (player {PlayerId}, region {RegionId})",
                    session.SessionId, session.PlayerId, session.RegionId);
            }
            else
            {
                // Token invalid or expired — create fresh session
                session = _sessions.Create(ws);
                _logger.LogInformation(
                    "Resume token invalid/expired, new session {SessionId} (player {PlayerId})",
                    session.SessionId, session.PlayerId);
            }
        }
        else
        {
            session = _sessions.Create(ws);
            _logger.LogInformation("Session {SessionId} connected (player {PlayerId})", session.SessionId, session.PlayerId);
        }

        var principal = ValheimPrincipal.From(context);
        var requestedRole = context.Request.Query["valheim_role"].FirstOrDefault();
        if (!resumed)
        {
            session.ValheimRole =
                string.Equals(requestedRole, "server", StringComparison.OrdinalIgnoreCase) &&
                principal?.Has(ValheimCapability.Producer) == true
                    ? "server"
                    : "client";
        }
        else if (!string.IsNullOrWhiteSpace(requestedRole) &&
                 !string.Equals(requestedRole, session.ValheimRole,
                     StringComparison.OrdinalIgnoreCase))
        {
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "resumed Valheim role changed",
                CancellationToken.None);
            _sessions.Remove(session.SessionId);
            return;
        }

        var logicalPeerId = ValheimLogicalPeerIdentity.Resolve(
            principal,
            session.ValheimRole,
            context.Request.Query["valheim_client"].FirstOrDefault(),
            context.Request.Query["valheim_character"].FirstOrDefault(),
            session.Reliable.ServerInstanceId,
            session.Reliable.WorldId,
            out var logicalPeerError);
        if (logicalPeerId is null ||
            (resumed && !string.Equals(
                session.ValheimLogicalPeerId, logicalPeerId, StringComparison.Ordinal)))
        {
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                logicalPeerError ?? "resumed logical peer changed",
                CancellationToken.None);
            _sessions.Remove(session.SessionId);
            return;
        }
        if (_sessions.GetAll().Any(candidate =>
                candidate.SessionId != session.SessionId &&
                string.Equals(candidate.ValheimLogicalPeerId, logicalPeerId,
                    StringComparison.Ordinal)))
        {
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "logical peer already connected",
                CancellationToken.None);
            _sessions.Remove(session.SessionId);
            return;
        }
        session.ValheimLogicalPeerId = logicalPeerId;

        // Set protocol mode based on handshake
        session.Protocol = useBinary ? ProtocolMode.Binary : ProtocolMode.Json;
        // The UDP token authenticates packets to this WebSocket session. Only enrollment-backed
        // sessions may publish Valheim motion, and the identifier retained here is opaque.
        session.ValheimRecipientId = principal?.Enrollment?.RecipientId;

        // Send session_started envelope (includes resume_token for future reconnects)
        // Include udp_token and udp_port so clients can bind a UDP channel
        var udpPort = _services.GetService<UdpTransport>()?.Port ?? 0;
        var startedPayload = new
        {
            session_id = session.SessionId,
            player_id = session.PlayerId,
            server_instance_id = session.Reliable.ServerInstanceId,
            world_id = session.Reliable.WorldId,
            protocol_version = session.Reliable.ProtocolVersion,
            client_connection_id = session.ConnectionId,
            logical_peer_id = session.ValheimLogicalPeerId,
            resume_epoch = session.ResumeEpoch,
            resume_token = session.ResumeToken,
            resumed,
            resume_rejected = !resumed && !string.IsNullOrEmpty(resumeToken),
            reliable_pending = session.Reliable.PendingCount,
            udp_token = session.UdpToken.ToString(),
            udp_port = udpPort,
            valheim_motion_available = session.ValheimRecipientId != null,
            valheim_role = session.ValheimRole,
        };
        var envelope = EnvelopeFactory.Create(MessageType.SessionStarted, startedPayload);
        var json = EnvelopeFactory.Serialize(envelope);
        await session.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            CancellationToken.None);

        // Reliable control is replayed before any fresh semantic snapshot. The client sees the
        // same sequence and idempotency key it saw before the socket detached.
        await session.ReplayReliableAsync(CancellationToken.None);

        // If resumed into a region, send a fresh world_snapshot
        if (resumed && session.RegionId != null)
        {
            var router = _services.GetRequiredService<MessageRouter>();
            await router.SendWorldSnapshotAsync(session);
        }

        // Get the message router for the receive loop
        var messageRouter = _services.GetRequiredService<MessageRouter>();

        // Receive loop
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ReceiveFrameAsync(ws, buffer, CancellationToken.None);
                if (result is null || result.MessageType == WebSocketMessageType.Close)
                    break;

                try
                {
                    Envelope incoming;

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Binary protocol: parse binary envelope header
                        var frame = new ReadOnlySpan<byte>(result.Payload);
                        var header = BinaryEnvelope.ReadHeader(frame);

                        // Upgrade session to binary mode if not already
                        session.Protocol = ProtocolMode.Binary;

                        // Fast path: deserialize binary payloads directly for hot-path messages
                        if (header.Type == MessageTypeId.PlayerInput)
                        {
                            var payload = BinaryEnvelope.GetPayload(frame, header);
                            var input = PayloadSerializers.ReadPlayerInput(payload);
                            messageRouter.HandlePlayerInputBinary(session, input);
                            continue; // skip JSON conversion entirely
                        }

                        if (header.Type == MessageTypeId.ValheimPlayerMotion)
                        {
                            if (BinaryEnvelope.FrameSize(header) != frame.Length)
                                throw new InvalidDataException("Valheim motion binary frame length mismatch");
                            var transport = _services.GetRequiredService<UdpTransport>();
                            await transport.HandleValheimMotionFrameAsync(
                                session,
                                header,
                                BinaryEnvelope.GetPayload(frame, header).ToArray(),
                                frame.ToArray(),
                                "websocket");
                            continue;
                        }

                        // Fallback: other message types still bridge through JSON payload
                        var typeName = MessageTypeMapping.ToName(header.Type);
                        var fallbackPayload = BinaryEnvelope.GetPayload(frame, header);

                        var payloadJson = fallbackPayload.Length > 0
                            ? System.Text.Json.JsonDocument.Parse(fallbackPayload.ToArray()).RootElement
                            : default;

                        incoming = new Envelope
                        {
                            Version = header.Version,
                            Type = typeName,
                            Seq = header.Seq,
                            Timestamp = DateTimeOffset.UtcNow,
                            Payload = payloadJson,
                        };
                    }
                    else
                    {
                        // JSON protocol: existing path
                        var raw = Encoding.UTF8.GetString(result.Payload);
                        incoming = EnvelopeFactory.Parse(raw);
                    }

                    _logger.LogDebug("Session {SessionId} received {Type}", session.SessionId, incoming.Type);

                    // Route to appropriate service
                    await messageRouter.RouteAsync(session, incoming);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process message from {SessionId}", session.SessionId);
                    var error = new ErrorMessage("INVALID_MESSAGE", "Failed to parse message");
                    var errEnvelope = EnvelopeFactory.Create(MessageType.Error, error);
                    var errJson = EnvelopeFactory.Serialize(errEnvelope);
                    await session.SendAsync(
                        Encoding.UTF8.GetBytes(errJson),
                        WebSocketMessageType.Text,
                        CancellationToken.None);
                }
            }
        }
        finally
        {
            // Save region before disconnect clears it (needed for resume)
            var regionBeforeDisconnect = session.RegionId;

            // Remove player from simulation (will be re-added on resume)
            try { await messageRouter.HandleDisconnectAsync(session); } catch { }

            // Restore region for detached session so resume knows where to rejoin
            session.RegionId = regionBeforeDisconnect;

            // Ownership leases are mutation authority, not transport replay state.
            // Any holder socket loss reclaims them; the same logical peer must obtain
            // a new epoch after resume.
            var reclaimed = _services
                .GetRequiredService<ValheimOwnershipLeaseService>()
                .ReclaimByLogicalPeer(
                    session.ValheimLogicalPeerId, DateTimeOffset.UtcNow);
            if (reclaimed > 0)
                _logger.LogInformation(
                    "Reclaimed {LeaseCount} Valheim ownership lease(s) for detached logical peer {LogicalPeer}",
                    reclaimed, session.ValheimLogicalPeerId);

            // Detach session (preserves identity for resume within 2min window)
            _sessions.Detach(session);
            _logger.LogInformation(
                "Session {SessionId} detached (player {PlayerId}, resume window 2min)",
                session.SessionId, session.PlayerId);

            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
        }
    }

    static async Task<ReceivedFrame?> ReceiveFrameAsync(
        System.Net.WebSockets.WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return new(result.MessageType, Array.Empty<byte>());
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("WebSocket frame exceeds the bounded semantic limit");
        } while (!result.EndOfMessage);
        return new(result.MessageType, stream.ToArray());
    }

    sealed record ReceivedFrame(WebSocketMessageType MessageType, byte[] Payload);
}

using System.Net.WebSockets;
using System.Text;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;

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

        // Set protocol mode based on handshake
        session.Protocol = useBinary ? ProtocolMode.Binary : ProtocolMode.Json;

        // Send session_started envelope (includes resume_token for future reconnects)
        // Include udp_token and udp_port so clients can bind a UDP channel
        var udpPort = _services.GetService<UdpTransport>()?.Port ?? 0;
        var startedPayload = new
        {
            session_id = session.SessionId,
            player_id = session.PlayerId,
            world_id = "world-default",
            resume_token = session.ResumeToken,
            resumed,
            udp_token = session.UdpToken.ToString(),
            udp_port = udpPort,
        };
        var envelope = EnvelopeFactory.Create(MessageType.SessionStarted, startedPayload);
        var json = EnvelopeFactory.Serialize(envelope);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

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
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                try
                {
                    Envelope incoming;

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Binary protocol: parse binary envelope header
                        var frame = new ReadOnlySpan<byte>(buffer, 0, result.Count);
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
                        var raw = Encoding.UTF8.GetString(buffer, 0, result.Count);
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
                    await ws.SendAsync(
                        Encoding.UTF8.GetBytes(errJson),
                        WebSocketMessageType.Text,
                        true,
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

            // Detach session (preserves identity for resume within 2min window)
            _sessions.Detach(session);
            _logger.LogInformation(
                "Session {SessionId} detached (player {PlayerId}, resume window 2min)",
                session.SessionId, session.PlayerId);

            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
        }
    }
}

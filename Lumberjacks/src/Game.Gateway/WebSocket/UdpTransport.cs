using System.Net;
using System.Net.Sockets;
using Game.Contracts.Protocol.Binary;
using Game.ServiceDefaults;
using Game.Simulation.Tick;
using Game.Simulation.World;

namespace Game.Gateway.WebSocket;

/// <summary>
/// UDP datagram channel — runs alongside the WebSocket server.
/// Handles the Datagram delivery lane (player_input inbound, entity_update outbound).
///
/// Packet format (both directions):
///   [udpToken: 8 bytes] [binaryEnvelope: header + payload]
///
/// Flow:
///   1. Client connects via WebSocket, receives session_started with udp_token + udp_port
///   2. Client sends first UDP packet to udp_port — server maps the source endpoint to the session
///   3. Server sends datagram-lane messages to the bound UDP endpoint
///   4. If no UDP endpoint is bound, TickBroadcaster falls back to WebSocket
///
/// Phase 3a — <c>Replication:UdpSockets</c> (default 1 = today's exact behavior, this bound
/// socket both receives AND sends): tests the hypothesis (Follow-up E) that parallel send
/// workers showed zero overlap because ~97% of update volume funneled through this one shared
/// UdpClient's synchronous, kernel-serialized <c>Send</c>. When resolved &gt; 1, this class
/// additionally opens N-1 send-ONLY UdpClients bound to ephemeral (OS-assigned) ports;
/// TickBroadcaster picks one deterministically per worker chunk (see
/// <see cref="SendFanOut.SocketForChunk"/>) so no socket is ever written by more than one
/// worker in the same tick. The original :Port socket (index 0) still handles ALL receiving
/// regardless of N — extra sockets are send-only.
///
/// NAT hazard: an ephemeral-source-port reply arrives at the client from a DIFFERENT source
/// port than the one it originally sent its bind packet to, which many real NATs/firewalls will
/// drop as an unsolicited inbound packet. The bench loader doesn't filter by source port, so it
/// won't see this, but real clients would. This is an experiment knob for isolating the
/// shared-socket bottleneck, not a production default — hence UdpSockets defaulting to 1.
/// </summary>
public class UdpTransport : BackgroundService
{
    public const int TokenBytes = 8;
    public const int MinPacketSize = TokenBytes + BinaryEnvelope.HeaderBytes;

    private readonly SessionManager _sessions;
    private readonly MessageRouter _router;
    private readonly ILogger<UdpTransport> _logger;
    private readonly int _port;
    private readonly int _socketCount;
    private readonly object[] _sendLocks;
    private UdpClient? _client;
    private UdpClient[]? _sendClients;

    public int Port => _port;

    /// <summary>
    /// The EFFECTIVE (post-auto-resolve) Replication:UdpSockets count — see
    /// <see cref="SendFanOut.ResolveUdpSocketCount"/>. Available immediately after construction
    /// (pure config + processor-count math), independent of whether the background service has
    /// started yet, so other components (TickBroadcaster's own startup log / TickMetrics) can
    /// read it without a startup-ordering dependency.
    /// </summary>
    public int SocketCount => _socketCount;

    public UdpTransport(
        SessionManager sessions,
        MessageRouter router,
        IConfiguration config,
        ILogger<UdpTransport> logger)
    {
        _sessions = sessions;
        _router = router;
        _logger = logger;
        _port = config.GetValue("Udp:Port", 4005);

        var replicationOptions = ReplicationOptions.FromConfiguration(config);
        var resolvedSendWorkers = SendFanOut.ResolveWorkerCount(replicationOptions.SendWorkers, Environment.ProcessorCount);
        _socketCount = SendFanOut.ResolveUdpSocketCount(replicationOptions.UdpSockets, resolvedSendWorkers);

        // Phase 3a′: one lock per resolved socket, built here (pure config math, same as
        // _socketCount) rather than in ExecuteAsync, so TrySend can rely on it even if called
        // before the BackgroundService has started (it already no-ops safely in that case via
        // the _client == null check below). See TrySend for why this lock exists.
        _sendLocks = new object[_socketCount];
        for (var i = 0; i < _sendLocks.Length; i++)
            _sendLocks[i] = new object();
    }

    // SIO_UDP_CONNRESET: suppress the Windows-only quirk where a prior send that
    // provoked an ICMP Port Unreachable poisons the *next* receive on this socket
    // with SocketException 10054, even though UDP has no real "connection" to reset.
    private const int SioUdpConnReset = -1744830452;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client = new UdpClient(_port);
        try
        {
            _client.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        catch (PlatformNotSupportedException)
        {
            // Non-Windows: this ioctl doesn't exist and isn't needed there.
        }

        // Phase 3a: socket 0 is always this bound receive/send socket — exactly today's
        // behavior when _socketCount == 1 (the default), no extra sockets touched at all.
        // Additional sockets (index 1..N-1), if any, are send-only and bind to an
        // OS-assigned ephemeral port (new UdpClient(0)) — see class doc for the NAT caveat.
        var sendClients = new UdpClient[_socketCount];
        sendClients[0] = _client;
        for (var i = 1; i < _socketCount; i++)
            sendClients[i] = new UdpClient(0);
        _sendClients = sendClients;

        _logger.LogInformation("UDP transport listening on port {Port} udpSockets={UdpSockets}", _port, _socketCount);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _client.ReceiveAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // Defense in depth alongside SIO_UDP_CONNRESET above — a bad
                    // peer should never be able to take down the whole transport.
                    _logger.LogDebug(ex, "UDP receive socket error — continuing");
                    continue;
                }

                if (result.Buffer.Length < MinPacketSize)
                {
                    LumberjacksTelemetry.RecordUdpPacket("invalid");
                    continue; // too small to be valid

                }

                try
                {
                    ProcessPacket(result.Buffer, result.RemoteEndPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to process UDP packet from {Endpoint}", result.RemoteEndPoint);
                }
            }
        }
        finally
        {
            // Extra send-only sockets (index 1..N-1) first, then the shared receive/send
            // socket (index 0 == _client) last — order doesn't matter functionally, but this
            // keeps the "extra sockets" concept visually separate from the original one.
            if (_sendClients != null)
            {
                for (var i = 1; i < _sendClients.Length; i++)
                    _sendClients[i].Dispose();
            }
            _client.Dispose();
            _logger.LogInformation("UDP transport stopped");
        }
    }

    private void ProcessPacket(byte[] data, IPEndPoint remoteEndpoint)
    {
        // Extract 8-byte UDP token
        var token = BitConverter.ToUInt64(data, 0);

        var session = _sessions.FindByUdpToken(token);
        LumberjacksTelemetry.RecordUdpPacket(session == null ? "unknown_session" : "received");
        if (session == null)
            return; // unknown token — drop silently

        // Bind/update the UDP endpoint for this session
        session.UdpEndpoint = remoteEndpoint;

        // Parse binary envelope from the rest of the packet
        var envelope = data.AsSpan(TokenBytes);
        if (envelope.Length < BinaryEnvelope.HeaderBytes)
            return;

        var header = BinaryEnvelope.ReadHeader(envelope);

        // Fast path: player_input
        if (header.Type == MessageTypeId.PlayerInput)
        {
            var payload = BinaryEnvelope.GetPayload(envelope, header);
            var input = PayloadSerializers.ReadPlayerInput(payload);
            _router.HandlePlayerInputBinary(session, input, "udp");
            return;
        }

        // Other datagram-lane messages could be handled here in the future
        _logger.LogDebug("Unhandled UDP message type {Type} from {PlayerId}", header.Type, session.PlayerId);
    }

    /// <summary>
    /// Send a raw datagram to a session's bound UDP endpoint.
    /// Returns false if the session has no UDP endpoint or send fails.
    /// </summary>
    /// <param name="session">Target session (must have a bound UDP endpoint).</param>
    /// <param name="binaryFrame">Wire payload, excluding the UDP token prefix.</param>
    /// <param name="chunkIndex">
    /// Phase 3a: the caller's (TickBroadcaster's) worker-chunk index for this tick. Maps
    /// deterministically to a send socket via <see cref="SendFanOut.SocketForChunk"/> — every
    /// call for the same chunk within a tick lands on the same socket, and by construction
    /// (UdpSockets auto-resolving to at least SendWorkers) different concurrently-running
    /// chunks land on different sockets. Phase 3a′ adds a per-socket lock in the body below as
    /// a defensive backstop for the case where UdpSockets is explicitly pinned below
    /// SendWorkers, so this guarantee no longer needs to hold for correctness — see the lock's
    /// comment. Ignored (always socket 0 — this bound socket) when UdpSockets resolves to 1,
    /// the default, so serial callers can omit it entirely.
    /// </param>
    public bool TrySend(GameSession session, ReadOnlySpan<byte> binaryFrame, int chunkIndex = 0)
    {
        if (_client == null || session.UdpEndpoint == null)
            return false;

        try
        {
            // Prepend UDP token to the binary frame
            var packet = new byte[TokenBytes + binaryFrame.Length];
            BitConverter.TryWriteBytes(packet.AsSpan(0, TokenBytes), session.UdpToken);
            binaryFrame.CopyTo(packet.AsSpan(TokenBytes));

            var socketIndex = _sendClients is { Length: > 0 } clients0
                ? SendFanOut.SocketForChunk(chunkIndex, clients0.Length)
                : 0;
            var client = _sendClients is { Length: > 0 } clients ? clients[socketIndex] : _client;

            // Phase 3a′ defensive lock: with TickBroadcaster's SendWorkers>1 path now
            // genuinely running chunks concurrently on distinct thread-pool threads (see
            // TickBroadcaster class doc), a per-socket lock is required here because
            // Socket.Send is NOT documented as safe for concurrent calls on the same handle.
            // In the safe/default configuration this lock is uncontended: UdpSockets
            // auto-resolves to (at least) the resolved SendWorkers count (see
            // SendFanOut.ResolveUdpSocketCount), and SendFanOut.SocketForChunk gives every
            // concurrently-running chunk index a DISTINCT socket when socketCount >=
            // workers — so two threads never contend for the same lock in practice. This
            // lock exists purely as a foot-gun guard for a misconfigured deployment that
            // pins Replication:UdpSockets below Replication:SendWorkers, where two worker
            // chunks legitimately map to the same socket index and would otherwise call
            // Send concurrently on it. Cost: one uncontended monitor enter/exit per send.
            lock (_sendLocks[socketIndex])
            {
                client!.Send(packet, packet.Length, session.UdpEndpoint);
            }
            LumberjacksTelemetry.RecordDelivery("udp");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UdpTransport.TrySend failed for session {SessionId}", session.SessionId);
            LumberjacksTelemetry.RecordUdpPacket("send_error");
            return false;
        }
    }
}

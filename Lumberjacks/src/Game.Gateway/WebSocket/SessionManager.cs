using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Game.Contracts.Protocol;
using Game.ServiceDefaults;

namespace Game.Gateway.WebSocket;

/// <summary>
/// Protocol mode for wire framing: JSON (text) or Binary (bit-packed).
/// </summary>
public enum ProtocolMode { Json, Binary }

public record GameSession(
    string SessionId,
    string PlayerId,
    System.Net.WebSockets.WebSocket Socket,
    ReliableGameSessionState Reliable)
{
    private readonly object _motionGate = new();
    private readonly SemaphoreSlim _socketSendGate = new(1, 1);
    private bool _motionSequenceSet;
    private ushort _lastMotionSequence;

    public string? GuildId { get; set; }
    public string? RegionId { get; set; }
    public string ResumeToken => Reliable.ResumeToken;
    public string ConnectionId => Reliable.ConnectionId;
    public long ResumeEpoch => Reliable.ResumeEpoch;

    /// <summary>Wire format for this session. Set during handshake based on query param.</summary>
    public ProtocolMode Protocol { get; set; } = ProtocolMode.Json;

    /// <summary>
    /// 8-byte token for UDP channel binding. Derived from SessionId.
    /// Client sends this in each UDP packet header so the server can map it to a session.
    /// </summary>
    public ulong UdpToken { get; init; } = GenerateUdpToken();

    /// <summary>
    /// Remote UDP endpoint, set when the client sends a UDP bind packet.
    /// Null means the client hasn't bound a UDP channel yet.
    /// </summary>
    public IPEndPoint? UdpEndpoint { get; set; }

    /// <summary>Opaque enrollment recipient; null sessions cannot publish Valheim motion.</summary>
    public string? ValheimRecipientId { get; set; }

    /// <summary>The player ZDO first claimed by this authenticated session.</summary>
    public long? ValheimMotionZdoUserId { get; private set; }
    public uint? ValheimMotionZdoId { get; private set; }

    /// <summary>
    /// WebSocket permits only one outstanding send. Tick broadcasts, control replies, and the
    /// Valheim motion fallback are independent producers, so serialize them at the session edge.
    /// </summary>
    public async Task SendAsync(byte[] payload, WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await _socketSendGate.WaitAsync(deadline.Token);
        try
        {
            await Socket.SendAsync(payload, messageType, true, deadline.Token);
        }
        finally
        {
            _socketSendGate.Release();
        }
    }

    public async Task<(bool Queued, long Sequence, string Reason)> SendReliableAsync(
        string type, object payload, CancellationToken cancellationToken)
    {
        if (!Reliable.TryEnqueue(type, payload, out var frame, out var reason))
            return (false, 0, reason);

        if (Socket.State == WebSocketState.Open)
            await SendAsync(frame.Bytes, WebSocketMessageType.Text, cancellationToken);
        return (true, frame.Sequence, "queued");
    }

    public async Task ReplayReliableAsync(CancellationToken cancellationToken)
    {
        foreach (var frame in Reliable.PendingFrames())
        {
            if (Socket.State != WebSocketState.Open) break;
            await SendAsync(frame.Bytes, WebSocketMessageType.Text, cancellationToken);
        }
    }

    /// <summary>
    /// Bind the first observed player ZDO to this session and reject duplicates/out-of-order
    /// datagrams thereafter. The half-range comparison handles ushort wrap without accepting an
    /// arbitrarily old packet after wrap.
    /// </summary>
    public bool TryAcceptValheimMotion(long zdoUserId, uint zdoId, ushort sequence)
    {
        lock (_motionGate)
        {
            if (string.IsNullOrWhiteSpace(ValheimRecipientId)) return false;
            if (ValheimMotionZdoUserId.HasValue &&
                (ValheimMotionZdoUserId.Value != zdoUserId || ValheimMotionZdoId != zdoId))
                return false;

            if (_motionSequenceSet)
            {
                var delta = unchecked((ushort)(sequence - _lastMotionSequence));
                if (delta == 0 || delta >= 0x8000) return false;
            }

            ValheimMotionZdoUserId ??= zdoUserId;
            ValheimMotionZdoId ??= zdoId;
            _lastMotionSequence = sequence;
            _motionSequenceSet = true;
            return true;
        }
    }

    private static ulong GenerateUdpToken()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

/// <summary>
/// Tracks detached sessions that can be resumed within a time window.
/// </summary>
public record DetachedSession(
    string PlayerId,
    string? GuildId,
    string? RegionId,
    ReliableGameSessionState Reliable,
    long DetachedEpoch,
    DateTimeOffset DetachedAt);

public sealed record ReliableFrame(long Sequence, byte[] Bytes);

public sealed class ReliableSessionProbe
{
    public ReliableSessionProbe(
        string runId,
        string probeId,
        string mode,
        long requestSequence,
        string connectionId)
    {
        RunId = runId;
        ProbeId = probeId;
        Mode = mode;
        RequestSequence = requestSequence;
        ConnectionId = connectionId;
    }

    public string RunId { get; }
    public string ProbeId { get; }
    public string Mode { get; }
    public long RequestSequence { get; }
    public string ConnectionId { get; }
    public int ResponseCount;
}

/// <summary>
/// Stable, bounded reliable state shared by every WebSocket incarnation of one Valheim
/// connection. It survives socket detach/resume but deliberately not a Gateway restart.
/// </summary>
public sealed class ReliableGameSessionState
{
    public const int MaxPendingFrames = 256;

    readonly object _gate = new();
    readonly SortedDictionary<long, ReliableFrame> _pending = new();
    readonly HashSet<string> _acceptedClientMessages = new(StringComparer.Ordinal);
    readonly Dictionary<string, ReliableSessionProbe> _probes = new(StringComparer.Ordinal);
    long _nextServerSequence = 1;
    long _lastClientSequence;
    string _resumeToken = Guid.NewGuid().ToString("N");
    long _resumeEpoch;

    public ReliableGameSessionState(string serverInstanceId, string worldId)
    {
        ServerInstanceId = serverInstanceId;
        WorldId = worldId;
    }

    public string ServerInstanceId { get; }
    public string WorldId { get; }
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public int ProtocolVersion => EnvelopeFactory.ProtocolVersion;
    public string ResumeToken { get { lock (_gate) return _resumeToken; } }
    public long ResumeEpoch { get { lock (_gate) return _resumeEpoch; } }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public bool CanAddProbe(string probeId)
    {
        lock (_gate) return _probes.Count < 32 && !_probes.ContainsKey(probeId);
    }

    public bool TryAddProbe(ReliableSessionProbe probe)
    {
        lock (_gate)
        {
            if (_probes.Count >= 32 || _probes.ContainsKey(probe.ProbeId)) return false;
            _probes[probe.ProbeId] = probe;
            return true;
        }
    }

    public bool TryGetProbe(string probeId, out ReliableSessionProbe probe)
    {
        lock (_gate) return _probes.TryGetValue(probeId, out probe!);
    }

    public bool TryEnqueue(string type, object payload, out ReliableFrame frame, out string reason)
    {
        lock (_gate)
        {
            if (_pending.Count >= MaxPendingFrames)
            {
                frame = new ReliableFrame(0, Array.Empty<byte>());
                reason = "reliable_send_queue_full";
                return false;
            }

            var sequence = _nextServerSequence++;
            var envelope = new Envelope
            {
                Type = type,
                Seq = sequence,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(payload, JsonOptions.Default),
            };
            frame = new ReliableFrame(
                sequence,
                Encoding.UTF8.GetBytes(EnvelopeFactory.Serialize(envelope)));
            _pending[sequence] = frame;
            reason = "queued";
            return true;
        }
    }

    public IReadOnlyList<ReliableFrame> PendingFrames()
    {
        lock (_gate) return _pending.Values.ToArray();
    }

    public int AcknowledgeThrough(long sequence)
    {
        lock (_gate)
        {
            var removed = 0;
            foreach (var key in _pending.Keys.Where(key => key <= sequence).ToArray())
                if (_pending.Remove(key)) removed++;
            return removed;
        }
    }

    public bool TryAcceptClientMessage(long sequence, string idempotencyKey)
    {
        lock (_gate)
        {
            if (sequence <= _lastClientSequence ||
                !_acceptedClientMessages.Add(idempotencyKey))
                return false;
            _lastClientSequence = sequence;
            return true;
        }
    }

    public void AdvanceResume()
    {
        lock (_gate)
        {
            _resumeEpoch++;
            _resumeToken = Guid.NewGuid().ToString("N");
        }
    }
}

public class SessionManager
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DetachedSession> _detached = new();

    private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(2);
    private readonly string _serverInstanceId =
        Environment.GetEnvironmentVariable("LUMBERJACKS_SERVER_INSTANCE_ID")
        ?? $"{Environment.MachineName}-{Environment.ProcessId}";
    private readonly string _worldId =
        Environment.GetEnvironmentVariable("LUMBERJACKS_WORLD_ID") ?? "world-default";

    public GameSession Create(System.Net.WebSockets.WebSocket socket)
    {
        var reliable = new ReliableGameSessionState(_serverInstanceId, _worldId);
        var session = new GameSession(
            SessionId: Guid.NewGuid().ToString(),
            PlayerId: Guid.NewGuid().ToString(),
            Socket: socket,
            Reliable: reliable);

        _sessions[session.SessionId] = session;
        LumberjacksTelemetry.SessionCreated(resumed: false);
        return session;
    }

    /// <summary>
    /// Try to resume a detached session with a new WebSocket.
    /// Returns a new GameSession with the original PlayerId, GuildId, and RegionId.
    /// </summary>
    public GameSession? TryResume(string resumeToken, System.Net.WebSockets.WebSocket socket)
    {
        if (!_detached.TryRemove(resumeToken, out var match)) return null;

        // Check if resume window has expired
        if (DateTimeOffset.UtcNow - match.DetachedAt > ResumeWindow)
        {
            return null;
        }

        match.Reliable.AdvanceResume();

        var session = new GameSession(
            SessionId: Guid.NewGuid().ToString(),
            PlayerId: match.PlayerId,
            Socket: socket,
            Reliable: match.Reliable)
        {
            GuildId = match.GuildId,
            RegionId = match.RegionId,
        };

        _sessions[session.SessionId] = session;
        LumberjacksTelemetry.SessionCreated(resumed: true);
        return session;
    }

    /// <summary>
    /// Detach a session (player disconnected but may reconnect).
    /// Preserves identity so the player can resume.
    /// </summary>
    public void Detach(GameSession session)
    {
        if (!_sessions.TryRemove(session.SessionId, out _)) return;
        LumberjacksTelemetry.SessionDetached();
        _detached[session.ResumeToken] = new DetachedSession(
            session.PlayerId,
            session.GuildId,
            session.RegionId,
            session.Reliable,
            session.ResumeEpoch,
            DateTimeOffset.UtcNow);
    }

    public void Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public GameSession? FindByPlayerId(string playerId)
    {
        return _sessions.Values.FirstOrDefault(s => s.PlayerId == playerId);
    }

    /// <summary>
    /// Find a session by its UDP token. Used by UdpTransport to map inbound UDP packets to sessions.
    /// </summary>
    public GameSession? FindByUdpToken(ulong udpToken)
    {
        return _sessions.Values.FirstOrDefault(s => s.UdpToken == udpToken);
    }

    public IReadOnlyCollection<GameSession> GetAll() => _sessions.Values.ToList();

    public IReadOnlyCollection<GameSession> GetByRegion(string regionId) =>
        _sessions.Values.Where(s => s.RegionId == regionId).ToList();

    /// <summary>
    /// Clean up expired detached sessions. Called periodically.
    /// </summary>
    public int PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - ResumeWindow;
        var expired = _detached.Where(kv => kv.Value.DetachedAt < cutoff).ToList();
        foreach (var kv in expired)
            _detached.TryRemove(kv.Key, out _);
        return expired.Count;
    }
}

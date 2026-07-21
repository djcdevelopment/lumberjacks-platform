using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Game.ServiceDefaults;

namespace Game.Gateway.WebSocket;

/// <summary>
/// Protocol mode for wire framing: JSON (text) or Binary (bit-packed).
/// </summary>
public enum ProtocolMode { Json, Binary }

public record GameSession(string SessionId, string PlayerId, System.Net.WebSockets.WebSocket Socket)
{
    public string? GuildId { get; set; }
    public string? RegionId { get; set; }
    public string ResumeToken { get; init; } = Guid.NewGuid().ToString("N");

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
public record DetachedSession(string PlayerId, string? GuildId, string? RegionId, string ResumeToken, DateTimeOffset DetachedAt);

public class SessionManager
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DetachedSession> _detached = new();

    private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(2);

    public GameSession Create(System.Net.WebSockets.WebSocket socket)
    {
        var session = new GameSession(
            SessionId: Guid.NewGuid().ToString(),
            PlayerId: Guid.NewGuid().ToString(),
            Socket: socket);

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
        // Find and remove the detached session
        var match = _detached.Values.FirstOrDefault(d => d.ResumeToken == resumeToken);
        if (match is null) return null;

        // Check if resume window has expired
        if (DateTimeOffset.UtcNow - match.DetachedAt > ResumeWindow)
        {
            _detached.TryRemove(match.ResumeToken, out _);
            return null;
        }

        _detached.TryRemove(match.ResumeToken, out _);

        var session = new GameSession(
            SessionId: Guid.NewGuid().ToString(),
            PlayerId: match.PlayerId,
            Socket: socket)
        {
            GuildId = match.GuildId,
            RegionId = match.RegionId,
            ResumeToken = Guid.NewGuid().ToString("N"), // new token for next resume
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
        _sessions.TryRemove(session.SessionId, out _);
        LumberjacksTelemetry.SessionDetached();
        _detached[session.ResumeToken] = new DetachedSession(
            session.PlayerId,
            session.GuildId,
            session.RegionId,
            session.ResumeToken,
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

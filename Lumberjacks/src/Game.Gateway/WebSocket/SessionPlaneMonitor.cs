using System.Net.WebSockets;
using Game.Gateway.Valheim;

namespace Game.Gateway.WebSocket;

/// <summary>
/// Periodic janitor for durable session-plane state. Its one alpha-critical duty is the
/// stalled-session abort: the server-side mirror of the mod's 5-second send guard. A WAN
/// consumer whose reliable queue shows no ACK progress is aborted so its logical peer is
/// held hostage for one reconnect+resume instead of indefinitely (candidates 8/11 wedged
/// here for 58s while the contention success frame sat undeliverable). The abort unwinds
/// the middleware receive loop, which detaches the session as resumable.
///
/// The same sweep retires the other "durable state outliving its session" hazards: expired
/// detached sessions, WAL-replayed recipient partitions whose consumer never re-attached,
/// and an oversized redirect WAL.
/// </summary>
public sealed class SessionPlaneMonitor : BackgroundService
{
    /// <summary>No ACK progress for this long with frames pending aborts the socket. The
    /// mod's send guard fires at 5s; the server allows double that so a client-side abort
    /// and resume always wins the race against a server-side eviction.</summary>
    public static readonly TimeSpan StallAbortAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly SessionManager _sessions;
    private readonly ValheimZdoRedirectService _redirects;
    private readonly ILogger<SessionPlaneMonitor> _logger;

    public SessionPlaneMonitor(
        SessionManager sessions,
        ValheimZdoRedirectService redirects,
        ILogger<SessionPlaneMonitor> logger)
    {
        _sessions = sessions;
        _redirects = redirects;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                SweepOnce(DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public int SweepOnce(DateTime utcNow)
    {
        var aborted = 0;
        foreach (var session in _sessions.GetAll())
        {
            if (!ShouldAbortStalledSession(session, utcNow)) continue;
            _logger.LogWarning(
                "Aborting stalled session {SessionId} (player {PlayerId}, peer {LogicalPeer}): " +
                "{PendingCount} pending frames, no ACK progress for {StalledSeconds:F1}s; session remains resumable",
                session.SessionId, session.PlayerId, session.ValheimLogicalPeerId,
                session.Reliable.PendingCount,
                session.Reliable.PendingStalledFor(utcNow).TotalSeconds);
            try { session.Socket.Abort(); } catch { }
            aborted++;
        }

        _sessions.PurgeExpired();

        try
        {
            var droppedPartitions = _redirects.PurgeStaleReplayedPartitions(utcNow);
            if (droppedPartitions > 0)
                _logger.LogWarning(
                    "Dropped {PartitionCount} replayed redirect partition(s) whose consumer never re-attached within {TtlSeconds}s",
                    droppedPartitions,
                    ValheimZdoRedirectService.ReplayedPartitionTtl.TotalSeconds);

            var reclaimed = _redirects.CompactIfOversized();
            if (reclaimed > 0)
                _logger.LogWarning(
                    "Compacted oversized redirect WAL, reclaimed {ReclaimedBytes} bytes (threshold {ThresholdBytes})",
                    reclaimed, ValheimZdoRedirectService.WalCompactThresholdBytes);
        }
        catch (Exception exception)
        {
            // WAL maintenance failure marks persistence unhealthy on the service itself;
            // it must not stop the stall sweep that keeps live sessions honest.
            _logger.LogError(exception, "Redirect WAL maintenance sweep failed");
        }

        return aborted;
    }

    public static bool ShouldAbortStalledSession(GameSession session, DateTime utcNow) =>
        session.Socket.State == WebSocketState.Open &&
        session.Reliable.PendingStalledFor(utcNow) >= StallAbortAfter;
}

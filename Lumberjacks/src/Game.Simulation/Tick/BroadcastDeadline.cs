namespace Game.Simulation.Tick;

/// <summary>
/// Pure decision logic for <c>Replication:BroadcastDeadlineMs</c> — the cliff killer. The
/// socket-facing mechanics (<see cref="System.Threading.CancellationTokenSource.CancelAfter(int)"/>,
/// calling <c>WebSocket.Abort()</c> on a canceled send) need a real socket and live in Gateway's
/// TickBroadcaster; this class holds the one bit of logic that doesn't.
/// </summary>
public static class BroadcastDeadline
{
    /// <summary>
    /// 0 (the default) or any non-positive value means "off": no CancellationTokenSource is
    /// created for the broadcast call and every send runs with CancellationToken.None, exactly
    /// as before this feature existed.
    /// </summary>
    public static bool IsEnabled(int deadlineMs) => deadlineMs > 0;
}

namespace Game.Simulation.Tick;

/// <summary>
/// Pure decision logic for <c>Replication:AdaptiveDegrade</c> — ADR-0011's "reduce frequency
/// before dropping".
///
/// v1 (<see cref="ShouldDegrade"/> + <see cref="ShouldSkipAlternating"/>) is next-tick-aligned:
/// TickBroadcaster tracks its own previous broadcast wall-clock time and asks whether THIS tick
/// should degrade. Still used for radius/full (<see cref="ShouldSkipAlternating"/>), which have
/// no mid band and so no burst schedule to align to.
///
/// v2 (<see cref="ShouldSuppressMidBand"/>) is burst-aligned and replaces v1 for the tiered
/// policy's mid-band suppression. Follow-up E found v1 mistimed: an overrun happens on a burst
/// tick (tiered's every-Nth-tick mid-band send is the expensive one), but v1 suppresses the
/// tick immediately AFTER it — which, for tiered, is almost always a cheap non-burst tick where
/// suppression is a no-op (InterestManager never schedules the mid band there anyway). By the
/// time the NEXT burst tick arrives, v1's tracked "previous broadcast" has long since been
/// overwritten by intervening cheap ticks, so the overrun's suppression opportunity is lost.
/// v2 fixes this by having the caller track only the LAST BURST TICK's wall time (see
/// <c>TickBroadcaster._lastBurstBroadcastWallMs</c>) and asking this class whether the tick
/// currently being decided — if and only if it is itself a burst tick — should suppress.
/// Both rules are stateless beyond the one number the caller feeds in, so degrade lifts the
/// instant the relevant broadcast fits inside budget again — no cooldown, no hysteresis.
/// </summary>
public static class AdaptiveDegrade
{
    /// <summary>
    /// v1: true when adaptive degrade is enabled AND the previous tick's broadcast wall time
    /// exceeded the tick budget. Exactly-at-budget is not an overrun. Still used to drive
    /// <see cref="ShouldSkipAlternating"/> for radius/full — see <see cref="ShouldSuppressMidBand"/>
    /// for the burst-aligned replacement used by the tiered policy's mid band.
    /// </summary>
    public static bool ShouldDegrade(bool enabled, double previousBroadcastWallMs, double budgetMs = TickMetrics.TickBudgetMs)
        => enabled && previousBroadcastWallMs > budgetMs;

    /// <summary>
    /// Alternating half-selection used for radius/full policies (which have no mid-band to
    /// suppress): true means "skip this session's update this tick". Operates on the session's
    /// position within the ROTATED order (see <see cref="SendFanOut.RotateOffset"/>), so which
    /// physical sessions get skipped shifts tick to tick along with the fairness rotation —
    /// no single session is skipped every degraded tick.
    /// </summary>
    public static bool ShouldSkipAlternating(int rotatedPosition) => (rotatedPosition & 1) == 1;

    /// <summary>
    /// v2, burst-aligned mid-band suppression for the tiered policy. True only when adaptive
    /// degrade is enabled, the tick being decided IS a burst tick (see
    /// <see cref="Game.Simulation.World.InterestManager.IsBurstTick"/> — the tick where the mid
    /// band is actually scheduled), AND the LAST burst tick's broadcast wall time exceeded
    /// budget. A non-burst tick always returns false regardless of
    /// <paramref name="lastBurstBroadcastWallMs"/> — suppressing it would be a no-op anyway
    /// (the mid band was never going out that tick), but callers get the right answer without
    /// needing to know that. Exactly-at-budget is not an overrun. Lifts the instant a burst
    /// tick's broadcast fits within budget again: the caller updates
    /// <paramref name="lastBurstBroadcastWallMs"/> ONLY on burst ticks, so an overrun on an
    /// intervening non-burst tick can never leak into this decision.
    /// </summary>
    public static bool ShouldSuppressMidBand(
        bool enabled, bool isBurstTick, double lastBurstBroadcastWallMs, double budgetMs = TickMetrics.TickBudgetMs)
        => enabled && isBurstTick && lastBurstBroadcastWallMs > budgetMs;
}

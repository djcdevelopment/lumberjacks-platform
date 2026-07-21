using Game.Simulation.Tick;
using Xunit;

namespace Game.Simulation.Tests;

public class AdaptiveDegradeTests
{
    // ── ShouldDegrade: triggers after overrun, lifts after fit ──

    [Theory]
    [InlineData(false, 200.0, false)]  // disabled — never degrades, even wildly over budget
    [InlineData(true, 0.0, false)]     // enabled, no prior broadcast recorded yet
    [InlineData(true, 49.9, false)]    // enabled, under budget
    [InlineData(true, 50.0, false)]    // enabled, exactly at budget — not an overrun
    [InlineData(true, 50.1, true)]     // enabled, over budget — triggers
    [InlineData(true, 200.0, true)]
    public void ShouldDegradeTriggersOnlyWhenEnabledAndOverBudget(bool enabled, double previousMs, bool expected)
        => Assert.Equal(expected, AdaptiveDegrade.ShouldDegrade(enabled, previousMs));

    [Fact]
    public void LiftsImmediatelyOnceThePreviousBroadcastFits()
    {
        // Stateless beyond "last broadcast ms": an overrun tick followed by a tick that fits
        // must lift degrade on the very next decision — no cooldown, no hysteresis.
        Assert.True(AdaptiveDegrade.ShouldDegrade(enabled: true, previousBroadcastWallMs: 80.0));
        Assert.False(AdaptiveDegrade.ShouldDegrade(enabled: true, previousBroadcastWallMs: 30.0));
    }

    [Fact]
    public void RespectsACustomBudget()
    {
        Assert.False(AdaptiveDegrade.ShouldDegrade(enabled: true, previousBroadcastWallMs: 15.0, budgetMs: 20.0));
        Assert.True(AdaptiveDegrade.ShouldDegrade(enabled: true, previousBroadcastWallMs: 25.0, budgetMs: 20.0));
    }

    // ── ShouldSkipAlternating: halving selection for radius/full ──

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(400, false)]
    [InlineData(401, true)]
    public void AlternatingSkipsOddRotatedPositions(int rotatedPosition, bool expectedSkip)
        => Assert.Equal(expectedSkip, AdaptiveDegrade.ShouldSkipAlternating(rotatedPosition));

    [Fact]
    public void AlternatingSelectsRoughlyHalfOfAnySequence()
    {
        var skipped = Enumerable.Range(0, 401).Count(AdaptiveDegrade.ShouldSkipAlternating);
        Assert.Equal(200, skipped); // positions 1,3,...,399 — half (rounded down for an odd count)
    }

    // ── ShouldSuppressMidBand (v2, burst-aligned — tiered policy only) ──

    [Theory]
    [InlineData(false, true, 200.0, false)]  // disabled — never suppresses, even wildly over budget
    [InlineData(true, false, 200.0, false)]  // non-burst tick — never suppressed, regardless of last-burst overrun
    [InlineData(true, true, 0.0, false)]     // burst tick, no prior burst recorded yet (cold start)
    [InlineData(true, true, 49.9, false)]    // burst tick, last burst tick was under budget
    [InlineData(true, true, 50.0, false)]    // exactly at budget — not an overrun
    [InlineData(true, true, 50.1, true)]     // burst tick, last burst tick was over budget — suppress
    [InlineData(true, true, 200.0, true)]
    public void ShouldSuppressMidBandTriggersOnlyOnABurstTickAfterABurstOverrun(
        bool enabled, bool isBurstTick, double lastBurstBroadcastWallMs, bool expected)
        => Assert.Equal(expected, AdaptiveDegrade.ShouldSuppressMidBand(enabled, isBurstTick, lastBurstBroadcastWallMs));

    [Fact]
    public void RespectsACustomBudgetForMidBandSuppression()
    {
        Assert.False(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: true, lastBurstBroadcastWallMs: 15.0, budgetMs: 20.0));
        Assert.True(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: true, lastBurstBroadcastWallMs: 25.0, budgetMs: 20.0));
    }

    [Fact]
    public void NonBurstTickNeverSuppressesRegardlessOfLastBurstOverrun()
    {
        // A non-burst tick is never suppressed — even with a huge tracked last-burst overrun,
        // isBurstTick=false always wins. InterestManager never schedules the mid band on a
        // non-burst tick anyway, so this would be a no-op even if it returned true — but the
        // function is safe on its own, without the caller needing to know that.
        Assert.False(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: false, lastBurstBroadcastWallMs: 500.0));
    }

    [Fact]
    public void BurstAlignedSequence_OverrunOnBurstTickSuppressesTheNextBurstTickOnly()
    {
        // Simulates TickBroadcaster's bookkeeping discipline for MidTickInterval = 4 (bursts
        // on ticks 4, 8, 12, ...): lastBurstBroadcastWallMs is updated ONLY when the tick that
        // just broadcast was itself a burst tick. This is the v1→v2 fix under test: v1 used
        // "the immediately preceding tick's wall time" and so lost the overrun signal to the
        // cheap non-burst ticks between bursts (Follow-up E's "wrong phase" bug).
        double lastBurstWallMs = 0;

        // Tick 4: first burst tick, overruns budget (60ms).
        Assert.False(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: true, lastBurstWallMs));
        lastBurstWallMs = 60.0; // tick 4 WAS a burst tick — tracker updates.

        // Ticks 5-7: non-burst. Even if one of them (say tick 6) also overran budget, that
        // must never feed into the burst tracker — only burst ticks may update it — so the
        // tracker must still read 60.0 (tick 4's value) going into tick 8.
        foreach (var _ in new[] { 5, 6, 7 })
            Assert.False(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: false, lastBurstWallMs));

        // Tick 8: the next burst tick — must be suppressed because tick 4 (the LAST burst
        // tick) overran, regardless of anything that happened on the intervening non-burst
        // ticks. This is exactly the case v1 got wrong.
        Assert.True(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: true, lastBurstWallMs));
    }

    [Fact]
    public void LiftsAfterABurstTickFitsWithinBudget()
    {
        // Tick 8 overran (suppression fires for tick 12). Tick 12 itself fits comfortably
        // within budget under the lighter (suppressed) load, so tick 16 must NOT be
        // suppressed — no cooldown, no hysteresis, exactly like v1's ShouldDegrade.
        double lastBurstWallMs = 80.0; // tick 8 overran
        Assert.True(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: true, lastBurstWallMs));

        lastBurstWallMs = 30.0; // tick 12 (suppressed) fit within budget
        Assert.False(AdaptiveDegrade.ShouldSuppressMidBand(enabled: true, isBurstTick: true, lastBurstWallMs));
    }
}

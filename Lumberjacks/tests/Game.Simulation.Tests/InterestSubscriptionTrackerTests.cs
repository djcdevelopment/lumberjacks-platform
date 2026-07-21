using Game.Simulation.World;
using Xunit;
using Snap = Game.Simulation.World.InterestSubscription.PlayerSnapshot;

namespace Game.Simulation.Tests;

public class InterestSubscriptionTrackerTests
{
    // ── InterestSubscription.ComputeSubscriptions (pure snapshot → per-observer sets) ──

    [Fact]
    public void ComputeExcludesSelf()
    {
        var subs = InterestSubscription.ComputeSubscriptions(
            new[] { new Snap("a", "r", 0, 0), new Snap("b", "r", 10, 0) }, radius: 100);

        Assert.DoesNotContain("a", subs["a"].Set);
        Assert.Contains("b", subs["a"].Set);
    }

    [Fact]
    public void ComputeIncludesWithinRadiusExcludesBeyond()
    {
        var subs = InterestSubscription.ComputeSubscriptions(
            new[]
            {
                new Snap("observer", "r", 0, 0),
                new Snap("inside", "r", 60, 0),   // 60 < 100
                new Snap("outside", "r", 140, 0),  // 140 > 100
            },
            radius: 100);

        Assert.Contains("inside", subs["observer"].Set);
        Assert.DoesNotContain("outside", subs["observer"].Set);
    }

    [Fact]
    public void ComputeUsesXzDistanceAndIsBoundaryInclusive()
    {
        // Exactly at the radius (XZ plane) must be included; Y is ignored (only X/Z carried).
        var subs = InterestSubscription.ComputeSubscriptions(
            new[] { new Snap("observer", "r", 0, 0), new Snap("edge", "r", 0, 100) }, radius: 100);

        Assert.Contains("edge", subs["observer"].Set);
    }

    [Fact]
    public void ComputeNeverPairsAcrossRegions()
    {
        var subs = InterestSubscription.ComputeSubscriptions(
            new[]
            {
                new Snap("a", "region-1", 0, 0),
                new Snap("b", "region-2", 1, 0), // physically adjacent but different region
            },
            radius: 1000);

        Assert.Empty(subs["a"].Set);
        Assert.Empty(subs["b"].Set);
        Assert.Equal("region-1", subs["a"].RegionId);
        Assert.Equal("region-2", subs["b"].RegionId);
    }

    [Fact]
    public void ComputeInfiniteRadiusSubscribesWholeRegionExceptSelf()
    {
        var subs = InterestSubscription.ComputeSubscriptions(
            new[]
            {
                new Snap("a", "r", 0, 0),
                new Snap("b", "r", 100_000, 0),
                new Snap("c", "r", -50_000, 9_999),
            },
            radius: double.PositiveInfinity);

        Assert.Equal(new HashSet<string> { "b", "c" }, subs["a"].Set);
        Assert.Equal(2, subs["a"].Set.Count);
    }

    [Fact]
    public void ComputeEmptyInputYieldsEmpty()
    {
        var subs = InterestSubscription.ComputeSubscriptions(Array.Empty<Snap>(), radius: 100);
        Assert.Empty(subs);
    }

    // ── InterestSubscriptionTracker.DiffAll (stateful diff across samples) ──

    private static Dictionary<string, (string RegionId, HashSet<string> Set)> Sample(
        params (string observer, string region, string[] subscribed)[] rows)
    {
        var d = new Dictionary<string, (string RegionId, HashSet<string> Set)>();
        foreach (var (observer, region, subscribed) in rows)
            d[observer] = (region, new HashSet<string>(subscribed));
        return d;
    }

    [Fact]
    public void FirstObservationReportsWholeSetAsAdded()
    {
        var tracker = new InterestSubscriptionTracker();

        var changes = tracker.DiffAll(Sample(("obs", "r", new[] { "x", "y" })));

        var change = Assert.Single(changes);
        Assert.Equal("obs", change.ObserverId);
        Assert.Equal("r", change.RegionId);
        Assert.Equal(new HashSet<string> { "x", "y" }, change.Added.ToHashSet());
        Assert.Empty(change.Removed);
        Assert.Equal(2, change.SubscribedCount);
    }

    [Fact]
    public void IdenticalSampleProducesNoChange()
    {
        var tracker = new InterestSubscriptionTracker();
        tracker.DiffAll(Sample(("obs", "r", new[] { "x", "y" })));

        var changes = tracker.DiffAll(Sample(("obs", "r", new[] { "x", "y" })));

        Assert.Empty(changes);
    }

    [Fact]
    public void EntryAndExitAreReportedAsAddedAndRemoved()
    {
        var tracker = new InterestSubscriptionTracker();
        tracker.DiffAll(Sample(("obs", "r", new[] { "x", "y" })));

        // y leaves, z enters
        var changes = tracker.DiffAll(Sample(("obs", "r", new[] { "x", "z" })));

        var change = Assert.Single(changes);
        Assert.Equal(new[] { "z" }, change.Added);
        Assert.Equal(new[] { "y" }, change.Removed);
        Assert.Equal(2, change.SubscribedCount);
    }

    [Fact]
    public void SubscriptionDroppingToEmptyReportsRemoval()
    {
        var tracker = new InterestSubscriptionTracker();
        tracker.DiffAll(Sample(("obs", "r", new[] { "x" })));

        var changes = tracker.DiffAll(Sample(("obs", "r", Array.Empty<string>())));

        var change = Assert.Single(changes);
        Assert.Empty(change.Added);
        Assert.Equal(new[] { "x" }, change.Removed);
        Assert.Equal(0, change.SubscribedCount);
    }

    [Fact]
    public void DepartedObserverAgesOutAndReappearanceReemitsFullSnapshot()
    {
        var tracker = new InterestSubscriptionTracker();
        tracker.DiffAll(Sample(("obs", "r", new[] { "x" })));

        // obs absent entirely (disconnected) — no event for it, and its baseline is pruned.
        var goneChanges = tracker.DiffAll(Sample(("other", "r", new[] { "q" })));
        Assert.DoesNotContain(goneChanges, c => c.ObserverId == "obs");

        // obs reappears with the same set — because it aged out, this is a fresh "added" snapshot,
        // not silence.
        var backChanges = tracker.DiffAll(Sample(("obs", "r", new[] { "x" }), ("other", "r", new[] { "q" })));
        var obs = Assert.Single(backChanges, c => c.ObserverId == "obs");
        Assert.Equal(new[] { "x" }, obs.Added);
        Assert.Empty(obs.Removed);
    }

    [Fact]
    public void OnlyChangedObserversAppearInResult()
    {
        var tracker = new InterestSubscriptionTracker();
        tracker.DiffAll(Sample(
            ("stable", "r", new[] { "x" }),
            ("mover", "r", new[] { "x" })));

        var changes = tracker.DiffAll(Sample(
            ("stable", "r", new[] { "x" }),      // unchanged
            ("mover", "r", new[] { "x", "y" }))); // gained y

        var change = Assert.Single(changes);
        Assert.Equal("mover", change.ObserverId);
        Assert.Equal(new[] { "y" }, change.Added);
    }

    [Fact]
    public void RegionIdIsCarriedThroughToTheChange()
    {
        var tracker = new InterestSubscriptionTracker();
        var changes = tracker.DiffAll(Sample(("obs", "region-north", new[] { "x" })));
        Assert.Equal("region-north", Assert.Single(changes).RegionId);
    }
}

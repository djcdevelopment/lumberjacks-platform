using Game.Contracts.Entities;
using Game.Simulation.World;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Game.Simulation.Tests;

public class InterestManagerTests
{
    private static Player MakePlayer(string id, Vec3 position) => new()
    {
        Id = id,
        Name = "Test",
        Position = position,
        RegionId = "region-spawn",
        Connected = true,
    };

    private static (InterestManager manager, SpatialGrid grid) CreateManager(ReplicationOptions? options = null)
    {
        var grid = new SpatialGrid(cellSize: 50);
        var manager = new InterestManager(grid, options);
        return (manager, grid);
    }

    // ── Default (no options passed — must equal ReplicationOptions defaults: tiered/100/300/4) ──

    [Fact]
    public void DefaultConstructorUsesTieredPolicyWithStandardValues()
    {
        var (manager, _) = CreateManager(); // no options — must default to tiered/100/300/4
        Assert.Equal(ReplicationPolicy.Tiered, manager.Policy);
    }

    [Fact]
    public void NearEntityIncludedEveryTick()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("near", new Vec3(50, 0, 0)); // 50 units away — within near band (100)

        var players = new Dictionary<string, Player>
        {
            ["observer"] = MakePlayer("observer", new Vec3(0, 0, 0)),
            ["near"] = MakePlayer("near", new Vec3(50, 0, 0)),
        };

        var changed = new HashSet<string> { "near" };

        // Should be visible on any tick (not just mid-band ticks)
        var visible1 = manager.FilterForObserver("observer", changed, players, tick: 1);
        var visible2 = manager.FilterForObserver("observer", changed, players, tick: 2);
        var visible3 = manager.FilterForObserver("observer", changed, players, tick: 3);

        Assert.Contains("near", visible1);
        Assert.Contains("near", visible2);
        Assert.Contains("near", visible3);
    }

    [Fact]
    public void MidEntityIncludedOnlyEveryNthTick()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("mid", new Vec3(200, 0, 0)); // 200 units — in mid band (100-300)

        var players = new Dictionary<string, Player>
        {
            ["observer"] = MakePlayer("observer", new Vec3(0, 0, 0)),
            ["mid"] = MakePlayer("mid", new Vec3(200, 0, 0)),
        };

        var changed = new HashSet<string> { "mid" };

        // Mid band only included on ticks divisible by MidBandTickInterval (4)
        var visibleTick1 = manager.FilterForObserver("observer", changed, players, tick: 1);
        var visibleTick4 = manager.FilterForObserver("observer", changed, players, tick: 4);

        Assert.DoesNotContain("mid", visibleTick1);
        Assert.Contains("mid", visibleTick4);
    }

    [Fact]
    public void FarEntityExcluded()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("far", new Vec3(400, 0, 0)); // 400 units — beyond mid band (300)

        var players = new Dictionary<string, Player>
        {
            ["observer"] = MakePlayer("observer", new Vec3(0, 0, 0)),
            ["far"] = MakePlayer("far", new Vec3(400, 0, 0)),
        };

        var changed = new HashSet<string> { "far" };

        // Should never be included (even on mid-band ticks)
        var visibleTick4 = manager.FilterForObserver("observer", changed, players, tick: 4);
        Assert.DoesNotContain("far", visibleTick4);
    }

    [Fact]
    public void SelfAlwaysIncluded()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));

        var players = new Dictionary<string, Player>
        {
            ["observer"] = MakePlayer("observer", new Vec3(0, 0, 0)),
        };

        var changed = new HashSet<string> { "observer" };

        var visible = manager.FilterForObserver("observer", changed, players, tick: 1);
        Assert.Contains("observer", visible);
    }

    [Fact]
    public void ObserverNotInGridFallsBackToAll()
    {
        var (manager, grid) = CreateManager();
        // Don't add "ghost" to grid
        grid.Update("entity", new Vec3(0, 0, 0));

        var players = new Dictionary<string, Player>
        {
            ["entity"] = MakePlayer("entity", new Vec3(0, 0, 0)),
        };

        var changed = new HashSet<string> { "entity" };

        var visible = manager.FilterForObserver("ghost", changed, players, tick: 1);
        Assert.Contains("entity", visible); // fallback — send everything
    }

    [Fact]
    public void MixedDistancesFilterCorrectly()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("near", new Vec3(50, 0, 0));     // near band
        grid.Update("mid", new Vec3(200, 0, 0));      // mid band
        grid.Update("far", new Vec3(400, 0, 0));      // far band

        var players = new Dictionary<string, Player>
        {
            ["observer"] = MakePlayer("observer", new Vec3(0, 0, 0)),
            ["near"] = MakePlayer("near", new Vec3(50, 0, 0)),
            ["mid"] = MakePlayer("mid", new Vec3(200, 0, 0)),
            ["far"] = MakePlayer("far", new Vec3(400, 0, 0)),
        };

        var changed = new HashSet<string> { "near", "mid", "far" };

        // Non-mid tick: only near
        var tick1 = manager.FilterForObserver("observer", changed, players, tick: 1);
        Assert.Contains("near", tick1);
        Assert.DoesNotContain("mid", tick1);
        Assert.DoesNotContain("far", tick1);

        // Mid tick: near + mid
        var tick4 = manager.FilterForObserver("observer", changed, players, tick: 4);
        Assert.Contains("near", tick4);
        Assert.Contains("mid", tick4);
        Assert.DoesNotContain("far", tick4);
    }

    [Fact]
    public void ExactlyAtNearBoundaryIncluded()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("edge", new Vec3(ReplicationOptions.DefaultNearRadius, 0, 0)); // exactly at boundary

        var players = new Dictionary<string, Player>
        {
            ["observer"] = MakePlayer("observer", new Vec3(0, 0, 0)),
            ["edge"] = MakePlayer("edge", new Vec3(ReplicationOptions.DefaultNearRadius, 0, 0)),
        };

        var changed = new HashSet<string> { "edge" };
        var visible = manager.FilterForObserver("observer", changed, players, tick: 1);
        Assert.Contains("edge", visible);
    }

    // ── Policy: Full ──

    [Fact]
    public void FullPolicyReturnsAllChangedIdsForAnyObserver()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Full };
        var (manager, _) = CreateManager(options);
        // Deliberately do NOT register "observer" or any entity in the grid —
        // full policy must short-circuit before any grid/distance lookup.
        var changed = new HashSet<string> { "near", "mid", "far", "unregistered" };

        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 1);

        Assert.Equal(changed, visible);
    }

    [Fact]
    public void FullPolicyIgnoresDistance()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Full };
        var (manager, grid) = CreateManager(options);
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("far", new Vec3(10_000, 0, 0)); // way beyond any tiered/radius band

        var changed = new HashSet<string> { "far" };
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 1);

        Assert.Contains("far", visible);
    }

    // ── Policy: Radius ──

    [Fact]
    public void RadiusPolicyIncludesInsideNearRadius()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Radius, NearRadius = 100.0 };
        var (manager, grid) = CreateManager(options);
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("inside", new Vec3(99, 0, 0));

        var changed = new HashSet<string> { "inside" };
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 1);

        Assert.Contains("inside", visible);
    }

    [Fact]
    public void RadiusPolicyExcludesOutsideNearRadius()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Radius, NearRadius = 100.0 };
        var (manager, grid) = CreateManager(options);
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("outside", new Vec3(101, 0, 0));
        // Even a distance that would be in-band for the tiered policy's mid band must be dropped.
        grid.Update("wouldBeMid", new Vec3(200, 0, 0));

        var changed = new HashSet<string> { "outside", "wouldBeMid" };
        // Use a tick divisible by the (unused, since radius has no mid band) tiered interval —
        // radius policy must never emit these regardless of tick.
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 4);

        Assert.DoesNotContain("outside", visible);
        Assert.DoesNotContain("wouldBeMid", visible);
    }

    [Fact]
    public void RadiusPolicyBoundaryIsInclusive()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Radius, NearRadius = 100.0 };
        var (manager, grid) = CreateManager(options);
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("edge", new Vec3(100, 0, 0)); // exactly at NearRadius

        var changed = new HashSet<string> { "edge" };
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 1);

        Assert.Contains("edge", visible);
    }

    // ── Policy: Tiered with custom values ──

    [Fact]
    public void TieredPolicyReproducesCurrentBehaviorWithCustomValues()
    {
        var options = new ReplicationOptions
        {
            Policy = ReplicationPolicy.Tiered,
            NearRadius = 10.0,
            MidRadius = 20.0,
            MidTickInterval = 2,
        };
        var (manager, grid) = CreateManager(options);
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("near", new Vec3(5, 0, 0));   // within custom near (10)
        grid.Update("mid", new Vec3(15, 0, 0));   // within custom mid (10-20)
        grid.Update("far", new Vec3(25, 0, 0));   // beyond custom mid (20)

        var changed = new HashSet<string> { "near", "mid", "far" };

        var oddTick = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 1);
        Assert.Contains("near", oddTick);
        Assert.DoesNotContain("mid", oddTick);
        Assert.DoesNotContain("far", oddTick);

        var evenTick = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 2);
        Assert.Contains("near", evenTick);
        Assert.Contains("mid", evenTick);
        Assert.DoesNotContain("far", evenTick);
    }

    // ── Adaptive degrade: suppressMidBand (see AdaptiveDegradeTests for the alternating half
    //    used by radius/full — the mid-band half lives here since it's InterestManager's rule) ──

    [Fact]
    public void SuppressMidBandForcesNoMidEvenOnAMidTick()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("mid", new Vec3(200, 0, 0)); // mid band (100-300)

        var changed = new HashSet<string> { "mid" };

        // tick 4 is normally a mid-band tick (MidTickInterval defaults to 4)
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 4, suppressMidBand: true);

        Assert.DoesNotContain("mid", visible);
    }

    [Fact]
    public void SuppressMidBandDoesNotAffectNearBand()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("near", new Vec3(50, 0, 0));

        var changed = new HashSet<string> { "near" };
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 4, suppressMidBand: true);

        Assert.Contains("near", visible);
    }

    [Fact]
    public void SuppressMidBandDefaultsToFalse()
    {
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("mid", new Vec3(200, 0, 0));

        var changed = new HashSet<string> { "mid" };
        // No suppressMidBand argument — must behave exactly like before this parameter existed.
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 4);

        Assert.Contains("mid", visible);
    }

    [Fact]
    public void SuppressMidBandIsNoOpForPoliciesWithoutAMidBand()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Full };
        var (manager, grid) = CreateManager(options);
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("far", new Vec3(10_000, 0, 0));

        var changed = new HashSet<string> { "far" };
        var visible = manager.FilterForObserver("observer", changed, new Dictionary<string, Player>(), tick: 4, suppressMidBand: true);

        Assert.Contains("far", visible); // Full ignores suppressMidBand entirely — no mid band to suppress
    }

    // ── IsBurstTick (phase 3a: adaptive-degrade v2's burst-alignment convention) ──

    [Theory]
    [InlineData(0, true)]     // 0 % 4 == 0
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(5, false)]
    public void IsBurstTickMatchesDefaultMidTickInterval(long tick, bool expected)
    {
        var (manager, _) = CreateManager(); // default MidTickInterval = 4
        Assert.Equal(expected, manager.IsBurstTick(tick));
    }

    [Fact]
    public void IsBurstTickUsesCustomMidTickInterval()
    {
        var options = new ReplicationOptions { MidTickInterval = 3 };
        var (manager, _) = CreateManager(options);

        Assert.True(manager.IsBurstTick(0));
        Assert.True(manager.IsBurstTick(3));
        Assert.True(manager.IsBurstTick(6));
        Assert.False(manager.IsBurstTick(1));
        Assert.False(manager.IsBurstTick(2));
        Assert.False(manager.IsBurstTick(4));
    }

    [Fact]
    public void IsBurstTickAgreesWithFilterForObserversIsMidTickDecision()
    {
        // IsBurstTick must reflect the SAME convention FilterForObserver uses internally to
        // gate the mid band (tick % MidTickInterval == 0) — this test pins that agreement so
        // the two can never silently drift apart.
        var (manager, grid) = CreateManager();
        grid.Update("observer", new Vec3(0, 0, 0));
        grid.Update("mid", new Vec3(200, 0, 0)); // mid band (100-300)
        var changed = new HashSet<string> { "mid" };
        var players = new Dictionary<string, Player>();

        for (long tick = 0; tick < 12; tick++)
        {
            var visible = manager.FilterForObserver("observer", changed, players, tick);
            Assert.Equal(manager.IsBurstTick(tick), visible.Contains("mid"));
        }
    }

    [Fact]
    public void IsBurstTickIsFalseWhenMidTickIntervalIsZero()
    {
        // Guards the same MidTickInterval > 0 check FilterForObserver uses — a misconfigured
        // 0 interval must never crash with a divide-by-zero, and must never claim every tick
        // is a burst tick.
        var options = new ReplicationOptions { MidTickInterval = 0 };
        var (manager, _) = CreateManager(options);

        Assert.False(manager.IsBurstTick(0));
        Assert.False(manager.IsBurstTick(4));
    }

    // ── ReplicationOptions.FromConfiguration ──

    [Fact]
    public void ConfigurationDefaultIsTieredWithStandardValues()
    {
        var config = new ConfigurationBuilder().Build(); // nothing set — must fall back to defaults
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(ReplicationPolicy.Tiered, options.Policy);
        Assert.Equal(100.0, options.NearRadius);
        Assert.Equal(300.0, options.MidRadius);
        Assert.Equal(4, options.MidTickInterval);
    }

    [Fact]
    public void ConfigurationDefaultsSendWorkersToTodaysSerialBehavior()
    {
        // workers=1 — the A/B isolates each send-loop mechanism (see also BroadcastDeadlineMs,
        // AdaptiveDegrade, added alongside deadline shedding / adaptive degrade respectively).
        var config = new ConfigurationBuilder().Build();
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(1, options.SendWorkers);
    }

    [Fact]
    public void ConfigurationReadsSendWorkers()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:SendWorkers"] = "4" })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(4, options.SendWorkers);
    }

    [Fact]
    public void ConfigurationReadsSendWorkersZeroAsAuto()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:SendWorkers"] = "0" })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(0, options.SendWorkers); // resolved to an effective count by SendFanOut.ResolveWorkerCount
    }

    [Theory]
    [InlineData("full", ReplicationPolicy.Full)]
    [InlineData("radius", ReplicationPolicy.Radius)]
    [InlineData("tiered", ReplicationPolicy.Tiered)]
    [InlineData("FULL", ReplicationPolicy.Full)] // case-insensitive
    [InlineData("bogus", ReplicationPolicy.Tiered)] // unknown value falls back to the safe default
    public void ConfigurationParsesPolicyFromReplicationPolicyKey(string raw, ReplicationPolicy expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:Policy"] = raw })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(expected, options.Policy);
    }

    [Fact]
    public void ConfigurationReadsCustomRadiusAndIntervalValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:Policy"] = "tiered",
                ["Replication:NearRadius"] = "50",
                ["Replication:MidRadius"] = "150",
                ["Replication:MidTickInterval"] = "8",
            })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(50.0, options.NearRadius);
        Assert.Equal(150.0, options.MidRadius);
        Assert.Equal(8, options.MidTickInterval);
    }

    [Fact]
    public void ConfigurationDefaultsBroadcastDeadlineToOff()
    {
        var config = new ConfigurationBuilder().Build();
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(0, options.BroadcastDeadlineMs);
    }

    [Fact]
    public void ConfigurationReadsBroadcastDeadlineMs()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:BroadcastDeadlineMs"] = "100" })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(100, options.BroadcastDeadlineMs);
    }

    [Fact]
    public void ConfigurationDefaultsAdaptiveDegradeToOff()
    {
        var config = new ConfigurationBuilder().Build();
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.False(options.AdaptiveDegrade);
    }

    [Fact]
    public void ConfigurationReadsAdaptiveDegrade()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:AdaptiveDegrade"] = "true" })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.True(options.AdaptiveDegrade);
    }

    [Fact]
    public void ConfigurationDefaultsUdpSocketsToTodaysSingleSocketBehavior()
    {
        // UdpSockets=1 — the single bound receive socket also sends, exactly as before
        // Replication:UdpSockets existed. See also SendWorkers/BroadcastDeadlineMs/
        // AdaptiveDegrade above for the same "default preserves current behavior" contract.
        var config = new ConfigurationBuilder().Build();
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(1, options.UdpSockets);
    }

    [Fact]
    public void ConfigurationReadsUdpSockets()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:UdpSockets"] = "4" })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(4, options.UdpSockets);
    }

    [Fact]
    public void ConfigurationReadsUdpSocketsZeroAsAuto()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:UdpSockets"] = "0" })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(0, options.UdpSockets); // resolved to an effective count by SendFanOut.ResolveUdpSocketCount
    }

    // ── Robustness: plausible-but-invalid operator values fall back to defaults, never throw ──
    // Regression guard for the exit-139 startup crash hit during the 2026-07-12 Phase-3a rerun:
    // the raw type-binder throws FormatException on values like "off"/"lots" that a bool/int
    // binder can't parse. See docs/benchmark-host-capacity-2026-07-12.md.

    [Theory]
    [InlineData("off")]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("garbage")]
    public void ConfigurationAdaptiveDegradeInvalidValueFallsBackToDefault(string raw)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:AdaptiveDegrade"] = raw })
            .Build();

        // Must not throw (was an unhandled FormatException that killed the process).
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(ReplicationOptions.DefaultAdaptiveDegrade, options.AdaptiveDegrade);
    }

    [Fact]
    public void ConfigurationGarbageIntFallsBackToDefault()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:SendWorkers"] = "lots",
                ["Replication:BroadcastDeadlineMs"] = "soon",
                ["Replication:MidTickInterval"] = "often",
            })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(ReplicationOptions.DefaultSendWorkers, options.SendWorkers);
        Assert.Equal(ReplicationOptions.DefaultBroadcastDeadlineMs, options.BroadcastDeadlineMs);
        Assert.Equal(ReplicationOptions.DefaultMidTickInterval, options.MidTickInterval);
    }

    [Fact]
    public void ConfigurationGarbageDoubleFallsBackToDefault()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:NearRadius"] = "close",
                ["Replication:MidRadius"] = "far",
            })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(ReplicationOptions.DefaultNearRadius, options.NearRadius);
        Assert.Equal(ReplicationOptions.DefaultMidRadius, options.MidRadius);
    }

    [Fact]
    public void ConfigurationInvalidValueInvokesWarningSinkWithKeyAndValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:AdaptiveDegrade"] = "off" })
            .Build();

        var warnings = new List<string>();
        var options = ReplicationOptions.FromConfiguration(config, warnings.Add);

        Assert.Equal(ReplicationOptions.DefaultAdaptiveDegrade, options.AdaptiveDegrade);
        var warning = Assert.Single(warnings);
        Assert.Contains("Replication:AdaptiveDegrade", warning);
        Assert.Contains("off", warning);
    }

    // ── Subscription-events config (interest_subscription_changed feed) ──

    [Fact]
    public void ConfigurationDefaultsSubscriptionEventsToOff()
    {
        var config = new ConfigurationBuilder().Build();
        var options = ReplicationOptions.FromConfiguration(config);

        Assert.False(options.SubscriptionEvents);
        Assert.Equal(20, options.SubscriptionSampleTicks);
    }

    [Fact]
    public void ConfigurationReadsSubscriptionEventsAndSampleTicks()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:SubscriptionEvents"] = "true",
                ["Replication:SubscriptionSampleTicks"] = "5",
            })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.True(options.SubscriptionEvents);
        Assert.Equal(5, options.SubscriptionSampleTicks);
    }

    [Fact]
    public void ConfigurationSubscriptionEventsInvalidValueFallsBackToDefault()
    {
        // Same exit-139 robustness contract as AdaptiveDegrade — a bad bool must not throw.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:SubscriptionEvents"] = "sometimes",
                ["Replication:SubscriptionSampleTicks"] = "occasionally",
            })
            .Build();

        var options = ReplicationOptions.FromConfiguration(config);

        Assert.Equal(ReplicationOptions.DefaultSubscriptionEvents, options.SubscriptionEvents);
        Assert.Equal(ReplicationOptions.DefaultSubscriptionSampleTicks, options.SubscriptionSampleTicks);
    }

    // ── SubscriptionRadius (policy-correct outer interest bound) ──

    [Theory]
    [InlineData(ReplicationPolicy.Tiered, 300.0)]  // MidRadius — outermost band ever sent
    [InlineData(ReplicationPolicy.Radius, 100.0)]  // NearRadius — its hard cutoff
    public void SubscriptionRadiusMatchesPolicyOuterBound(ReplicationPolicy policy, double expected)
    {
        var options = new ReplicationOptions { Policy = policy, NearRadius = 100.0, MidRadius = 300.0 };
        var (manager, _) = CreateManager(options);
        Assert.Equal(expected, manager.SubscriptionRadius);
    }

    [Fact]
    public void SubscriptionRadiusIsInfiniteForFullPolicy()
    {
        var options = new ReplicationOptions { Policy = ReplicationPolicy.Full };
        var (manager, _) = CreateManager(options);
        Assert.True(double.IsPositiveInfinity(manager.SubscriptionRadius));
    }

    [Fact]
    public void ConfigurationValidValuesDoNotWarn()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:AdaptiveDegrade"] = "true",
                ["Replication:SendWorkers"] = "4",
                ["Replication:NearRadius"] = "50",
                ["Replication:Policy"] = "radius",
            })
            .Build();

        var warnings = new List<string>();
        ReplicationOptions.FromConfiguration(config, warnings.Add);

        Assert.Empty(warnings);
    }
}

using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Contracts.Valheim;
using Xunit;

namespace Game.Contracts.Tests;

public class ValheimPriorityDeliveryPlannerTests
{
    [Fact]
    public void CreatePlan_Preserves_stable_reliable_then_datagram_order()
    {
        var plan = ValheimPriorityDeliveryPlanner.CreatePlan(
            new[]
            {
                Obj("decor", "decorative_far", 6, 6, 60),
                Obj("portal", "portal", 1, 1, 30),
                Obj("floor", "structural_anchor", 2, 2, 10),
                Obj("support", "support_piece", 5, 5, 20),
                Obj("player", "player_critical", 0, 3, 0),
            },
            reliableBudget: 3,
            datagramBudget: 4);

        Assert.Equal(new[] { "player", "portal", "floor" }, plan.Reliable.Select(i => i.StableKey));
        Assert.Equal(new[] { "support", "decor" }, plan.Datagram.Select(i => i.StableKey));
        Assert.Empty(plan.Deferred);
        Assert.All(plan.Reliable, item => Assert.Equal(DeliveryLane.Reliable, item.Lane));
        Assert.All(plan.Datagram, item => Assert.Equal(DeliveryLane.Datagram, item.Lane));
    }

    [Fact]
    public void CreatePlan_De_duplicates_by_stable_key_using_best_priority()
    {
        var plan = ValheimPriorityDeliveryPlanner.CreatePlan(
            new[]
            {
                Obj("portal", "decorative_far", 6, 9, 90),
                Obj("portal", "portal", 1, 1, 20),
                Obj("floor", "structural_anchor", 2, 2, 10),
            },
            reliableBudget: 8,
            datagramBudget: 8);

        Assert.Equal(3, plan.TotalInputObjects);
        Assert.Equal(2, plan.UniqueObjects);
        Assert.Equal(1, plan.DuplicatesRemoved);
        Assert.Equal(new[] { "portal", "floor" }, plan.Reliable.Select(i => i.StableKey));
        Assert.Equal("portal", plan.Reliable[0].PriorityTier);
    }

    [Fact]
    public void CreatePlan_Defers_objects_after_datagram_budget()
    {
        var plan = ValheimPriorityDeliveryPlanner.CreatePlan(
            new[]
            {
                Obj("a", "support_piece", 5, 1, 10),
                Obj("b", "support_piece", 5, 2, 11),
                Obj("c", "decorative_far", 6, 3, 12),
            },
            reliableBudget: 0,
            datagramBudget: 2);

        Assert.Empty(plan.Reliable);
        Assert.Equal(new[] { "a", "b" }, plan.Datagram.Select(i => i.StableKey));
        Assert.Equal(new[] { "c" }, plan.Deferred.Select(i => i.StableKey));
    }

    [Fact]
    public void CreatePlan_carries_landmark_reach_through_to_the_delivery_item()
    {
        // Reach is an authored property that must survive dedup + lane assignment onto the
        // reliable-lane announcement — the client needs it to size the far-field proxy.
        var plan = ValheimPriorityDeliveryPlanner.CreatePlan(
            new[] { Obj("lighthouse", "structural_anchor", 2, 1, 10, reach: 1500) },
            reliableBudget: 4,
            datagramBudget: 4);

        var item = Assert.Single(plan.Reliable);
        Assert.Equal(1500, item.ReachMeters);
        Assert.Equal(DeliveryLane.Reliable, item.Lane); // a landmark rides the region-wide reliable lane
    }

    [Fact]
    public void CreatePlan_defaults_reach_to_zero_for_unmarked_objects()
    {
        var plan = ValheimPriorityDeliveryPlanner.CreatePlan(
            new[] { Obj("rug", "decorative_far", 6, 1, 10) },
            reliableBudget: 4,
            datagramBudget: 4);

        Assert.Equal(0, Assert.Single(plan.Datagram).ReachMeters);
    }

    private static ValheimPriorityObject Obj(
        string key, string tier, int rank, int order, double distance, double reach = 0) =>
        new(
            StableKey: key,
            ObjectName: key,
            ObjectKind: "piece",
            PriorityTier: tier,
            PriorityRank: rank,
            PriorityOrder: order,
            DistanceMeters: distance,
            RouteStopId: "route",
            SampleId: "sample",
            Position: new Vec3(distance, 0, 0),
            ReachMeters: reach);
}

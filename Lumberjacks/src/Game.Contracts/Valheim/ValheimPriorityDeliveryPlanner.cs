using Game.Contracts.Entities;
using Game.Contracts.Protocol;

namespace Game.Contracts.Valheim;

public sealed record ValheimPriorityObject(
    string StableKey,
    string ObjectName,
    string ObjectKind,
    string PriorityTier,
    int PriorityRank,
    int PriorityOrder,
    double DistanceMeters,
    string RouteStopId,
    string SampleId,
    Vec3 Position);

public sealed record ValheimPriorityDeliveryItem(
    string StableKey,
    string ObjectName,
    string ObjectKind,
    string PriorityTier,
    int PriorityRank,
    int PriorityOrder,
    double DistanceMeters,
    DeliveryLane Lane,
    int DeliveryOrder,
    string RouteStopId,
    string SampleId,
    Vec3 Position);

public sealed record ValheimPriorityDeliveryPlan(
    IReadOnlyList<ValheimPriorityDeliveryItem> Reliable,
    IReadOnlyList<ValheimPriorityDeliveryItem> Datagram,
    IReadOnlyList<ValheimPriorityDeliveryItem> Deferred,
    int TotalInputObjects,
    int UniqueObjects,
    int ReliableBudget,
    int DatagramBudget)
{
    public int DuplicatesRemoved => TotalInputObjects - UniqueObjects;
}

public static class ValheimPriorityDeliveryPlanner
{
    private static readonly HashSet<string> ReliableTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "player_critical",
        "portal",
        "structural_anchor",
        "storage_crafting",
    };

    public static ValheimPriorityDeliveryPlan CreatePlan(
        IEnumerable<ValheimPriorityObject> objects,
        int reliableBudget,
        int datagramBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reliableBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(datagramBudget);

        var input = objects.ToList();
        var unique = input
            .GroupBy(o => o.StableKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(o => o.PriorityRank)
                .ThenBy(o => o.PriorityOrder)
                .ThenBy(o => o.DistanceMeters)
                .First())
            .OrderBy(o => o.PriorityRank)
            .ThenBy(o => o.PriorityOrder)
            .ThenBy(o => o.DistanceMeters)
            .ThenBy(o => o.StableKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reliable = new List<ValheimPriorityDeliveryItem>();
        var datagram = new List<ValheimPriorityDeliveryItem>();
        var deferred = new List<ValheimPriorityDeliveryItem>();

        foreach (var item in unique)
        {
            if (ReliableTiers.Contains(item.PriorityTier) && reliable.Count < reliableBudget)
            {
                reliable.Add(ToDeliveryItem(item, DeliveryLane.Reliable, reliable.Count + 1));
                continue;
            }

            if (datagram.Count < datagramBudget)
            {
                datagram.Add(ToDeliveryItem(item, DeliveryLane.Datagram, datagram.Count + 1));
                continue;
            }

            deferred.Add(ToDeliveryItem(item, DeliveryLane.Datagram, datagramBudget + deferred.Count + 1));
        }

        return new ValheimPriorityDeliveryPlan(
            reliable,
            datagram,
            deferred,
            input.Count,
            unique.Count,
            reliableBudget,
            datagramBudget);
    }

    private static ValheimPriorityDeliveryItem ToDeliveryItem(
        ValheimPriorityObject item,
        DeliveryLane lane,
        int deliveryOrder) =>
        new(
            item.StableKey,
            item.ObjectName,
            item.ObjectKind,
            item.PriorityTier,
            item.PriorityRank,
            item.PriorityOrder,
            item.DistanceMeters,
            lane,
            deliveryOrder,
            item.RouteStopId,
            item.SampleId,
            item.Position);
}

namespace Game.Simulation.World;

/// <summary>
/// One observer's interest-subscription change between two samples: the set of OTHER players
/// that entered (<see cref="Added"/>) or left (<see cref="Removed"/>) its interest radius, plus
/// the resulting total (<see cref="SubscribedCount"/>). Emitted as the canonical
/// <c>interest_subscription_changed</c> event (see <see cref="InterestSubscriptionTracker"/>).
/// </summary>
public sealed record SubscriptionChange(
    string ObserverId,
    string RegionId,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    int SubscribedCount);

/// <summary>
/// Computes per-observer interest subscriptions and diffs them across samples so the AoI filter's
/// tier transitions can be surfaced as the (previously defined-but-never-emitted)
/// <c>interest_subscription_changed</c> event — evidence for replication-policy experiments
/// (Goal 6), not a hot-path concern.
///
/// A "subscription" is deliberately RADIUS-based, independent of the tiered policy's mid-tick
/// send throttle: an observer is subscribed to every other player within its interest radius
/// (<see cref="InterestManager.SubscriptionRadius"/>), whether or not a given tick actually
/// carries that band. The throttle changes send RATE; it does not change what the observer is
/// interested in. This keeps the event low-frequency: a pair only churns when relative distance
/// actually crosses the radius, not every burst tick.
///
/// The type is split into a pure static <see cref="InterestSubscription"/> (snapshot → per-observer
/// sets) and a stateful <see cref="InterestSubscriptionTracker"/> (diff against the previous
/// sample). Both are I/O-free and independent of sockets, HTTP, and the live grid — the caller
/// snapshots player positions off the tick thread and drives these, so nothing here can race the
/// broadcast send loop or inflate its measured wall time.
/// </summary>
public sealed class InterestSubscriptionTracker
{
    // observerId -> the set of players it was subscribed to at the previous sample. Replaced
    // wholesale each DiffAll call, so observers absent from the new sample (disconnected, or the
    // whole region went quiet) age out naturally — no unbounded growth, no manual removal.
    private Dictionary<string, HashSet<string>> _previous = new();

    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    /// <summary>
    /// Diff a full sample of per-observer subscriptions against the previous sample, then adopt it
    /// as the new baseline. Returns one <see cref="SubscriptionChange"/> per observer whose set
    /// actually changed (empty added AND removed → no event).
    ///
    /// An observer seen for the first time (no prior set) reports its whole current set as
    /// <see cref="SubscriptionChange.Added"/> — the legitimate "subscribed to nobody → these N"
    /// transition — with empty removed.
    /// </summary>
    /// <param name="current">
    /// observerId → (regionId, subscribed player-id set) for every observer in this sample.
    /// Typically produced by <see cref="InterestSubscription.ComputeSubscriptions"/>.
    /// </param>
    public IReadOnlyList<SubscriptionChange> DiffAll(
        IReadOnlyDictionary<string, (string RegionId, HashSet<string> Set)> current)
    {
        var changes = new List<SubscriptionChange>();

        foreach (var (observerId, entry) in current)
        {
            var cur = entry.Set;
            _previous.TryGetValue(observerId, out var prev);

            List<string>? added = null;
            foreach (var id in cur)
                if (prev == null || !prev.Contains(id))
                    (added ??= new List<string>()).Add(id);

            List<string>? removed = null;
            if (prev != null)
                foreach (var id in prev)
                    if (!cur.Contains(id))
                        (removed ??= new List<string>()).Add(id);

            if (added == null && removed == null)
                continue; // unchanged — no event

            changes.Add(new SubscriptionChange(
                observerId,
                entry.RegionId,
                (IReadOnlyList<string>?)added ?? Empty,
                (IReadOnlyList<string>?)removed ?? Empty,
                cur.Count));
        }

        // Adopt the new sample as the baseline. Rebuild rather than mutate so departed observers
        // drop out (their next appearance correctly re-emits a fresh "added" snapshot).
        var next = new Dictionary<string, HashSet<string>>(current.Count);
        foreach (var (observerId, entry) in current)
            next[observerId] = entry.Set;
        _previous = next;

        return changes;
    }
}

/// <summary>Pure snapshot → per-observer interest-subscription computation. See <see cref="InterestSubscriptionTracker"/>.</summary>
public static class InterestSubscription
{
    /// <summary>A player's position at sample time, decoupled from the live <see cref="SpatialGrid"/>.</summary>
    public readonly record struct PlayerSnapshot(string Id, string RegionId, double X, double Z);

    /// <summary>
    /// For every observer in <paramref name="players"/>, compute the set of OTHER players in the
    /// SAME region within <paramref name="radius"/> (XZ-plane distance) — the observer's interest
    /// subscription. Self is never included. Cross-region pairs are never included (regions are
    /// independent coordinate spaces).
    ///
    /// O(region_pop²) per region, which is why the caller runs this off the tick thread on a
    /// sampling interval, not every tick. <paramref name="radius"/> of <see cref="double.PositiveInfinity"/>
    /// (the Full policy) yields the whole region as each observer's subscription.
    /// </summary>
    public static Dictionary<string, (string RegionId, HashSet<string> Set)> ComputeSubscriptions(
        IReadOnlyList<PlayerSnapshot> players,
        double radius)
    {
        // Bucket by region first so the O(n²) inner loop only pairs same-region players.
        var byRegion = new Dictionary<string, List<PlayerSnapshot>>();
        foreach (var p in players)
        {
            if (!byRegion.TryGetValue(p.RegionId, out var list))
                byRegion[p.RegionId] = list = new List<PlayerSnapshot>();
            list.Add(p);
        }

        var result = new Dictionary<string, (string RegionId, HashSet<string> Set)>(players.Count);
        var unbounded = double.IsPositiveInfinity(radius);
        var radiusSq = unbounded ? 0.0 : radius * radius;

        foreach (var (regionId, list) in byRegion)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var observer = list[i];
                var set = new HashSet<string>();
                for (var j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    var other = list[j];
                    if (!unbounded)
                    {
                        var dx = observer.X - other.X;
                        var dz = observer.Z - other.Z;
                        if (dx * dx + dz * dz > radiusSq) continue;
                    }
                    set.Add(other.Id);
                }
                result[observer.Id] = (regionId, set);
            }
        }

        return result;
    }
}

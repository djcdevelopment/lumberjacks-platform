using Game.Contracts.Entities;

namespace Game.Simulation.World;

/// <summary>
/// Per-player Area of Interest (AoI) filtering.
/// Determines which entity updates each player should receive, per the active
/// <see cref="ReplicationPolicy"/> (see <see cref="ReplicationOptions"/>):
///
///   Tiered (default) — Near (0–NearRadius) every tick, Mid (NearRadius–MidRadius)
///     every MidTickInterval-th tick, Far dropped. Reproduces the original
///     hardcoded 100/300/4 behavior when constructed with default options.
///   Full   — no filtering; every observer gets every changed entity every tick.
///   Radius — hard cutoff at NearRadius; inside every tick, outside dropped.
///
/// Reliable-lane messages (structure placed, entity removed, etc.) always go to the full region.
/// This class only filters datagram-lane tick broadcasts.
/// </summary>
public class InterestManager
{
    private readonly SpatialGrid _grid;
    private readonly ReplicationOptions _options;

    public InterestManager(SpatialGrid grid, ReplicationOptions? options = null)
    {
        _grid = grid;
        _options = options ?? new ReplicationOptions();
    }

    /// <summary>The active replication policy for this manager.</summary>
    public ReplicationPolicy Policy => _options.Policy;

    /// <summary>
    /// The outer radius that defines an observer's interest SUBSCRIPTION (the entities it can
    /// receive datagram updates for at all), independent of the mid-tick send throttle — used to
    /// emit <c>interest_subscription_changed</c> (see <see cref="InterestSubscriptionTracker"/>),
    /// not by <see cref="FilterForObserver"/>. Tiered → <see cref="ReplicationOptions.MidRadius"/>
    /// (mid is the outermost band that is ever sent); Radius → <see cref="ReplicationOptions.NearRadius"/>
    /// (its hard cutoff); Full → <see cref="double.PositiveInfinity"/> (no interest filtering, so
    /// every co-region player is "subscribed").
    /// </summary>
    public double SubscriptionRadius => _options.Policy switch
    {
        ReplicationPolicy.Full => double.PositiveInfinity,
        ReplicationPolicy.Radius => _options.NearRadius,
        _ => _options.MidRadius,
    };

    /// <summary>
    /// True when <paramref name="tick"/> is a "burst tick" — a tick where the Tiered policy's
    /// mid band is actually scheduled to go out, per the same <c>tick % MidTickInterval == 0</c>
    /// convention <see cref="FilterForObserver"/> uses internally to compute <c>isMidTick</c>.
    /// Exposed so callers (adaptive-degrade v2 — see <c>AdaptiveDegrade.ShouldSuppressMidBand</c>)
    /// can align their overrun bookkeeping to the SAME ticks that actually carry mid-band cost,
    /// instead of every tick. Meaningless for Full/Radius (no mid band) but harmless to call —
    /// it's pure arithmetic on <see cref="ReplicationOptions.MidTickInterval"/>, independent of policy.
    /// </summary>
    public bool IsBurstTick(long tick) => _options.MidTickInterval > 0 && tick % _options.MidTickInterval == 0;

    /// <summary>
    /// Determine which changed entities a given observer should receive on this tick.
    /// </summary>
    /// <param name="observerId">The player receiving updates.</param>
    /// <param name="changedEntityIds">All entities that changed this tick.</param>
    /// <param name="players">Current player state (for position lookup).</param>
    /// <param name="tick">Current tick number (for mid-band throttling).</param>
    /// <param name="suppressMidBand">
    /// Adaptive degrade (Replication:AdaptiveDegrade): when true, forces the mid band off for
    /// this call regardless of <paramref name="tick"/>'s normal MidTickInterval schedule. Only
    /// meaningful for the Tiered policy — Full and Radius have no mid band, so this is a no-op
    /// for them.
    /// </param>
    /// <returns>Filtered set of entity IDs the observer should receive.</returns>
    public HashSet<string> FilterForObserver(
        string observerId,
        HashSet<string> changedEntityIds,
        IReadOnlyDictionary<string, Player> players,
        long tick,
        bool suppressMidBand = false)
        => FilterForObserver(observerId, changedEntityIds, players, tick, out _, suppressMidBand);

    /// <summary>
    /// As the other overload, additionally reporting this observer's band POPULATION in
    /// <paramref name="bands"/> — how many changed entities fall in the near / mid / far distance
    /// bands around it, independent of the mid-band send throttle and of the active policy's send
    /// decision. This is the "which band" signal the AoI knee sweep needs: it measures where
    /// entities ARE, not whether they were sent this tick. Full policy, and an observer missing from
    /// the grid, report <see cref="BandPopulation.Empty"/> — there is no distance to bucket by.
    /// </summary>
    public HashSet<string> FilterForObserver(
        string observerId,
        HashSet<string> changedEntityIds,
        IReadOnlyDictionary<string, Player> players,
        long tick,
        out BandPopulation bands,
        bool suppressMidBand = false)
    {
        bands = BandPopulation.Empty;

        // Full replication: no interest filtering at all — short-circuit before any
        // grid lookup or distance math.
        if (_options.Policy == ReplicationPolicy.Full)
            return changedEntityIds;

        var observerPos = _grid.GetPosition(observerId);
        if (observerPos == null)
            return changedEntityIds; // Observer not in grid — fall back to sending everything

        var result = new HashSet<string>();
        var nearRadiusSq = _options.NearRadius * _options.NearRadius;
        var midRadiusSq = _options.MidRadius * _options.MidRadius;
        var useMidBand = _options.Policy == ReplicationPolicy.Tiered;
        var isMidTick = !suppressMidBand && useMidBand && _options.MidTickInterval > 0 && tick % _options.MidTickInterval == 0;

        long near = 0, mid = 0, far = 0;
        foreach (var entityId in changedEntityIds)
        {
            if (entityId == observerId)
            {
                // Always send self-updates (for input seq echo / correction). Self is the
                // observer, not an object around it, so it never counts toward a band.
                result.Add(entityId);
                continue;
            }

            var distSq = _grid.DistanceSq(observerId, entityId);
            if (distSq == null)
            {
                // Entity not in grid — include it (safety fallback). With no distance it cannot
                // be bucketed, so it does not count toward any band population either.
                result.Add(entityId);
                continue;
            }

            // Band POPULATION — pure geometry, counted before and independent of the throttled
            // send decision below. Every in-grid, non-self changed entity lands in exactly one
            // band, so near+mid+far equals the count of such entities no matter what the policy
            // actually sends this tick.
            if (distSq.Value <= nearRadiusSq) near++;
            else if (distSq.Value <= midRadiusSq) mid++;
            else far++;

            if (distSq.Value <= nearRadiusSq)
            {
                // Near band — every tick
                result.Add(entityId);
            }
            else if (useMidBand && distSq.Value <= midRadiusSq && isMidTick)
            {
                // Mid band (tiered policy only) — every Nth tick
                result.Add(entityId);
            }
            // Far band (tiered), or anything beyond NearRadius (radius policy) — skip
        }

        bands = new BandPopulation(near, mid, far);
        return result;
    }
}

/// <summary>
/// How many changed entities populate each of an observer's distance bands on a tick, split by
/// <see cref="ReplicationOptions.NearRadius"/> / <see cref="ReplicationOptions.MidRadius"/>:
/// Near = 0–NearRadius, Mid = NearRadius–MidRadius, Far = beyond MidRadius. This is the physical
/// distribution of load around an observer, independent of the mid-band send throttle — the
/// missing "which band" signal for the AoI knee sweep. Summed across observers and ticks it
/// becomes the window's band population totals on <see cref="Game.Simulation.Tick.ReplicationWindowStats"/>.
/// </summary>
public readonly record struct BandPopulation(long Near, long Mid, long Far)
{
    /// <summary>All-zero — used for Full policy and observers absent from the grid.</summary>
    public static readonly BandPopulation Empty = default;

    /// <summary>Near + Mid + Far — every in-grid, non-self changed entity counted once.</summary>
    public long Total => Near + Mid + Far;
}

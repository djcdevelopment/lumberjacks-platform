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
    {
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

        foreach (var entityId in changedEntityIds)
        {
            if (entityId == observerId)
            {
                // Always send self-updates (for input seq echo / correction)
                result.Add(entityId);
                continue;
            }

            var distSq = _grid.DistanceSq(observerId, entityId);
            if (distSq == null)
            {
                // Entity not in grid — include it (safety fallback)
                result.Add(entityId);
                continue;
            }

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

        return result;
    }
}

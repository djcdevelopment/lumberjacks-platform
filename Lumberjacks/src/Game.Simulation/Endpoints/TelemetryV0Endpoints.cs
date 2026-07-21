using Game.ServiceDefaults;
using Game.Simulation.Tick;
using Game.Simulation.World;
using Microsoft.Extensions.Configuration;

namespace Game.Simulation.Endpoints;

/// <summary>
/// Public Telemetry API v0 (community-telemetry-strategy.md Phase 3, G1) — the four
/// world/tick-facing endpoints that only need <see cref="WorldState"/> / <see cref="TickMetrics"/>
/// / configuration (no Gateway-only session state). The sessions aggregate endpoint lives
/// alongside SessionManager in Game.Gateway.Endpoints instead — see TelemetryV0SessionsEndpoints.
///
/// Read-only, versioned, explicitly unstable (see <see cref="PublicTelemetryV0"/>). Every
/// response is built by a plain static method taking only its dependencies as parameters (no
/// HttpContext) so it's directly unit-testable without spinning up a host — see
/// tests/Game.Simulation.Tests/TelemetryV0EndpointsTests.cs, including the privacy test that
/// serializes every v0 response and asserts no player id ever appears.
///
/// Hard privacy rule: no player ids, names, or positions in ANY v0 response, anywhere. These
/// four endpoints only ever touch aggregate/static world facts (tick counters, timing
/// percentiles, replication counters, region bounds) — never Player/session records.
/// </summary>
public static class TelemetryV0Endpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapTelemetryGroup();

        group.MapGet("/server", (WorldState world, IConfiguration config) =>
            Results.Ok(BuildServerInfo(world, config)));

        group.MapGet("/tick", (HttpContext http) =>
        {
            var metrics = http.RequestServices.GetService<TickMetrics>();
            return Results.Ok(BuildTickInfo(metrics));
        });

        group.MapGet("/delivery", () => Results.Ok(BuildDeliveryInfo()));

        group.MapGet("/regions", (WorldState world) => Results.Ok(BuildRegionsInfo(world)));

        group.MapGet("/events", (IConfiguration config) =>
        {
            var delay = ResolveEventsDelay(config);
            return Results.Ok(BuildEventsInfo(GameplayEventFeed.Snapshot(), delay, DateTimeOffset.UtcNow));
        });
    }

    /// <summary>Config key for the public /events exposure delay, in seconds (env: Telemetry__PublicEventsDelaySeconds).</summary>
    public const string EventsDelayConfigKey = "Telemetry:PublicEventsDelaySeconds";

    /// <summary>Default exposure delay when unconfigured: the unauthed feed serves events at least 30s old.</summary>
    public const int DefaultEventsDelaySeconds = 30;

    /// <summary>
    /// Reads the exposure delay from configuration (default <see cref="DefaultEventsDelaySeconds"/>); a
    /// value of 0 (or negative) disables the delay so the feed is effectively live.
    /// </summary>
    public static TimeSpan ResolveEventsDelay(IConfiguration config)
    {
        var seconds = config.GetValue(EventsDelayConfigKey, DefaultEventsDelaySeconds);
        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    /// <summary>
    /// api_version/stability envelope + current_tick/tick_rate_hz/uptime_seconds/started_at +
    /// the active replication policy and its configured knobs. send_workers is the EFFECTIVE
    /// (post-auto-resolve) count — see <see cref="SendFanOut.ResolveWorkerCount"/> — computed
    /// the same pure way TickBroadcaster does at startup, so this is available immediately
    /// (doesn't wait on the first ~5s TickMetrics window).
    /// </summary>
    public static object BuildServerInfo(WorldState world, IConfiguration config)
    {
        var uptime = DateTimeOffset.UtcNow - world.StartedAt;
        var options = ReplicationOptions.FromConfiguration(config);
        var effectiveSendWorkers = SendFanOut.ResolveWorkerCount(options.SendWorkers, Environment.ProcessorCount);

        return new
        {
            api_version = PublicTelemetryV0.ApiVersion,
            stability = PublicTelemetryV0.Stability,
            current_tick = world.CurrentTick,
            tick_rate_hz = 20,
            uptime_seconds = (int)uptime.TotalSeconds,
            started_at = world.StartedAt,
            replication = new
            {
                policy = options.PolicyName,
                near_radius = options.NearRadius,
                mid_radius = options.MidRadius,
                mid_tick_interval = options.MidTickInterval,
                send_workers = effectiveSendWorkers,
                deadline_ms = options.BroadcastDeadlineMs,
                adaptive = options.AdaptiveDegrade,
            },
        };
    }

    /// <summary>
    /// The TickMetrics LastWindow snapshot (same shape as /tick's tick_timing — phases +
    /// replication counters) wrapped in the v0 envelope. Null (graceful, not an error) until
    /// the first ~5s window closes, exactly like /tick.
    /// </summary>
    public static object BuildTickInfo(TickMetrics? metrics) => new
    {
        api_version = PublicTelemetryV0.ApiVersion,
        stability = PublicTelemetryV0.Stability,
        tick_timing = metrics?.LastWindow,
    };

    /// <summary>
    /// Delivery-path, session-transition, and UDP packet-outcome counters — all aggregate
    /// tallies only. The <c>udp_packets</c> block carries the per-outcome counts
    /// (received/invalid/unknown_session/send_error) plus a derived <c>reject_rate</c>. NOTE:
    /// <c>reject_rate</c> is a SERVER-SIDE reject/error rate over packets the server actually
    /// received — it is NOT network packet loss (unobservable server-side; true client-measured
    /// loss lives in synthclient's <c>loss_rate</c>).
    /// </summary>
    public static object BuildDeliveryInfo() => new
    {
        api_version = PublicTelemetryV0.ApiVersion,
        stability = PublicTelemetryV0.Stability,
        delivery = LumberjacksTelemetry.SnapshotDelivery(),
        transitions = LumberjacksTelemetry.SnapshotTransitions(),
        udp_packets = BuildUdpPacketsInfo(LumberjacksTelemetry.SnapshotUdpPackets()),
    };

    /// <summary>
    /// Shapes a UDP packet-outcome snapshot into the <c>udp_packets</c> block: every outcome
    /// count as-is, a <c>total</c>, and a derived <c>reject_rate</c> =
    /// (invalid + unknown_session + send_error) / total, guarding divide-by-zero (0 packets →
    /// 0.0). Pure function of the snapshot so it's directly unit-testable. Aggregates only — no
    /// identifiers. This is a reject/error rate, NOT network loss (see <see cref="BuildDeliveryInfo"/>).
    /// </summary>
    public static object BuildUdpPacketsInfo(IReadOnlyDictionary<string, long> udp)
    {
        long Count(string outcome) => udp.TryGetValue(outcome, out var v) ? v : 0;

        var received = Count("received");
        var invalid = Count("invalid");
        var unknownSession = Count("unknown_session");
        var sendError = Count("send_error");
        var total = received + invalid + unknownSession + sendError;
        var rejected = invalid + unknownSession + sendError;
        var rejectRate = total == 0 ? 0.0 : (double)rejected / total;

        return new
        {
            received,
            invalid,
            unknown_session = unknownSession,
            send_error = sendError,
            total,
            reject_rate = rejectRate,
        };
    }

    /// <summary>Per-region static world facts — id, name, live player_count, bounds, tick_rate.</summary>
    public static object BuildRegionsInfo(WorldState world) => new
    {
        api_version = PublicTelemetryV0.ApiVersion,
        stability = PublicTelemetryV0.Stability,
        regions = world.Regions.Values.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            player_count = r.PlayerCount,
            bounds = new
            {
                min = new { x = r.BoundsMin.X, y = r.BoundsMin.Y, z = r.BoundsMin.Z },
                max = new { x = r.BoundsMax.X, y = r.BoundsMax.Y, z = r.BoundsMax.Z },
            },
            tick_rate = r.TickRate,
        }).ToList(),
    };

    /// <summary>
    /// The public-safe gameplay event feed (G4) wrapped in the v0 envelope. Pure function of a
    /// point-in-time <see cref="GameplayEventFeed.Snapshot"/>, the exposure <paramref name="delay"/>,
    /// and the current time — no DB, no HttpContext, so it's unit-tested directly.
    ///
    /// Applies the exposure delay: only events whose <c>occurred_at</c> is at or before
    /// <paramref name="now"/> minus <paramref name="delay"/> are returned. The snapshot is already
    /// newest-first, so the filtered slice preserves that order. <c>count</c> reflects the returned
    /// (post-delay) array, while <c>capacity</c> and <c>dropped_since_start</c> describe the live ring.
    /// </summary>
    public static object BuildEventsInfo(FeedSnapshot snapshot, TimeSpan delay, DateTimeOffset now)
    {
        var cutoff = now - delay;
        var visible = snapshot.Events
            .Where(e => e.OccurredAt <= cutoff)
            .Select(e => new
            {
                event_id = e.EventId,
                event_type = e.EventType,
                occurred_at = e.OccurredAt,
                region_id = e.RegionId,
                detail = e.Detail,
                provenance = e.Provenance,
            })
            .ToList();

        return new
        {
            api_version = PublicTelemetryV0.ApiVersion,
            stability = PublicTelemetryV0.Stability,
            events = visible,
            count = visible.Count,
            capacity = snapshot.Capacity,
            dropped_since_start = snapshot.DroppedSinceStart,
            delay_seconds = (int)delay.TotalSeconds,
        };
    }
}

using Game.Gateway.WebSocket;
using Game.Gateway.Valheim;
using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// In-repo live signals for the operator console, sourced from in-process
/// telemetry tallies (no Cloud Monitoring). Mirrors the /tick live-state pattern.
/// </summary>
public static class LiveMetricsEndpoints
{
    public static void Map(WebApplication app)
    {
        // Transport distribution — 3-way split of delivered entity updates.
        app.MapGet("/live/transport", () =>
        {
            var delivery = LumberjacksTelemetry.SnapshotDelivery();

            long udp = delivery.TryGetValue("udp", out var u) ? u : 0;
            long binaryWs = delivery.TryGetValue("binary_ws", out var b) ? b : 0;
            long jsonWs = delivery.TryGetValue("json_ws", out var j) ? j : 0;

            return Results.Ok(new
            {
                paths = new
                {
                    udp,
                    binary_ws = binaryWs,
                    json_ws = jsonWs,
                },
                total = udp + binaryWs + jsonWs,
            });
        });

        // Session transitions — live active count plus cumulative created/resumed/detached.
        app.MapGet("/live/sessions", (SessionManager sessions) =>
        {
            var transitions = LumberjacksTelemetry.SnapshotTransitions();

            return Results.Ok(new
            {
                active = sessions.GetAll().Count,
                created = transitions.TryGetValue("created", out var c) ? c : 0,
                resumed = transitions.TryGetValue("resumed", out var r) ? r : 0,
                detached = transitions.TryGetValue("detached", out var d) ? d : 0,
            });
        });

        // R&D composition prerequisite: this deliberately exposes only aggregate/readiness
        // state, never session or world identity. Physical clients should not be launched until
        // the canonical dedicated server has connected and published the selected run descriptor.
        app.MapGet("/live/valheim-cutover", (
            SessionManager sessions,
            ValheimWorldZoneService worldZones,
            ValheimTelemetryHeartbeatService heartbeats,
            ValheimZdoRedirectService redirects,
            ValheimZdoConsumerTelemetryService consumers,
            ValheimWindowActivityService activity) =>
        {
            const long heartbeatFreshnessMs = 5_000;
            var server = sessions.GetAll().FirstOrDefault(session =>
                string.Equals(session.ValheimRole, "server", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(session.ValheimLogicalPeerId) &&
                session.Socket.State == System.Net.WebSockets.WebSocketState.Open);
            var descriptor = worldZones.Current();
            var inboundAgeMs = server is null
                ? (long?)null
                : Math.Max(
                    0L,
                    (long)(DateTime.UtcNow - server.LastInboundUtc)
                        .TotalMilliseconds);
            var heartbeatFresh =
                inboundAgeMs.HasValue &&
                inboundAgeMs.Value <= heartbeatFreshnessMs;
            // Effective mode + admission state, so "server up but not admitted" is one
            // curl away. Runtime arming is per-run and in-memory on the mod side; both
            // silent transitions on 2026-08-05 (abort left lumberjacks-primary armed,
            // container restart dropped to native) were invisible until players saw an
            // empty world.
            var (effectiveMode, manifestId, modeSeen) = heartbeats.LatestModeInfo();
            var modeStale = modeSeen is null ||
                DateTimeOffset.UtcNow - modeSeen > TimeSpan.FromSeconds(15);
            var verdict = heartbeats.LastVerdict;
            var authoritativeComplete = string.IsNullOrWhiteSpace(manifestId)
                ? (bool?)null
                : heartbeats.IsAuthoritativeComplete(
                    manifestId, redirects, consumers, activity, DateTime.UtcNow);
            return Results.Ok(new
            {
                ready =
                    server is not null && heartbeatFresh &&
                    descriptor is not null,
                canonical_server_connected = server is not null,
                server_heartbeat_fresh = heartbeatFresh,
                server_heartbeat_freshness_ms = heartbeatFreshnessMs,
                descriptor_published = descriptor is not null,
                descriptor_run_id = descriptor?.RunId ?? "",
                server_last_inbound_age_ms = inboundAgeMs,
                effective_mode = effectiveMode,
                effective_mode_stale = modeStale,
                enrollment_manifest_id = manifestId,
                authoritative_complete = authoritativeComplete,
                admission = verdict is null ? null : new
                {
                    admitted = verdict.Admitted,
                    mode = verdict.Mode,
                    enrollment_manifest_id = verdict.EnrollmentManifestId,
                    at_utc = verdict.AtUtc,
                },
            });
        });

        app.MapGet("/live/valheim-motion", (ValheimMotionTelemetry motion) =>
            Results.Ok(motion.Snapshot()));
    }
}

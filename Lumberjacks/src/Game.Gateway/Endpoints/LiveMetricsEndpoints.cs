using Game.Gateway.WebSocket;
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
    }
}

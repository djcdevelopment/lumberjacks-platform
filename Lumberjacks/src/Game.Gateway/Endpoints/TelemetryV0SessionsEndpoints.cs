using Game.Gateway.WebSocket;
using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Public Telemetry API v0 (community-telemetry-strategy.md Phase 3, G1) — the sessions
/// aggregate endpoint. Split out from Game.Simulation.Endpoints.TelemetryV0Endpoints because
/// it needs Gateway-only <see cref="SessionManager"/> state (Game.Simulation.Tests has no
/// reference to Game.Gateway) — see tests/Game.Gateway.Tests/TelemetryV0SessionsEndpointsTests.cs.
///
/// Hard privacy rule: AGGREGATES ONLY. GameSession carries PlayerId/SessionId/UdpEndpoint and
/// (via RegionId + WorldState) an implicit position — none of that, nor any per-session
/// breakdown that could be correlated back to an individual, may appear here. Only totals and
/// bucketed counts (protocol, region).
/// </summary>
public static class TelemetryV0SessionsEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapTelemetryGroup();

        group.MapGet("/sessions", (SessionManager sessions) => Results.Ok(BuildSessionsInfo(sessions)));
    }

    public static object BuildSessionsInfo(SessionManager sessions)
    {
        var all = sessions.GetAll();

        var byProtocol = all
            .GroupBy(s => s.Protocol == ProtocolMode.Binary ? "binary" : "json")
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var byRegion = all
            .GroupBy(s => s.RegionId ?? "unassigned")
            .ToDictionary(g => g.Key, g => (long)g.Count());

        return new
        {
            api_version = PublicTelemetryV0.ApiVersion,
            stability = PublicTelemetryV0.Stability,
            total = all.Count,
            by_protocol = byProtocol,
            by_region = byRegion,
        };
    }
}

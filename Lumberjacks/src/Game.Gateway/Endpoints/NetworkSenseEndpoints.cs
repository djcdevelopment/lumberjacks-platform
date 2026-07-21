using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// NetworkSense HUD (community-telemetry-strategy.md Phase 4/G3 UI first pass) — a single
/// self-contained HTML page (inline CSS+JS, zero external deps) that polls the Public
/// Telemetry API v0 (<see cref="Game.Simulation.Endpoints.TelemetryV0Endpoints"/>,
/// <see cref="TelemetryV0SessionsEndpoints"/>) every 2s from the browser. Served verbatim from
/// Community/networksense.html (drafted by Gemini Pro via HEARTH, reviewed and fixed up here —
/// see docs/ui/g3-g4-g5-first-pass.md).
///
/// Deliberately a glanceable, color-coded OVERLAY panel — not a dense table — because it's
/// meant to sit over live gameplay ("everyone is an alpha tester") and convey health at a
/// glance without stealing focus. All data on this page is REAL, LIVE v0 API data (tick
/// timing, sessions, delivery mix); no sample/mock data appears here.
///
/// Sibling of <see cref="CommunityViewEndpoints"/> — same serving pattern (read once at
/// startup, cached in memory, graceful fallback if the file is missing).
/// </summary>
public static class NetworkSenseEndpoints
{
    private const string RelativePath = "Community/networksense.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — NetworkSense HUD</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>NetworkSense HUD unavailable</h1>" +
        "<p>The networksense.html asset failed to load on the server. The Public Telemetry API v0 " +
        "(<code>/api/v0/telemetry/*</code>) is unaffected — query it directly.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/networksense", () => Results.Text(html, "text/html"))
            .RequireCors(PublicTelemetryV0.CorsPolicyName);
    }

    private static string LoadHtml(string contentRoot, ILogger logger)
    {
        var path = Path.Combine(contentRoot, RelativePath);
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Could not load {Path} for GET /networksense — serving a minimal fallback page instead. " +
                "The Public Telemetry API v0 endpoints are unaffected.", path);
            return FallbackHtml;
        }
    }
}

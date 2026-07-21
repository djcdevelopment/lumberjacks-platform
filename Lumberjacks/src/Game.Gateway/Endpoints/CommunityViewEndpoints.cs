using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Live Community View (community-telemetry-strategy.md Phase 4, G2) — a single
/// self-contained HTML page (inline CSS+JS, zero external deps) that polls the Public
/// Telemetry API v0 (<see cref="Game.Simulation.Endpoints.TelemetryV0Endpoints"/>,
/// <see cref="TelemetryV0SessionsEndpoints"/>) every 2s from the browser. Served verbatim from
/// Community/community.html (drafted by Gemini Pro via HEARTH, reviewed and fixed up here —
/// see docs/api/telemetry-v0.md).
///
/// Read once at startup and cached in memory — the file is static content, not a template;
/// re-reading it per request would be pure overhead. Shares the public CORS policy with the
/// v0 API even though a browser navigating here doesn't need CORS for itself (the page's own
/// fetch() calls are same-origin) — kept for consistency with "CORS enabled for /api/v0/* and
/// /community" and to allow the page to be fetched/embedded cross-origin (e.g. an iframe on a
/// community site pulling the raw HTML) without a separate policy to maintain.
///
/// Startup read is graceful, matching Program.cs's "works without a DB" posture for the
/// loaders: a missing/misplaced file (wrong working directory, a publish step that dropped the
/// Content item) logs a warning and falls back to a tiny static page instead of throwing —
/// this is an optional public dashboard, not something that should take the whole Gateway
/// process down.
/// </summary>
public static class CommunityViewEndpoints
{
    private const string RelativePath = "Community/community.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — Live Community View</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Live Community View unavailable</h1>" +
        "<p>The community.html asset failed to load on the server. The Public Telemetry API v0 " +
        "(<code>/api/v0/telemetry/*</code>) is unaffected — query it directly.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/community", () => Results.Text(html, "text/html"))
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
                "Could not load {Path} for GET /community — serving a minimal fallback page instead. " +
                "The Public Telemetry API v0 endpoints are unaffected.", path);
            return FallbackHtml;
        }
    }
}

using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Local Testing Tools (community-telemetry-strategy.md Phase 4/G5 UI first pass) — a single
/// self-contained HTML page (inline CSS+JS, zero external deps) served verbatim from
/// Community/testing.html (drafted by Gemini Pro via HEARTH, reviewed and fixed up here — see
/// docs/ui/g3-g4-g5-first-pass.md).
///
/// A control panel of clickable scenario cards (spawn enemy group, run benchmark scenario,
/// start/stop telemetry capture, begin/end replay route) — "UI over console commands." None of
/// the trigger endpoints exist server-side yet: every action requires an explicit confirm step
/// and then only logs "would POST to &lt;endpoint&gt; (backend pending)" to an on-page status
/// strip — nothing destructive is wired. The one live network call this page makes is a
/// read-only poll of <c>GET /api/v0/telemetry/server</c> (an existing aggregates-only v0
/// endpoint) purely to show whether the gateway is reachable.
///
/// The benchmark card deliberately wraps <c>scripts/load-test-dual-channel.js</c> (the one
/// blessed load harness) instead of describing a second load path, per the strategy's explicit
/// refinement #5.
///
/// Sibling of <see cref="CommunityViewEndpoints"/> — same serving pattern (read once at
/// startup, cached in memory, graceful fallback if the file is missing).
/// </summary>
public static class LocalTestingEndpoints
{
    private const string RelativePath = "Community/testing.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — Local Testing Tools</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Local Testing Tools unavailable</h1>" +
        "<p>The testing.html asset failed to load on the server. This page is a first-pass mockup — " +
        "the scenario trigger backends do not exist yet.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/testing", () => Results.Text(html, "text/html"))
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
                "Could not load {Path} for GET /testing — serving a minimal fallback page instead.", path);
            return FallbackHtml;
        }
    }
}

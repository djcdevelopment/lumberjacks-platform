using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Gameplay Event Telemetry (community-telemetry-strategy.md Phase 4/G4) — a single
/// self-contained HTML page (inline CSS+JS, zero external deps) served verbatim from
/// Community/events.html (originally drafted by Gemini Pro via HEARTH, reviewed and fixed up
/// here — see docs/ui/g3-g4-g5-first-pass.md).
///
/// The page is now a THIN CLIENT of the REAL backend: it polls
/// <c>GET /api/v0/telemetry/events</c> (see <see cref="Game.Simulation.Endpoints.TelemetryV0Endpoints"/>),
/// which snapshots the in-process, DB-less <see cref="GameplayEventFeed"/> ring buffer. There is
/// NO sample/fallback dataset: on a fetch failure the page shows an error banner and keeps only
/// the real rows it last held — it never fabricates events. The feed is DELAYED (a configurable
/// server-side exposure delay, surfaced as <c>delay_seconds</c>, so the header badge reflects the
/// delay rather than claiming "LIVE") and ANONYMIZED (no player id / name / position ever leaves
/// the server). Each row carries a provenance badge reusing the exact four-tier vocabulary/colors
/// established by the achievements model (<see cref="Game.Contracts.Achievements.ProvenanceTier"/>)
/// so the whole telemetry surface shares one provenance mental model.
///
/// Reality today: only 4 of the 18 public-safe event types have live producers
/// (structure_placed, item_picked_up, item_stored, interest_subscription_changed), so the feed is
/// legitimately sparse; the page's dictionary covers all 18 for forward-compat.
///
/// Sibling of <see cref="CommunityViewEndpoints"/> — same serving pattern (read once at
/// startup, cached in memory, graceful fallback if the file is missing).
/// </summary>
public static class GameplayEventsEndpoints
{
    private const string RelativePath = "Community/events.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — Gameplay Event Telemetry</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Gameplay Event Telemetry unavailable</h1>" +
        "<p>The events.html asset failed to load on the server. The live feed endpoint " +
        "(GET /api/v0/telemetry/events) is unaffected and can be queried directly.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/events", () => Results.Text(html, "text/html"))
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
                "Could not load {Path} for GET /events — serving a minimal fallback page instead.", path);
            return FallbackHtml;
        }
    }
}

using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Data &amp; Trust page (GET /data-and-trust, M1-1) — a single self-contained HTML page
/// (inline CSS, zero external deps) stating what the quest mod and telemetry capture, where it
/// goes, who can see it, and how to opt out. Linked from /community and from the volunteer
/// onboarding download page so every new installer sees it. The canonical text lives in the repo
/// at docs/data-and-trust.md; this is the community-facing surface of it.
///
/// Mirrors <see cref="CommunityViewEndpoints"/> exactly: read once at startup and cached, served
/// verbatim from Community/data-and-trust.html (an explicit Content item in Game.Gateway.csproj so
/// it travels through `dotnet publish`); a missing/misplaced file logs a warning and falls back to
/// a tiny static page rather than throwing — this is an optional public page, not something that
/// should take the Gateway process down.
/// </summary>
public static class DataTrustEndpoints
{
    private const string RelativePath = "Community/data-and-trust.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — Data &amp; Trust</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Data &amp; Trust page unavailable</h1>" +
        "<p>The data-and-trust.html asset failed to load on the server. The canonical text lives in " +
        "the repository at <code>docs/data-and-trust.md</code>.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/data-and-trust", () => Results.Text(html, "text/html"))
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
                "Could not load {Path} for GET /data-and-trust — serving a minimal fallback page instead.", path);
            return FallbackHtml;
        }
    }
}

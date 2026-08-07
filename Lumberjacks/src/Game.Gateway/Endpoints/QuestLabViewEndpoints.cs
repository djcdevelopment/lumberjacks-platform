using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Quest Lab Tome — the web-facing version of the in-game spellbook.
/// Sibling of <see cref="CommunityViewEndpoints"/> — same serving pattern (read once at
/// startup, single static HTML page).
/// </summary>
public static class QuestLabViewEndpoints
{
    private const string RelativePath = "Community/questlab.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — Quest Lab Tome</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Quest Lab Tome unavailable</h1>" +
        "<p>The questlab.html asset failed to load on the server.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/questlab", () => Results.Text(html, "text/html"))
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
                "Could not load {Path} for GET /questlab — serving a minimal fallback page instead.", path);
            return FallbackHtml;
        }
    }
}

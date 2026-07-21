using System.Security.Cryptography;
using System.Text;
using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Serves the generated, self-contained Valheim volunteer roadmap. The source of
/// truth is docs/roadmap; scripts/roadmap.mjs renders this published asset and checks
/// its append-only commit journal. The document is re-read per request behind an
/// mtime-and-length cache, and <c>LUMBERJACKS_ROADMAP_HTML</c> relocates it, so a freshly
/// rendered asset can be mounted over the built-in copy and republished without an image
/// rebuild or a restart. Resolution degrades in steps — mounted asset, then the copy built
/// into the image, then the fallback page — so a missing mount costs freshness, not the
/// page. Every response carries the SHA-256 of the file on disk in
/// <c>X-Roadmap-Sha256</c>, so a served page can be verified byte-for-byte against the
/// committed artifact. Like the other community surfaces, a missing optional asset
/// degrades to a small explanation instead of preventing the Gateway from starting.
/// </summary>
public static class RoadmapViewEndpoints
{
    private const string RelativePath = "Community/roadmap.html";
    private const string PathVariable = "LUMBERJACKS_ROADMAP_HTML";
    private const string ShaHeader = "X-Roadmap-Sha256";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks — Valheim roadmap</title></head>" +
        "<body style=\"background:#0b1013;color:#edf5f2;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Valheim roadmap unavailable</h1>" +
        "<p>The generated roadmap asset was not included in this Gateway build. " +
        "Run <code>npm run roadmap:render</code>, publish again, or read " +
        "<code>docs/network/valheim-volunteer-platform-plan.md</code>.</p>" +
        "</body></html>";

    /// <summary>A resolved roadmap document plus the stat fields that decide whether it is stale.</summary>
    private sealed record Document(string Path, DateTime WriteTimeUtc, long Length, string Html, string Sha256);

    private static Document? cache;

    public static void Map(WebApplication app)
    {
        var contentRoot = app.Environment.ContentRootPath;
        var logger = app.Logger;

        app.MapGet("/roadmap", (HttpContext context) =>
            {
                var document = Load(contentRoot, logger);
                context.Response.Headers[ShaHeader] = document.Sha256;
                return Results.Text(document.Html, "text/html; charset=utf-8");
            })
            .RequireCors(PublicTelemetryV0.CorsPolicyName);
    }

    public static string LoadHtml(string contentRoot, ILogger logger) => Load(contentRoot, logger).Html;

    /// <summary>
    /// Prefers a mounted asset, but only once it is actually present: an absent or empty mount
    /// falls through to the copy built into the image rather than to the unavailable page.
    /// </summary>
    public static string ResolvePath(string contentRoot) =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured && File.Exists(configured)
            ? configured
            : Path.Combine(contentRoot, RelativePath);

    private static Document Load(string contentRoot, ILogger logger)
    {
        var path = ResolvePath(contentRoot);
        var (writeTimeUtc, length) = Stat(path);

        var cached = Volatile.Read(ref cache);
        if (cached is not null && cached.Path == path && cached.WriteTimeUtc == writeTimeUtc && cached.Length == length)
        {
            return cached;
        }

        Document document;
        try
        {
            var bytes = File.ReadAllBytes(path);
            document = new Document(path, writeTimeUtc, length, Decode(bytes), Sha256(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Could not load {Path} for GET /roadmap — serving a minimal fallback page instead.",
                path);
            document = new Document(path, writeTimeUtc, length, FallbackHtml, Sha256(Encoding.UTF8.GetBytes(FallbackHtml)));
        }

        // A losing racer re-reads the same file and writes an equal document; the race is benign.
        Volatile.Write(ref cache, document);
        return document;
    }

    /// <summary>Stat fields for a file, or (MinValue, -1) when it is absent or unreadable.</summary>
    private static (DateTime WriteTimeUtc, long Length) Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (DateTime.MinValue, -1L);
        }
    }

    // Strip a UTF-8 BOM the way File.ReadAllText would; a leading U+FEFF would otherwise
    // render as page content. The hash stays over the raw bytes so it matches the artifact.
    private static string Decode(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

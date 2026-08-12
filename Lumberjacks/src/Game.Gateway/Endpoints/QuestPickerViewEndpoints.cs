using System.Security.Cryptography;
using System.Text;
using Game.ServiceDefaults;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Serves the synthetic, self-contained Quest Picker imported from an exact published
/// comfy-quest release. The mounted asset is re-read behind an mtime-and-length cache so a
/// verified picker refresh can be published without rebuilding or restarting the Gateway.
/// </summary>
public static class QuestPickerViewEndpoints
{
    private const string AssetDirectory = "Community";
    private const string AssetFile = "quest-picker.html";
    private const string PathVariable = "LUMBERJACKS_QUESTPICKER_HTML";
    private const string ShaHeader = "X-QuestPicker-Sha256";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Comfy Quest Picker</title></head>" +
        "<body style=\"background:#0b1013;color:#edf5f2;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Quest Picker unavailable</h1>" +
        "<p>No verified Quest Picker release is mounted on this Gateway yet. " +
        "Download the synthetic starter kit from <a href=\"/workbench/downloads/quest-picker\">" +
        "the Community Workbench</a>.</p></body></html>";

    private sealed record Document(string Path, DateTime WriteTimeUtc, long Length, string Html, string Sha256);

    private static Document? cache;

    public static void Map(WebApplication app)
    {
        var contentRoot = app.Environment.ContentRootPath;
        var logger = app.Logger;

        app.MapGet("/questpicker", (HttpContext context) =>
            {
                var document = Load(contentRoot, logger);
                context.Response.Headers[ShaHeader] = document.Sha256;
                return Results.Text(document.Html, "text/html; charset=utf-8");
            })
            .RequireCors(PublicTelemetryV0.CorsPolicyName);
    }

    public static string LoadHtml(string contentRoot, ILogger logger) => Load(contentRoot, logger).Html;

    public static string ResolvePath(string contentRoot) =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured && File.Exists(configured)
            ? configured
            : Path.Combine(contentRoot, AssetDirectory, AssetFile);

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
                "Could not load {Path} for GET /questpicker - serving a minimal fallback page instead.", path);
            document = new Document(path, writeTimeUtc, length, FallbackHtml, Sha256(Encoding.UTF8.GetBytes(FallbackHtml)));
        }

        Volatile.Write(ref cache, document);
        return document;
    }

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

    private static string Decode(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

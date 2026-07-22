using Game.Gateway.BoundaryEvents;
using System.Net;

namespace Game.Gateway.Endpoints;

public static class BoundaryDiagnosticsEndpoints
{
    private const string RelativePath = "Community/boundary.html";

    private const string FallbackHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Lumberjacks Boundary Diagnostics</title></head>" +
        "<body style=\"background:#0f1115;color:#e2e8f0;font-family:system-ui,sans-serif;padding:2rem\">" +
        "<h1>Boundary Diagnostics unavailable</h1>" +
        "<p>The boundary.html asset failed to load on the server. Try <code>/ops/boundary/summary</code>.</p>" +
        "</body></html>";

    public static void Map(WebApplication app)
    {
        var html = LoadHtml(app.Environment.ContentRootPath, app.Logger);

        app.MapGet("/ops/boundary", (HttpContext context) =>
            IsOperatorPlane(context)
                ? Results.Text(html, "text/html")
                : Results.Json(new { error = "operator_plane_required" }, statusCode: StatusCodes.Status403Forbidden));

        app.MapGet("/ops/boundary/summary", (
            HttpContext context,
            BoundaryEventDiagnostics diagnostics,
            int? max_files,
            int? max_rows) =>
        {
            if (!IsOperatorPlane(context))
                return Results.Json(new { error = "operator_plane_required" }, statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(diagnostics.Snapshot(
                Math.Clamp(max_files ?? 8, 1, 32),
                Math.Clamp(max_rows ?? 20_000, 100, 200_000)));
        });
    }

    private static bool IsOperatorPlane(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (IPAddress.TryParse(first, out var forwardedAddress) && !IsPrivateOrLoopback(forwardedAddress))
                return false;
        }

        return IsPrivateOrLoopback(context.Connection.RemoteIpAddress);
    }

    private static bool IsPrivateOrLoopback(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
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
                "Could not load {Path} for GET /ops/boundary - serving a minimal fallback page instead.", path);
            return FallbackHtml;
        }
    }
}

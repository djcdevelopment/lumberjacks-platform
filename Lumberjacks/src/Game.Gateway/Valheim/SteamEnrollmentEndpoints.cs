using System.Net;

namespace Game.Gateway.Valheim;

public static class SteamEnrollmentEndpoints
{
    public static void Map(WebApplication app)
    {
        // The capability middleware already requires admin for /api/v0/enrollment*;
        // the legacy header check stays as defense in depth for deployments that
        // still run without the middleware-gated private plane.
        app.MapPost("/api/v0/enrollment/invites", (HttpRequest request, SteamEnrollmentService service) =>
        {
            var admin = Environment.GetEnvironmentVariable("LUMBERJACKS_ADMIN_KEY");
            if (!string.IsNullOrWhiteSpace(admin) && request.Headers["X-Lumberjacks-Admin-Key"] != admin) return Results.Unauthorized();
            var invite = service.CreateInvite(TimeSpan.FromHours(24));
            var baseUrl = Environment.GetEnvironmentVariable("LUMBERJACKS_ENROLLMENT_PUBLIC_URL") ?? "http://127.0.0.1:4006";
            return Results.Ok(new { invite_url = $"{baseUrl.TrimEnd('/')}/join?t={invite.Token}", expires_utc = invite.ExpiresUtc });
        }).RequireRateLimiting("enrollment-admin");

        app.MapGet("/api/v0/enrollment", (SteamEnrollmentService service) =>
            Results.Ok(new
            {
                schema_version = 1,
                enrollments = service.List().Select(ToResponse),
            })).RequireRateLimiting("enrollment-admin");

        app.MapPost("/api/v0/enrollment/{enrollmentId}/revoke", (string enrollmentId, RevokeRequest? body, SteamEnrollmentService service) =>
        {
            if (!service.Revoke(enrollmentId, body?.Reason ?? "unspecified"))
                return Results.NotFound(new { error = "enrollment_unknown_or_not_active" });
            return Results.Ok(new { ok = true, enrollment_id = enrollmentId });
        }).RequireRateLimiting("enrollment-admin");

        // Pseudonymous self-view for the enrolled client (M2 preflight consumes this).
        app.MapGet("/api/v0/valheim/enrollment/me", (HttpContext context) =>
        {
            var principal = ValheimPrincipal.From(context);
            if (principal?.Enrollment is null)
                return Results.Json(new { error = "enrollment_credential_required" }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(ToResponse(principal.Enrollment));
        });

        app.MapGet("/join", (HttpRequest request) =>
        {
            var token = request.Query["t"].ToString();
            var login = SteamLoginUrl(request, $"/join/steam-callback?t={Uri.EscapeDataString(token)}");
            return Results.Text($"<html><body><h1>Lumberjacks invite</h1><p>Use Steam to redeem this one-time invitation.</p><a href=\"{WebUtility.HtmlEncode(login)}\">Sign in with Steam</a></body></html>", "text/html");
        }).RequireRateLimiting("join");

        app.MapGet("/join/steam-callback", async (HttpRequest request, SteamEnrollmentService service, IHttpClientFactory clients) =>
        {
            var token = request.Query["t"].ToString();
            var (steamId, error) = await VerifySteamOpenId(request, clients);
            if (error is not null) return error;

            if (!service.TryRedeem(token, steamId!, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });
            return Results.Text(BuildHandoff(issued, ReissueUrl(request)), "text/plain");
        }).RequireRateLimiting("join");

        // Self-serve re-issue for a volunteer whose setup code expired before they
        // installed. No invite token: the standing authorization is the enrollment
        // itself, and the identity proof is the same Steam sign-in that created it —
        // nothing copyable (least of all the expired code) can mint a fresh one.
        app.MapGet("/join/reissue", (HttpRequest request) =>
        {
            var login = SteamLoginUrl(request, "/join/reissue/steam-callback");
            return Results.Text("<html><body><h1>Lumberjacks setup code re-issue</h1><p>Setup code expired before you installed? Sign in with Steam to get a fresh one. Your enrollment stays as it is; the old code stops working.</p><a href=\"" + WebUtility.HtmlEncode(login) + "\">Sign in with Steam</a></body></html>", "text/html");
        }).RequireRateLimiting("join");

        app.MapGet("/join/reissue/steam-callback", async (HttpRequest request, SteamEnrollmentService service, IHttpClientFactory clients) =>
        {
            var (steamId, error) = await VerifySteamOpenId(request, clients);
            if (error is not null) return error;

            if (!service.TryReissueBootstrap(steamId!, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });
            return Results.Text(BuildHandoff(issued, ReissueUrl(request)), "text/plain");
        }).RequireRateLimiting("join");

        // Public by design: the bootstrap token is the only credential, it is
        // single-use, and the installer has nothing else to authenticate with yet.
        // POST rather than GET so the code cannot be spent by pasting the URL into a
        // browser, and so it stays out of history, referers, and access logs.
        app.MapPost("/join/bootstrap", (BootstrapRequest? body, HttpRequest request, SteamEnrollmentService service) =>
        {
            if (!service.TryConsumeBootstrap(body?.Token ?? string.Empty, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });
            var gateway = Environment.GetEnvironmentVariable("LUMBERJACKS_PLAYER_GATEWAY_URL") ?? baseUrlFor(request);
            return Results.Text(BuildConfig(issued, gateway), "text/plain");
        }).RequireRateLimiting("join");
    }

    static object ToResponse(SteamEnrollmentService.EnrollmentView view) => new
    {
        enrollment_id = view.EnrollmentId,
        steam_id = view.SteamId,
        recipient_id = view.RecipientId,
        status = view.Status,
        enrolled_utc = view.EnrolledUtc,
        last_used_utc = view.LastUsedUtc,
        queue_window_id = view.QueueWindowId,
        bootstrap_issue_count = view.BootstrapIssueCount,
    };

    static string baseUrlFor(HttpRequest request) => $"{request.Scheme}://{request.Host}";

    static string PublicBaseUrl(HttpRequest request) =>
        (Environment.GetEnvironmentVariable("LUMBERJACKS_ENROLLMENT_PUBLIC_URL") ?? baseUrlFor(request)).TrimEnd('/');

    static string ReissueUrl(HttpRequest request) => $"{PublicBaseUrl(request)}/join/reissue";

    static string SteamLoginUrl(HttpRequest request, string returnPath)
    {
        var baseUrl = PublicBaseUrl(request);
        var callback = baseUrl + returnPath;
        return "https://steamcommunity.com/openid/login?openid.ns=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0&openid.mode=checkid_setup&openid.return_to=" + Uri.EscapeDataString(callback) + "&openid.realm=" + Uri.EscapeDataString(baseUrl) + "&openid.identity=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0%2Fidentifier_select&openid.claimed_id=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0%2Fidentifier_select";
    }

    static async Task<(string? SteamId, IResult? Error)> VerifySteamOpenId(HttpRequest request, IHttpClientFactory clients)
    {
        var values = request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        if (!string.Equals(values.GetValueOrDefault("openid.mode"), "id_res", StringComparison.Ordinal) ||
            !values.TryGetValue("openid.claimed_id", out var claimed) ||
            !claimed.StartsWith("https://steamcommunity.com/openid/id/", StringComparison.OrdinalIgnoreCase))
            return (null, Results.BadRequest(new { error = "Steam authentication was not completed" }));

        using var form = new FormUrlEncodedContent(values.Concat(new[] { new KeyValuePair<string, string>("openid.mode", "check_authentication") }));
        using var response = await clients.CreateClient().PostAsync("https://steamcommunity.com/openid/login", form);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || !body.Contains("is_valid:true", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Unauthorized());

        return (claimed[(claimed.LastIndexOf('/') + 1)..], null);
    }

    // What the browser sees. Deliberately not the config: this page is the one part of
    // the flow that lands in history, screenshots, and over-the-shoulder views, so it
    // carries a code that is worthless the moment the installer spends it.
    static string BuildHandoff(SteamEnrollmentService.BootstrapIssued issued, string reissueUrl) =>
        $"Lumberjacks enrollment complete. SteamID={issued.Enrollment.SteamId}\n\n" +
        "Setup code (works once):\n\n" +
        $"    {issued.BootstrapToken}\n\n" +
        $"Paste it into the Lumberjacks installer to finish setup. It expires {issued.ExpiresUtc:u}.\n" +
        $"If it expires before you install, sign in again at {reissueUrl} for a fresh one.\n" +
        "If it was already used, ask the operator.\n";

    // The raw access token appears exactly once: in this response, to whoever spent the
    // bootstrap. It is minted here, stored only as a hash, and never returned again.
    static string BuildConfig(SteamEnrollmentService.EnrollmentIssued issued, string gateway) =>
        $"Lumberjacks enrollment complete. SteamID={issued.Enrollment.SteamId}\n\n" +
        "[Lumberjacks]\n" +
        $"lumberjacksGatewayUrl = {gateway}\n" +
        $"lumberjacksAuthoritativeWindowId = {issued.Enrollment.QueueWindowId}\n" +
        $"lumberjacksEnrollmentId = {issued.Enrollment.EnrollmentId}\n" +
        $"lumberjacksClientAccessKey = {issued.AccessToken}\n";

    public sealed record RevokeRequest(string? Reason);
    public sealed record BootstrapRequest(string? Token);
}

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

        // Admin rescue/update path for a known alpha tester whose browser invite/reissue flow is
        // fighting them. This rotates the enrollment's client credential and streams the same
        // personalized drop-in zip as /join/pack, without exposing a reusable bootstrap in Discord.
        app.MapPost("/api/v0/enrollment/pack", (AdminPackRequest? body, HttpRequest request, SteamEnrollmentService service) =>
        {
            var admin = Environment.GetEnvironmentVariable("LUMBERJACKS_ADMIN_KEY");
            if (!string.IsNullOrWhiteSpace(admin) && request.Headers["X-Lumberjacks-Admin-Key"] != admin) return Results.Unauthorized();

            if (!service.TryIssueReplacementCredential(body?.EnrollmentId, body?.SteamId, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });

            var pack = TryBuildPersonalizedPack(request, issued, out var error);
            return pack is null ? error! : Results.File(pack, "application/zip", "Comfy-P7-Mods.zip");
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
            return Results.Text(EnrollmentPages.InvitePage(login), "text/html");
        }).RequireRateLimiting("join");

        app.MapGet("/join/steam-callback", async (HttpRequest request, SteamEnrollmentService service, IHttpClientFactory clients) =>
        {
            var token = request.Query["t"].ToString();
            var (steamId, error) = await VerifySteamOpenId(request, clients);
            if (error is not null) return error;

            if (!service.TryRedeem(token, steamId!, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });
            return Results.Text(EnrollmentPages.DownloadPage(issued.Enrollment.SteamId, issued.BootstrapToken, PublicBaseUrl(request)), "text/html");
        }).RequireRateLimiting("join");

        // Self-serve re-issue for a volunteer whose setup code expired before they
        // installed. No invite token: the standing authorization is the enrollment
        // itself, and the identity proof is the same Steam sign-in that created it —
        // nothing copyable (least of all the expired code) can mint a fresh one.
        app.MapGet("/join/reissue", (HttpRequest request) =>
        {
            var login = SteamLoginUrl(request, "/join/reissue/steam-callback");
            return Results.Text(EnrollmentPages.ReissuePage(login), "text/html");
        }).RequireRateLimiting("join");

        app.MapGet("/join/reissue/steam-callback", async (HttpRequest request, SteamEnrollmentService service, IHttpClientFactory clients) =>
        {
            var (steamId, error) = await VerifySteamOpenId(request, clients);
            if (error is not null) return error;

            if (!service.TryReissueBootstrap(steamId!, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });
            return Results.Text(EnrollmentPages.DownloadPage(issued.Enrollment.SteamId, issued.BootstrapToken, PublicBaseUrl(request)), "text/html");
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

        // The self-service download: the button on the callback page POSTs the single-use bootstrap
        // here (form field, so it stays a POST — out of history/referers, and single-use). Consuming
        // it mints the access token at that moment (stored only as a hash), which is injected into the
        // base mod-pack template and streamed back as a drop-in personalized zip — no code relay, no
        // config editing. The credential rides in the download by design (over TLS in production); the
        // link works once. `request.Form[...]` is read directly rather than model-bound so no
        // antiforgery token is required for this credential-free-until-consumed POST.
        app.MapPost("/join/pack", (HttpRequest request, SteamEnrollmentService service) =>
        {
            var token = request.HasFormContentType ? request.Form["token"].ToString() : string.Empty;
            if (!service.TryConsumeBootstrap(token, out var issued, out var reason))
                return Results.BadRequest(new { error = reason });

            var pack = TryBuildPersonalizedPack(request, issued, out var error);
            return pack is null ? error! : Results.File(pack, "application/zip", "Comfy-P7-Mods.zip");
        }).RequireRateLimiting("join");
    }

    static byte[]? TryBuildPersonalizedPack(
        HttpRequest request,
        SteamEnrollmentService.EnrollmentIssued issued,
        out IResult? error)
    {
        error = null;
        var templatePath = Environment.GetEnvironmentVariable("LUMBERJACKS_MODPACK_TEMPLATE");
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            error = Results.Problem(
                "mod pack template not configured (LUMBERJACKS_MODPACK_TEMPLATE)", statusCode: 503);
            return null;
        }

        var gateway = Environment.GetEnvironmentVariable("LUMBERJACKS_PLAYER_GATEWAY_URL") ?? baseUrlFor(request);
        try
        {
            return ModPackBuilder.BuildPersonalizedPack(
                File.ReadAllBytes(templatePath),
                gateway,
                issued.Enrollment.QueueWindowId,
                issued.Enrollment.EnrollmentId,
                issued.AccessToken);
        }
        catch (InvalidOperationException ex)
        {
            error = Results.Problem(ex.Message, statusCode: 503);
            return null;
        }
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
    public sealed record AdminPackRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("enrollment_id")] string? EnrollmentId,
        [property: System.Text.Json.Serialization.JsonPropertyName("steam_id")] string? SteamId);
}

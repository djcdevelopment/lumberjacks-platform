using Game.ServiceDefaults;

namespace Game.Gateway.Valheim;

public static class ValheimTelemetryHeartbeatEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/valheim/telemetry");

        group.MapPost("/heartbeat", (HttpRequest request,
            ValheimTelemetryHeartbeat heartbeat,
            ValheimTelemetryHeartbeatService service,
            ValheimZdoRedirectService redirects,
            ValheimZdoConsumerTelemetryService consumers) =>
        {
            var expected = Environment.GetEnvironmentVariable("VALHEIM_TELEMETRY_KEY");
            if (!string.IsNullOrWhiteSpace(expected) &&
                request.Headers["X-Lumberjacks-Telemetry-Key"] != expected)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(heartbeat.InstanceId) ||
                string.IsNullOrWhiteSpace(heartbeat.ModVersion) ||
                string.IsNullOrWhiteSpace(heartbeat.TimestampUtc))
            {
                return Results.BadRequest(new { error = "instance_id, mod_version, and timestamp_utc are required" });
            }

            if (!string.IsNullOrWhiteSpace(heartbeat.CutoverMode) &&
                heartbeat.CutoverMode is not ("native" or "mirrored" or "lumberjacks-primary"))
            {
                return Results.BadRequest(new
                {
                    error = "cutover_mode must be native, mirrored, or lumberjacks-primary",
                });
            }

            if (!service.CanAcceptPrimaryHeartbeat(heartbeat, redirects, consumers))
            {
                return Results.Conflict(new
                {
                    error = "lumberjacks-primary requires full traffic coverage and, while peers are connected, a fully applied authoritative window",
                    coverage_total = heartbeat.CoverageTotal,
                    coverage_native_only = heartbeat.CoverageNativeOnly,
                });
            }

            service.Record(heartbeat);
            return Results.Ok(new { ok = true, received_at = DateTimeOffset.UtcNow });
        }).RequireRateLimiting("telemetry");

        app.MapGet("/api/v0/telemetry/valheim", (ValheimTelemetryHeartbeatService service) =>
            Results.Ok(service.Snapshot()))
            .RequireCors(PublicTelemetryV0.CorsPolicyName);

        app.MapGet("/api/v0/telemetry/cutover", (ValheimTelemetryHeartbeatService service,
            ValheimZdoRedirectService redirects, ValheimZdoConsumerTelemetryService consumers) =>
            Results.Ok(service.CutoverSnapshot(redirects, consumers)))
            .RequireCors(PublicTelemetryV0.CorsPolicyName);

        app.MapGet("/api/v0/valheim/enrollment/{manifestId}", (string manifestId, HttpContext context, ValheimTelemetryHeartbeatService service) =>
        {
            if (string.IsNullOrWhiteSpace(manifestId))
            {
                return Results.BadRequest(new { error = "manifestId is required" });
            }

            // Enrollment credentials only see their own snapshot; the private
            // plane (operator) may read any.
            var principal = ValheimPrincipal.From(context);
            if (principal?.Enrollment is not null &&
                !string.Equals(principal.Enrollment.EnrollmentId, manifestId, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "recipient_forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(service.EnrollmentSnapshot(manifestId));
        }).RequireCors(PublicTelemetryV0.CorsPolicyName);
    }
}

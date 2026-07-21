using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Game.ServiceDefaults;

/// <summary>
/// Shared constants and wiring for the Public Telemetry API v0 (community-telemetry-strategy.md
/// Phase 3) and the /community view it feeds (Phase 4). Deliberately explicit and unstable:
/// the telemetry model is still moving, so every response is stamped with
/// <see cref="ApiVersion"/> / <see cref="Stability"/> (both as JSON fields on the envelope AND
/// as the <see cref="StabilityHeader"/> response header) so consumers cannot mistake this for a
/// stable contract.
///
/// CORS: <see cref="CorsPolicyName"/> is GET-only, any-origin — this surface is meant to be
/// polled from third-party dashboards and the bundled /community page, unlike the operator
/// default CORS policy (origin allow-list) used by the rest of the Gateway.
/// </summary>
public static class PublicTelemetryV0
{
    public const string ApiVersion = "v0";
    public const string Stability = "unstable";
    public const string CorsPolicyName = "PublicTelemetryV0";
    public const string StabilityHeader = "X-API-Stability";

    /// <summary>
    /// Maps a route group under <c>/api/v0/telemetry</c> with the public CORS policy applied
    /// and the <see cref="StabilityHeader"/> stamped on every response. Callers add their own
    /// GET endpoints onto the returned group.
    /// </summary>
    public static RouteGroupBuilder MapTelemetryGroup(this WebApplication app) =>
        app.MapGroup("/api/v0/telemetry")
            .RequireCors(CorsPolicyName)
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers[StabilityHeader] = Stability;
                return await next(context);
            });
}

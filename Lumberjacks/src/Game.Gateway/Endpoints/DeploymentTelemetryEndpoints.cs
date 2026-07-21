using Game.ServiceDefaults;
using Game.Gateway.Valheim;

namespace Game.Gateway.Endpoints;

/// <summary>
/// Deployment identity for dashboards. This is deliberately read-only and contains
/// no host, player, or control-plane data.
/// </summary>
public static class DeploymentTelemetryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v0/telemetry/deployment", (ValheimTelemetryHeartbeatService heartbeat) =>
        {
            var version = Environment.GetEnvironmentVariable("LUMBERJACKS_VERSION")
                ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION")
                ?? "unknown";
            var environment = Environment.GetEnvironmentVariable("DEPLOYMENT_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES")
                    ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .FirstOrDefault(parts => parts.Length == 2 && parts[0] == "deployment.environment")?[1]
                ?? "local";

            return Results.Ok(new
            {
                stability = "unstable",
                service = "lumberjacks-gateway",
                environment,
                lumberjacks_version = version,
                comfy_network_sense_version = heartbeat.LatestModVersion()
                    ?? Environment.GetEnvironmentVariable("COMFY_NETWORKSENSE_VERSION")
                    ?? "unknown",
                observed_at = DateTimeOffset.UtcNow,
            });
        }).RequireCors(PublicTelemetryV0.CorsPolicyName);
    }
}

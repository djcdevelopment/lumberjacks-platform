using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Game.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? builder.Environment.ApplicationName;
        var serviceVersion = Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION")
            ?? typeof(ServiceDefaultsExtensions).Assembly.GetName().Version?.ToString();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsoleFormatter<GoogleCloudJsonConsoleFormatter, ConsoleFormatterOptions>();
        builder.Logging.AddConsole(options => options.FormatterName = GoogleCloudJsonConsoleFormatter.FormatterName);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddSource(LumberjacksTelemetry.ActivitySourceName)
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(LumberjacksTelemetry.MeterName)
                .AddMeter("Npgsql")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                // CORS_ORIGINS env var: comma-separated list of allowed origins
                // e.g. "https://myapp.azurecontainerapps.io,http://localhost:5173"
                // Falls back to localhost dev defaults if not set.
                var originsEnv = builder.Configuration["CORS_ORIGINS"]
                    ?? Environment.GetEnvironmentVariable("CORS_ORIGINS");

                var origins = !string.IsNullOrWhiteSpace(originsEnv)
                    ? originsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : new[] { "http://localhost:5173", "http://localhost:5174" };

                policy.WithOrigins(origins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });

            // Public telemetry API v0 (/api/v0/*) and the community view (/community):
            // read-only, GET-only, any-origin by design — this is a public surface meant
            // to be embedded in third-party dashboards, not the operator-only default
            // policy above. No credentials, so AllowAnyOrigin is safe here.
            options.AddPolicy(PublicTelemetryV0.CorsPolicyName, policy =>
            {
                policy.AllowAnyOrigin()
                    .WithMethods("GET")
                    .AllowAnyHeader();
            });
        });

        builder.Services.AddHealthChecks();
        builder.Services.AddHttpClient();

        return builder;
    }

    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        app.UseCors();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var serviceName = app.Environment.ApplicationName
                    .Replace("Game.", "")
                    .ToLowerInvariant();
                await context.Response.WriteAsJsonAsync(new
                {
                    status = report.Status == HealthStatus.Healthy ? "ok" : "degraded",
                    service = serviceName,
                    timestamp = DateTimeOffset.UtcNow,
                });
            },
        });

        return app;
    }
}

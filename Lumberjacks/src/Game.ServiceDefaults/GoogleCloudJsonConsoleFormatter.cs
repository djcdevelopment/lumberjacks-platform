using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Game.ServiceDefaults;

public sealed class GoogleCloudJsonConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "google-cloud-json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _serviceName;
    private readonly string? _serviceVersion;

    public GoogleCloudJsonConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _ = options;
        _serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "lumberjacks";
        _serviceVersion = Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION");
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception)
            ?? logEntry.State?.ToString()
            ?? string.Empty;

        if (logEntry.Exception is not null)
            message = $"{message}{Environment.NewLine}{logEntry.Exception}";

        var payload = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            // The Ops Agent promotes this reserved field to LogEntry.severity.
            // A plain "severity" property remains in jsonPayload and leaves the
            // actual LogEntry severity at DEFAULT, breaking severity-based alerts.
            ["logging.googleapis.com/severity"] = ToCloudSeverity(logEntry.LogLevel),
            ["message"] = message,
            ["service"] = _serviceName,
            ["category"] = logEntry.Category,
            ["event_id"] = logEntry.EventId.Id,
            ["serviceContext"] = new { service = _serviceName, version = _serviceVersion },
        };

        var activity = Activity.Current;
        var projectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        if (activity is not null)
        {
            payload["trace_id"] = activity.TraceId.ToString();
            payload["span_id"] = activity.SpanId.ToString();

            if (!string.IsNullOrWhiteSpace(projectId))
            {
                payload["logging.googleapis.com/trace"] =
                    $"projects/{projectId}/traces/{activity.TraceId}";
                payload["logging.googleapis.com/spanId"] = activity.SpanId.ToString();
                payload["logging.googleapis.com/trace_sampled"] = activity.Recorded;
            }
        }

        if (logEntry.Exception is not null)
        {
            payload["exception"] = new
            {
                type = logEntry.Exception.GetType().FullName,
                logEntry.Exception.Message,
                stack_trace = logEntry.Exception.StackTrace,
            };
        }

        if (scopeProvider is not null)
        {
            var scopes = new List<object?>();
            scopeProvider.ForEachScope(static (scope, state) => state.Add(scope), scopes);
            if (scopes.Count > 0)
                payload["scopes"] = scopes;
        }

        textWriter.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string ToCloudSeverity(LogLevel level) => level switch
    {
        LogLevel.Trace => "DEBUG",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "DEFAULT",
    };
}

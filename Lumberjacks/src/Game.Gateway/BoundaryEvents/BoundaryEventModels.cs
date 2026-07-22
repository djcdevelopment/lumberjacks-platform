using System.Diagnostics;
using System.Text.Json;
using Game.Contracts.Protocol;

namespace Game.Gateway.BoundaryEvents;

public sealed record BoundaryEventEnvelope(
    int SchemaVersion,
    int EventVersion,
    string EventId,
    DateTimeOffset TimestampUtc,
    string EventType,
    string? TraceId,
    string? SpanId,
    BoundaryEventSource Source,
    object Data)
{
    public static BoundaryEventEnvelope Create(
        string eventType, BoundaryEventSource source, object data, Activity? activity = null) =>
        new(1, 1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, eventType,
            activity?.TraceId.ToString(), activity?.SpanId.ToString(), source, data);

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions.Default);
}

public sealed record BoundaryEventSource(
    string Service,
    string Instance,
    string Release,
    string? ConfigFingerprint);

public interface IBoundaryEventSink
{
    bool TryWrite(BoundaryEventEnvelope envelope);
}

public sealed class NullBoundaryEventSink : IBoundaryEventSink
{
    public static readonly NullBoundaryEventSink Instance = new();
    private NullBoundaryEventSink() { }
    public bool TryWrite(BoundaryEventEnvelope envelope) => false;
}

public sealed class BoundaryRequestContext
{
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string Method { get; init; } = "";
    public string Route { get; init; } = "";

    public static readonly object ItemKey = typeof(BoundaryRequestContext);

    public static BoundaryRequestContext GetOrCreate(HttpContext context)
    {
        if (context.Items[ItemKey] is BoundaryRequestContext current) return current;
        var activity = Activity.Current;
        var created = new BoundaryRequestContext
        {
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            Method = context.Request.Method,
            Route = context.Request.Path.Value ?? "/",
        };
        context.Items[ItemKey] = created;
        return created;
    }
}

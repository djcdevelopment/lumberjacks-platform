using System.Collections.Concurrent;

namespace Game.Gateway.Valheim;

public sealed record ValheimZdoConsumerHeartbeat
{
    public string? WindowId { get; init; }
    public string? ConsumerId { get; init; }
    public string? ModVersion { get; init; }
    public string? TimestampUtc { get; init; }
    public long Applied { get; init; }
    public long Superseded { get; init; }
    public long Acknowledged { get; init; }
    public long Rejected { get; init; }
    public long Duplicates { get; init; }
    public long Retried { get; init; }
    public long Pending { get; init; }
    public long PriorityTagged { get; init; }
    public long PriorityFastLaneApplied { get; init; }
    public string? LastCorrelationId { get; init; }
    public string? LastOperationResult { get; init; }
    public string? FirstCorrelationId { get; init; }
    public string? FirstOperationResult { get; init; }
}

public sealed record ValheimZdoConsumerWindowStatus(
    string WindowId,
    int ActiveConsumers,
    long Applied,
    long Superseded,
    long Acknowledged,
    long Rejected,
    long Duplicates,
    long Retried,
    long Pending,
    DateTimeOffset? LastSeen)
{
    public long PriorityTagged { get; init; }
    public long PriorityFastLaneApplied { get; init; }
    public string? LastCorrelationId { get; init; }
    public string? LastOperationResult { get; init; }
    public string? FirstCorrelationId { get; init; }
    public string? FirstOperationResult { get; init; }
}

/// <summary>Latest aggregate, identity-free telemetry from enrolled ZDO consumers.</summary>
public sealed class ValheimZdoConsumerTelemetryService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, ConsumerSample> _samples =
        new(StringComparer.Ordinal);

    public void Record(ValheimZdoConsumerHeartbeat heartbeat)
    {
        var key = heartbeat.WindowId + "@" + heartbeat.ConsumerId;
        _samples[key] = new(heartbeat, DateTimeOffset.UtcNow);
    }

    public ValheimZdoConsumerWindowStatus GetWindowStatus(string windowId)
    {
        var now = DateTimeOffset.UtcNow;
        var active = _samples.Values
            .Where(sample => sample.Heartbeat.WindowId == windowId && now - sample.SeenAt <= StaleAfter)
            .ToList();
        var matching = _samples.Values
            .Where(sample => sample.Heartbeat.WindowId == windowId)
            .ToList();
        // Preserve the last observed counters when a consumer heartbeat expires.
        // ActiveConsumers remains zero, so callers cannot mistake retained data
        // for a live consumer, but operators can still see the terminal counts.
        var counted = active.Count > 0
            ? active
            : matching.OrderByDescending(sample => sample.SeenAt).Take(1).ToList();

        var latest = matching.OrderByDescending(sample => sample.SeenAt).FirstOrDefault();
        return new(
            windowId,
            active.Count,
            counted.Sum(sample => sample.Heartbeat.Applied),
            counted.Sum(sample => sample.Heartbeat.Superseded),
            counted.Sum(sample => sample.Heartbeat.Acknowledged),
            counted.Sum(sample => sample.Heartbeat.Rejected),
            counted.Sum(sample => sample.Heartbeat.Duplicates),
            counted.Sum(sample => sample.Heartbeat.Retried),
            counted.Sum(sample => sample.Heartbeat.Pending),
            matching.Count == 0 ? null : matching.Max(sample => sample.SeenAt))
        {
            PriorityTagged = counted.Sum(sample => sample.Heartbeat.PriorityTagged),
            PriorityFastLaneApplied = counted.Sum(sample => sample.Heartbeat.PriorityFastLaneApplied),
            LastCorrelationId = latest?.Heartbeat.LastCorrelationId,
            LastOperationResult = latest?.Heartbeat.LastOperationResult,
            FirstCorrelationId = latest?.Heartbeat.FirstCorrelationId,
            FirstOperationResult = latest?.Heartbeat.FirstOperationResult,
        };
    }

    public ValheimZdoConsumerWindowStatus GetRecipientStatus(string windowId, string recipientId)
    {
        var now = DateTimeOffset.UtcNow;
        var matching = _samples.Values
            .Where(sample => sample.Heartbeat.WindowId == windowId &&
                sample.Heartbeat.ConsumerId == recipientId)
            .ToList();
        var active = matching.Where(sample => now - sample.SeenAt <= StaleAfter).ToList();
        var counted = active.Count > 0
            ? active
            : matching.OrderByDescending(sample => sample.SeenAt).Take(1).ToList();
        var latest = matching.OrderByDescending(sample => sample.SeenAt).FirstOrDefault();
        return new(
            windowId,
            active.Count,
            counted.Sum(sample => sample.Heartbeat.Applied),
            counted.Sum(sample => sample.Heartbeat.Superseded),
            counted.Sum(sample => sample.Heartbeat.Acknowledged),
            counted.Sum(sample => sample.Heartbeat.Rejected),
            counted.Sum(sample => sample.Heartbeat.Duplicates),
            counted.Sum(sample => sample.Heartbeat.Retried),
            counted.Sum(sample => sample.Heartbeat.Pending),
            matching.Count == 0 ? null : matching.Max(sample => sample.SeenAt))
        {
            PriorityTagged = counted.Sum(sample => sample.Heartbeat.PriorityTagged),
            PriorityFastLaneApplied = counted.Sum(sample => sample.Heartbeat.PriorityFastLaneApplied),
            LastCorrelationId = latest?.Heartbeat.LastCorrelationId,
            LastOperationResult = latest?.Heartbeat.LastOperationResult,
            FirstCorrelationId = latest?.Heartbeat.FirstCorrelationId,
            FirstOperationResult = latest?.Heartbeat.FirstOperationResult,
        };
    }

    public object Snapshot(string windowId)
    {
        var status = GetWindowStatus(windowId);
        return new
        {
            stability = "unstable",
            window_id = windowId,
            stale = status.ActiveConsumers == 0,
            active_consumers = status.ActiveConsumers,
            applied = status.Applied,
            superseded = status.Superseded,
            acknowledged = status.Acknowledged,
            rejected = status.Rejected,
            duplicates = status.Duplicates,
            retried = status.Retried,
            pending = status.Pending,
            priority_tagged = status.PriorityTagged,
            priority_fast_lane_applied = status.PriorityFastLaneApplied,
            last_correlation_id = status.LastCorrelationId,
            last_operation_result = status.LastOperationResult,
            first_correlation_id = status.FirstCorrelationId,
            first_operation_result = status.FirstOperationResult,
            last_seen = status.LastSeen,
        };
    }

    private sealed record ConsumerSample(ValheimZdoConsumerHeartbeat Heartbeat, DateTimeOffset SeenAt);
}

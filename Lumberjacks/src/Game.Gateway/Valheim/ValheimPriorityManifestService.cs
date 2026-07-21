using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Contracts.Valheim;

namespace Game.Gateway.Valheim;

public sealed record ValheimPriorityManifestResult(
    string ManifestId,
    string EventLogUrl,
    int SourceEventCount,
    int MatchedEventCount,
    ValheimPriorityDeliveryPlan Plan);

public sealed record ValheimPriorityManifestActivation(
    string ManifestId,
    DateTimeOffset ActivatedAt,
    int SourceEventCount,
    int MatchedEventCount,
    ValheimPriorityDeliveryPlan Plan);

public sealed class ValheimPriorityManifestService
{
    private const string ObjectEventType = "valheim.priority_manifest.objects";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ValheimPriorityManifestService> _logger;
    private readonly ConcurrentDictionary<string, ValheimPriorityManifestActivation> _activePlans = new(StringComparer.OrdinalIgnoreCase);

    public ValheimPriorityManifestService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ValheimPriorityManifestService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ValheimPriorityManifestResult> LoadDeliveryPlanAsync(
        string manifestId,
        int reliableBudget,
        int datagramBudget,
        int eventLimit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentOutOfRangeException.ThrowIfNegative(reliableBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(datagramBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventLimit);

        var eventLogUrl = (_configuration["ServiceUrls:EventLog"] ?? "http://localhost:4002").TrimEnd('/');
        var events = await QueryObjectEventsAsync(eventLogUrl, eventLimit, cancellationToken);
        var objects = new List<ValheimPriorityObject>();
        var matchedEvents = 0;

        foreach (var gameEvent in events.Events.OrderBy(e => e.OccurredAt))
        {
            var payload = ReadPayload(gameEvent.Payload);
            if (payload.ValueKind != JsonValueKind.Object)
                continue;

            if (!string.Equals(GetString(payload, "manifest_id"), manifestId, StringComparison.OrdinalIgnoreCase))
                continue;

            matchedEvents++;
            objects.AddRange(ReadObjects(payload));
        }

        var plan = ValheimPriorityDeliveryPlanner.CreatePlan(objects, reliableBudget, datagramBudget);
        _logger.LogInformation(
            "Loaded Valheim priority manifest {ManifestId}: {Objects} objects, {Reliable} reliable, {Datagram} datagram, {Deferred} deferred",
            manifestId,
            plan.TotalInputObjects,
            plan.Reliable.Count,
            plan.Datagram.Count,
            plan.Deferred.Count);

        return new ValheimPriorityManifestResult(
            manifestId,
            eventLogUrl,
            events.Events.Count,
            matchedEvents,
            plan);
    }

    public async Task<ValheimPriorityManifestActivation> ActivateDeliveryPlanAsync(
        string manifestId,
        int reliableBudget,
        int datagramBudget,
        int eventLimit,
        CancellationToken cancellationToken)
    {
        var result = await LoadDeliveryPlanAsync(
            manifestId,
            reliableBudget,
            datagramBudget,
            eventLimit,
            cancellationToken);

        var activation = new ValheimPriorityManifestActivation(
            result.ManifestId,
            DateTimeOffset.UtcNow,
            result.SourceEventCount,
            result.MatchedEventCount,
            result.Plan);

        _activePlans[result.ManifestId] = activation;
        return activation;
    }

    public IReadOnlyCollection<ValheimPriorityManifestActivation> GetActivePlans() =>
        _activePlans.Values
            .OrderByDescending(p => p.ActivatedAt)
            .ToList();

    private async Task<EventLogResponse> QueryObjectEventsAsync(
        string eventLogUrl,
        int eventLimit,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ValheimPriorityManifestService));
        var url = $"{eventLogUrl}/events?type={Uri.EscapeDataString(ObjectEventType)}&limit={eventLimit}";

        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<EventLogResponse>(stream, JsonOptions.Default, cancellationToken)
            ?? new EventLogResponse([], 0);
    }

    private static JsonElement ReadPayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.String)
        {
            var raw = payload.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return default;

            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }

        return payload.Clone();
    }

    private static IEnumerable<ValheimPriorityObject> ReadObjects(JsonElement payload)
    {
        if (!payload.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            yield break;

        var routeStopId = GetString(payload, "route_stop_id");
        var sampleId = GetString(payload, "sample_id");

        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
                continue;

            var objectName = GetString(record, "object_name", "unknown");
            var stableKey = GetString(record, "object_stable_key");
            var x = GetDouble(record, "object_x");
            var y = GetDouble(record, "object_y");
            var z = GetDouble(record, "object_z");

            if (string.IsNullOrWhiteSpace(stableKey))
                stableKey = $"{objectName}@{x:0.###},{y:0.###},{z:0.###}";

            yield return new ValheimPriorityObject(
                StableKey: stableKey,
                ObjectName: objectName,
                ObjectKind: GetString(record, "object_kind", "unknown"),
                PriorityTier: GetString(record, "priority_tier", "decorative_far"),
                PriorityRank: GetInt(record, "priority_rank", int.MaxValue),
                PriorityOrder: GetInt(record, "priority_order", int.MaxValue),
                DistanceMeters: GetDouble(record, "distance_meters", double.MaxValue),
                RouteStopId: GetString(record, "route_stop_id", routeStopId),
                SampleId: GetString(record, "sample_id", sampleId),
                Position: new Vec3(x, y, z));
        }
    }

    private static string GetString(JsonElement element, string propertyName, string fallback = "")
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback,
        };
    }

    private static int GetInt(JsonElement element, string propertyName, int fallback = 0)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return fallback;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return int.TryParse(GetString(element, propertyName), out number) ? number : fallback;
    }

    private static double GetDouble(JsonElement element, string propertyName, double fallback = 0)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return fallback;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        return double.TryParse(GetString(element, propertyName), out number) ? number : fallback;
    }

    private sealed record EventLogResponse(
        [property: JsonPropertyName("events")] IReadOnlyList<EventLogEvent> Events,
        [property: JsonPropertyName("count")] int Count);

    private sealed record EventLogEvent(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
        [property: JsonPropertyName("payload")] JsonElement Payload);
}

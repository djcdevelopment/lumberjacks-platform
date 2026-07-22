using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Game.Gateway.BoundaryEvents;

public sealed class BoundaryEventDiagnostics
{
    private static readonly string[] RequiredFields =
    [
        "schema_version",
        "event_version",
        "event_id",
        "timestamp_utc",
        "event_type",
        "trace_id",
        "span_id",
        "source",
        "data",
    ];

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "identity.resolved",
        "authorization.decided",
        "zdo.batch.queued",
        "zdo.batch.polled",
        "zdo.batch.acknowledged",
        "zdo.consumer.heartbeat",
        "request.completed",
    };

    private readonly BoundaryEventOptions _options;
    private readonly BoundaryEventWriter _writer;

    public BoundaryEventDiagnostics(IOptions<BoundaryEventOptions> options, BoundaryEventWriter writer)
    {
        _options = options.Value;
        _writer = writer;
    }

    public BoundaryEventDiagnosticsSnapshot Snapshot(int maxFiles = 8, int maxRows = 20_000)
    {
        var root = string.IsNullOrWhiteSpace(_options.Path)
            ? string.Empty
            : Path.GetFullPath(_options.Path);
        var snapshot = new BoundaryEventDiagnosticsSnapshot
        {
            Enabled = _options.Enabled,
            Root = root,
            WriterDroppedRows = _writer.DroppedRows,
            WriterFaults = _writer.Faults,
        };

        if (!_options.Enabled || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return snapshot;

        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.open.jsonl", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(Math.Max(1, maxFiles))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        snapshot.Files = files.Select(file => new BoundaryEventFileSummary(
            file.Name, file.Length, file.LastWriteTimeUtc, file.Name.EndsWith(".open.jsonl", StringComparison.Ordinal)))
            .ToArray();

        foreach (var file in files)
        {
            ReadFile(file.FullName, file.Name.EndsWith(".open.jsonl", StringComparison.Ordinal), snapshot, maxRows);
            if (snapshot.Rows >= maxRows) break;
        }

        snapshot.ByEventType = Sort(snapshot.ByEventType);
        snapshot.ByVersion = Sort(snapshot.ByVersion);
        snapshot.AuthorizationResultReason = Sort(snapshot.AuthorizationResultReason);
        snapshot.RequiredGrantedCapabilities = Sort(snapshot.RequiredGrantedCapabilities);
        snapshot.IdentityResolution = Sort(snapshot.IdentityResolution);
        snapshot.ProxyBoundary = Sort(snapshot.ProxyBoundary);
        snapshot.RouteStatus = Sort(snapshot.RouteStatus);
        snapshot.ZdoByOperation = Sort(snapshot.ZdoByOperation);
        snapshot.ZdoByWindow = Sort(snapshot.ZdoByWindow);
        snapshot.ZdoByRecipient = Sort(snapshot.ZdoByRecipient);
        snapshot.ZdoRelease = Sort(snapshot.ZdoRelease);
        snapshot.ZdoConsumerResult = Sort(snapshot.ZdoConsumerResult);
        snapshot.UnknownEventTypes.Sort(StringComparer.Ordinal);
        snapshot.RecentAuthorization = snapshot.RecentAuthorization
            .OrderByDescending(item => item.TimestampUtc)
            .Take(40)
            .ToList();
        snapshot.RecentEvents = snapshot.RecentEvents
            .OrderByDescending(item => item.TimestampUtc)
            .Take(80)
            .ToList();
        return snapshot;
    }

    private static void ReadFile(string path, bool openSegment, BoundaryEventDiagnosticsSnapshot snapshot, int maxRows)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        while (!reader.EndOfStream && snapshot.Rows < maxRows)
        {
            var line = reader.ReadLine();
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var document = Parse(line, path, openSegment, lineNumber, snapshot);
            if (document is null) continue;
            using (document)
            {
                var row = document.RootElement;
                if (!HasRequiredShape(row, path, lineNumber, snapshot)) continue;

                snapshot.Rows++;
                var eventType = row.GetProperty("event_type").GetString() ?? "<missing>";
                Increment(snapshot.ByEventType, eventType);
            if (!KnownTypes.Contains(eventType) && !snapshot.UnknownEventTypes.Contains(eventType, StringComparer.Ordinal))
                snapshot.UnknownEventTypes.Add(eventType);

                var version = $"{row.GetProperty("schema_version").GetRawText()}/{row.GetProperty("event_version").GetRawText()}";
                Increment(snapshot.ByVersion, version);

                var timestamp = ReadTimestamp(row);
                var data = row.GetProperty("data");
                ObserveRecent(row, data, timestamp, snapshot);
                if (eventType == "authorization.decided") ObserveAuthorization(data, timestamp, snapshot);
                else if (eventType == "identity.resolved") ObserveIdentity(data, snapshot);
                else if (eventType == "request.completed") ObserveRequest(data, snapshot);
                else if (eventType.StartsWith("zdo.", StringComparison.Ordinal)) ObserveZdo(eventType, data, snapshot);
            }
        }
    }

    private static JsonDocument Parse(string line, string path, bool openSegment, int lineNumber,
        BoundaryEventDiagnosticsSnapshot snapshot)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            if (openSegment) snapshot.TruncatedRows++;
            else snapshot.MalformedRows++;
            if (snapshot.Errors.Count < 20)
                snapshot.Errors.Add($"{Path.GetFileName(path)}:{lineNumber}: malformed JSON");
            return null!;
        }
    }

    private static bool HasRequiredShape(JsonElement row, string path, int lineNumber,
        BoundaryEventDiagnosticsSnapshot snapshot)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            snapshot.MalformedRows++;
            return false;
        }

        var missing = RequiredFields.Where(field => !row.TryGetProperty(field, out _)).ToArray();
        if (missing.Length > 0)
        {
            snapshot.MalformedRows++;
            if (snapshot.Errors.Count < 20)
                snapshot.Errors.Add($"{Path.GetFileName(path)}:{lineNumber}: missing {string.Join(",", missing)}");
            return false;
        }

        if (row.GetProperty("source").ValueKind != JsonValueKind.Object ||
            row.GetProperty("data").ValueKind != JsonValueKind.Object)
        {
            snapshot.MalformedRows++;
            return false;
        }

        return true;
    }

    private static void ObserveAuthorization(JsonElement data, DateTimeOffset? timestamp,
        BoundaryEventDiagnosticsSnapshot snapshot)
    {
        var result = ReadString(data, "result");
        var reason = ReadString(data, "reason");
        var required = ReadString(data, "required_capabilities");
        var granted = ReadString(data, "granted_capabilities");
        var policy = ReadString(data, "policy");

        Increment(snapshot.AuthorizationResultReason, $"{result}/{reason}");
        Increment(snapshot.RequiredGrantedCapabilities, $"{required} -> {granted}");
        snapshot.RecentAuthorization.Add(new BoundaryAuthorizationDecision(
            timestamp, policy, result, reason, required, granted));
    }

    private static void ObserveIdentity(JsonElement data, BoundaryEventDiagnosticsSnapshot snapshot)
    {
        var principal = ReadString(data, "principal_kind");
        var basis = ReadString(data, "resolution_basis");
        var socket = ReadString(data, "socket_peer_class");
        var forwarded = ReadString(data, "forwarded_peer_claim_class");
        Increment(snapshot.IdentityResolution, $"{principal}/{basis}");
        Increment(snapshot.ProxyBoundary, $"socket:{socket} forwarded:{forwarded}");
        if (string.Equals(socket, "private", StringComparison.Ordinal) &&
            string.Equals(forwarded, "public", StringComparison.Ordinal))
            snapshot.ProxyBoundaryWarnings++;
    }

    private static void ObserveRequest(JsonElement data, BoundaryEventDiagnosticsSnapshot snapshot)
    {
        var route = ReadString(data, "route");
        var status = ReadString(data, "status_code");
        Increment(snapshot.RouteStatus, $"{status} {route}");

        if (data.TryGetProperty("duration_ms", out var duration) &&
            duration.ValueKind == JsonValueKind.Number &&
            duration.TryGetDouble(out var value))
        {
            snapshot.RequestDuration = snapshot.RequestDuration.Add(value);
        }
    }

    private static void ObserveZdo(string eventType, JsonElement data, BoundaryEventDiagnosticsSnapshot snapshot)
    {
        Increment(snapshot.ZdoByOperation, eventType);
        Increment(snapshot.ZdoByWindow, ReadString(data, "window_id"));
        Increment(snapshot.ZdoByRecipient, ReadString(data, "recipient_id"));
        Increment(snapshot.ZdoRelease, ReadString(data, "observed_mod_release"));
        if (eventType == "zdo.consumer.heartbeat")
            Increment(snapshot.ZdoConsumerResult, ReadString(data, "last_operation_result"));

        snapshot.ZdoTotals = snapshot.ZdoTotals with
        {
            QueuedEnvelopes = snapshot.ZdoTotals.QueuedEnvelopes + (eventType == "zdo.batch.queued" ? ReadLong(data, "envelope_count") : 0),
            AcceptedEnvelopes = snapshot.ZdoTotals.AcceptedEnvelopes + ReadLong(data, "accepted_count"),
            RequestBytes = snapshot.ZdoTotals.RequestBytes + ReadLong(data, "request_bytes"),
            PolledEnvelopes = snapshot.ZdoTotals.PolledEnvelopes + (eventType == "zdo.batch.polled" ? ReadLong(data, "envelope_count") : 0),
            AckSequenceCount = snapshot.ZdoTotals.AckSequenceCount + ReadLong(data, "sequence_count"),
            AcknowledgedCount = snapshot.ZdoTotals.AcknowledgedCount + ReadLong(data, "acknowledged_count"),
            UnknownAckCount = snapshot.ZdoTotals.UnknownAckCount + ReadLong(data, "unknown_count"),
            AppliedCount = snapshot.ZdoTotals.AppliedCount + ReadLong(data, "applied"),
            SupersededCount = snapshot.ZdoTotals.SupersededCount + ReadLong(data, "superseded"),
            RejectedCount = snapshot.ZdoTotals.RejectedCount + ReadLong(data, "rejected"),
            PendingCount = snapshot.ZdoTotals.PendingCount + ReadLong(data, "pending"),
            PriorityFastLaneApplied = snapshot.ZdoTotals.PriorityFastLaneApplied + ReadLong(data, "priority_fast_lane_applied"),
        };

        if (TryReadDouble(data, "queue_duration_ms", out var queueDuration))
            snapshot.ZdoQueueDuration = snapshot.ZdoQueueDuration.Add(queueDuration);
        if (TryReadDouble(data, "poll_duration_ms", out var pollDuration))
            snapshot.ZdoPollDuration = snapshot.ZdoPollDuration.Add(pollDuration);
        if (TryReadDouble(data, "ack_duration_ms", out var ackDuration))
            snapshot.ZdoAckDuration = snapshot.ZdoAckDuration.Add(ackDuration);
    }

    private static void ObserveRecent(JsonElement row, JsonElement data, DateTimeOffset? timestamp,
        BoundaryEventDiagnosticsSnapshot snapshot)
    {
        if (snapshot.RecentEvents.Count >= 240) return;
        var eventType = row.GetProperty("event_type").GetString() ?? "<missing>";
        snapshot.RecentEvents.Add(new BoundaryRecentEvent(
            timestamp,
            eventType,
            ReadString(data, "route"),
            ReadString(data, "window_id"),
            ReadString(data, "recipient_id"),
            ReadString(data, "result"),
            ReadString(data, "reason"),
            FirstNonMissing(
                ReadString(data, "envelope_count"),
                ReadString(data, "acknowledged_count"),
                ReadString(data, "applied"),
                ReadString(data, "status_code"))));
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement row) =>
        row.TryGetProperty("timestamp_utc", out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static string ReadString(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value)) return "<missing>";
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? "<empty>" : value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "<null>",
            _ => value.ValueKind.ToString().ToLowerInvariant(),
        };
    }

    private static long ReadLong(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out parsed)) return parsed;
        return 0;
    }

    private static bool TryReadDouble(JsonElement data, string property, out double parsed)
    {
        parsed = 0;
        if (!data.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed)) return true;
        return value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static string FirstNonMissing(params string[] values) =>
        values.FirstOrDefault(value => !string.Equals(value, "<missing>", StringComparison.Ordinal)) ?? "<missing>";

    private static void Increment(IDictionary<string, long> values, string key) =>
        values[key] = values.TryGetValue(key, out var current) ? current + 1 : 1;

    private static Dictionary<string, long> Sort(IDictionary<string, long> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

public sealed record BoundaryEventFileSummary(string Name, long Bytes, DateTime LastWriteUtc, bool Open);

public sealed record BoundaryAuthorizationDecision(
    DateTimeOffset? TimestampUtc,
    string Policy,
    string Result,
    string Reason,
    string RequiredCapabilities,
    string GrantedCapabilities);

public sealed record BoundaryRecentEvent(
    DateTimeOffset? TimestampUtc,
    string EventType,
    string Route,
    string WindowId,
    string RecipientId,
    string Result,
    string Reason,
    string Count);

public sealed record BoundaryZdoTotals(
    long QueuedEnvelopes,
    long AcceptedEnvelopes,
    long RequestBytes,
    long PolledEnvelopes,
    long AckSequenceCount,
    long AcknowledgedCount,
    long UnknownAckCount,
    long AppliedCount,
    long SupersededCount,
    long RejectedCount,
    long PendingCount,
    long PriorityFastLaneApplied)
{
    public static BoundaryZdoTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record BoundaryDurationSummary(long Count, double Min, double Max, double Average)
{
    public static BoundaryDurationSummary Empty { get; } = new(0, 0, 0, 0);

    public BoundaryDurationSummary Add(double value)
    {
        if (Count == 0) return new(1, value, value, value);
        var count = Count + 1;
        return new(count, Math.Min(Min, value), Math.Max(Max, value),
            ((Average * Count) + value) / count);
    }
}

public sealed class BoundaryEventDiagnosticsSnapshot
{
    public bool Enabled { get; set; }
    public string Root { get; set; } = string.Empty;
    public long Rows { get; set; }
    public long MalformedRows { get; set; }
    public long TruncatedRows { get; set; }
    public long WriterDroppedRows { get; set; }
    public long WriterFaults { get; set; }
    public long ProxyBoundaryWarnings { get; set; }
    public IReadOnlyList<BoundaryEventFileSummary> Files { get; set; } = Array.Empty<BoundaryEventFileSummary>();
    public Dictionary<string, long> ByEventType { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ByVersion { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AuthorizationResultReason { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> RequiredGrantedCapabilities { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> IdentityResolution { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ProxyBoundary { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> RouteStatus { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ZdoByOperation { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ZdoByWindow { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ZdoByRecipient { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ZdoRelease { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ZdoConsumerResult { get; set; } = new(StringComparer.Ordinal);
    public BoundaryDurationSummary RequestDuration { get; set; } = BoundaryDurationSummary.Empty;
    public BoundaryDurationSummary ZdoQueueDuration { get; set; } = BoundaryDurationSummary.Empty;
    public BoundaryDurationSummary ZdoPollDuration { get; set; } = BoundaryDurationSummary.Empty;
    public BoundaryDurationSummary ZdoAckDuration { get; set; } = BoundaryDurationSummary.Empty;
    public BoundaryZdoTotals ZdoTotals { get; set; } = BoundaryZdoTotals.Empty;
    public List<string> Errors { get; } = [];
    public List<string> UnknownEventTypes { get; set; } = [];
    public List<BoundaryAuthorizationDecision> RecentAuthorization { get; set; } = [];
    public List<BoundaryRecentEvent> RecentEvents { get; set; } = [];
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using ComfyNetworkSense;

namespace AuthorityLab;

/// <summary>
/// Applies the current pure Lumberjacks distance-band policy to normalized native
/// observations. This is a comparison seam, not a claim that native Valheim has
/// adopted the Lumberjacks decision.
/// </summary>
internal static class NativeCandidateReplay
{
    public static int Execute(Options options)
    {
        var inputRun = options.Required("run");
        var output = options.Required("output");
        var sourceReceiptPath = Path.Combine(inputRun, "receipt.json");
        var sourceEventsPath = Path.Combine(inputRun, "raw", "events.jsonl");
        if (!File.Exists(sourceReceiptPath) || !File.Exists(sourceEventsPath))
            throw new ScenarioException("replay-native requires a normalized run with receipt.json and raw/events.jsonl");
        var sourceReceipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(sourceReceiptPath), ProgramJson.Options)
            ?? throw new ScenarioException("source receipt is empty");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "raw"));
        var sourceInputPath = Path.Combine(inputRun, "normalized-input.json");
        if (File.Exists(sourceInputPath)) File.Copy(sourceInputPath, Path.Combine(output, "normalized-input.json"), true);
        File.Copy(sourceEventsPath, Path.Combine(output, "raw", "native-events.jsonl"), true);

        var runId = Path.GetFileName(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var events = new List<EventEnvelope>();
        var candidateCount = 0;
        foreach (var line in File.ReadLines(sourceEventsPath))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("event_type").GetString(), "authority.native_candidate_observed", StringComparison.Ordinal)) continue;
            var payload = root.GetProperty("payload");
            if (!payload.TryGetProperty("distance_meters", out var distanceElement) || !distanceElement.TryGetDouble(out var distance)) continue;
            var action = ZdoBandPolicy.Classify(
                distance,
                sourceReceipt.Policy.NearMeters,
                sourceReceipt.Policy.OuterMeters,
                0,
                0,
                -1,
                1 / Math.Max(sourceReceipt.Policy.MidHz, 0.001));
            var sourceLine = payload.TryGetProperty("source_line", out var sourceLineElement) && sourceLineElement.TryGetInt32(out var lineNumber)
                ? lineNumber : root.GetProperty("tick").GetInt32();
            var sourceEventId = root.GetProperty("event_id").GetString() ?? $"native-{sourceLine:000000}";
            var decisionPayload = new Dictionary<string, object?>
            {
                ["source_event_id"] = sourceEventId,
                ["source_line"] = sourceLine,
                ["distance_meters"] = distance,
                ["policy_name"] = sourceReceipt.Policy.Name,
                ["lumberjacks_action"] = action.ToString(),
                ["emits"] = ZdoBandPolicy.Emits(action),
                ["comparison_basis"] = "distance_band_only",
                ["native_priority_tier"] = Text(payload, "priority_tier"),
                ["native_priority_rank"] = Number(payload, "priority_rank")
            };
            var tick = candidateCount++;
            events.Add(Event(sourceReceipt, runId, tick, "authority.lumberjacks_decision", $"replay-decision-{tick:000000}", decisionPayload));
            events.Add(Event(sourceReceipt, runId, tick, "authority.decision_compared", $"replay-comparison-{tick:000000}", new Dictionary<string, object?>(decisionPayload)
            {
                ["native_decision"] = null,
                ["agreement"] = null,
                ["result"] = "observation_only"
            }));
        }

        var normalized = JsonSerializer.Serialize(events.Select(e => new
        {
            e.SchemaVersion, e.EventType, e.ExperimentId, e.ScenarioId, e.Seed, e.Tick, e.Driver, e.Payload
        }), ProgramJson.NormalizedOptions);
        WriteJsonl(Path.Combine(output, "raw", "events.jsonl"), events);
        File.WriteAllText(Path.Combine(output, "raw", "normalized-decisions.json"), normalized + Environment.NewLine, new UTF8Encoding(false));
        var invariants = new List<InvariantResult>
        {
            new() { Name = "normalized_source_readable", Passed = true, Detail = "source receipt and event stream loaded" },
            new() { Name = "candidate_policy_decisions_emitted", Passed = candidateCount > 0, Detail = $"replayed {candidateCount} candidate observation(s)" },
            new() { Name = "native_authority_claim_not_made", Passed = events.All(e => e.EventType != "authority.native_candidate_observed"), Detail = "replay rows are explicitly decisions/comparisons with observation_only result" }
        };
        var receipt = new Receipt
        {
            SchemaVersion = 1,
            ExperimentId = sourceReceipt.ExperimentId,
            ScenarioId = sourceReceipt.ScenarioId,
            RunId = runId,
            SourceRevision = options.Value("source-revision") ?? sourceReceipt.SourceRevision,
            DirtyState = options.Has("dirty-state") || sourceReceipt.DirtyState,
            ScenarioSha256 = sourceReceipt.ScenarioSha256,
            NormalizedInputSha256 = sourceReceipt.NormalizedInputSha256,
            Policy = sourceReceipt.Policy,
            Driver = "replay",
            Seed = sourceReceipt.Seed,
            StartedUtc = DateTimeOffset.UtcNow.ToString("o"),
            EndedUtc = DateTimeOffset.UtcNow.ToString("o"),
            StopResult = "completed",
            EventCounts = events.GroupBy(e => e.EventType).ToDictionary(g => g.Key, g => g.Count()),
            Invariants = invariants,
            PredictionObservations = new List<PredictionObservation>
            {
                new() { Name = "replayed_candidate_count", Observed = candidateCount.ToString(CultureInfo.InvariantCulture) },
                new() { Name = "comparison_result", Observed = "observation_only" }
            },
            RawEvidencePaths = new[] { "normalized-input.json", "raw/native-events.jsonl", "raw/events.jsonl", "raw/normalized-decisions.json" },
            NormalizedDecisionSha256 = Hashing.Sha256(normalized),
            ResultClassification = candidateCount > 0 ? "supported" : "inconclusive"
        };
        File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(receipt, ProgramJson.NormalizedOptions) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "summary.md"), $"# Native candidate replay\n\n- Candidates: `{candidateCount}`\n- Result: `{receipt.ResultClassification}`\n- Boundary: distance-band comparison only; native authority remains unclaimed.\n", new UTF8Encoding(false));
        Console.WriteLine($"replay-native: {receipt.ResultClassification}; candidates={candidateCount}; receipt={Path.Combine(output, "receipt.json")}");
        return receipt.ResultClassification == "supported" ? 0 : 3;
    }

    private static EventEnvelope Event(Receipt source, string runId, int tick, string eventType, string id, Dictionary<string, object?> payload) => new()
    {
        EventId = id,
        TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
        EventType = eventType,
        ExperimentId = source.ExperimentId,
        ScenarioId = source.ScenarioId,
        RunId = runId,
        Seed = source.Seed,
        Tick = tick,
        Driver = "replay",
        Payload = payload
    };

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static object? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt64(out var integer)) return integer;
        return value.TryGetDouble(out var number) ? number : null;
    }

    private static void WriteJsonl(string path, IEnumerable<EventEnvelope> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, ProgramJson.CompactOptions));
    }
}

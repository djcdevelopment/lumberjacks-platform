using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyNetworkSense;

namespace AuthorityLab;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            return args[0].ToLowerInvariant() switch
            {
                "generate" => Generate(Options.Parse(args[1..])),
                "run" => Run(Options.Parse(args[1..])),
                "compare" => Compare(Options.Parse(args[1..])),
                "check" => Check(Options.Parse(args[1..])),
                _ => Usage()
            };
        }
        catch (ScenarioException ex)
        {
            Console.Error.WriteLine($"scenario rejected: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"authority-lab failed: {ex.Message}");
            return 1;
        }
    }

    private static int Generate(Options options)
    {
        var scenarioPath = options.Required("scenario");
        var output = options.Required("output");
        var scenario = Scenario.Load(scenarioPath);
        Directory.CreateDirectory(output);
        var normalized = scenario.NormalizedJson();
        File.WriteAllText(Path.Combine(output, "normalized-input.json"), normalized + Environment.NewLine, Encoding.UTF8);
        File.WriteAllText(Path.Combine(output, "normalized-input.sha256"), Hashing.Sha256(normalized) + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"generated {scenario.ExperimentId}/{scenario.ScenarioId} -> {output}");
        return 0;
    }

    private static int Run(Options options)
    {
        var scenarioPath = options.Required("scenario");
        var output = options.Required("output");
        var forceTimeout = options.Has("force-timeout");
        var sourceRevision = options.Value("source-revision") ?? Environment.GetEnvironmentVariable("AUTHORITY_LAB_SOURCE_REVISION") ?? "working_tree";
        var scenario = Scenario.Load(scenarioPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "raw"));

        var normalized = scenario.NormalizedJson();
        File.WriteAllText(Path.Combine(output, "normalized-input.json"), normalized + Environment.NewLine, Encoding.UTF8);
        var runId = Path.GetFileName(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var events = AuthorityEngine.Execute(scenario, forceTimeout);
        foreach (var eventEnvelope in events.Events) eventEnvelope.RunId = runId;
        var eventsPath = Path.Combine(output, "raw", "events.jsonl");
        using (var writer = new StreamWriter(eventsPath, false, new UTF8Encoding(false)))
        {
            foreach (var item in events.Events)
                writer.WriteLine(JsonSerializer.Serialize(item, ProgramJson.CompactOptions));
        }

        var receipt = new Receipt
        {
            SchemaVersion = 1,
            ExperimentId = scenario.ExperimentId,
            ScenarioId = scenario.ScenarioId,
            RunId = runId,
            SourceRevision = sourceRevision,
            DirtyState = sourceRevision == "working_tree",
            ScenarioSha256 = Hashing.File(scenarioPath),
            NormalizedInputSha256 = Hashing.Sha256(normalized),
            Policy = scenario.Policy,
            Driver = scenario.Driver,
            Seed = scenario.Seed,
            StartedUtc = events.StartedUtc,
            EndedUtc = events.EndedUtc,
            DurationSeconds = events.DurationSeconds,
            StopResult = events.StopResult,
            EventCounts = events.Events.GroupBy(e => e.EventType).ToDictionary(g => g.Key, g => g.Count()),
            Invariants = events.Invariants,
            PredictionObservations = events.PredictionObservations,
            RawEvidencePaths = new[] { "normalized-input.json", "raw/events.jsonl" },
            NormalizedDecisionSha256 = Hashing.Sha256(events.NormalizedDecisionJson),
            ResultClassification = events.ResultClassification
        };
        File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(receipt, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "raw", "normalized-decisions.json"), events.NormalizedDecisionJson + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "summary.md"), SummaryMarkdown(receipt), new UTF8Encoding(false));
        Console.WriteLine($"run {receipt.ExperimentId}/{receipt.ScenarioId}: {receipt.ResultClassification}; events={events.Events.Count}; receipt={Path.Combine(output, "receipt.json")}");
        return 0;
    }

    private static int Compare(Options options)
    {
        var left = ReadReceipt(options.Required("left"));
        var right = ReadReceipt(options.Required("right"));
        var output = options.Required("output");
        Directory.CreateDirectory(output);
        var equal = left.NormalizedDecisionSha256 == right.NormalizedDecisionSha256 && left.NormalizedInputSha256 == right.NormalizedInputSha256;
        var result = new
        {
            schema_version = 1,
            comparison = "normalized_decisions",
            left_run_id = left.RunId,
            right_run_id = right.RunId,
            normalized_input_equal = left.NormalizedInputSha256 == right.NormalizedInputSha256,
            normalized_decisions_equal = left.NormalizedDecisionSha256 == right.NormalizedDecisionSha256,
            result = equal ? "equal" : "different"
        };
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"compare: {(equal ? "equal" : "different")}");
        return equal ? 0 : 3;
    }

    private static int Check(Options options)
    {
        var run = options.Required("run");
        var receiptPath = Path.Combine(run, "receipt.json");
        if (!File.Exists(receiptPath)) throw new InvalidDataException($"missing receipt: {receiptPath}");
        var receipt = ReadReceipt(receiptPath);
        foreach (var required in new[] { receipt.ExperimentId, receipt.ScenarioId, receipt.RunId, receipt.Driver, receipt.StopResult, receipt.NormalizedDecisionSha256 })
            if (string.IsNullOrWhiteSpace(required)) throw new InvalidDataException("receipt has an empty required field");
        var eventsPath = Path.Combine(run, "raw", "events.jsonl");
        if (!File.Exists(eventsPath)) throw new InvalidDataException($"missing event stream: {eventsPath}");
        var lineNumber = 0;
        foreach (var line in File.ReadLines(eventsPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"blank/truncated event at line {lineNumber}");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            foreach (var name in new[] { "schema_version", "event_id", "timestamp_utc", "event_type", "experiment_id", "scenario_id", "run_id", "seed", "tick", "driver", "payload" })
                if (!root.TryGetProperty(name, out _)) throw new InvalidDataException($"event line {lineNumber} missing {name}");
        }
        Console.WriteLine($"check: valid receipt and {lineNumber} complete event(s)");
        return 0;
    }

    private static Receipt ReadReceipt(string pathOrRun)
    {
        var path = Directory.Exists(pathOrRun) ? Path.Combine(pathOrRun, "receipt.json") : pathOrRun;
        return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"empty receipt: {path}");
    }

    private static string SummaryMarkdown(Receipt receipt)
    {
        var lines = new List<string>
        {
            $"# {receipt.ExperimentId} / {receipt.ScenarioId}", "",
            $"- Run: `{receipt.RunId}`", $"- Driver: `{receipt.Driver}`", $"- Seed: `{receipt.Seed}`",
            $"- Classification: `{receipt.ResultClassification}`", $"- Stop: `{receipt.StopResult}`", "",
            "## Invariants", ""
        };
        foreach (var invariant in receipt.Invariants) lines.Add($"- {(invariant.Passed ? "PASS" : "FAIL")} `{invariant.Name}` — {invariant.Detail}");
        lines.AddRange(new[] { "", "## Prediction observations", "" });
        foreach (var observation in receipt.PredictionObservations) lines.Add($"- `{observation.Name}`: {observation.Observed}");
        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: authority-lab <generate|run|compare|check> --scenario/--run ...");
        return 2;
    }
}

public sealed class Options
{
    private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ScenarioException($"unexpected argument `{args[i]}`");
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) result.values[key] = args[++i];
            else result.values[key] = null;
        }
        return result;
    }
    public bool Has(string key) => values.ContainsKey(key);
    public string? Value(string key) => values.TryGetValue(key, out var value) ? value : null;
    public string Required(string key) => Value(key) ?? throw new ScenarioException($"missing --{key}");
}

public sealed class Scenario
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("experiment_id")] public string ExperimentId { get; init; } = "";
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; init; } = "";
    [JsonPropertyName("seed")] public int Seed { get; init; }
    [JsonPropertyName("plane")] public string Plane { get; init; } = "";
    [JsonPropertyName("duration_seconds")] public int DurationSeconds { get; init; }
    [JsonPropertyName("actors")] public List<Actor> Actors { get; init; } = new();
    [JsonPropertyName("objects")] public ObjectFixture Objects { get; init; } = new();
    [JsonPropertyName("policy")] public PolicyConfig Policy { get; init; } = new();
    [JsonPropertyName("driver")] public string Driver { get; init; } = "";
    [JsonPropertyName("stop_rules")] public List<string> StopRules { get; init; } = new();
    [JsonPropertyName("parameters")] public Dictionary<string, JsonElement> Parameters { get; init; } = new();

    public static Scenario Load(string path)
    {
        if (!File.Exists(path)) throw new ScenarioException($"scenario not found: {path}");
        try
        {
            var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), ProgramJson.Options) ?? throw new ScenarioException("empty scenario");
            if (scenario.SchemaVersion != 1) throw new ScenarioException("schema_version must be 1");
            if (string.IsNullOrWhiteSpace(scenario.ExperimentId) || string.IsNullOrWhiteSpace(scenario.ScenarioId)) throw new ScenarioException("experiment_id and scenario_id are required");
            if (scenario.Seed < 0) throw new ScenarioException("seed must be non-negative");
            if (scenario.DurationSeconds <= 0 || scenario.DurationSeconds > 300) throw new ScenarioException("duration_seconds must be between 1 and 300");
            if (!new[] { "pure", "gateway", "replay", "local_valheim_shadow", "local_valheim_strict", "p7_shadow", "p7_canary" }.Contains(scenario.Driver, StringComparer.OrdinalIgnoreCase)) throw new ScenarioException($"unknown driver: {scenario.Driver}");
            if (scenario.Actors.Count == 0) throw new ScenarioException("at least one actor is required");
            return scenario;
        }
        catch (JsonException ex) { throw new ScenarioException($"scenario must be JSON-compatible YAML: {ex.Message}"); }
    }

    public string NormalizedJson() => JsonSerializer.Serialize(this, ProgramJson.NormalizedOptions);

    public int IntParameter(string name, int fallback) => Parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : fallback;
    public double DoubleParameter(string name, double fallback) => Parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) ? result : fallback;
}

public sealed class Actor
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("trajectory")] public string Trajectory { get; init; } = "";
}

public sealed class ObjectFixture
{
    [JsonPropertyName("generator")] public string Generator { get; init; } = "";
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("classes")] public List<string> Classes { get; init; } = new();
}

public sealed class PolicyConfig
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("near_meters")] public double NearMeters { get; init; } = 30;
    [JsonPropertyName("outer_meters")] public double OuterMeters { get; init; } = 64;
    [JsonPropertyName("mid_hz")] public double MidHz { get; init; } = 5;
}

public sealed class EventEnvelope
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("event_id")] public string EventId { get; init; } = "";
    [JsonPropertyName("timestamp_utc")] public string TimestampUtc { get; init; } = "";
    [JsonPropertyName("event_type")] public string EventType { get; init; } = "authority.lumberjacks_decision";
    [JsonPropertyName("experiment_id")] public string ExperimentId { get; init; } = "";
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; init; } = "";
    [JsonPropertyName("run_id")] public string RunId { get; set; } = "lab";
    [JsonPropertyName("seed")] public int Seed { get; init; }
    [JsonPropertyName("tick")] public int Tick { get; init; }
    [JsonPropertyName("driver")] public string Driver { get; init; } = "pure";
    [JsonPropertyName("payload")] public Dictionary<string, object?> Payload { get; init; } = new();
}

public sealed class Receipt
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("experiment_id")] public string ExperimentId { get; init; } = "";
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; init; } = "";
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("source_revision")] public string SourceRevision { get; init; } = "";
    [JsonPropertyName("dirty_state")] public bool DirtyState { get; init; }
    [JsonPropertyName("scenario_sha256")] public string ScenarioSha256 { get; init; } = "";
    [JsonPropertyName("normalized_input_sha256")] public string NormalizedInputSha256 { get; init; } = "";
    [JsonPropertyName("policy")] public PolicyConfig Policy { get; init; } = new();
    [JsonPropertyName("driver")] public string Driver { get; init; } = "";
    [JsonPropertyName("seed")] public int Seed { get; init; }
    [JsonPropertyName("started_utc")] public string StartedUtc { get; init; } = "";
    [JsonPropertyName("ended_utc")] public string EndedUtc { get; init; } = "";
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("stop_result")] public string StopResult { get; init; } = "";
    [JsonPropertyName("event_counts")] public Dictionary<string, int> EventCounts { get; init; } = new();
    [JsonPropertyName("invariants")] public List<InvariantResult> Invariants { get; init; } = new();
    [JsonPropertyName("prediction_observations")] public List<PredictionObservation> PredictionObservations { get; init; } = new();
    [JsonPropertyName("raw_evidence_paths")] public string[] RawEvidencePaths { get; init; } = Array.Empty<string>();
    [JsonPropertyName("normalized_decision_sha256")] public string NormalizedDecisionSha256 { get; init; } = "";
    [JsonPropertyName("result_classification")] public string ResultClassification { get; init; } = "pending";
}

public sealed class InvariantResult
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("passed")] public bool Passed { get; init; }
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
}

public sealed class PredictionObservation
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("observed")] public string Observed { get; init; } = "";
}

public sealed class ScenarioException(string message) : Exception(message);

internal static class ProgramJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static readonly JsonSerializerOptions NormalizedOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DictionaryKeyPolicy = null };
    public static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DictionaryKeyPolicy = null };
}

internal static class Hashing
{
    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string File(string path) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))).ToLowerInvariant();
}

internal sealed class EngineResult
{
    public List<EventEnvelope> Events { get; init; } = new();
    public List<InvariantResult> Invariants { get; init; } = new();
    public List<PredictionObservation> PredictionObservations { get; init; } = new();
    public string StartedUtc { get; init; } = "";
    public string EndedUtc { get; init; } = "";
    public double DurationSeconds { get; init; }
    public string StopResult { get; init; } = "completed";
    public string ResultClassification { get; init; } = "pending";
    public string NormalizedDecisionJson { get; init; } = "[]";
}

internal static class AuthorityEngine
{
    public static EngineResult Execute(Scenario scenario, bool forceTimeout)
    {
        var started = DateTimeOffset.UtcNow;
        var events = new List<EventEnvelope>();
        var invariants = new List<InvariantResult>();
        var predictions = new List<PredictionObservation>();
        switch (scenario.ExperimentId.ToLowerInvariant())
        {
            case "m7-e00-lab-truth": ExecuteE00(scenario, events, invariants, predictions); break;
            case "m7-e01-relevance-shape": ExecuteE01(scenario, events, invariants, predictions); break;
            case "m7-e02-recipient-fanout": ExecuteE02(scenario, events, invariants, predictions); break;
            case "m7-e03-motion-fingerprints": ExecuteE03(scenario, events, invariants, predictions); break;
            default: throw new ScenarioException($"no pure executor for {scenario.ExperimentId}");
        }
        var ended = DateTimeOffset.UtcNow;
        var normalized = JsonSerializer.Serialize(events.Select(e => new { e.SchemaVersion, e.EventType, e.ExperimentId, e.ScenarioId, e.Seed, e.Tick, e.Driver, e.Payload }), ProgramJson.NormalizedOptions);
        var allPassed = invariants.All(i => i.Passed);
        return new EngineResult
        {
            Events = events,
            Invariants = invariants,
            PredictionObservations = predictions,
            StartedUtc = started.ToString("o"),
            EndedUtc = ended.ToString("o"),
            DurationSeconds = Math.Max((ended - started).TotalSeconds, 0.000001),
            StopResult = forceTimeout ? "timeout" : "completed",
            ResultClassification = forceTimeout ? "inconclusive" : (allPassed ? "supported" : "refuted"),
            NormalizedDecisionJson = normalized
        };
    }

    private static EventEnvelope Event(Scenario s, int tick, string id, Dictionary<string, object?> payload) => new()
    {
        EventId = id,
        TimestampUtc = "1970-01-01T00:00:00.0000000+00:00",
        ExperimentId = s.ExperimentId,
        ScenarioId = s.ScenarioId,
        Seed = s.Seed,
        Tick = tick,
        Driver = s.Driver,
        Payload = payload
    };

    private static void ExecuteE00(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var count = Math.Max(4, s.Objects.Count);
        for (var i = 0; i < count; i++)
        {
            var distance = 10 + ((s.Seed * 17 + i * 13) % 70);
            var action = ZdoBandPolicy.Classify(distance, s.Policy.NearMeters, s.Policy.OuterMeters, 0, i, -1, 1 / s.Policy.MidHz);
            events.Add(Event(s, i, $"e00-{i:000}", new Dictionary<string, object?>
            {
                ["observer_id"] = s.Actors[0].Id,
                ["object_id"] = $"object-{i:000}",
                ["distance_meters"] = distance,
                ["action"] = action.ToString(),
                ["emits"] = ZdoBandPolicy.Emits(action)
            }));
        }
        invariants.Add(new() { Name = "deterministic_event_count", Passed = events.Count == count, Detail = $"expected={count}; observed={events.Count}" });
        invariants.Add(new() { Name = "snake_case_payload", Passed = events.All(e => e.Payload.Keys.All(k => k == k.ToLowerInvariant() && !k.Contains('-'))), Detail = "payload keys are lowercase" });
        predictions.Add(new() { Name = "same_seed_same_input", Observed = "repeated runs should produce byte-identical normalized decisions" });
    }

    private static void ExecuteE01(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var distances = new[] { 29.9, 30.0, 30.1, 63.9, 64.0, 64.1 };
        var densities = new[] { 1, 2, 4 };
        var emittedByDensity = new Dictionary<int, int>();
        var tick = 0;
        foreach (var density in densities)
        {
            var emitted = 0;
            foreach (var distance in distances)
            {
                for (var copy = 0; copy < density; copy++)
                {
                    var action = ZdoBandPolicy.Classify(distance, s.Policy.NearMeters, s.Policy.OuterMeters, 0, 10, -1, 1 / s.Policy.MidHz);
                    var emits = ZdoBandPolicy.Emits(action);
                    if (emits) emitted++;
                    events.Add(Event(s, tick++, $"e01-{tick:0000}", new Dictionary<string, object?>
                    {
                        ["observer_id"] = s.Actors[0].Id,
                        ["object_id"] = $"density-{density}-distance-{distance:0.0}-copy-{copy}",
                        ["distance_meters"] = distance,
                        ["density_multiplier"] = density,
                        ["band"] = action.ToString(),
                        ["emits"] = emits
                    }));
                }
            }
            emittedByDensity[density] = emitted;
        }
        var densityMonotonic = emittedByDensity[1] <= emittedByDensity[2] && emittedByDensity[2] <= emittedByDensity[4];
        invariants.Add(new() { Name = "density_response_monotonic", Passed = densityMonotonic, Detail = string.Join(",", emittedByDensity.Select(kvp => $"{kvp.Key}x={kvp.Value}")) });
        var expected = new[] { "EmitFull", "EmitFull", "EmitThinned", "EmitThinned", "EmitThinned", "Drop" };
        var observed = distances.Select(d => ZdoBandPolicy.Classify(d, 30, 64, 0, 10, -1, .2).ToString()).ToArray();
        invariants.Add(new() { Name = "boundary_shape", Passed = observed.SequenceEqual(expected), Detail = $"observed={string.Join(",", observed)}" });
        predictions.Add(new() { Name = "radius_and_density_direction", Observed = "near and mid objects emit; far objects drop; higher density increases emitted decisions" });
        predictions.Add(new() { Name = "boundary_chatter", Observed = "clean crossings are monotonic; repeated noisy samples are retained for later chatter analysis" });
    }

    private static void ExecuteE02(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var totals = new Dictionary<int, int>();
        foreach (var n in new[] { 2, 10, 100 })
        {
            var observers = Enumerable.Range(0, n).Select(i => new FanoutObserverInput(
                i == n - 1 ? "observer-000" : $"observer-{i:000}",
                i % 3 == 0 ? 20 : (i % 3 == 1 ? 45 : 90),
                i == 1 ? 410L : null)).ToArray();
            var decisions = ZdoFanoutPlan.Evaluate(410, 30, 64, 0, observers);
            totals[n] = decisions.Count(d => d.Emit);
            foreach (var (decision, index) in decisions.Select((d, i) => (d, i)))
            {
                events.Add(Event(s, n * 1000 + index, $"e02-{n:000}-{index:000}", new Dictionary<string, object?>
                {
                    ["observer_count"] = n,
                    ["recipient"] = decision.Recipient,
                    ["distance_meters"] = decision.DistanceMeters,
                    ["disposition"] = decision.Disposition.ToString(),
                    ["emit"] = decision.Emit,
                    ["data_revision"] = 410
                }));
            }
        }
        var duplicateFreePerObserverCount = events
            .Where(e => e.Payload.TryGetValue("emit", out var value) && value is true)
            .GroupBy(e => (int)e.Payload["observer_count"]!)
            .All(group => group.Select(e => (string)e.Payload["recipient"]!).Distinct(StringComparer.Ordinal).Count() == group.Count());
        invariants.Add(new() { Name = "recipient_isolation", Passed = duplicateFreePerObserverCount, Detail = "each recipient/revision has at most one terminal emit within each observer-count case" });
        invariants.Add(new() { Name = "fanout_scales_with_observers", Passed = totals[2] < totals[10] && totals[10] < totals[100], Detail = string.Join(",", totals.Select(kvp => $"n={kvp.Key}:{kvp.Value}")) });
        invariants.Add(new() { Name = "already_delivered_is_local", Passed = events.Any(e => e.Payload.TryGetValue("disposition", out var d) && Equals(d, "AlreadyDelivered")), Detail = "one observer starts at revision 410" });
        predictions.Add(new() { Name = "cross_recipient_activity", Observed = "none expected; slow, duplicate, and already-delivered observers remain local" });
        predictions.Add(new() { Name = "scaling_shape", Observed = "emissions grow with in-band observers; this is not a 100-player capacity claim" });
    }

    private static void ExecuteE03(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var patterns = s.Actors.Select(a => a.Trajectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fingerprints = new Dictionary<string, double>();
        foreach (var pattern in patterns)
        {
            var position = (x: 0d, y: 0d);
            var previous = position;
            var errorTotal = 0d;
            var samples = 20;
            for (var tick = 0; tick < samples; tick++)
            {
                var next = Motion(pattern, tick, position);
                var dx = next.x - position.x;
                var dy = next.y - position.y;
                var velocity = (x: position.x - previous.x, y: position.y - previous.y);
                var predicted = (position.x + velocity.x, position.y + velocity.y);
                var correction = Math.Sqrt(Math.Pow(next.x - predicted.Item1, 2) + Math.Pow(next.y - predicted.Item2, 2));
                errorTotal += correction;
                events.Add(Event(s, tick, $"e03-{pattern}-{tick:000}", new Dictionary<string, object?>
                {
                    ["pattern"] = pattern,
                    ["input_interval_ms"] = 50,
                    ["output_interval_ms"] = pattern.Equals("stutter_north", StringComparison.OrdinalIgnoreCase) && tick % 3 == 0 ? 150 : 50,
                    ["x"] = next.x,
                    ["y"] = next.y,
                    ["correction_meters"] = correction,
                    ["sequence_lag"] = pattern.Equals("stutter_north", StringComparison.OrdinalIgnoreCase) && tick % 3 == 0 ? 2 : 0
                }));
                previous = position;
                position = next;
            }
            fingerprints[pattern] = errorTotal;
        }
        invariants.Add(new() { Name = "motion_patterns_present", Passed = patterns.Length >= 4, Detail = $"patterns={string.Join(",", patterns)}" });
        invariants.Add(new() { Name = "motion_fingerprints_distinguishable", Passed = fingerprints.Values.Distinct().Count() >= Math.Min(4, fingerprints.Count), Detail = string.Join(",", fingerprints.Select(kvp => $"{kvp.Key}={kvp.Value:0.###}")) });
        predictions.Add(new() { Name = "cadence_vs_interpolation", Observed = "stutter has larger output intervals and sequence lag; this does not identify the live interpolation cause" });
        predictions.Add(new() { Name = "transport_ordering", Observed = "pure driver preserves logical event order; UDP/WebSocket comparison is deferred to Gateway driver" });
    }

    private static (double x, double y) Motion(string pattern, int tick, (double x, double y) current) => pattern.ToLowerInvariant() switch
    {
        "straight_north" => (current.x, current.y + 1),
        "stutter_north" => (current.x, current.y + (tick % 3 == 0 ? 0 : 1)),
        "stop_start" => (current.x, current.y + (tick is >= 5 and < 10 ? 1 : 0)),
        "turn_90" => tick < 10 ? (current.x + 1, current.y) : (current.x, current.y + 1),
        "circle" => (Math.Cos(tick * Math.PI / 5), Math.Sin(tick * Math.PI / 5)),
        "teleport" => tick == 10 ? (current.x + 20, current.y + 20) : (current.x + .2, current.y + .2),
        _ => (current.x + 1, current.y)
    };
}

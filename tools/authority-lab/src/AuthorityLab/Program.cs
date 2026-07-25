using System.Security.Cryptography;
using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComfyNetworkSense;
using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Game.Gateway.Valheim;
using Game.Gateway.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
                "normalize-native" => NativeCandidateNormalizer.Execute(Options.Parse(args[1..])),
                "replay-native" => NativeCandidateReplay.Execute(Options.Parse(args[1..])),
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
        var dirtyState = options.Has("dirty-state") || sourceRevision == "working_tree";
        var scenario = Scenario.Load(scenarioPath);
        var requestedDriver = options.Value("driver");
        if (!string.IsNullOrWhiteSpace(requestedDriver)) scenario.SetDriver(requestedDriver);
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
            DirtyState = dirtyState,
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
        var normalizedInputPath = Path.Combine(run, "normalized-input.json");
        if (!File.Exists(normalizedInputPath)) throw new InvalidDataException($"missing normalized input: {normalizedInputPath}");
        var normalizedInput = File.ReadAllText(normalizedInputPath).TrimEnd('\r', '\n');
        if (!string.Equals(Hashing.Sha256(normalizedInput), receipt.NormalizedInputSha256, StringComparison.Ordinal))
            throw new InvalidDataException("normalized input hash does not match receipt");
        var decisionsPath = Path.Combine(run, "raw", "normalized-decisions.json");
        if (!File.Exists(decisionsPath)) throw new InvalidDataException($"missing normalized decisions: {decisionsPath}");
        var normalizedDecisions = File.ReadAllText(decisionsPath).TrimEnd('\r', '\n');
        if (!string.Equals(Hashing.Sha256(normalizedDecisions), receipt.NormalizedDecisionSha256, StringComparison.Ordinal))
            throw new InvalidDataException("normalized decision hash does not match receipt");
        var eventsPath = Path.Combine(run, "raw", "events.jsonl");
        if (!File.Exists(eventsPath)) throw new InvalidDataException($"missing event stream: {eventsPath}");
        var lineNumber = 0;
        var eventCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(eventsPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException($"blank/truncated event at line {lineNumber}");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            foreach (var name in new[] { "schema_version", "event_id", "timestamp_utc", "event_type", "experiment_id", "scenario_id", "run_id", "seed", "tick", "driver", "payload" })
                if (!root.TryGetProperty(name, out _)) throw new InvalidDataException($"event line {lineNumber} missing {name}");
            if (root.GetProperty("run_id").GetString() != receipt.RunId ||
                root.GetProperty("experiment_id").GetString() != receipt.ExperimentId ||
                root.GetProperty("scenario_id").GetString() != receipt.ScenarioId ||
                root.GetProperty("driver").GetString() != receipt.Driver)
                throw new InvalidDataException($"event line {lineNumber} identity does not match receipt");
            var eventType = root.GetProperty("event_type").GetString() ?? "";
            eventCounts[eventType] = eventCounts.GetValueOrDefault(eventType) + 1;
        }
        if (!eventCounts.OrderBy(pair => pair.Key).SequenceEqual(receipt.EventCounts.OrderBy(pair => pair.Key)))
            throw new InvalidDataException("event counts do not match receipt");
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
        Console.Error.WriteLine("usage: authority-lab <generate|run|normalize-native|replay-native|compare|check> --scenario/--run ...");
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
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";
    [JsonPropertyName("stop_rules")] public List<string> StopRules { get; init; } = new();
    [JsonPropertyName("parameters")] public Dictionary<string, JsonElement> Parameters { get; init; } = new();

    public static Scenario Load(string path)
    {
        if (!File.Exists(path)) throw new ScenarioException($"scenario not found: {path}");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ScenarioException("scenario root must be an object");
            var requiredProperties = new[] { "schema_version", "experiment_id", "scenario_id", "seed", "plane", "duration_seconds", "actors", "objects", "policy", "driver", "stop_rules" };
            foreach (var property in requiredProperties)
                if (!root.TryGetProperty(property, out _)) throw new ScenarioException($"missing required field: {property}");
            var allowedProperties = requiredProperties.Append("parameters").ToHashSet(StringComparer.Ordinal);
            var unknown = root.EnumerateObject().Select(property => property.Name).Where(name => !allowedProperties.Contains(name)).ToArray();
            if (unknown.Length > 0) throw new ScenarioException($"unknown root field(s): {string.Join(",", unknown)}");
            var scenario = JsonSerializer.Deserialize<Scenario>(root.GetRawText(), ProgramJson.Options) ?? throw new ScenarioException("empty scenario");
            if (scenario.SchemaVersion != 1) throw new ScenarioException("schema_version must be 1");
            if (string.IsNullOrWhiteSpace(scenario.ExperimentId) || string.IsNullOrWhiteSpace(scenario.ScenarioId)) throw new ScenarioException("experiment_id and scenario_id are required");
            if (scenario.Seed < 0) throw new ScenarioException("seed must be non-negative");
            if (scenario.DurationSeconds <= 0 || scenario.DurationSeconds > 300) throw new ScenarioException("duration_seconds must be between 1 and 300");
            if (!new[] { "pure", "gateway", "gateway_durable", "gateway_udp", "replay", "local_valheim_shadow", "local_valheim_strict", "p7_shadow", "p7_canary" }.Contains(scenario.Driver, StringComparer.OrdinalIgnoreCase)) throw new ScenarioException($"unknown driver: {scenario.Driver}");
            if (!new[] { "relevance", "replication", "ownership", "motion", "rpc", "runtime" }.Contains(scenario.Plane, StringComparer.OrdinalIgnoreCase)) throw new ScenarioException($"unknown plane: {scenario.Plane}");
            if (scenario.Actors.Count == 0) throw new ScenarioException("at least one actor is required");
            return scenario;
        }
        catch (JsonException ex) { throw new ScenarioException($"scenario must be JSON-compatible YAML: {ex.Message}"); }
    }

    public string NormalizedJson() => JsonSerializer.Serialize(this, ProgramJson.NormalizedOptions);

    public void SetDriver(string driver)
    {
        if (!AllowedDrivers.Contains(driver, StringComparer.OrdinalIgnoreCase)) throw new ScenarioException($"unknown driver: {driver}");
        Driver = driver;
    }

    private static readonly string[] AllowedDrivers = ["pure", "gateway", "gateway_durable", "gateway_udp", "replay", "local_valheim_shadow", "local_valheim_strict", "p7_shadow", "p7_canary"];

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
            case "m7-e02-recipient-fanout":
                if (scenario.Driver.Equals("gateway_durable", StringComparison.OrdinalIgnoreCase)) ExecuteGatewayE02Durable(scenario, events, invariants, predictions);
                else if (scenario.Driver.Equals("gateway", StringComparison.OrdinalIgnoreCase)) ExecuteGatewayE02(scenario, events, invariants, predictions);
                else ExecuteE02(scenario, events, invariants, predictions);
                break;
            case "m7-e03-motion-fingerprints":
                if (scenario.Driver.Equals("gateway_udp", StringComparison.OrdinalIgnoreCase)) ExecuteGatewayE03Udp(scenario, events, invariants, predictions);
                else if (scenario.Driver.Equals("gateway", StringComparison.OrdinalIgnoreCase)) ExecuteGatewayE03(scenario, events, invariants, predictions);
                else ExecuteE03(scenario, events, invariants, predictions);
                break;
            case "cre-e01-runtime-envelope":
                ExecuteCreativeRuntimeEnvelope(scenario, events, invariants, predictions);
                break;
            case "cre-e02-gateway-pressure-route":
                if (scenario.Driver.Equals("gateway_udp", StringComparison.OrdinalIgnoreCase))
                    ExecuteCreativeRuntimeGateway(scenario, events, invariants, predictions, useUdp: true);
                else if (scenario.Driver.Equals("gateway", StringComparison.OrdinalIgnoreCase))
                    ExecuteCreativeRuntimeGateway(scenario, events, invariants, predictions, useUdp: false);
                else
                    throw new ScenarioException("cre-e02 requires driver gateway or gateway_udp");
                break;
            case "cre-e03-transport-faults":
                if (scenario.Driver.Equals("gateway_udp", StringComparison.OrdinalIgnoreCase))
                    ExecuteCreativeRuntimeTransportFaults(scenario, events, invariants, predictions, useUdp: true);
                else if (scenario.Driver.Equals("gateway", StringComparison.OrdinalIgnoreCase))
                    ExecuteCreativeRuntimeTransportFaults(scenario, events, invariants, predictions, useUdp: false);
                else
                    throw new ScenarioException("cre-e03 requires driver gateway or gateway_udp");
                break;
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

    private static EventEnvelope Event(
        Scenario s,
        int tick,
        string id,
        Dictionary<string, object?> payload,
        string eventType = "authority.lumberjacks_decision") => new()
    {
        EventId = id,
        TimestampUtc = "1970-01-01T00:00:00.0000000+00:00",
        EventType = eventType,
        ExperimentId = s.ExperimentId,
        ScenarioId = s.ScenarioId,
        Seed = s.Seed,
        Tick = tick,
        Driver = s.Driver,
        Payload = payload
    };

    private static void ExecuteCreativeRuntimeEnvelope(
        Scenario s,
        List<EventEnvelope> events,
        List<InvariantResult> invariants,
        List<PredictionObservation> predictions)
    {
        var deferredCapacity = s.IntParameter("deferred_capacity", 4);
        var bands = RuntimePressureBands(s);
        var byBand = new Dictionary<string, IReadOnlyList<RuntimeGateDecision>>(StringComparer.Ordinal);

        foreach (var (band, bandIndex) in bands.Select((value, index) => (value, index)))
        {
            var decisions = RuntimeEnvelopePolicy.Evaluate(
                RuntimeRequests(band),
                band.BudgetUnits,
                deferredCapacity);
            byBand[band.Name] = decisions;
            AppendRuntimeGateEvents(s, events, band, bandIndex, decisions, "cre-e01");
        }

        var allDecisions = byBand.Values.SelectMany(value => value).ToArray();
        var criticalPreserved = allDecisions
            .Where(decision => decision.Request.Criticality == "critical")
            .All(decision =>
                decision.SelectedMode == "full" &&
                decision.Transport == "binary_websocket" &&
                decision.BudgetRemainingUnits >= 0);
        var budgetsBounded = allDecisions.All(decision => decision.BudgetRemainingUnits >= 0);
        var routesMatchSemantics = allDecisions.All(decision =>
            decision.SelectedMode is "deferred" or "dropped"
                ? decision.Transport == "none"
                : decision.Request.Criticality == "critical"
                    ? decision.Transport == "binary_websocket"
                    : decision.Transport == "session_udp" &&
                      decision.Request.FallbackTransport == "binary_websocket");
        var selectedModes = allDecisions.Select(decision => decision.SelectedMode).ToHashSet(StringComparer.Ordinal);
        var allModesObserved = new[] { "full", "reduced", "deferred", "dropped" }
            .All(selectedModes.Contains);
        var greenFull = PresentationCount(byBand["green"], "full");
        var amberFull = PresentationCount(byBand["amber"], "full");
        var redFull = PresentationCount(byBand["red"], "full");
        var greenDegraded = DegradedCount(byBand["green"]);
        var amberDegraded = DegradedCount(byBand["amber"]);
        var redDegraded = DegradedCount(byBand["red"]);
        var degradationMonotonic =
            greenFull > amberFull &&
            amberFull > redFull &&
            greenDegraded < amberDegraded &&
            amberDegraded < redDegraded;

        invariants.Add(new()
        {
            Name = "critical_work_preserved",
            Passed = criticalPreserved,
            Detail = "player_death and projectile_hit remained full on binary WebSocket in every pressure band"
        });
        invariants.Add(new()
        {
            Name = "budget_never_negative",
            Passed = budgetsBounded,
            Detail = $"minimum_remaining={allDecisions.Min(decision => decision.BudgetRemainingUnits)}"
        });
        invariants.Add(new()
        {
            Name = "transport_follows_semantics",
            Passed = routesMatchSemantics,
            Detail = "critical mutations used binary WebSocket; emitted presentation used session UDP with binary WebSocket fallback"
        });
        invariants.Add(new()
        {
            Name = "all_degradation_modes_observed",
            Passed = allModesObserved,
            Detail = $"modes={string.Join(",", selectedModes.OrderBy(value => value, StringComparer.Ordinal))}"
        });
        invariants.Add(new()
        {
            Name = "degradation_tracks_pressure",
            Passed = degradationMonotonic,
            Detail = $"presentation_full={greenFull},{amberFull},{redFull}; degraded={greenDegraded},{amberDegraded},{redDegraded}"
        });
        invariants.Add(new()
        {
            Name = "deferred_queue_bounded",
            Passed = allDecisions.Max(decision => decision.DeferredDepth) <= deferredCapacity,
            Detail = $"capacity={deferredCapacity}; observed_max={allDecisions.Max(decision => decision.DeferredDepth)}"
        });

        predictions.Add(new()
        {
            Name = "selective_degradation",
            Observed = $"green/amber/red presentation full counts were {greenFull}/{amberFull}/{redFull}; protected mutations stayed full"
        });
        predictions.Add(new()
        {
            Name = "explainable_transport",
            Observed = "every decision records requested mode, selected mode, reason, cost, route, fallback, and remaining budget"
        });
    }

    private static void ExecuteCreativeRuntimeGateway(
        Scenario s,
        List<EventEnvelope> events,
        List<InvariantResult> invariants,
        List<PredictionObservation> predictions,
        bool useUdp)
    {
        var fixture = new GatewayMotionFixture(useUdp ? FindFreeUdpPort() : null);
        var source = fixture.CreateSession("region-runtime", "source-a");
        var target = fixture.CreateSession("region-runtime", "target-b");
        using var sourceUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
        using var targetUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
        var deferredCapacity = s.IntParameter("deferred_capacity", 4);
        var bands = RuntimePressureBands(s);
        var expectedRoutedKeys = new HashSet<string>(StringComparer.Ordinal);
        var observedRoutedKeys = new HashSet<string>(StringComparer.Ordinal);
        var udpTargetFrames = new List<byte[]>();
        ushort sequence = 1;

        if (useUdp)
        {
            fixture.Transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var targetBind = BuildMotionFrame(600, (0, 0), sentMilliseconds: 0);
            SendUdp(targetUdp!, target.Session.UdpToken, targetBind.Frame, fixture.Transport.Port);
            if (!SpinWait.SpinUntil(() => target.Session.UdpEndpoint is not null, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("CRE-E02 Gateway UDP target endpoint did not bind");
        }

        try
        {
            foreach (var (band, bandIndex) in bands.Select((value, index) => (value, index)))
            {
                var decisions = RuntimeEnvelopePolicy.Evaluate(
                    RuntimeRequests(band),
                    band.BudgetUnits,
                    deferredCapacity);
                AppendRuntimeGateEvents(s, events, band, bandIndex, decisions, "cre-e02");

                foreach (var decision in decisions.Where(decision =>
                             decision.Request.Criticality == "presentation" &&
                             decision.SelectedMode is "full" or "reduced"))
                {
                    var key = $"{band.Name}/{decision.Request.WorkId}";
                    expectedRoutedKeys.Add(key);
                    var frame = BuildMotionFrame(
                        sequence++,
                        (bandIndex * 100 + expectedRoutedKeys.Count, decision.SelectedMode == "full" ? 1 : .5),
                        sentMilliseconds: bandIndex * 1000 + expectedRoutedKeys.Count * 50);
                    bool delivered;
                    bool targetTokenMatches;

                    if (useUdp)
                    {
                        SendUdp(sourceUdp!, source.Session.UdpToken, frame.Frame, fixture.Transport.Port);
                        var relayed = ReceiveUdp(targetUdp!);
                        udpTargetFrames.Add(relayed);
                        delivered = true;
                        targetTokenMatches =
                            BitConverter.ToUInt64(relayed, 0) == target.Session.UdpToken;
                    }
                    else
                    {
                        var before = target.Socket.SentFrames.Count;
                        fixture.Transport
                            .HandleValheimMotionFrameAsync(
                                source,
                                frame.Header,
                                frame.Payload,
                                frame.Frame,
                                "websocket")
                            .GetAwaiter()
                            .GetResult();
                        delivered = target.Socket.SentFrames.Count == before + 1;
                        targetTokenMatches = true;
                    }

                    if (delivered) observedRoutedKeys.Add(key);
                    events.Add(Event(
                        s,
                        bandIndex,
                        $"cre-e02-route-{band.Name}-{decision.Request.WorkId}",
                        new Dictionary<string, object?>
                        {
                            ["pressure_band"] = band.Name,
                            ["work_id"] = decision.Request.WorkId,
                            ["selected_mode"] = decision.SelectedMode,
                            ["declared_transport"] = decision.Request.PreferredTransport,
                            ["observed_transport"] = useUdp ? "session_udp" : "binary_websocket_fallback",
                            ["sequence"] = (int)frame.Header.Seq,
                            ["delivered"] = delivered,
                            ["target_token_matches"] = targetTokenMatches
                        },
                        "transport.route_observed"));
                }
            }

            var telemetry = fixture.MotionSnapshot();
            var routeEvents = events.Where(item => item.EventType == "transport.route_observed").ToArray();
            var suppressedGateRows = events.Where(item =>
                item.EventType == "performance.gate_decision" &&
                Equals(item.Payload["criticality"], "presentation") &&
                (Equals(item.Payload["selected_mode"], "deferred") ||
                 Equals(item.Payload["selected_mode"], "dropped"))).ToArray();
            var sequenceValues = useUdp
                ? udpTargetFrames
                    .Select(frame => (int)BinaryEnvelope.ReadHeader(frame.AsSpan(UdpTransport.TokenBytes)).Seq)
                    .ToArray()
                : target.Socket.SentFrames
                    .Select(frame => (int)BinaryEnvelope.ReadHeader(frame).Seq)
                    .ToArray();
            var ordered = sequenceValues.SequenceEqual(sequenceValues.OrderBy(value => value));
            var routeSetMatches =
                expectedRoutedKeys.SetEquals(observedRoutedKeys) &&
                routeEvents.Length == expectedRoutedKeys.Count;
            var noSuppressedRoute = suppressedGateRows.All(gate =>
                !observedRoutedKeys.Contains($"{gate.Payload["pressure_band"]}/{gate.Payload["work_id"]}"));
            var deliveryCount = useUdp
                ? telemetry.GetProperty("relayed_udp").GetInt64()
                : telemetry.GetProperty("relayed_websocket").GetInt64();
            var receiveCount = useUdp
                ? telemetry.GetProperty("received_udp").GetInt64()
                : telemetry.GetProperty("received_websocket").GetInt64();
            var expectedReceiveCount = routeEvents.Length + (useUdp ? 1 : 0);
            var telemetryMatches =
                deliveryCount == routeEvents.Length &&
                receiveCount == expectedReceiveCount;
            var tokensMatch = routeEvents.All(item => Equals(item.Payload["target_token_matches"], true));

            invariants.Add(new()
            {
                Name = "gateway_only_routes_selected_presentation",
                Passed = routeSetMatches && noSuppressedRoute,
                Detail = $"selected={expectedRoutedKeys.Count}; observed={observedRoutedKeys.Count}; suppressed={suppressedGateRows.Length}"
            });
            invariants.Add(new()
            {
                Name = useUdp ? "gateway_udp_delivery" : "gateway_websocket_fallback_delivery",
                Passed = telemetryMatches && tokensMatch,
                Detail = $"received={receiveCount}; relayed={deliveryCount}; expected_relayed={routeEvents.Length}"
            });
            invariants.Add(new()
            {
                Name = "gateway_route_sequence_monotonic",
                Passed = ordered,
                Detail = $"sequences={string.Join(",", sequenceValues)}"
            });
            invariants.Add(new()
            {
                Name = "critical_carriage_not_overclaimed",
                Passed = routeEvents.All(item =>
                    !Equals(item.Payload["work_id"], "player_death") &&
                    !Equals(item.Payload["work_id"], "projectile_hit")),
                Detail = "CRE-E02 exercises presentation motion transport only; critical state requires its own reliable-carriage experiment"
            });
            predictions.Add(new()
            {
                Name = "pressure_to_transport",
                Observed = $"{routeEvents.Length} selected presentation decisions reached the real {(useUdp ? "bound UDP" : "WebSocket fallback")} seam; {suppressedGateRows.Length} deferred/dropped decisions did not"
            });
            predictions.Add(new()
            {
                Name = "critical_transport_boundary",
                Observed = "critical mutations remain gate decisions only in CRE-E02; this result makes no death, hit, build, or inventory durability claim"
            });
        }
        finally
        {
            if (useUdp)
                fixture.Transport.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static void ExecuteCreativeRuntimeTransportFaults(
        Scenario s,
        List<EventEnvelope> events,
        List<InvariantResult> invariants,
        List<PredictionObservation> predictions,
        bool useUdp)
    {
        var fixture = new GatewayMotionFixture(useUdp ? FindFreeUdpPort() : null);
        var target = fixture.CreateSession("region-faults", "target-b");
        using var targetUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
        var observations = new List<FaultObservation>();
        var primaryTargetDeliveries = 0;

        if (useUdp)
        {
            fixture.Transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var targetBind = BuildMotionFrame(600, (0, 0), sentMilliseconds: 0, zdoUserId: 900, zdoId: 900);
            SendUdp(targetUdp!, target.Session.UdpToken, targetBind.Frame, fixture.Transport.Port);
            if (!SpinWait.SpinUntil(() => target.Session.UdpEndpoint is not null, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("CRE-E03 Gateway UDP target endpoint did not bind");
        }

        // Endpoint binding is fixture setup, not an observed source attempt. Retain an
        // explicit baseline so transport setup cannot inflate the experiment counters.
        var measurementBaseline = fixture.MotionSnapshot();

        try
        {
            var sourceA = fixture.CreateSession("region-faults", "source-a");
            using var sourceAUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
            RunFaultAttempt("baseline_100", "source_a", 100, true, 100, 200, sourceA, sourceAUdp);
            RunFaultAttempt("baseline_101", "source_a", 101, true, 100, 200, sourceA, sourceAUdp);
            RunFaultAttempt("duplicate_101", "source_a", 101, false, 100, 200, sourceA, sourceAUdp);
            RunFaultAttempt("reordered_99", "source_a", 99, false, 100, 200, sourceA, sourceAUdp);
            RunFaultAttempt("recovery_102", "source_a", 102, true, 100, 200, sourceA, sourceAUdp);
            // Sequence 103 is intentionally never sent. Motion is transient, so the next fresh
            // sample must be accepted without waiting for retransmission.
            RunFaultAttempt("gap_after_loss_104", "source_a", 104, true, 100, 200, sourceA, sourceAUdp);

            var sourceWrap = fixture.CreateSession("region-faults", "source-wrap");
            using var sourceWrapUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
            RunFaultAttempt("wrap_65534", "source_wrap", 65534, true, 101, 201, sourceWrap, sourceWrapUdp);
            RunFaultAttempt("wrap_65535", "source_wrap", 65535, true, 101, 201, sourceWrap, sourceWrapUdp);
            RunFaultAttempt("wrap_0", "source_wrap", 0, true, 101, 201, sourceWrap, sourceWrapUdp);
            RunFaultAttempt("wrap_1", "source_wrap", 1, true, 101, 201, sourceWrap, sourceWrapUdp);
            RunFaultAttempt("old_after_wrap_65535", "source_wrap", 65535, false, 101, 201, sourceWrap, sourceWrapUdp);

            var sourceReconnect = fixture.CreateSession("region-faults", "source-reconnect");
            using var sourceReconnectUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
            RunFaultAttempt("before_reconnect_500", "source_reconnect", 500, true, 102, 202, sourceReconnect, sourceReconnectUdp);
            var oldUdpToken = sourceReconnect.Session.UdpToken;
            var resumed = fixture.DetachAndResume(sourceReconnect, "source-reconnect");

            if (useUdp)
            {
                var oldFrame = BuildMotionFrame(501, (501, 0), 501 * 50, 102, 202);
                var before = fixture.MotionSnapshot();
                SendUdp(sourceReconnectUdp!, oldUdpToken, oldFrame.Frame, fixture.Transport.Port);
                var unexpectedlyDelivered = SpinWait.SpinUntil(
                    () => targetUdp!.Available > 0,
                    TimeSpan.FromMilliseconds(150));
                if (unexpectedlyDelivered) ReceiveUdp(targetUdp!);
                var after = fixture.MotionSnapshot();
                var unchanged =
                    MotionCount(before, "received_udp") == MotionCount(after, "received_udp") &&
                    MotionCount(before, "relayed_udp") == MotionCount(after, "relayed_udp") &&
                    MotionCount(before, "dropped_stale") == MotionCount(after, "dropped_stale");
                observations.Add(new(
                    "old_udp_token_after_detach",
                    "source_reconnect",
                    501,
                    "unknown_session",
                    !unexpectedlyDelivered && unchanged ? "unknown_session" : "unexpected_delivery"));
                events.Add(FaultEvent(
                    s,
                    "old_udp_token_after_detach",
                    "source_reconnect",
                    501,
                    "unknown_session",
                    !unexpectedlyDelivered && unchanged ? "unknown_session" : "unexpected_delivery",
                    useUdp));
            }

            using var resumedUdp = useUdp ? new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) : null;
            RunFaultAttempt("resumed_sequence_reset_1", "source_reconnect", 1, true, 102, 202, resumed, resumedUdp);

            var snapshot = fixture.MotionSnapshot();
            const int expectedAcceptedFrames = 10;
            const int expectedPrimaryTargetDeliveries = 10;
            // Four source-a frames fan out to one peer, four source-wrap frames to
            // two peers, and the pre/post reconnect frames to three peers each.
            const int expectedRegionalRelayDeliveries = (4 * 1) + (4 * 2) + (1 * 3) + (1 * 3);
            var received =
                MotionCount(snapshot, useUdp ? "received_udp" : "received_websocket") -
                MotionCount(measurementBaseline, useUdp ? "received_udp" : "received_websocket");
            var relayed =
                MotionCount(snapshot, useUdp ? "relayed_udp" : "relayed_websocket") -
                MotionCount(measurementBaseline, useUdp ? "relayed_udp" : "relayed_websocket");
            var droppedStale =
                MotionCount(snapshot, "dropped_stale") -
                MotionCount(measurementBaseline, "dropped_stale");
            var expectationsMatch = observations.All(observation =>
                observation.ExpectedResult == observation.ObservedResult);
            var gapAccepted = observations.Any(observation =>
                observation.Case == "gap_after_loss_104" &&
                observation.ObservedResult == "relayed");
            var wrapAccepted = new[] { "wrap_65534", "wrap_65535", "wrap_0", "wrap_1" }
                .All(name => observations.Any(observation =>
                    observation.Case == name &&
                    observation.ObservedResult == "relayed"));
            var reconnectAccepted = observations.Any(observation =>
                observation.Case == "resumed_sequence_reset_1" &&
                observation.ObservedResult == "relayed");
            var oldTokenRejected = !useUdp || observations.Any(observation =>
                observation.Case == "old_udp_token_after_detach" &&
                observation.ObservedResult == "unknown_session");

            invariants.Add(new()
            {
                Name = "duplicate_and_reorder_rejected",
                Passed = expectationsMatch && droppedStale == 3,
                Detail = $"dropped_stale={droppedStale}; expected=3"
            });
            invariants.Add(new()
            {
                Name = "transient_gap_does_not_block_fresh_motion",
                Passed = gapAccepted,
                Detail = "sequence 104 relayed even though sequence 103 was intentionally absent"
            });
            invariants.Add(new()
            {
                Name = "ushort_sequence_wrap_preserved",
                Passed = wrapAccepted,
                Detail = "65534,65535,0,1 relayed; an old 65535 after wrap was rejected"
            });
            invariants.Add(new()
            {
                Name = "authenticated_session_reconnect_resets_motion_sequence",
                Passed = reconnectAccepted && oldTokenRejected,
                Detail = useUdp
                    ? "resumed session accepted sequence 1 and detached UDP token produced no motion relay"
                    : "resumed session accepted sequence 1 on the authenticated WebSocket seam"
            });
            invariants.Add(new()
            {
                Name = "accepted_source_frames_accounted",
                Passed = received == expectedAcceptedFrames,
                Detail = $"accepted_source_frames={received}; expected={expectedAcceptedFrames}; setup traffic excluded"
            });
            invariants.Add(new()
            {
                Name = "primary_target_delivery_accounted",
                Passed = primaryTargetDeliveries == expectedPrimaryTargetDeliveries,
                Detail = $"primary_target_deliveries={primaryTargetDeliveries}; expected={expectedPrimaryTargetDeliveries}"
            });
            invariants.Add(new()
            {
                Name = "regional_fanout_accounted",
                Passed = relayed == expectedRegionalRelayDeliveries,
                Detail = $"aggregate_relay_deliveries={relayed}; expected={expectedRegionalRelayDeliveries}; topology=4x1+4x2+1x3+1x3"
            });
            predictions.Add(new()
            {
                Name = "sequence_guard_behavior",
                Observed = "duplicate and old motion is dropped per session; gaps and ushort wrap preserve fresh motion"
            });
            predictions.Add(new()
            {
                Name = "reconnect_behavior",
                Observed = useUdp
                    ? "new authenticated session state accepts a fresh sequence and the detached UDP token no longer maps to a session"
                    : "new authenticated session state accepts a fresh sequence after resume"
            });
            predictions.Add(new()
            {
                Name = "fanout_cost_behavior",
                Observed = $"{received} accepted source frames produced {primaryTargetDeliveries} deliveries to the primary target and {relayed} aggregate deliveries across the changing region topology"
            });

            void RunFaultAttempt(
                string caseName,
                string sourceName,
                ushort sequenceValue,
                bool expectedAccepted,
                long zdoUserId,
                uint zdoId,
                CapturingSession source,
                UdpClient? sender)
            {
                var frame = BuildMotionFrame(
                    sequenceValue,
                    (sequenceValue, observations.Count),
                    observations.Count * 50,
                    zdoUserId,
                    zdoId);
                var before = fixture.MotionSnapshot();
                var primaryTargetFramesBefore = target.Socket.SentFrames.Count;

                if (useUdp)
                    SendUdp(sender!, source.Session.UdpToken, frame.Frame, fixture.Transport.Port);
                else
                    fixture.Transport
                        .HandleValheimMotionFrameAsync(
                            source,
                            frame.Header,
                            frame.Payload,
                            frame.Frame,
                            "websocket")
                        .GetAwaiter()
                        .GetResult();

                var counterChanged = SpinWait.SpinUntil(
                    () =>
                    {
                        var current = fixture.MotionSnapshot();
                        return MotionCount(current, useUdp ? "received_udp" : "received_websocket") >
                                   MotionCount(before, useUdp ? "received_udp" : "received_websocket") ||
                               MotionCount(current, "dropped_stale") >
                                   MotionCount(before, "dropped_stale");
                    },
                    TimeSpan.FromSeconds(2));
                if (!counterChanged)
                    throw new InvalidOperationException($"fault fixture did not settle: {caseName}");

                var after = fixture.MotionSnapshot();
                var accepted =
                    MotionCount(after, useUdp ? "received_udp" : "received_websocket") >
                    MotionCount(before, useUdp ? "received_udp" : "received_websocket");
                var stale =
                    MotionCount(after, "dropped_stale") >
                    MotionCount(before, "dropped_stale");
                if (useUdp && accepted)
                {
                    ReceiveUdp(targetUdp!);
                    primaryTargetDeliveries++;
                }
                else if (!useUdp && accepted &&
                         target.Socket.SentFrames.Count == primaryTargetFramesBefore + 1)
                {
                    primaryTargetDeliveries++;
                }
                var expectedResult = expectedAccepted ? "relayed" : "dropped_stale";
                var observedResult = accepted ? "relayed" : stale ? "dropped_stale" : "unknown";
                observations.Add(new(caseName, sourceName, sequenceValue, expectedResult, observedResult));
                events.Add(FaultEvent(
                    s,
                    caseName,
                    sourceName,
                    sequenceValue,
                    expectedResult,
                    observedResult,
                    useUdp));
            }
        }
        finally
        {
            if (useUdp)
                fixture.Transport.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static long MotionCount(JsonElement snapshot, string property) =>
        snapshot.GetProperty(property).GetInt64();

    private static EventEnvelope FaultEvent(
        Scenario s,
        string caseName,
        string source,
        ushort sequence,
        string expectedResult,
        string observedResult,
        bool useUdp) =>
        Event(
            s,
            sequence,
            $"cre-e03-{source}-{caseName}",
            new Dictionary<string, object?>
            {
                ["fault_case"] = caseName,
                ["source"] = source,
                ["sequence"] = (int)sequence,
                ["transport"] = useUdp ? "session_udp" : "binary_websocket_fallback",
                ["expected_result"] = expectedResult,
                ["observed_result"] = observedResult
            },
            "transport.fault_observed");

    private static RuntimePressureBand[] RuntimePressureBands(Scenario s) =>
    [
        new(
            "green",
            s.IntParameter("green_budget_units", 100),
            s.IntParameter("green_presentation_count", 4)),
        new(
            "amber",
            s.IntParameter("amber_budget_units", 70),
            s.IntParameter("amber_presentation_count", 10)),
        new(
            "red",
            s.IntParameter("red_budget_units", 50),
            s.IntParameter("red_presentation_count", 18))
    ];

    private static List<RuntimeWorkRequest> RuntimeRequests(RuntimePressureBand band)
    {
        var requests = new List<RuntimeWorkRequest>
        {
            new(
                "player_death",
                "critical_world_mutation",
                "critical",
                20,
                20,
                false,
                "binary_websocket",
                "stop_retry",
                1000),
            new(
                "projectile_hit",
                "critical_world_mutation",
                "critical",
                25,
                25,
                false,
                "binary_websocket",
                "stop_retry",
                900)
        };
        requests.AddRange(Enumerable.Range(0, band.PresentationCount).Select(index =>
            new RuntimeWorkRequest(
                $"projectile_trail_{index:000}",
                "transient_presentation",
                "presentation",
                7,
                3,
                true,
                "session_udp",
                "binary_websocket",
                100)));
        return requests;
    }

    private static void AppendRuntimeGateEvents(
        Scenario s,
        List<EventEnvelope> events,
        RuntimePressureBand band,
        int bandIndex,
        IReadOnlyList<RuntimeGateDecision> decisions,
        string eventPrefix)
    {
        foreach (var (decision, index) in decisions.Select((value, index) => (value, index)))
        {
            events.Add(Event(
                s,
                bandIndex,
                $"{eventPrefix}-{band.Name}-{index:000}",
                new Dictionary<string, object?>
                {
                    ["pressure_band"] = band.Name,
                    ["tick_budget_units"] = band.BudgetUnits,
                    ["work_id"] = decision.Request.WorkId,
                    ["work_class"] = decision.Request.WorkClass,
                    ["criticality"] = decision.Request.Criticality,
                    ["requested_mode"] = "full",
                    ["selected_mode"] = decision.SelectedMode,
                    ["requested_cost_units"] = decision.Request.FullCostUnits,
                    ["selected_cost_units"] = decision.SelectedCostUnits,
                    ["budget_remaining_units"] = decision.BudgetRemainingUnits,
                    ["reason"] = decision.Reason,
                    ["transport"] = decision.Transport,
                    ["fallback_transport"] = decision.Request.FallbackTransport,
                    ["deferred_depth"] = decision.DeferredDepth
                },
                "performance.gate_decision"));
        }
    }

    private static int PresentationCount(
        IReadOnlyList<RuntimeGateDecision> decisions,
        string selectedMode) =>
        decisions.Count(decision =>
            decision.Request.Criticality == "presentation" &&
            decision.SelectedMode == selectedMode);

    private static int DegradedCount(IReadOnlyList<RuntimeGateDecision> decisions) =>
        decisions.Count(decision =>
            decision.Request.Criticality == "presentation" &&
            decision.SelectedMode != "full");

    private sealed record RuntimePressureBand(string Name, int BudgetUnits, int PresentationCount);

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

    private static void ExecuteGatewayE02(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var totals = new Dictionary<int, int>();
        var gateway = new ValheimZdoRedirectService(walPath: null);
        foreach (var n in new[] { 2, 10, 100 })
        {
            var observers = Enumerable.Range(0, n).Select(i => new FanoutObserverInput(
                i == n - 1 ? "observer-000" : $"observer-{i:000}",
                i % 3 == 0 ? 20 : (i % 3 == 1 ? 45 : 90),
                i == 1 ? 410L : null)).ToArray();
            var decisions = ZdoFanoutPlan.Evaluate(410, 30, 64, 0, observers);
            var windowId = $"authority-lab-n-{n}";
            var envelopes = decisions
                .Where(decision => decision.Emit)
                .Select((decision, index) => new ValheimZdoRedirectEnvelope
                {
                    Seq = 410,
                    Recipient = decision.Recipient,
                    RecipientId = decision.Recipient,
                    CorrelationId = $"m7-e02-{n}-{index}",
                    IdempotencyKey = $"m7-e02-{n}-{decision.Recipient}-410",
                    Prefab = 1,
                    BodyB64 = "YXV0aG9yaXR5LWxhYg=="
                }).ToList();
            gateway.RecordEnvelopes(windowId, "authority-lab", envelopes, envelope => envelope.Recipient);
            // Repeat the same batch to exercise the real Gateway duplicate tracker. Pending state
            // remains one logical revision per recipient and ACK still has one terminal effect.
            gateway.RecordEnvelopes(windowId, "authority-lab", envelopes, envelope => envelope.Recipient);
            totals[n] = envelopes.Count;

            foreach (var (decision, index) in decisions.Select((value, index) => (value, index)))
            {
                var status = gateway.GetStatus(windowId, decision.Recipient);
                var pending = gateway.Pending(windowId, decision.Recipient, 1024);
                var ack = gateway.Acknowledge(windowId, decision.Recipient, pending.Where(envelope => envelope.Seq.HasValue).Select(envelope => envelope.Seq!.Value).ToArray());
                events.Add(Event(s, n * 1000 + index, $"e02-gateway-{n:000}-{index:000}", new Dictionary<string, object?>
                {
                    ["observer_count"] = n,
                    ["recipient"] = decision.Recipient,
                    ["distance_meters"] = decision.DistanceMeters,
                    ["disposition"] = decision.Disposition.ToString(),
                    ["emit"] = decision.Emit,
                    ["gateway_recorded"] = decision.Emit,
                    ["pending_before_ack"] = pending.Count,
                    ["acknowledged"] = ack.Acknowledged,
                    ["duplicates_observed"] = status.Duplicates,
                    ["partition"] = status.RecipientId
                }));
            }
        }

        var emitted = events.Where(e => e.Payload.TryGetValue("emit", out var value) && value is true).ToArray();
        var isolated = emitted.All(e => (int)e.Payload["acknowledged"]! == 1 && (int)e.Payload["pending_before_ack"]! == 1);
        var scaling = totals[2] < totals[10] && totals[10] < totals[100];
        var duplicateTracker = emitted.All(e => (long)e.Payload["duplicates_observed"]! >= 1);
        invariants.Add(new() { Name = "gateway_recipient_partition", Passed = isolated, Detail = "ValheimZdoRedirectService kept one pending revision and one terminal ACK per emitted recipient" });
        invariants.Add(new() { Name = "gateway_duplicate_terminal_apply", Passed = duplicateTracker, Detail = "replayed batches were counted as duplicates without producing a second pending item" });
        invariants.Add(new() { Name = "gateway_fanout_scales_with_observers", Passed = scaling, Detail = string.Join(",", totals.Select(kvp => $"n={kvp.Key}:{kvp.Value}")) });
        predictions.Add(new() { Name = "gateway_queue_partition", Observed = "recipient-local pending and ACK state remains independent through the real redirect service" });
        predictions.Add(new() { Name = "gateway_duplicate_handling", Observed = "duplicate records are observable and do not double-apply at ACK" });
    }

    private static void ExecuteGatewayE02Durable(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var windowId = "authority-lab-reconnect";
        var walPath = Path.Combine(Path.GetTempPath(), $"authority-lab-{Guid.NewGuid():N}.wal");
        var recipients = new[] { "observer-a", "observer-b" };
        var envelopes = recipients.Select((recipient, index) => new ValheimZdoRedirectEnvelope
        {
            Seq = 410 + index,
            Recipient = recipient,
            RecipientId = recipient,
            CorrelationId = $"m7-e02-durable-{index}",
            IdempotencyKey = $"m7-e02-durable-{recipient}",
            Prefab = 1,
            BodyB64 = "YXV0aG9yaXR5LWxhYg=="
        }).ToList();

        try
        {
            var first = new ValheimZdoRedirectService(walPath);
            first.RecordEnvelopes(windowId, "authority-lab", envelopes, envelope => envelope.Recipient);
            first.RecordEnvelopes(windowId, "authority-lab-retry", envelopes, envelope => envelope.Recipient);

            var restarted = new ValheimZdoRedirectService(walPath);
            var pendingAfterRestart = new List<int>();
            var acknowledgedAfterRestart = new List<int>();
            var durablePendingAfterAck = new List<int>();
            var durableAcknowledgedAfterAck = new List<long>();

            foreach (var (recipient, index) in recipients.Select((value, index) => (value, index)))
            {
                var status = restarted.GetStatus(windowId, recipient);
                var pending = restarted.Pending(windowId, recipient, 64);
                var ack = restarted.Acknowledge(windowId, recipient, pending.Select(item => item.Seq!.Value).ToArray());
                var afterAck = new ValheimZdoRedirectService(walPath).GetStatus(windowId, recipient);
                pendingAfterRestart.Add(pending.Count);
                acknowledgedAfterRestart.Add(ack.Acknowledged);
                durablePendingAfterAck.Add((int)afterAck.Pending);
                durableAcknowledgedAfterAck.Add(afterAck.Acknowledged);
                events.Add(Event(s, index, $"e02-gateway-durable-{index:000}", new Dictionary<string, object?>
                {
                    ["recipient"] = recipient,
                    ["restarted_receipts"] = status.Receipts,
                    ["restarted_duplicates"] = status.Duplicates,
                    ["pending_after_restart"] = pending.Count,
                    ["acknowledged_after_restart"] = ack.Acknowledged,
                    ["pending_after_ack_restart"] = afterAck.Pending,
                    ["acknowledged_after_ack_restart"] = afterAck.Acknowledged,
                    ["wal_bytes"] = new FileInfo(walPath).Length
                }));
            }

            invariants.Add(new() { Name = "gateway_wal_reconnect_pending", Passed = pendingAfterRestart.All(count => count == 1), Detail = $"pending_after_restart={string.Join(",", pendingAfterRestart)}" });
            invariants.Add(new() { Name = "gateway_wal_duplicate_replay", Passed = recipients.All(recipient => new ValheimZdoRedirectService(walPath).GetStatus(windowId, recipient).Duplicates >= 1), Detail = "duplicate record batches survived service restart" });
            invariants.Add(new() { Name = "gateway_wal_ack_recovery", Passed = acknowledgedAfterRestart.All(count => count == 1) && durablePendingAfterAck.All(count => count == 0) && durableAcknowledgedAfterAck.All(count => count == 1), Detail = $"acknowledged={string.Join(",", acknowledgedAfterRestart)};pending_after_ack={string.Join(",", durablePendingAfterAck)}" });
            predictions.Add(new() { Name = "gateway_restart_recovery", Observed = "WAL replay reconstructs recipient-local pending state before reconnect" });
            predictions.Add(new() { Name = "gateway_ack_durability", Observed = "ACK written after reconnect remains terminal after a second service restart" });
        }
        finally
        {
            if (File.Exists(walPath)) File.Delete(walPath);
        }
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

    private static void ExecuteGatewayE03(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var fixture = new GatewayMotionFixture();
        var source = fixture.CreateSession("region-lab", "source-a");
        var target = fixture.CreateSession("region-lab", "target-b");
        var patterns = s.Actors.Select(actor => actor.Trajectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fingerprints = new Dictionary<string, double>();
        ushort sequence = 1;
        foreach (var pattern in patterns)
        {
            var position = (x: 0d, y: 0d);
            var previous = position;
            var errorTotal = 0d;
            for (var tick = 0; tick < 20; tick++)
            {
                var next = Motion(pattern, tick, position);
                var velocity = (x: position.x - previous.x, y: position.y - previous.y);
                var predicted = (x: position.x + velocity.x, y: position.y + velocity.y);
                var correction = Math.Sqrt(Math.Pow(next.x - predicted.x, 2) + Math.Pow(next.y - predicted.y, 2));
                var frame = BuildMotionFrame(sequence++, next, sentMilliseconds: tick * 50);
                fixture.Transport.HandleValheimMotionFrameAsync(source, frame.Header, frame.Payload, frame.Frame, "websocket").GetAwaiter().GetResult();
                errorTotal += correction;
                events.Add(Event(s, tick, $"e03-gateway-{pattern}-{tick:000}", new Dictionary<string, object?>
                {
                    ["pattern"] = pattern,
                    ["input_interval_ms"] = 50,
                    ["output_interval_ms"] = pattern.Equals("stutter_north", StringComparison.OrdinalIgnoreCase) && tick % 3 == 0 ? 150 : 50,
                    ["correction_meters"] = correction,
                    ["transport_path"] = "websocket_fallback",
                    ["sequence"] = (int)frame.Header.Seq
                }));
                previous = position;
                position = next;
            }
            fingerprints[pattern] = errorTotal;
        }

        var telemetry = fixture.MotionSnapshot();
        var sequenceValues = target.Socket.SentFrames.Select(frame => (int)BinaryEnvelope.ReadHeader(frame).Seq).ToArray();
        var ordered = sequenceValues.SequenceEqual(sequenceValues.OrderBy(value => value));
        var expected = patterns.Length * 20;
        var relayCount = telemetry.GetProperty("relayed_websocket").GetInt64();
        var receiveCount = telemetry.GetProperty("received_websocket").GetInt64();
        invariants.Add(new() { Name = "gateway_motion_relayed", Passed = receiveCount == expected && relayCount == expected, Detail = $"received_websocket={receiveCount}; relayed_websocket={relayCount}; expected={expected}" });
        invariants.Add(new() { Name = "gateway_websocket_fallback", Passed = relayCount == target.Socket.SentFrames.Count, Detail = $"captured_target_frames={target.Socket.SentFrames.Count}" });
        invariants.Add(new() { Name = "gateway_logical_order", Passed = ordered, Detail = "captured fallback frames have monotonic envelope sequence" });
        invariants.Add(new() { Name = "gateway_motion_fingerprints", Passed = fingerprints.Values.Distinct().Count() >= Math.Min(4, fingerprints.Count), Detail = string.Join(",", fingerprints.Select(kvp => $"{kvp.Key}={kvp.Value:0.###}")) });
        predictions.Add(new() { Name = "gateway_transport_path", Observed = "all frames used the real UdpTransport WebSocket fallback seam because no UDP endpoint was bound" });
        predictions.Add(new() { Name = "gateway_cadence_vs_interpolation", Observed = "the same synthetic correction fingerprints survive Gateway relay; live interpolation remains unproven" });
    }

    private static void ExecuteGatewayE03Udp(Scenario s, List<EventEnvelope> events, List<InvariantResult> invariants, List<PredictionObservation> predictions)
    {
        var fixture = new GatewayMotionFixture(FindFreeUdpPort());
        var source = fixture.CreateSession("region-lab", "source-a");
        var target = fixture.CreateSession("region-lab", "target-b");
        using var sourceUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var targetUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var targetFrames = new List<byte[]>();
        var patterns = s.Actors.Select(actor => actor.Trajectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fingerprints = new Dictionary<string, double>();
        ushort sequence = 1;

        fixture.Transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var targetBind = BuildMotionFrame(600, (0, 0), sentMilliseconds: 0);
            SendUdp(targetUdp, target.Session.UdpToken, targetBind.Frame, fixture.Transport.Port);
            if (!SpinWait.SpinUntil(() => target.Session.UdpEndpoint is not null, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Gateway UDP target endpoint did not bind");

            foreach (var pattern in patterns)
            {
                var position = (x: 0d, y: 0d);
                var previous = position;
                var errorTotal = 0d;
                for (var tick = 0; tick < 20; tick++)
                {
                    var next = Motion(pattern, tick, position);
                    var velocity = (x: position.x - previous.x, y: position.y - previous.y);
                    var predicted = (x: position.x + velocity.x, y: position.y + velocity.y);
                    var correction = Math.Sqrt(Math.Pow(next.x - predicted.x, 2) + Math.Pow(next.y - predicted.y, 2));
                    var frame = BuildMotionFrame(sequence++, next, sentMilliseconds: tick * 50);
                    SendUdp(sourceUdp, source.Session.UdpToken, frame.Frame, fixture.Transport.Port);
                    var relayed = ReceiveUdp(targetUdp);
                    targetFrames.Add(relayed);
                    errorTotal += correction;
                    events.Add(Event(s, tick, $"e03-gateway-udp-{pattern}-{tick:000}", new Dictionary<string, object?>
                    {
                        ["pattern"] = pattern,
                        ["input_interval_ms"] = 50,
                        ["output_interval_ms"] = pattern.Equals("stutter_north", StringComparison.OrdinalIgnoreCase) && tick % 3 == 0 ? 150 : 50,
                        ["correction_meters"] = correction,
                        ["transport_path"] = "udp_bound",
                        ["sequence"] = (int)frame.Header.Seq,
                        ["target_token_matches"] = BitConverter.ToUInt64(relayed, 0) == target.Session.UdpToken
                    }));
                    previous = position;
                    position = next;
                }
                fingerprints[pattern] = errorTotal;
            }

            var telemetry = fixture.MotionSnapshot();
            var sequenceValues = targetFrames.Select(frame => (int)BinaryEnvelope.ReadHeader(frame.AsSpan(UdpTransport.TokenBytes)).Seq).ToArray();
            var ordered = sequenceValues.SequenceEqual(sequenceValues.OrderBy(value => value));
            var expected = patterns.Length * 20;
            var receivedUdp = telemetry.GetProperty("received_udp").GetInt64();
            var relayedUdp = telemetry.GetProperty("relayed_udp").GetInt64();
            invariants.Add(new() { Name = "gateway_udp_bound_relayed", Passed = receivedUdp == expected + 1 && relayedUdp == expected, Detail = $"received_udp={receivedUdp}; relayed_udp={relayedUdp}; expected_motion={expected}" });
            invariants.Add(new() { Name = "gateway_udp_target_delivery", Passed = targetFrames.Count == expected && events.All(e => (bool)e.Payload["target_token_matches"]!), Detail = $"captured_target_frames={targetFrames.Count}; token_matches={events.Count(e => (bool)e.Payload["target_token_matches"]!)}" });
            invariants.Add(new() { Name = "gateway_udp_logical_order", Passed = ordered, Detail = "captured UDP frames have monotonic envelope sequence" });
            invariants.Add(new() { Name = "gateway_udp_motion_fingerprints", Passed = fingerprints.Values.Distinct().Count() >= Math.Min(4, fingerprints.Count), Detail = string.Join(",", fingerprints.Select(kvp => $"{kvp.Key}={kvp.Value:0.###}")) });
            predictions.Add(new() { Name = "gateway_udp_path", Observed = "source motion binds a UDP endpoint and reaches the distinct target through UdpTransport.TrySend" });
            predictions.Add(new() { Name = "gateway_udp_vs_websocket", Observed = "the same synthetic correction fingerprints survive the bound UDP path; real client interpolation remains unproven" });
        }
        finally
        {
            fixture.Transport.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static int FindFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static void SendUdp(UdpClient sender, ulong token, byte[] frame, int port)
    {
        var packet = new byte[UdpTransport.TokenBytes + frame.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0, UdpTransport.TokenBytes), token);
        frame.CopyTo(packet, UdpTransport.TokenBytes);
        sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static byte[] ReceiveUdp(UdpClient receiver)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return receiver.ReceiveAsync(timeout.Token).GetAwaiter().GetResult().Buffer;
    }

    private static (BinaryEnvelopeHeader Header, byte[] Payload, byte[] Frame) BuildMotionFrame(
        ushort sequence,
        (double x, double y) position,
        long sentMilliseconds,
        long zdoUserId = 100,
        uint zdoId = 200)
    {
        var payload = new byte[PayloadSerializers.ValheimPlayerMotionBytes];
        var payloadLength = PayloadSerializers.WriteValheimPlayerMotion(
            payload,
            zdoUserId,
            zdoId,
            new Vec3((float)position.x, 0, (float)position.y),
            new Vec3(1, 0, 0),
            yaw: 90,
            sentMilliseconds: (uint)Math.Max(0, sentMilliseconds));
        var frame = new byte[BinaryEnvelope.HeaderBytes + payloadLength];
        var frameLength = BinaryEnvelope.Write(frame, version: 1, MessageTypeId.ValheimPlayerMotion,
            DeliveryLane.Datagram, sequence, payload.AsSpan(0, payloadLength));
        Array.Resize(ref frame, frameLength);
        return new(BinaryEnvelope.ReadHeader(frame), payload[..payloadLength], frame);
    }

    private sealed class GatewayMotionFixture
    {
        private readonly SessionManager sessions = new();
        private readonly ValheimMotionTelemetry motionTelemetry = new();

        public GatewayMotionFixture(int? udpPort = null)
        {
            var config = new ConfigurationBuilder();
            if (udpPort.HasValue)
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Udp:Port"] = udpPort.Value.ToString() });
            Transport = new UdpTransport(sessions, router: null!, motionTelemetry,
                config.Build(), NullLogger<UdpTransport>.Instance);
        }

        public UdpTransport Transport { get; }

        public CapturingSession CreateSession(string region, string recipient)
        {
            var socket = new CapturingWebSocket();
            var session = sessions.Create(socket);
            session.RegionId = region;
            session.ValheimRecipientId = recipient;
            return new CapturingSession(session, socket);
        }

        public CapturingSession DetachAndResume(CapturingSession current, string recipient)
        {
            var resumeToken = current.Session.ResumeToken;
            sessions.Detach(current.Session);
            var socket = new CapturingWebSocket();
            var session = sessions.TryResume(resumeToken, socket)
                ?? throw new InvalidOperationException("Gateway session did not resume");
            session.ValheimRecipientId = recipient;
            return new CapturingSession(session, socket);
        }

        public JsonElement MotionSnapshot()
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(motionTelemetry.Snapshot()));
            return document.RootElement.Clone();
        }
    }

    private sealed record CapturingSession(GameSession Session, CapturingWebSocket Socket)
    {
        public static implicit operator GameSession(CapturingSession capturing) => capturing.Session;
    }

    private sealed record FaultObservation(
        string Case,
        string Source,
        ushort Sequence,
        string ExpectedResult,
        string ObservedResult);

    private sealed class CapturingWebSocket : WebSocket
    {
        public List<byte[]> SentFrames { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentFrames.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
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

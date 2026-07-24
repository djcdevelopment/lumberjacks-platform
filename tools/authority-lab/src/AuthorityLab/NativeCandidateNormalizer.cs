using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AuthorityLab;

/// <summary>
/// Converts committed/native candidate JSONL into the common M7 evidence envelope.
/// This is observation only: it never claims that a native row is a Lumberjacks
/// decision and it preserves the original source alongside normalized rows.
/// </summary>
internal static class NativeCandidateNormalizer
{
    private const string EventType = "authority.native_candidate_observed";

    public static int Execute(Options options)
    {
        var scenarioPath = options.Required("scenario");
        var inputPath = options.Required("input");
        var output = options.Required("output");
        var sourceRevision = options.Value("source-revision")
            ?? Environment.GetEnvironmentVariable("AUTHORITY_LAB_SOURCE_REVISION")
            ?? "working_tree";
        var dirtyState = options.Has("dirty-state") || sourceRevision == "working_tree";
        if (!File.Exists(inputPath)) throw new ScenarioException($"native source not found: {inputPath}");

        var scenario = Scenario.Load(scenarioPath);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "raw"));
        var normalizedInput = scenario.NormalizedJson();
        File.WriteAllText(Path.Combine(output, "normalized-input.json"), normalizedInput + Environment.NewLine, new UTF8Encoding(false));
        File.Copy(inputPath, Path.Combine(output, "raw", "native-source.jsonl"), true);

        var runId = Path.GetFileName(Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var events = new List<EventEnvelope>();
        var anomalies = new List<Dictionary<string, object?>>();
        var ignored = new List<Dictionary<string, object?>>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(inputPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                ignored.Add(Ignored(lineNumber, "blank_line", ""));
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    anomalies.Add(Anomaly(lineNumber, "root_not_object", line));
                    continue;
                }

                var nativeEvent = Text(root, "event") ?? Text(root, "event_type") ?? "";
                if (nativeEvent.Equals("object", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(BuildObjectEvent(scenario, runId, lineNumber, root));
                }
                else if (nativeEvent.Equals("zdo", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Text(root, "dir"), "send", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(BuildZdoEvent(scenario, runId, lineNumber, root));
                }
                else
                {
                    ignored.Add(Ignored(lineNumber, "non_candidate_event", nativeEvent));
                }
            }
            catch (JsonException exception)
            {
                anomalies.Add(Anomaly(lineNumber, "malformed_json", line, exception.Message));
            }
        }

        WriteJsonl(Path.Combine(output, "raw", "events.jsonl"), events);
        WriteJsonl(Path.Combine(output, "raw", "anomalies.jsonl"), anomalies);
        WriteJsonl(Path.Combine(output, "raw", "ignored.jsonl"), ignored);
        var normalizedDecisions = JsonSerializer.Serialize(events.Select(e => new
        {
            e.SchemaVersion,
            e.EventType,
            e.ExperimentId,
            e.ScenarioId,
            e.Seed,
            e.Tick,
            e.Driver,
            e.Payload
        }), ProgramJson.NormalizedOptions);
        File.WriteAllText(Path.Combine(output, "raw", "normalized-decisions.json"), normalizedDecisions + Environment.NewLine, new UTF8Encoding(false));

        var invariants = new List<InvariantResult>
        {
            new() { Name = "native_source_readable", Passed = true, Detail = $"read {lineNumber} source line(s)" },
            new() { Name = "native_candidate_rows_normalized", Passed = events.Count > 0, Detail = $"normalized {events.Count} candidate row(s)" },
            new() { Name = "native_rows_not_silently_dropped", Passed = true, Detail = $"ignored={ignored.Count}; malformed={anomalies.Count}; raw source and sidecars retained" },
            new() { Name = "native_source_is_malformed_free", Passed = anomalies.Count == 0, Detail = $"malformed={anomalies.Count}" }
        };
        var predictions = new List<PredictionObservation>
        {
            new() { Name = "native_candidate_count", Observed = events.Count.ToString(CultureInfo.InvariantCulture) },
            new() { Name = "native_ignored_count", Observed = ignored.Count.ToString(CultureInfo.InvariantCulture) },
            new() { Name = "native_malformed_count", Observed = anomalies.Count.ToString(CultureInfo.InvariantCulture) }
        };
        var classification = anomalies.Count > 0 ? "inconclusive" : events.Count > 0 ? "supported" : "inconclusive";
        var now = DateTimeOffset.UtcNow.ToString("o");
        var receipt = new Receipt
        {
            SchemaVersion = 1,
            ExperimentId = scenario.ExperimentId,
            ScenarioId = scenario.ScenarioId,
            RunId = runId,
            SourceRevision = sourceRevision,
            DirtyState = dirtyState,
            ScenarioSha256 = Hashing.File(scenarioPath),
            NormalizedInputSha256 = Hashing.Sha256(normalizedInput),
            Policy = scenario.Policy,
            Driver = scenario.Driver,
            Seed = scenario.Seed,
            StartedUtc = now,
            EndedUtc = now,
            DurationSeconds = 0,
            StopResult = "completed",
            EventCounts = events.GroupBy(e => e.EventType).ToDictionary(g => g.Key, g => g.Count()),
            Invariants = invariants,
            PredictionObservations = predictions,
            RawEvidencePaths = new[]
            {
                "normalized-input.json", "raw/native-source.jsonl", "raw/events.jsonl",
                "raw/anomalies.jsonl", "raw/ignored.jsonl", "raw/normalized-decisions.json"
            },
            NormalizedDecisionSha256 = Hashing.Sha256(normalizedDecisions),
            ResultClassification = classification
        };
        File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(receipt, ProgramJson.NormalizedOptions) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "summary.md"), Summary(receipt), new UTF8Encoding(false));
        Console.WriteLine($"normalize-native {receipt.ExperimentId}/{receipt.ScenarioId}: {classification}; candidates={events.Count}; malformed={anomalies.Count}; receipt={Path.Combine(output, "receipt.json")}");
        return classification == "supported" ? 0 : 3;
    }

    private static EventEnvelope BuildObjectEvent(Scenario scenario, string runId, int line, JsonElement root) =>
        Event(scenario, runId, line, "object", new Dictionary<string, object?>
        {
            ["source_event"] = "object",
            ["source_line"] = line,
            ["native_order"] = line,
            ["object_key"] = Text(root, "object_stable_key"),
            ["object_class"] = Text(root, "object_kind"),
            ["object_name"] = Text(root, "object_name"),
            ["distance_meters"] = Number(root, "distance_meters"),
            ["priority_tier"] = Text(root, "priority_tier"),
            ["priority_rank"] = Number(root, "priority_rank"),
            ["position"] = Position(root)
        });

    private static EventEnvelope BuildZdoEvent(Scenario scenario, string runId, int line, JsonElement root) =>
        Event(scenario, runId, line, "zdo", new Dictionary<string, object?>
        {
            ["source_event"] = "zdo",
            ["source_line"] = line,
            ["native_order"] = line,
            ["direction"] = "send",
            ["uid"] = Number(root, "uid") ?? Text(root, "uid"),
            ["owner"] = Number(root, "owner"),
            ["owner_revision"] = Number(root, "owner_revision"),
            ["data_revision"] = Number(root, "data_revision"),
            ["position"] = Position(root)
        });

    private static EventEnvelope Event(Scenario scenario, string runId, int line, string sourceEvent, Dictionary<string, object?> payload) => new()
    {
        EventId = $"native-{line:000000}",
        TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
        EventType = EventType,
        ExperimentId = scenario.ExperimentId,
        ScenarioId = scenario.ScenarioId,
        RunId = runId,
        Seed = scenario.Seed,
        Tick = line,
        Driver = scenario.Driver,
        Payload = payload
    };

    private static Dictionary<string, object?> Position(JsonElement root) => new()
    {
        ["x"] = Number(root, "pos_x"),
        ["y"] = Number(root, "pos_y"),
        ["z"] = Number(root, "pos_z")
    };

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static object? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt64(out var integer)) return integer;
        return value.TryGetDouble(out var number) ? number : null;
    }

    private static Dictionary<string, object?> Anomaly(int line, string reason, string raw, string? detail = null) => new()
    {
        ["line_number"] = line, ["reason"] = reason, ["detail"] = detail, ["raw_line"] = raw
    };

    private static Dictionary<string, object?> Ignored(int line, string reason, string eventName) => new()
    {
        ["line_number"] = line, ["reason"] = reason, ["event"] = eventName
    };

    private static void WriteJsonl<T>(string path, IEnumerable<T> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, ProgramJson.CompactOptions));
    }

    private static string Summary(Receipt receipt) => string.Join(Environment.NewLine, new[]
    {
        $"# {receipt.ExperimentId} / {receipt.ScenarioId}", "",
        $"- Run: `{receipt.RunId}`", $"- Driver: `{receipt.Driver}`",
        $"- Classification: `{receipt.ResultClassification}`", "",
        "## Native normalization", "",
        $"- Candidate events: `{receipt.EventCounts.Values.Sum()}`",
        $"- Raw evidence: `{string.Join("`, `", receipt.RawEvidencePaths)}`", "",
        "## Invariants", "",
        string.Join(Environment.NewLine, receipt.Invariants.Select(i => $"- {(i.Passed ? "PASS" : "FAIL")} `{i.Name}`: {i.Detail}")), ""
    });
}

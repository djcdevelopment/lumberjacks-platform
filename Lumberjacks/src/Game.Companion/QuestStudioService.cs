using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ComfyQuestContracts;
using Newtonsoft.Json;

namespace Lumberjacks.Companion;

sealed class QuestStudioService
{
    readonly object _lock = new();
    readonly string _projectPath;
    readonly string _historyPath;
    readonly QuestPackPublisher _publisher;
    readonly ValheimLocator _locator;

    public QuestStudioService(WorkbenchStore store, QuestPackPublisher publisher, ValheimLocator locator)
    {
        var root = Path.Combine(store.RootDirectory, "quest-studio");
        Directory.CreateDirectory(root);
        _projectPath = Path.Combine(root, "project.json");
        _historyPath = Path.Combine(root, "history");
        _publisher = publisher;
        _locator = locator;
    }

    public QuestStudioProject Read()
    {
        lock (_lock)
        {
            if (!File.Exists(_projectPath)) return QuestStudioProject.Starter();
            try { return System.Text.Json.JsonSerializer.Deserialize<QuestStudioProject>(File.ReadAllText(_projectPath), Json.Options) ?? QuestStudioProject.Starter(); }
            catch { return QuestStudioProject.Starter() with { LastError = "project_state_unreadable" }; }
        }
    }

    public object Events() => new { schema_version = 1, events = CanonicalEventCatalog.All };

    public object Receipts()
    {
        var valheim = _locator.Find();
        if (valheim is null) return new { schema_version = 1, available = false, receipts = Array.Empty<JsonElement>() };
        var root = Path.Combine(valheim, "BepInEx", "config", "comfy-quest-runtime");
        var store = new RuntimeReceiptStore(root);
        var values = new List<JsonElement>();
        foreach (var path in store.List(50))
        {
            try { if (new FileInfo(path).Length <= 128 * 1024) values.Add(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()); }
            catch { /* a partial or malformed receipt is ignored, never trusted as runtime evidence */ }
        }
        return new { schema_version = 1, available = true, receipts = values };
    }

    public QuestStudioResult Save(QuestStudioProject? project)
    {
        var validation = ValidateProject(project);
        if (validation is not null) return QuestStudioResult.Fail(validation);
        lock (_lock)
        {
            var temporary = _projectPath + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(project, Json.Options));
            File.Move(temporary, _projectPath, true);
        }
        var certified = Certify(project!);
        if (certified.Ok) StoreSnapshot(project!, certified.ContentHash!);
        return certified with { Status = "saved" };
    }

    public object History()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_historyPath)) return new { schema_version = 1, versions = Array.Empty<QuestStudioSnapshot>() };
            var snapshots = Directory.GetFiles(_historyPath, "*.json").Select(ReadSnapshot).Where(x => x is not null)
                .Cast<QuestStudioSnapshot>().OrderByDescending(x => x.SavedUtc).Take(100).ToArray();
            return new { schema_version = 1, versions = snapshots };
        }
    }

    public QuestStudioDiff Diff(string? from, string? to)
    {
        if (!SafeHash(from) || !SafeHash(to)) return QuestStudioDiff.Fail("history_hash_invalid");
        lock (_lock)
        {
            var left = ReadSnapshot(Path.Combine(_historyPath, from + ".json"));
            var right = ReadSnapshot(Path.Combine(_historyPath, to + ".json"));
            if (left is null || right is null) return QuestStudioDiff.Fail("history_version_missing");
            var changes = new List<QuestStudioFieldChange>();
            Add("pack_id", left.Project.PackId, right.Project.PackId, changes);
            Add("version", left.Project.Version, right.Project.Version, changes);
            Add("experience_id", left.Project.ExperienceId, right.Project.ExperienceId, changes);
            Add("title", left.Project.Title, right.Project.Title, changes);
            Add("event", left.Project.Event, right.Project.Event, changes);
            Add("target", left.Project.Target, right.Project.Target, changes);
            Add("message", left.Project.Message, right.Project.Message, changes);
            return new(true, null, left, right, changes);
        }
    }

    public QuestStudioResult Certify(QuestStudioProject? project)
    {
        var validation = ValidateProject(project);
        if (validation is not null) return QuestStudioResult.Fail(validation);
        var json = BuildExperienceJson(project!);
        var compiled = ExperienceCompiler.CompileJson(json, CanonicalEventCatalog.CreateSet());
        return compiled.IsValid
            ? QuestStudioResult.Success("certified", json, QuestPackContent.ComputeHash(new[] { new KeyValuePair<string, byte[]>($"experiences/{project!.ExperienceId}.json", Encoding.UTF8.GetBytes(json)) }))
            : QuestStudioResult.Fail("experience_invalid", compiled.Diagnostics);
    }

    public async Task<QuestStudioPublishResult> PublishAsync(QuestStudioProject? project, CancellationToken cancellationToken)
    {
        var certified = Certify(project);
        if (!certified.Ok) return QuestStudioPublishResult.Fail(certified.Error!, certified.Diagnostics);
        var saved = Save(project);
        if (!saved.Ok) return QuestStudioPublishResult.Fail(saved.Error!, saved.Diagnostics);
        var bytes = BuildPack(project!, certified.ExperienceJson!, certified.ContentHash!);
        await using var stream = new MemoryStream(bytes, writable: false);
        var filename = $"{project!.PackId}-{project.Version}.questpack";
        var receipt = await _publisher.PublishAsync(stream, filename, cancellationToken);
        return new(receipt.Ok, receipt.Ok ? "published" : "rejected", receipt.Error, receipt, certified.Diagnostics);
    }

    static string? ValidateProject(QuestStudioProject? p)
    {
        if (p is null) return "project_required";
        if (!SafeId(p.PackId) || !SafeId(p.ExperienceId)) return "stable_id_invalid";
        if (!SemanticVersion.TryParse(p.Version, out _)) return "version_invalid";
        if (!CanonicalEventCatalog.Contains(p.Event)) return "event_unknown";
        if (p.Title?.Length is < 1 or > 120 || p.Target?.Length > 120 || p.Message?.Length is < 1 or > 500) return "field_bounds_invalid";
        return null;
    }

    static bool SafeId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');
    static bool SafeHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    void StoreSnapshot(QuestStudioProject project, string contentHash)
    {
        Directory.CreateDirectory(_historyPath);
        var target = Path.Combine(_historyPath, contentHash + ".json");
        if (File.Exists(target)) return;
        var temporary = target + ".tmp";
        var snapshot = new QuestStudioSnapshot(1, contentHash, DateTimeOffset.UtcNow, project with { LastError = null });
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(snapshot, Json.Options));
        try { File.Move(temporary, target); } catch (IOException) when (File.Exists(target)) { File.Delete(temporary); }
    }

    static QuestStudioSnapshot? ReadSnapshot(string path)
    {
        try { return new FileInfo(path).Length <= 1024 * 1024 ? System.Text.Json.JsonSerializer.Deserialize<QuestStudioSnapshot>(File.ReadAllText(path), Json.Options) : null; }
        catch { return null; }
    }
    static void Add(string field, string? from, string? to, List<QuestStudioFieldChange> changes) { if (!string.Equals(from, to, StringComparison.Ordinal)) changes.Add(new(field, from, to)); }

    static string BuildExperienceJson(QuestStudioProject p)
    {
        var trigger = new TriggerExpression { Op = "EVENT", Event = p.Event, Target = string.IsNullOrWhiteSpace(p.Target) ? null : p.Target };
        var action = new ExperienceAction { Id = "message-1", Type = "message", Parameters = new Dictionary<string, Newtonsoft.Json.Linq.JToken> { ["text"] = p.Message } };
        var document = new ExperienceDocument {
            Schema = ExperienceSchema.Id, Id = p.ExperienceId, Title = p.Title, EntryStage = "start",
            Stages = new() { new ExperienceStage { Id = "start", Transitions = new() { new ExperienceTransition { Id = "complete", Priority = 100, When = trigger, Actions = new() { action }, Outcome = "complete" } } } },
            Bindings = new() { new ExperienceBinding { Id = "default", ExperienceId = p.ExperienceId } }
        };
        return JsonConvert.SerializeObject(document, Formatting.Indented);
    }

    static byte[] BuildPack(QuestStudioProject p, string experienceJson, string contentHash)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(archive, "manifest.json", JsonConvert.SerializeObject(new QuestPackManifest { PackId = p.PackId, Version = p.Version, ContentHash = contentHash }, Formatting.Indented));
            Write(archive, $"experiences/{p.ExperienceId}.json", experienceJson);
        }
        return output.ToArray();
    }

    static void Write(ZipArchive archive, string name, string content) { using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); writer.Write(content); }
}

sealed record QuestStudioProject(string PackId, string Version, string ExperienceId, string Title, string Event, string? Target, string Message, string? LastError = null)
{
    public static QuestStudioProject Starter() => new("first-quest", "1.0.0", "first-quest", "First Quest", "kill", "$enemy_greyling", "The Charm answers your deed.");
}
sealed record QuestStudioResult(bool Ok, string Status, string? Error, string? ExperienceJson, string? ContentHash, IReadOnlyList<ContractDiagnostic> Diagnostics)
{
    public static QuestStudioResult Success(string status, string json, string hash) => new(true, status, null, json, hash, Array.Empty<ContractDiagnostic>());
    public static QuestStudioResult Fail(string error, IReadOnlyList<ContractDiagnostic>? diagnostics = null) => new(false, "rejected", error, null, null, diagnostics ?? Array.Empty<ContractDiagnostic>());
}
sealed record QuestStudioPublishResult(bool Ok, string Status, string? Error, QuestPackPublishReceipt? Receipt, IReadOnlyList<ContractDiagnostic> Diagnostics)
{
    public static QuestStudioPublishResult Fail(string error, IReadOnlyList<ContractDiagnostic>? diagnostics = null) => new(false, "rejected", error, null, diagnostics ?? Array.Empty<ContractDiagnostic>());
}
sealed record QuestStudioSnapshot(int SchemaVersion, string ContentHash, DateTimeOffset SavedUtc, QuestStudioProject Project);
sealed record QuestStudioFieldChange(string Field, string? From, string? To);
sealed record QuestStudioDiff(bool Ok, string? Error, QuestStudioSnapshot? From, QuestStudioSnapshot? To, IReadOnlyList<QuestStudioFieldChange> Changes)
{
    public static QuestStudioDiff Fail(string error) => new(false, error, null, null, Array.Empty<QuestStudioFieldChange>());
}

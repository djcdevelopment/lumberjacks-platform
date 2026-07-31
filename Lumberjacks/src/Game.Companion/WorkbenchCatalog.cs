using System.Text.Json;

namespace Lumberjacks.Companion;

static class WorkbenchCatalog
{
    public static JsonElement Read()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "workbench-checklist.json");
        if (!File.Exists(path))
        {
            return JsonSerializer.SerializeToElement(new
            {
                schema_version = 1,
                catalog_kind = "lumberjacks_workbench_checklist",
                source_of_truth = Array.Empty<string>(),
                milestones = Array.Empty<string>(),
                execution_lanes = Array.Empty<string>(),
                reconstruct = new { steps = Array.Empty<string>() },
            }, Json.Options);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static object Snapshot(JsonElement catalog, CompanionState saved, string? install)
    {
        var now = DateTimeOffset.UtcNow;
        return new
        {
            schema_version = 1,
            event_type = "companion.workbench_snapshot",
            generated_utc = now,
            source = new
            {
                companion_version = CompanionVersion.Value,
                bootstrap_release = CompanionVersion.BootstrapRelease,
                source_revision = CompanionVersion.SourceRevision,
                source_branch = CompanionVersion.SourceBranch,
                source_dirty = CompanionVersion.SourceDirty,
                image = CompanionVersion.Image,
                gateway_url = GatewayClient.GatewayUrl,
            },
            local = new
            {
                valheim_found = install is not null,
                config_found = install is not null && File.Exists(ValheimLocator.ConfigPath(install)),
                valheim_running = ValheimLocator.IsRunning(),
                profile_linked = saved.profile is not null,
                installed_release = saved.installed?.release,
                installed_mod_release = saved.installed?.mod_release,
                installed_package_sha256_short = ShortHash(saved.installed?.package_sha256),
                last_error = saved.last_error,
            },
            catalog,
        };
    }

    public static string WriteSnapshot(CompanionStateStore state, object snapshot)
    {
        var directory = Path.Combine(state.DataDirectory, "workbench-snapshots");
        Directory.CreateDirectory(directory);
        var name = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-workbench.json";
        var path = Path.Combine(directory, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json.Options));
        File.Move(temporary, path, true);
        return path;
    }

    public static string? LatestSnapshot(CompanionStateStore state)
    {
        var directory = Path.Combine(state.DataDirectory, "workbench-snapshots");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*-workbench.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    static string? ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }
}

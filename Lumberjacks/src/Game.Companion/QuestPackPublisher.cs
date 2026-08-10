using ComfyQuestContracts;

namespace Lumberjacks.Companion;

sealed class QuestPackPublisher
{
    public const long MaxPackageBytes = 8 * 1024 * 1024;
    readonly ValheimLocator _locator;

    public QuestPackPublisher(ValheimLocator locator) => _locator = locator;

    public async Task<QuestPackPublishReceipt> PublishAsync(Stream source, string filename, CancellationToken cancellationToken)
    {
        if (!SafeFilename(filename)) return QuestPackPublishReceipt.Fail("filename_invalid");
        var valheim = _locator.Find();
        if (valheim is null) return QuestPackPublishReceipt.Fail("valheim_not_found", manualCopyAvailable: true);

        var runtimeRoot = Path.Combine(valheim, "BepInEx", "config", "comfy-quest-runtime");
        var inbox = Path.Combine(runtimeRoot, "inbox");
        Directory.CreateDirectory(inbox);
        var temporary = Path.Combine(inbox, ".incoming-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var copied = await CopyBoundedAsync(source, temporary, cancellationToken);
            if (!copied) return QuestPackPublishReceipt.Fail("package_too_large");

            var store = new QuestPackStore(runtimeRoot);
            var candidate = store.Inspect(temporary);
            if (!candidate.IsValid)
                return QuestPackPublishReceipt.Fail("pack_invalid", diagnostics: candidate.Diagnostics);

            foreach (var existing in store.CheckInbox())
            {
                if (!existing.IsValid || existing.Manifest.PackId != candidate.Manifest.PackId || existing.Manifest.Version != candidate.Manifest.Version) continue;
                if (!string.Equals(existing.ContentHash, candidate.ContentHash, StringComparison.OrdinalIgnoreCase))
                    return QuestPackPublishReceipt.Fail("same_version_hash_collision");
                return QuestPackPublishReceipt.Success(existing.Manifest, existing.ContentHash, existing.Sha256, Path.GetFileName(existing.Path), alreadyPresent: true);
            }

            var destination = Path.Combine(inbox, filename);
            if (File.Exists(destination)) return QuestPackPublishReceipt.Fail("filename_collision");
            File.Move(temporary, destination);
            return QuestPackPublishReceipt.Success(candidate.Manifest, candidate.ContentHash, candidate.Sha256, filename, alreadyPresent: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return QuestPackPublishReceipt.Fail("publish_failed", detail: ex.Message); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static bool SafeFilename(string filename) =>
        !string.IsNullOrWhiteSpace(filename) && filename.Length <= 120 &&
        filename == Path.GetFileName(filename) && filename.EndsWith(".questpack", StringComparison.OrdinalIgnoreCase) &&
        filename.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');

    static async Task<bool> CopyBoundedAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return true;
            total += read;
            if (total > MaxPackageBytes) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

sealed record QuestPackPublishReceipt(
    int SchemaVersion,
    bool Ok,
    string Status,
    string? Error,
    string? Detail,
    bool ManualCopyAvailable,
    string? PackId,
    string? Version,
    string? ContentHash,
    string? PackageSha256,
    string? Filename,
    bool AlreadyPresent,
    IReadOnlyList<ContractDiagnostic>? Diagnostics)
{
    public static QuestPackPublishReceipt Fail(string error, string? detail = null, bool manualCopyAvailable = false, IReadOnlyList<ContractDiagnostic>? diagnostics = null) =>
        new(1, false, "rejected", error, detail, manualCopyAvailable, null, null, null, null, null, false, diagnostics);

    public static QuestPackPublishReceipt Success(QuestPackManifest manifest, string contentHash, string packageSha256, string filename, bool alreadyPresent) =>
        new(1, true, alreadyPresent ? "already_present" : "published", null, null, false, manifest.PackId, manifest.Version, contentHash, packageSha256, filename, alreadyPresent, Array.Empty<ContractDiagnostic>());
}

using System.Security.Cryptography;
using System.Text.Json;
using Game.Gateway.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// The tools.json contract between the producer (tools/workbench/Publish-WorkbenchAssets.ps1) and
/// the consumer (<see cref="WorkbenchDownloadEndpoints"/>).
///
/// This suite exists because of a real outage: the script wrote the artifact array under the key
/// "tools" while the endpoint deserializes into DownloadPointer(schema_version, downloads) and
/// treats a null list as invalid. Both sides had SHA-256 gates and both passed — neither checked
/// that the file one writes is the shape the other reads, so the first real deploy was the
/// detector and every /workbench/downloads/{id} answered 503.
///
/// Tests drive <see cref="WorkbenchDownloadEndpoints.Resolve"/> rather than HTTP: this project has
/// no WebApplicationFactory harness (see ValheimConsumerHeartbeatPolicyTests), and Resolve is the
/// whole decision — a refusal returns no path, so "did not stream bytes" is an assertion about the
/// only value Map could ever open a stream from.
/// </summary>
public sealed class WorkbenchDownloadPointerContractTests : IDisposable
{
    const string DirectoryVariable = "LUMBERJACKS_WORKBENCH_DOWNLOADS_DIR";
    const string HtmlVariable = "LUMBERJACKS_WORKBENCH_HTML";
    const string ToolId = "quest-picker";
    const string ZipName = "quest-picker.zip";

    static readonly byte[] PackageBytes = "PK pretend this is a zip"u8.ToArray();

    readonly string _root = Path.Combine(Path.GetTempPath(), "lumberjacks-downloads-" + Guid.NewGuid().ToString("N"));

    public WorkbenchDownloadPointerContractTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, ZipName), PackageBytes);
        Environment.SetEnvironmentVariable(DirectoryVariable, _root);
        Environment.SetEnvironmentVariable(HtmlVariable, null);
    }

    static string PackageSha => Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant();

    void WritePointer(string json) => File.WriteAllText(Path.Combine(_root, "tools.json"), json);

    static WorkbenchDownloadEndpoints.DownloadResolution Resolve(string id = ToolId) =>
        WorkbenchDownloadEndpoints.Resolve(id, NullLogger.Instance);

    /// <summary>The shape the endpoint's own doc comment documents, written out literally.</summary>
    static string DocumentedPointer(string sha, long size = 0, string key = "downloads") => $$"""
        {
          "schema_version": 1,
          "{{key}}": [
            {
              "id": "{{ToolId}}",
              "file": "{{ZipName}}",
              "sha256": "{{sha}}",
              "size_bytes": {{(size > 0 ? size : PackageBytes.Length)}}
            }
          ]
        }
        """;

    [Fact]
    public void Resolve_ServesAPointerWrittenInTheDocumentedShape()
    {
        WritePointer(DocumentedPointer(PackageSha));

        var resolution = Resolve();

        Assert.Equal(WorkbenchDownloadEndpoints.DownloadOutcome.Served, resolution.Outcome);
        Assert.Equal(Path.Combine(_root, ZipName), resolution.PackagePath);
        // The digest handed to the X-Download-Sha256 header is recomputed from the file, not echoed
        // from the pointer, so it is what a visitor can verify against the published catalog.
        Assert.Equal(PackageSha, resolution.Sha256);
        Assert.Null(resolution.Error);
    }

    /// <summary>
    /// The exact regression. "tools" is a plausible key — it is what workbench.json calls the same
    /// list — and it parses without throwing, which is why it reached production.
    /// </summary>
    [Fact]
    public void Resolve_RefusesAPointerThatNamesTheArrayToolsInsteadOfDownloads()
    {
        WritePointer(DocumentedPointer(PackageSha, key: "tools"));

        var resolution = Resolve();

        Assert.Equal(WorkbenchDownloadEndpoints.DownloadOutcome.Unavailable, resolution.Outcome);
        Assert.Contains("invalid", resolution.Error);
        Assert.Null(resolution.PackagePath);
    }

    [Fact]
    public void Resolve_RefusesAndOpensNothingWhenTheDigestDisagreesWithTheFileOnDisk()
    {
        WritePointer(DocumentedPointer(new string('a', 64)));

        var resolution = Resolve();

        Assert.Equal(WorkbenchDownloadEndpoints.DownloadOutcome.Unavailable, resolution.Outcome);
        Assert.Contains("SHA-256", resolution.Error);
        // No path means Map has nothing to File.OpenRead: the refusal cannot degrade into a stream
        // of the very bytes that failed verification.
        Assert.Null(resolution.PackagePath);
        Assert.Null(resolution.Sha256);
    }

    [Fact]
    public void Resolve_RefusesWhenTheSizeDisagreesWithTheFileOnDisk()
    {
        WritePointer(DocumentedPointer(PackageSha, size: PackageBytes.Length + 1));

        var resolution = Resolve();

        Assert.Equal(WorkbenchDownloadEndpoints.DownloadOutcome.Unavailable, resolution.Outcome);
        Assert.Contains("size", resolution.Error);
        Assert.Null(resolution.PackagePath);
    }

    [Fact]
    public void Resolve_ReportsAMissingPointerRatherThanServingWhatHappensToBeInTheDirectory()
    {
        var resolution = Resolve();

        Assert.Equal(WorkbenchDownloadEndpoints.DownloadOutcome.Unavailable, resolution.Outcome);
        Assert.Contains("tools.json", resolution.Error);
        Assert.Null(resolution.PackagePath);
    }

    /// <summary>
    /// The drift guard. Fixtures/workbench-pointer.sample.json is a copy of what the publish script
    /// emits, and Publish-WorkbenchAssets.ps1 compares its own serialized pointer against that same
    /// file before uploading. This end asserts the sample still lands in the record the endpoint
    /// reads, so renaming a field on either side breaks here or at publish — not on a deploy.
    /// </summary>
    [Fact]
    public void ThePublishedSamplePointerDeserializesIntoTheRecordTheEndpointUses()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workbench-pointer.sample.json");
        Assert.True(File.Exists(samplePath), $"the pointer contract sample is missing at {samplePath}");

        var pointer = JsonSerializer.Deserialize<WorkbenchDownloadEndpoints.DownloadPointer>(
            File.ReadAllText(samplePath),
            WorkbenchDownloadEndpoints.PointerJsonOptions);

        Assert.NotNull(pointer);
        Assert.Equal(1, pointer!.schema_version);
        Assert.NotNull(pointer.downloads);
        Assert.NotEmpty(pointer.downloads!);

        foreach (var entry in pointer.downloads!)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.id));
            Assert.False(string.IsNullOrWhiteSpace(entry.file));
            Assert.True(entry.size_bytes > 0, $"{entry.id} has no size_bytes");
            Assert.Matches("^[0-9a-f]{64}$", entry.sha256);
        }
    }

    /// <summary>The sample is only a real guard if the endpoint actually serves from it.</summary>
    [Fact]
    public void TheSamplePointerServesOnceItsArtifactsExist()
    {
        var samplePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workbench-pointer.sample.json");
        var sample = JsonSerializer.Deserialize<WorkbenchDownloadEndpoints.DownloadPointer>(
            File.ReadAllText(samplePath),
            WorkbenchDownloadEndpoints.PointerJsonOptions)!;

        // Re-stamp the sample's digests and sizes to artifacts we actually write, so this test
        // covers the pointer's shape rather than the placeholder hashes it carries.
        var entries = sample.downloads!.Select((entry, index) =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"artifact {index} for {entry.id}");
            File.WriteAllBytes(Path.Combine(_root, entry.file), bytes);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return entry with { sha256 = sha, size_bytes = bytes.Length };
        }).ToList();

        WritePointer(JsonSerializer.Serialize(
            new WorkbenchDownloadEndpoints.DownloadPointer(1, entries),
            WorkbenchDownloadEndpoints.PointerJsonOptions));

        foreach (var entry in entries)
        {
            var resolution = Resolve(entry.id);
            Assert.Equal(WorkbenchDownloadEndpoints.DownloadOutcome.Served, resolution.Outcome);
            Assert.Equal(entry.sha256, resolution.Sha256);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DirectoryVariable, null);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

using System.IO.Compression;
using System.Text;
using Comfy.Quest.Studio;
using Xunit;

namespace Game.Companion.Tests;

public sealed class QuestPackPublisherTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "quest-publish-test-" + Guid.NewGuid().ToString("N"));

    public QuestPackPublisherTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "valheim.exe"), []);
    }

    [Fact]
    public async Task PublishesCertifiedPackAtomicallyAndIsIdempotent()
    {
        var publisher = new QuestPackPublisher(Host(_root));
        await using var first = Pack("1.0.0", "Skal!");
        var receipt = await publisher.PublishAsync(first, "demo-1.0.0.questpack", default);
        Assert.True(receipt.Ok);
        Assert.Equal("published", receipt.Status);
        Assert.True(File.Exists(Path.Combine(_root, "BepInEx", "config", "comfy-quest-runtime", "inbox", "demo-1.0.0.questpack")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "BepInEx", "config", "comfy-quest-runtime", "inbox"), ".incoming-*"));

        await using var duplicate = Pack("1.0.0", "Skal!");
        var again = await publisher.PublishAsync(duplicate, "renamed.questpack", default);
        Assert.True(again.Ok);
        Assert.True(again.AlreadyPresent);
    }

    [Fact]
    public async Task RejectsTraversalAndMalformedContent()
    {
        var publisher = new QuestPackPublisher(Host(_root));
        await using var valid = Pack("1.0.0", "Skal!");
        Assert.Equal("filename_invalid", (await publisher.PublishAsync(valid, "../escape.questpack", default)).Error);
        await using var malformed = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
        var result = await publisher.PublishAsync(malformed, "bad.questpack", default);
        Assert.Equal("pack_invalid", result.Error);
    }

    [Fact]
    public async Task SameVersionDifferentContentFailsClosed()
    {
        var publisher = new QuestPackPublisher(Host(_root));
        await using var first = Pack("1.0.0", "Skal!");
        Assert.True((await publisher.PublishAsync(first, "a.questpack", default)).Ok);
        await using var collision = Pack("1.0.0", "Hail!");
        var result = await publisher.PublishAsync(collision, "b.questpack", default);
        Assert.Equal("same_version_hash_collision", result.Error);
    }

    [Fact]
    public async Task MissingValheimReturnsManualCopyFallback()
    {
        // Host.FindValheim() returning null is the contract for "not found" (mirrors
        // ValheimLocator.Find() returning null when valheim.exe isn't present).
        var publisher = new QuestPackPublisher(Host(null));
        await using var pack = Pack("1.0.0", "Skal!");
        var result = await publisher.PublishAsync(pack, "demo.questpack", default);
        Assert.Equal("valheim_not_found", result.Error);
        Assert.True(result.ManualCopyAvailable);
    }

    TestQuestStudioHost Host(string? valheimPath) => new() { StateDirectory = _root, ValheimPath = valheimPath };

    static MemoryStream Pack(string version, string message)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write($"{{\"schema\":\"comfy-quest-pack/v2\",\"pack_id\":\"demo\",\"version\":\"{version}\"}}");
            using (var writer = new StreamWriter(zip.CreateEntry("experiences/demo.json").Open()))
                writer.Write("{\"schema\":\"comfy-quest-experience/v1\",\"id\":\"demo\",\"entry_stage\":\"start\",\"stages\":[{\"id\":\"start\",\"transitions\":[{\"id\":\"done\",\"priority\":1,\"when\":{\"op\":\"EVENT\",\"event\":\"kill\"},\"actions\":[{\"id\":\"say\",\"type\":\"message\",\"text\":\"" + message + "\"}],\"outcome\":\"complete\"}]}]}");
        }
        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

using Lumberjacks.Companion;
using ComfyQuestContracts;
using System.Text.Json;
using Xunit;

namespace Game.Companion.Tests;

public sealed class QuestStudioServiceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "quest-studio-test-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string,string?> _prior = new();

    public QuestStudioServiceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "valheim.exe"), []);
        Set("LUMBERJACKS_VALHEIM_PATH", _root);
        Set("LUMBERJACKS_COMPANION_DATA", Path.Combine(_root, "data"));
    }

    [Fact] public void StarterCertifiesThroughSharedContract()
    {
        var studio = Create();
        var result = studio.Certify(QuestStudioProject.Starter());
        Assert.True(result.Ok);
        Assert.NotNull(result.ContentHash);
        Assert.Contains("comfy-quest-experience/v1", result.ExperienceJson);
    }

    [Fact] public void SaveIsDurableAndUnknownEventsFailClosed()
    {
        var studio = Create();
        var project = QuestStudioProject.Starter() with { Title = "A Durable Rune" };
        Assert.True(studio.Save(project).Ok);
        Assert.Equal("A Durable Rune", Create().Read().Title);
        Assert.Equal("event_unknown", studio.Certify(project with { Event = "arbitrary_rpc" }).Error);
    }

    [Fact] public async Task PublishBuildsHashedPackAndUsesConfinedPublisher()
    {
        var result = await Create().PublishAsync(QuestStudioProject.Starter(), default);
        Assert.True(result.Ok);
        Assert.Equal("published", result.Status);
        Assert.NotNull(result.Receipt?.ContentHash);
        Assert.True(File.Exists(Path.Combine(_root, "BepInEx", "config", "comfy-quest-runtime", "inbox", "first-quest-1.0.0.questpack")));
    }

    [Fact] public void ReadsRuntimeReceiptsWithoutLiveConnection()
    {
        var runtimeRoot = Path.Combine(_root, "BepInEx", "config", "comfy-quest-runtime");
        new RuntimeReceiptStore(runtimeRoot).Write(new RuntimeReceipt { Operation = "load", Status = "activated", PackId = "demo", Version = "1.0.0" });
        var json = JsonSerializer.Serialize(Create().Receipts());
        Assert.Contains("activated", json);
        Assert.Contains("demo", json);
    }

    [Fact] public void SavesImmutableHistoryAndReturnsSemanticDiff()
    {
        var studio = Create();
        var first = QuestStudioProject.Starter();
        var second = first with { Version = "1.1.0", Title = "A Changed Rune", Message = "A different answer." };
        var firstHash = studio.Certify(first).ContentHash!;
        var secondHash = studio.Certify(second).ContentHash!;
        Assert.True(studio.Save(first).Ok);
        Assert.True(studio.Save(second).Ok);
        var history = JsonSerializer.Serialize(studio.History());
        Assert.Contains(firstHash, history);
        Assert.Contains(secondHash, history);
        var diff = studio.Diff(firstHash, secondHash);
        Assert.True(diff.Ok);
        Assert.Contains(diff.Changes, x => x.Field == "version" && x.To == "1.1.0");
        Assert.Contains(diff.Changes, x => x.Field == "message" && x.To == "A different answer.");
        Assert.Equal("history_hash_invalid", studio.Diff("../escape", secondHash).Error);
    }

    QuestStudioService Create()
    {
        var state = new CompanionStateStore();
        var workbench = new WorkbenchStore(state);
        var locator = new ValheimLocator();
        return new QuestStudioService(workbench, new QuestPackPublisher(locator), locator);
    }
    void Set(string key,string value){_prior[key]=Environment.GetEnvironmentVariable(key);Environment.SetEnvironmentVariable(key,value);}
    public void Dispose(){foreach(var p in _prior)Environment.SetEnvironmentVariable(p.Key,p.Value);if(Directory.Exists(_root))Directory.Delete(_root,true);}
}

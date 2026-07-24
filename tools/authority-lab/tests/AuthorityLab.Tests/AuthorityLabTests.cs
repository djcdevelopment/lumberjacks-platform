using System.Text.Json;
using AuthorityLab;
using ComfyNetworkSense;
using Xunit;

namespace AuthorityLab.Tests;

public sealed class AuthorityLabTests
{
    [Fact]
    public void BandPolicyHasExpectedBoundaryShape()
    {
        Assert.Equal(ZdoBandAction.EmitFull, ZdoBandPolicy.Classify(30, 30, 64, 0, 0, -1, .2));
        Assert.Equal(ZdoBandAction.EmitThinned, ZdoBandPolicy.Classify(30.1, 30, 64, 0, 0, -1, .2));
        Assert.Equal(ZdoBandAction.Drop, ZdoBandPolicy.Classify(64.1, 30, 64, 0, 0, -1, .2));
    }

    [Fact]
    public void FanoutKeepsRecipientsIndependent()
    {
        var decisions = ZdoFanoutPlan.Evaluate(7, 30, 64, 0, new[]
        {
            new FanoutObserverInput("a", 20, null),
            new FanoutObserverInput("b", 20, 7),
            new FanoutObserverInput("c", 90, null)
        });
        Assert.True(decisions[0].Emit);
        Assert.Equal(ZdoFanoutDisposition.AlreadyDelivered, decisions[1].Disposition);
        Assert.Equal(ZdoFanoutDisposition.OutOfBand, decisions[2].Disposition);
    }

    [Fact]
    public void ScenarioRejectsUnknownDriver()
    {
        var path = Path.Combine(Path.GetTempPath(), $"authority-scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"schema_version\":1,\"experiment_id\":\"m7-e00-lab-truth\",\"scenario_id\":\"bad\",\"seed\":1,\"plane\":\"relevance\",\"duration_seconds\":1,\"actors\":[{\"id\":\"a\",\"trajectory\":\"stationary\"}],\"objects\":{\"generator\":\"fixed\",\"count\":1,\"classes\":[\"player\"]},\"policy\":{\"name\":\"tiered\",\"near_meters\":30,\"outer_meters\":64,\"mid_hz\":5},\"driver\":\"nope\",\"stop_rules\":[]}");
        Assert.Throws<ScenarioException>(() => Scenario.Load(path));
        File.Delete(path);
    }

    [Fact]
    public void NormalizedScenarioSerializationUsesSnakeCase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"authority-scenario-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"schema_version\":1,\"experiment_id\":\"m7-e00-lab-truth\",\"scenario_id\":\"case\",\"seed\":1,\"plane\":\"relevance\",\"duration_seconds\":1,\"actors\":[{\"id\":\"a\",\"trajectory\":\"stationary\"}],\"objects\":{\"generator\":\"fixed\",\"count\":1,\"classes\":[\"player\"]},\"policy\":{\"name\":\"tiered\",\"near_meters\":30,\"outer_meters\":64,\"mid_hz\":5},\"driver\":\"pure\",\"stop_rules\":[]}");
        var json = Scenario.Load(path).NormalizedJson();
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("schema_version", out _));
        Assert.False(json.Contains("SchemaVersion", StringComparison.Ordinal));
        File.Delete(path);
    }

    [Fact]
    public void FanoutDoesNotEmitDuplicateRecipient()
    {
        var decisions = ZdoFanoutPlan.Evaluate(3, 30, 64, 0, new[]
        {
            new FanoutObserverInput("a", 20, null),
            new FanoutObserverInput("a", 20, null)
        });
        Assert.Single(decisions, d => d.Emit);
    }

    [Fact]
    public void LandmarkOverridesDistanceBand()
    {
        Assert.Equal(ZdoBandAction.Landmark, ZdoBandPolicy.Classify(80, 30, 64, 100, 0, -1, .2));
    }
}

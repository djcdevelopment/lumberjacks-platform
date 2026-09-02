using System;
using System.IO;
using CommunitySurvival.Forest;
using Xunit;

namespace Game.Contracts.Tests;

public sealed class ForestStormScenarioTests
{
    [Fact]
    public void CanonicalScenarioRoundTripsAndHashes()
    {
        var scenario = new ForestStormScenario();
        scenario.Validate();

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, scenario.ToCanonicalJson());
            var loaded = ForestStormScenario.Load(path);
            Assert.Equal(scenario.ContentHash(), loaded.ContentHash());
            Assert.Equal(0f, loaded.EvaluateIntensity(0f));
            Assert.Equal(loaded.EvaluateIntensity(3.25f), loaded.EvaluateIntensity(83.25f), 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidationRejectsNonFiniteAndOutOfRangeInputs()
    {
        Assert.Throws<InvalidDataException>(() =>
            (new ForestStormScenario { PeakWindMetersPerSecond = float.NaN }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (new ForestStormScenario { WindDirectionDegrees = 360f }).Validate());
        Assert.Throws<InvalidDataException>(() =>
            (new ForestStormScenario { IntensityCurve = [0f, 1f] }).Validate());
    }

    [Fact]
    public void PlacementIsDeterministicAndChunked()
    {
        var scenario = new ForestStormScenario { Seed = 7429u };
        var first = ForestPlacementGenerator.Generate(scenario, 512, 64);
        var second = ForestPlacementGenerator.Generate(scenario, 512, 64);

        Assert.Equal(512, first.Placements.Count);
        Assert.Equal(first.PlacementHash, second.PlacementHash);
        Assert.All(first.Placements, placement =>
        {
            Assert.InRange(placement.ChunkX, 0, 7);
            Assert.InRange(placement.ChunkZ, 0, 7);
            Assert.InRange(placement.Moisture, 0f, 1f);
            Assert.True(float.IsFinite(placement.Y));
        });
    }

    [Fact]
    public void DifferentSeedsProduceDifferentForests()
    {
        var first = ForestPlacementGenerator.Generate(new ForestStormScenario { Seed = 1u }, 256, 64);
        var second = ForestPlacementGenerator.Generate(new ForestStormScenario { Seed = 2u }, 256, 64);
        Assert.NotEqual(first.PlacementHash, second.PlacementHash);
    }
}

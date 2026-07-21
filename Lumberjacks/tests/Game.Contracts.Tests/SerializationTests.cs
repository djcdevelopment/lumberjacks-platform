using System.Text.Json;
using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.Contracts.Protocol;
using Xunit;

namespace Game.Contracts.Tests;

public class SerializationTests
{
    [Fact]
    public void Vec3_serializes_to_snake_case()
    {
        var vec = new Vec3(1.0, 2.5, 3.0);
        var json = JsonSerializer.Serialize(vec, JsonOptions.Default);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("x", out _));
        Assert.True(doc.RootElement.TryGetProperty("y", out _));
        Assert.True(doc.RootElement.TryGetProperty("z", out _));
    }

    [Fact]
    public void Player_serializes_to_snake_case()
    {
        var player = new Player
        {
            Id = "p1",
            Name = "TestPlayer",
            Position = new Vec3(0, 0, 0),
            RegionId = "region-spawn",
        };

        var json = JsonSerializer.Serialize(player, JsonOptions.Default);
        Assert.Contains("\"region_id\"", json);
        Assert.Contains("\"guild_id\"", json);
    }

    [Fact]
    public void Player_round_trips_through_json()
    {
        var player = new Player
        {
            Id = "p1",
            Name = "Test",
            GuildId = "g1",
            Rank = 5,
            Position = new Vec3(1, 2, 3),
            RegionId = "region-spawn",
            Connected = true,
        };

        var json = JsonSerializer.Serialize(player, JsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<Player>(json, JsonOptions.Default);

        Assert.NotNull(deserialized);
        Assert.Equal(player.Id, deserialized.Id);
        Assert.Equal(player.Name, deserialized.Name);
        Assert.Equal(player.GuildId, deserialized.GuildId);
        Assert.Equal(player.Rank, deserialized.Rank);
        Assert.Equal(player.Position, deserialized.Position);
        Assert.Equal(player.RegionId, deserialized.RegionId);
        Assert.Equal(player.Connected, deserialized.Connected);
    }

    [Fact]
    public void GameEvent_serializes_to_snake_case()
    {
        var evt = new GameEvent
        {
            EventId = "e1",
            EventType = EventType.PlayerConnected,
            OccurredAt = DateTimeOffset.UtcNow,
            WorldId = "world-default",
            SourceService = "test",
            SchemaVersion = 1,
            Payload = JsonSerializer.SerializeToElement(new { }),
        };

        var json = JsonSerializer.Serialize(evt, JsonOptions.Default);
        Assert.Contains("\"event_id\"", json);
        Assert.Contains("\"event_type\"", json);
        Assert.Contains("\"occurred_at\"", json);
        Assert.Contains("\"world_id\"", json);
        Assert.Contains("\"source_service\"", json);
        Assert.Contains("\"schema_version\"", json);
    }

    [Fact]
    public void Region_round_trips_through_json()
    {
        var region = new Region
        {
            Id = "region-spawn",
            Name = "Spawn Island",
            BoundsMin = new Vec3(-500, -10, -500),
            BoundsMax = new Vec3(500, 200, 500),
            Active = true,
            PlayerCount = 42,
            TickRate = 20,
        };

        var json = JsonSerializer.Serialize(region, JsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<Region>(json, JsonOptions.Default);

        Assert.NotNull(deserialized);
        Assert.Equal(region.Id, deserialized.Id);
        Assert.Equal(region.BoundsMin, deserialized.BoundsMin);
        Assert.Equal(region.BoundsMax, deserialized.BoundsMax);
        Assert.Equal(region.PlayerCount, deserialized.PlayerCount);
    }
}

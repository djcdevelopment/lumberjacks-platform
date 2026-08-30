using Game.Contracts.Entities;
using Game.Contracts.Protocol;
using Game.Simulation.Tick;
using Game.Simulation.World;
using Xunit;

namespace Game.Simulation.Tests;

public class SimulationStepTests
{
    private static WorldState CreateWorldWithPlayer(string playerId = "player-1", Vec3? position = null, Vec3? velocity = null)
    {
        var world = new WorldState();
        world.Players[playerId] = new Player
        {
            Id = playerId,
            Name = "Test",
            Position = position ?? new Vec3(0, 0, 0),
            Velocity = velocity ?? new Vec3(0, 0, 0),
            RegionId = "region-spawn",
            Connected = true,
        };
        return world;
    }

    [Fact]
    public void InputMovesPlayerInDirection()
    {
        var world = CreateWorldWithPlayer();
        var queue = new InputQueue();

        // Direction 0 = north (positive Z in our coordinate system)
        queue.Enqueue("player-1", new PlayerInputMessage
        {
            Direction = 0,
            SpeedPercent = 100,
            InputSeq = 1,
        }, currentTick: 0);

        var changed = SimulationStep.Execute(world, queue, tick: 1);

        Assert.Contains("player-1", changed.PlayerIds);
        var player = world.Players["player-1"];
        // Direction 0 → heading 0° → sin(0)=0 for X, cos(0)=1 for Z
        Assert.Equal(0, player.Position.X, precision: 5);
        Assert.True(player.Position.Z > 0, "Player should move in positive Z direction");
    }

    [Fact]
    public void SpeedClampedTo100Percent()
    {
        var world = CreateWorldWithPlayer();
        var queue = new InputQueue();

        queue.Enqueue("player-1", new PlayerInputMessage
        {
            Direction = 0,
            SpeedPercent = 200, // exceeds max
            InputSeq = 1,
        }, currentTick: 0);

        SimulationStep.Execute(world, queue, tick: 1);

        var player = world.Players["player-1"];
        // Should be clamped to 100% → MaxSpeedPerTick
        Assert.Equal(SimulationStep.MaxSpeedPerTick, player.Position.Z, precision: 5);
    }

    [Fact]
    public void PositionClampedToRegionBounds()
    {
        var world = CreateWorldWithPlayer(position: new Vec3(499, 0, 499));
        var queue = new InputQueue();

        // Push player beyond region bounds (region-spawn max is 500)
        queue.Enqueue("player-1", new PlayerInputMessage
        {
            Direction = 0, // north → +Z
            SpeedPercent = 100,
            InputSeq = 1,
        }, currentTick: 0);

        SimulationStep.Execute(world, queue, tick: 1);

        var player = world.Players["player-1"];
        Assert.True(player.Position.Z <= 500, "Position should be clamped to region bounds");
    }

    [Fact]
    public void FrictionDeceleratesStationaryInput()
    {
        // Player has velocity but no input this tick → friction applies
        var world = CreateWorldWithPlayer(velocity: new Vec3(0, 0, 5));
        var queue = new InputQueue();

        var changed = SimulationStep.Execute(world, queue, tick: 1);

        Assert.Contains("player-1", changed.PlayerIds);
        var player = world.Players["player-1"];
        // Velocity should decrease by FrictionPerTick
        Assert.True(player.Velocity.Z < 5, "Velocity should decrease due to friction");
        Assert.True(player.Velocity.Z > 0, "Velocity should still be positive (not fully stopped)");
    }

    [Fact]
    public void FrictionStopsPlayerAtLowSpeed()
    {
        // Velocity below friction threshold → full stop
        var world = CreateWorldWithPlayer(velocity: new Vec3(0, 0, 0.05));
        var queue = new InputQueue();

        SimulationStep.Execute(world, queue, tick: 1);

        var player = world.Players["player-1"];
        Assert.Equal(0, player.Velocity.X);
        Assert.Equal(0, player.Velocity.Y);
        Assert.Equal(0, player.Velocity.Z);
    }

    [Fact]
    public void DisconnectedPlayersSkipped()
    {
        var world = new WorldState();
        world.Players["disconnected"] = new Player
        {
            Id = "disconnected",
            Name = "Ghost",
            Position = new Vec3(0, 0, 0),
            Velocity = new Vec3(0, 0, 5),
            RegionId = "region-spawn",
            Connected = false,
        };

        var queue = new InputQueue();
        queue.Enqueue("disconnected", new PlayerInputMessage
        {
            Direction = 0,
            SpeedPercent = 100,
            InputSeq = 1,
        }, currentTick: 0);

        var changed = SimulationStep.Execute(world, queue, tick: 1);

        Assert.DoesNotContain("disconnected", changed.PlayerIds);
    }

    [Fact]
    public void ZeroVelocityPlayerNotMarkedChanged()
    {
        var world = CreateWorldWithPlayer(); // velocity = (0,0,0)
        var queue = new InputQueue();

        var changed = SimulationStep.Execute(world, queue, tick: 1);

        Assert.DoesNotContain("player-1", changed.PlayerIds);
    }

    [Fact]
    public void InputSeqUpdatedOnPlayer()
    {
        var world = CreateWorldWithPlayer();
        var queue = new InputQueue();

        queue.Enqueue("player-1", new PlayerInputMessage
        {
            Direction = 0,
            SpeedPercent = 50,
            InputSeq = 42,
        }, currentTick: 0);

        SimulationStep.Execute(world, queue, tick: 1);

        Assert.Equal(42, world.Players["player-1"].LastInputSeq);
    }

    [Fact]
    public void AxePressStrikesNearestResourceOnceAndReturnsMutation()
    {
        var world = CreateWorldWithPlayer();
        world.Players["player-1"] = world.Players["player-1"] with { EquippedItemType = "axe" };
        AddTree(world, "tree-far", new Vec3(0, 0, 2));
        AddTree(world, "tree-near", new Vec3(0, 0, 1));
        var queue = new InputQueue();

        EnqueueAxe(queue, tick: 0, flags: SimulationStep.AxeActionFlag);
        var first = SimulationStep.Execute(world, queue, tick: 1);
        EnqueueAxe(queue, tick: 1, flags: SimulationStep.AxeActionFlag);
        var held = SimulationStep.Execute(world, queue, tick: 2);

        Assert.Equal(87.5, world.NaturalResources["tree-near"].Health);
        Assert.Equal(100, world.NaturalResources["tree-far"].Health);
        var mutation = Assert.Single(first.ResourceMutations);
        Assert.Equal("tree-near", mutation.ResourceId);
        Assert.Equal(1, mutation.StrikeCount);
        Assert.Empty(held.ResourceMutations);
    }

    [Fact]
    public void AxeCooldownRejectsRapidReleaseAndRepress()
    {
        var world = CreateWorldWithPlayer();
        world.Players["player-1"] = world.Players["player-1"] with { EquippedItemType = "axe" };
        AddTree(world, "tree", new Vec3(0, 0, 1));
        var queue = new InputQueue();

        EnqueueAxe(queue, tick: 0, flags: SimulationStep.AxeActionFlag);
        SimulationStep.Execute(world, queue, tick: 1);
        EnqueueAxe(queue, tick: 1, flags: 0);
        SimulationStep.Execute(world, queue, tick: 2);
        EnqueueAxe(queue, tick: 2, flags: SimulationStep.AxeActionFlag);
        var tooSoon = SimulationStep.Execute(world, queue, tick: 3);
        EnqueueAxe(queue, tick: 9, flags: 0);
        SimulationStep.Execute(world, queue, tick: 10);
        EnqueueAxe(queue, tick: 10, flags: SimulationStep.AxeActionFlag);
        var ready = SimulationStep.Execute(world, queue, tick: 11);

        Assert.Empty(tooSoon.ResourceMutations);
        Assert.Single(ready.ResourceMutations);
        Assert.Equal(75, world.NaturalResources["tree"].Health);
    }

    [Fact]
    public void TerminalStrikeRecordsDeterministicFallHeading()
    {
        var world = CreateWorldWithPlayer(position: new Vec3(0, 0, 0));
        world.Players["player-1"] = world.Players["player-1"] with { EquippedItemType = "axe" };
        AddTree(world, "tree", new Vec3(1, 0, 0), health: SimulationStep.AxeDamage);
        world.RegionProfiles["region-spawn"] = new RegionProfile
        {
            Id = "profile",
            RegionId = "region-spawn",
            TradeWindX = 0,
            TradeWindZ = 1,
        };
        var queue = new InputQueue();

        EnqueueAxe(queue, tick: 0, flags: SimulationStep.AxeActionFlag);
        var result = SimulationStep.Execute(world, queue, tick: 1);

        var mutation = Assert.Single(result.ResourceMutations);
        Assert.True(mutation.Felled);
        Assert.Equal(0, mutation.Resource.Health);
        Assert.Equal("70.7", mutation.Resource.GrowthHistory["fall_heading"]);
    }

    [Fact]
    public void FellingEventIdentityIsStableAndWorldScoped()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-30T12:00:00Z");

        var first = NaturalResourcePersistenceWorker.CreateFellingEventId("world-a", "tree", createdAt);
        var repeated = NaturalResourcePersistenceWorker.CreateFellingEventId("world-a", "tree", createdAt);
        var otherWorld = NaturalResourcePersistenceWorker.CreateFellingEventId("world-b", "tree", createdAt);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherWorld);
        Assert.True(Guid.TryParse(first, out _));
    }

    private static void AddTree(WorldState world, string id, Vec3 position, double health = 100)
    {
        world.NaturalResources[id] = new NaturalResource
        {
            Id = id,
            Type = "eastern_white_pine",
            Position = position,
            RegionId = "region-spawn",
            Health = health,
        };
        world.SpatialGrid.Update(id, position);
    }

    private static void EnqueueAxe(InputQueue queue, long tick, byte flags)
    {
        queue.Enqueue("player-1", new PlayerInputMessage
        {
            Direction = 0,
            SpeedPercent = 0,
            InputSeq = checked((ushort)(tick + 1)),
            ActionFlags = flags,
        }, currentTick: tick);
    }
}

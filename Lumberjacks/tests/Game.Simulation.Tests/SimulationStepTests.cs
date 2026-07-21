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
        var world = CreateWorldWithPlayer(velocity: new Vec3(0, 0, 1.0));
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
}

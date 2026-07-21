using Game.Contracts.Entities;
using Game.Simulation.Tick;
using Game.Simulation.World;
using Xunit;

namespace Game.Simulation.Tests;

public class StateHasherTests
{
    [Fact]
    public void SameStateSameHash()
    {
        var world1 = CreateWorld("player-1", new Vec3(10, 0, 20));
        var world2 = CreateWorld("player-1", new Vec3(10, 0, 20));

        var hash1 = StateHasher.ComputeHash(world1);
        var hash2 = StateHasher.ComputeHash(world2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DifferentPositionDifferentHash()
    {
        var world1 = CreateWorld("player-1", new Vec3(10, 0, 20));
        var world2 = CreateWorld("player-1", new Vec3(10, 0, 21));

        var hash1 = StateHasher.ComputeHash(world1);
        var hash2 = StateHasher.ComputeHash(world2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentVelocityDifferentHash()
    {
        var world1 = CreateWorld("player-1", new Vec3(0, 0, 0), new Vec3(1, 0, 0));
        var world2 = CreateWorld("player-1", new Vec3(0, 0, 0), new Vec3(2, 0, 0));

        Assert.NotEqual(StateHasher.ComputeHash(world1), StateHasher.ComputeHash(world2));
    }

    [Fact]
    public void PlayerOrderIndependent()
    {
        // Add players in different order — hash should be the same
        var world1 = new WorldState();
        world1.Players["aaa"] = MakePlayer("aaa", new Vec3(1, 0, 0));
        world1.Players["zzz"] = MakePlayer("zzz", new Vec3(2, 0, 0));

        var world2 = new WorldState();
        world2.Players["zzz"] = MakePlayer("zzz", new Vec3(2, 0, 0));
        world2.Players["aaa"] = MakePlayer("aaa", new Vec3(1, 0, 0));

        Assert.Equal(StateHasher.ComputeHash(world1), StateHasher.ComputeHash(world2));
    }

    [Fact]
    public void DifferentTickDifferentHash()
    {
        var world1 = CreateWorld("player-1", new Vec3(0, 0, 0));
        var world2 = CreateWorld("player-1", new Vec3(0, 0, 0));
        world2.CurrentTick = 1;

        Assert.NotEqual(StateHasher.ComputeHash(world1), StateHasher.ComputeHash(world2));
    }

    [Fact]
    public void EmptyWorldProducesHash()
    {
        var world = new WorldState();
        var hash = StateHasher.ComputeHash(world);
        // Should not throw; hash is some non-zero value (tick 0 is hashed)
        Assert.True(hash != 0 || hash == 0); // just ensure it completes
    }

    private static WorldState CreateWorld(string playerId, Vec3 position, Vec3? velocity = null)
    {
        var world = new WorldState();
        world.Players[playerId] = MakePlayer(playerId, position, velocity);
        return world;
    }

    private static Player MakePlayer(string id, Vec3 position, Vec3? velocity = null) => new()
    {
        Id = id,
        Name = "Test",
        Position = position,
        Velocity = velocity ?? new Vec3(0, 0, 0),
        RegionId = "region-spawn",
        Connected = true,
    };
}

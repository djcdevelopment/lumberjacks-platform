using System.Collections.Concurrent;
using Game.Contracts.Entities;

namespace Game.Simulation.World;

public class WorldState
{
    public ConcurrentDictionary<string, Region> Regions { get; } = new();
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Structure> Structures { get; } = new();
    public ConcurrentDictionary<string, WorldItem> WorldItems { get; } = new();
    public ConcurrentDictionary<string, NaturalResource> NaturalResources { get; } = new();
    public ConcurrentDictionary<string, RegionProfile> RegionProfiles { get; } = new();

    /// <summary>Monotonically increasing tick counter, incremented by TickLoop at 20Hz.</summary>
    public long CurrentTick { get; set; }

    /// <summary>When the tick loop started (for uptime reporting).</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>CRC32 hash of world state at the last tick. Used for desync detection.</summary>
    public uint LastStateHash { get; set; }

    /// <summary>Spatial index for fast radius queries. Updated by SimulationStep and PlayerHandler.</summary>
    public SpatialGrid SpatialGrid { get; } = new();

    public WorldState()
    {
        // Seed with default spawn region matching TS service
        Regions["region-spawn"] = new Region
        {
            Id = "region-spawn",
            Name = "Spawn Island",
            BoundsMin = new Vec3(-500, -10, -500),
            BoundsMax = new Vec3(500, 200, 500),
            Active = true,
            PlayerCount = 0,
            TickRate = 20,
        };
    }
}

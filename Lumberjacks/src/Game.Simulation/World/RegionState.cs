using System.Collections.Concurrent;
using Game.Contracts.Entities;

namespace Game.Simulation.World;

public class RegionState
{
    public required string RegionId { get; init; }
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Structure> Structures { get; } = new();
}

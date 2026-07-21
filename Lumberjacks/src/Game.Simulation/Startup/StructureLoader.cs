using System.Text.Json;
using Game.Contracts.Entities;
using Game.Persistence;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Startup;

public class StructureLoader
{
    private readonly WorldState _world;
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly ILogger<StructureLoader> _logger;

    public StructureLoader(WorldState world, IDbContextFactory<GameDbContext> dbFactory, ILogger<StructureLoader> logger)
    {
        _world = world;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.Structures.ToListAsync();

        foreach (var e in entities)
        {
            var tags = new List<string>();
            try { tags = JsonSerializer.Deserialize<List<string>>(e.Tags) ?? []; } catch { }

            _world.Structures[e.Id] = new Structure
            {
                Id = e.Id,
                Type = e.Type,
                Position = new Vec3(e.PositionX, e.PositionY, e.PositionZ),
                Rotation = e.Rotation,
                OwnerId = e.OwnerId,
                RegionId = e.RegionId,
                PlacedAt = e.PlacedAt,
                Tags = tags,
            };
        }

        _logger.LogInformation("Loaded {Count} structures from database", entities.Count);
    }
}

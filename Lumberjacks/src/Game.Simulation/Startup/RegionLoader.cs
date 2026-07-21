using Game.Contracts.Entities;
using Game.Persistence;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Startup;

public class RegionLoader
{
    private readonly WorldState _world;
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly ILogger<RegionLoader> _logger;

    public RegionLoader(WorldState world, IDbContextFactory<GameDbContext> dbFactory, ILogger<RegionLoader> logger)
    {
        _world = world;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.Regions.ToListAsync();

        foreach (var e in entities)
        {
            _world.Regions[e.Id] = new Region
            {
                Id = e.Id,
                Name = e.Name,
                BoundsMin = new Vec3(e.BoundsMinX, e.BoundsMinY, e.BoundsMinZ),
                BoundsMax = new Vec3(e.BoundsMaxX, e.BoundsMaxY, e.BoundsMaxZ),
                Active = e.Active,
                PlayerCount = 0,
                TickRate = e.TickRate,
            };
        }

        _logger.LogInformation("Loaded {Count} regions from database (+ default spawn)", entities.Count);
    }
}

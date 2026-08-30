using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Game.Contracts.Entities;
using Game.Persistence;
using Game.Persistence.Entities;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Startup;

/// <summary>
/// Loads NaturalResources (Trees) from the DB or generates a forest if missing.
/// Implements Nature 2.0 "Phase 2" Biomimetic growth history.
/// </summary>
public class NaturalResourceLoader
{
    public const string FeaturedTreeId = "northwoods-old-pine";
    private readonly WorldState _world;
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly ILogger<NaturalResourceLoader> _logger;

    public NaturalResourceLoader(WorldState world, IDbContextFactory<GameDbContext> dbFactory, ILogger<NaturalResourceLoader> logger)
    {
        _world = world;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        
        foreach (var regionId in _world.Regions.Keys)
        {
            var entities = await db.NaturalResources.Where(n => n.RegionId == regionId).ToListAsync();
            
            var changed = false;
            if (entities.Count == 0)
            {
                _logger.LogInformation("Generating initial forest for region {RegionId}", regionId);
                entities = GenerateInitialForest(regionId);
                db.NaturalResources.AddRange(entities);
                changed = true;
            }

            if (string.Equals(regionId, "region-spawn", StringComparison.Ordinal) &&
                entities.All(entity => !string.Equals(entity.Id, FeaturedTreeId, StringComparison.Ordinal)))
            {
                var featured = CreateFeaturedTree(regionId);
                entities.Add(featured);
                db.NaturalResources.Add(featured);
                changed = true;
            }

            if (changed)
                await db.SaveChangesAsync();

            foreach (var e in entities)
            {
                _world.NaturalResources[e.Id] = new NaturalResource
                {
                    Id = e.Id,
                    Type = e.Type,
                    Position = new Vec3(e.PositionX, e.PositionY, e.PositionZ),
                    RegionId = e.RegionId,
                    Health = e.Health,
                    StumpHealth = e.StumpHealth,
                    RegrowthProgress = e.RegrowthProgress,
                    LeanX = e.LeanX,
                    LeanZ = e.LeanZ,
                    GrowthHistory = JsonSerializer.Deserialize<Dictionary<string, string>>(e.GrowthHistory) ?? [],
                    CreatedAt = e.CreatedAt,
                };
                
                _world.SpatialGrid.Update(e.Id, _world.NaturalResources[e.Id].Position);
            }
        }

        _logger.LogInformation("Loaded {Count} natural resources", _world.NaturalResources.Count);
    }

    private List<NaturalResourceEntity> GenerateInitialForest(string regionId)
    {
        var entities = new List<NaturalResourceEntity>();
        var random = new Random(StableSeed(regionId));
        
        if (!_world.RegionProfiles.TryGetValue(regionId, out var profile) || !_world.Regions.TryGetValue(regionId, out var region))
            return entities;

        // Density determined by HumidityMap
        // Trade winds affect "Twist" (Phase 2 history)
        var boundsMin = region.BoundsMin;
        var boundsMax = region.BoundsMax;
        var gridWidth = profile.GridWidth;
        var gridHeight = profile.GridHeight;

        for (int i = 0; i < gridWidth * gridHeight; i++)
        {
            double humidity = profile.HumidityGrid[i];
            double altitude = profile.AltitudeGrid[i];

            // Trees prefer mid-humidity and lower altitudes (above sea level)
            if (humidity > 0.4 && altitude is > 5.0 and < 150.0)
            {
                // Chance to spawn tree in this grid cell
                if (random.NextDouble() < (humidity * 0.15))
                {
                    int gx = i % gridWidth;
                    int gy = i / gridWidth;
                    
                    // Map grid position to world coordinates
                    double worldX = boundsMin.X + (gx * (boundsMax.X - boundsMin.X) / gridWidth) + (random.NextDouble() * 5.0);
                    double worldZ = boundsMin.Z + (gy * (boundsMax.Z - boundsMin.Z) / gridHeight) + (random.NextDouble() * 5.0);
                    
                    // PoC Override: Ensure some trees are near (0,0,0) and visible from start
                    if (gx < 4 && gy < 4) 
                    {
                        worldX = (gx * 4.0) - 8.0; 
                        worldZ = (gy * 4.0) - 8.0;
                    }

                    // Calculate "Twist" based on TradeWinds at this growth years (Phase 2 history)
                    var age = random.Next(20, 200);
                    var twist = (profile.TradeWindX + profile.TradeWindZ) * (age / 100.0) * (random.NextDouble() * 0.5);
                    var survivedFire = random.NextDouble() > 0.9;

                    // Preserve an authored clearing around spawn.  This also makes nearest-target
                    // selection obvious in the first public slice.
                    if (worldX * worldX + worldZ * worldZ < 144)
                        continue;

                    entities.Add(new NaturalResourceEntity
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = "oak_tree", // Randomize based on biome later
                        PositionX = worldX,
                        PositionY = altitude,
                        PositionZ = worldZ,
                        RegionId = regionId,
                        Health = 100.0,
                        StumpHealth = 50.0,
                        RegrowthProgress = 0.0,
                        LeanX = 0,
                        LeanZ = 0,
                        GrowthHistory = JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["twist"] = twist.ToString("F2"),
                            ["age_years"] = age.ToString(),
                            ["fire_scars"] = survivedFire.ToString()
                        })
                    });
                }
            }
        }

        return entities;
    }

    private static NaturalResourceEntity CreateFeaturedTree(string regionId) => new()
    {
        Id = FeaturedTreeId,
        Type = "pine_tree",
        PositionX = 0,
        PositionY = 0,
        PositionZ = 5,
        RegionId = regionId,
        Health = 100,
        StumpHealth = 50,
        RegrowthProgress = 0,
        LeanX = 0,
        LeanZ = 0,
        GrowthHistory = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["featured"] = "true",
            ["name"] = "The Storm Pine",
            ["species"] = "eastern white pine",
            ["age_years"] = "173",
            ["twist"] = "1.35",
            ["fire_scars"] = "True",
            ["field_note"] = "Storm-scarred, wind-shaped, and still standing.",
            ["strike_count"] = "0",
        }),
    };

    private static int StableSeed(string value) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);
}

using System.Text.Json;
using Game.Contracts.Entities;
using Game.Persistence;
using Game.Persistence.Entities;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Startup;

/// <summary>
/// Loads RegionProfiles from the DB or generates a new one based on the region seed.
/// Implements Nature 2.0 "Phase 0" environmental baseline.
/// </summary>
public class RegionProfileLoader
{
    private readonly WorldState _world;
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly ILogger<RegionProfileLoader> _logger;

    public RegionProfileLoader(WorldState world, IDbContextFactory<GameDbContext> dbFactory, ILogger<RegionProfileLoader> logger)
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
            var entity = await db.RegionProfiles.FirstOrDefaultAsync(p => p.RegionId == regionId);
            
            if (entity == null)
            {
                _logger.LogInformation("Generating new climate profile for region {RegionId}", regionId);
                entity = GenerateDefaultProfile(regionId);
                db.RegionProfiles.Add(entity);
                await db.SaveChangesAsync();
            }

            _world.RegionProfiles[regionId] = new RegionProfile
            {
                Id = entity.Id,
                RegionId = entity.RegionId,
                AltitudeGrid = JsonSerializer.Deserialize<List<double>>(entity.AltitudeGrid) ?? [],
                HumidityGrid = JsonSerializer.Deserialize<List<double>>(entity.HumidityGrid) ?? [],
                GridWidth = entity.GridWidth,
                GridHeight = entity.GridHeight,
                TradeWindX = entity.TradeWindX,
                TradeWindZ = entity.TradeWindZ,
                GeologicHistory = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.GeologicHistory) ?? []
            };
        }

        _logger.LogInformation("Loaded {Count} region profiles", _world.RegionProfiles.Count);
    }

    private RegionProfileEntity GenerateDefaultProfile(string regionId)
    {
        // Deterministic generation based on ID
        var random = new Random(regionId.GetHashCode());
        var width = 50;
        var height = 50;
        
        var altitude = new List<double>();
        var humidity = new List<double>();
        
        // Simple procedural generation: 
        // Trade winds generally blow from East (+X) to West (-X) with some variation
        var tradeWindX = -5.0 + (random.NextDouble() * 2.0);
        var tradeWindZ = -2.0 + (random.NextDouble() * 4.0);

        for (int i = 0; i < width * height; i++)
        {
            // Simple noise-like altitude (higher in center for testing)
            int x = i % width;
            int y = i / width;
            double distToCenter = Math.Sqrt(Math.Pow(x - 25, 2) + Math.Pow(y - 25, 2));
            
            double alt = Math.Max(0, 100.0 - (distToCenter * 4.0)) + (random.NextDouble() * 10.0);
            altitude.Add(alt);
            
            // Humidity is higher at low altitudes (near "coast") and wind-ward side
            double hum = Math.Clamp(1.0 - (alt / 150.0) + (x / 100.0), 0.0, 1.0);
            humidity.Add(hum);
        }

        return new RegionProfileEntity
        {
            Id = Guid.NewGuid().ToString(),
            RegionId = regionId,
            AltitudeGrid = JsonSerializer.Serialize(altitude),
            HumidityGrid = JsonSerializer.Serialize(humidity),
            GridWidth = width,
            GridHeight = height,
            TradeWindX = tradeWindX,
            TradeWindZ = tradeWindZ,
            GeologicHistory = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["volcanic_activity"] = random.NextDouble() > 0.8 ? "high" : "low",
                ["uplift_rate"] = (random.NextDouble() * 2.0).ToString("F2"),
                ["age_millions"] = random.Next(10, 500).ToString()
            })
        };
    }
}

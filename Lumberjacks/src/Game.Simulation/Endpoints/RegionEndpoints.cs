using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.Persistence;
using Game.Persistence.Entities;
using Game.ServiceDefaults;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Endpoints;

public static class RegionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/regions", (WorldState world) =>
            Results.Ok(world.Regions.Values.Select(r => FormatRegion(r))));

        app.MapGet("/regions/{id}", (string id, WorldState world) =>
        {
            if (!world.Regions.TryGetValue(id, out var region))
                return Results.NotFound(new { error = "Region not found" });

            return Results.Ok(FormatRegion(region));
        });

        app.MapPost("/regions", async (CreateRegionRequest request, WorldState world, IDbContextFactory<GameDbContext> dbFactory) =>
        {
            // Validate bounds: min must be less than max on all axes
            if (request.BoundsMin.X >= request.BoundsMax.X ||
                request.BoundsMin.Y >= request.BoundsMax.Y ||
                request.BoundsMin.Z >= request.BoundsMax.Z)
            {
                return Results.BadRequest(new { error = "bounds_min must be less than bounds_max on all axes" });
            }

            var id = request.Id ?? $"region-{Guid.NewGuid().ToString("N")[..8]}";

            if (world.Regions.ContainsKey(id))
                return Results.Conflict(new { error = "Region already exists", region_id = id });

            var region = new Region
            {
                Id = id,
                Name = request.Name ?? id,
                BoundsMin = request.BoundsMin,
                BoundsMax = request.BoundsMax,
                Active = true,
                PlayerCount = 0,
                TickRate = request.TickRate > 0 ? request.TickRate : 20,
            };

            world.Regions[id] = region;

            // Public telemetry feed (G4): a region became active — capture the region id only,
            // at the in-process seam. No actor, no detail. Pre-persistence.
            GameplayEventFeed.Capture(EventType.RegionActivated, regionId: region.Id, detail: null, provenance: "observed");

            // Persist to Postgres
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Regions.Add(new RegionEntity
            {
                Id = region.Id,
                Name = region.Name,
                BoundsMinX = region.BoundsMin.X,
                BoundsMinY = region.BoundsMin.Y,
                BoundsMinZ = region.BoundsMin.Z,
                BoundsMaxX = region.BoundsMax.X,
                BoundsMaxY = region.BoundsMax.Y,
                BoundsMaxZ = region.BoundsMax.Z,
                Active = region.Active,
                TickRate = region.TickRate,
            });
            await db.SaveChangesAsync();

            return Results.Created($"/regions/{id}", FormatRegion(region));
        });

        app.MapDelete("/regions/{id}", async (string id, WorldState world, IDbContextFactory<GameDbContext> dbFactory) =>
        {
            if (id == "region-spawn")
                return Results.BadRequest(new { error = "Cannot delete the spawn region" });

            if (!world.Regions.TryGetValue(id, out var region))
                return Results.NotFound(new { error = "Region not found" });

            if (region.PlayerCount > 0)
                return Results.BadRequest(new { error = "Cannot delete region with active players", player_count = region.PlayerCount });

            world.Regions.TryRemove(id, out _);

            // Public telemetry feed (G4): a region was deactivated/removed — region id only,
            // at the in-process seam. No actor, no detail. Pre-persistence.
            GameplayEventFeed.Capture(EventType.RegionDeactivated, regionId: id, detail: null, provenance: "observed");

            // Remove from Postgres
            await using var db = await dbFactory.CreateDbContextAsync();
            var entity = await db.Regions.FindAsync(id);
            if (entity != null)
            {
                db.Regions.Remove(entity);
                await db.SaveChangesAsync();
            }

            return Results.Ok(new { deleted = true, region_id = id });
        });
    }

    private static object FormatRegion(Region r) => new
    {
        id = r.Id,
        name = r.Name,
        bounds_min = new { x = r.BoundsMin.X, y = r.BoundsMin.Y, z = r.BoundsMin.Z },
        bounds_max = new { x = r.BoundsMax.X, y = r.BoundsMax.Y, z = r.BoundsMax.Z },
        active = r.Active,
        player_count = r.PlayerCount,
        tick_rate = r.TickRate,
    };
}

public record CreateRegionRequest
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public required Vec3 BoundsMin { get; init; }
    public required Vec3 BoundsMax { get; init; }
    public double TickRate { get; init; }
}

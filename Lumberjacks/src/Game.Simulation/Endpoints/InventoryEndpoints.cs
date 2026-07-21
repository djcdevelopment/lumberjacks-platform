using Game.Contracts.Entities;
using Game.Persistence;
using Game.Simulation.Handlers;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Endpoints;

public static class InventoryEndpoints
{
    public static void Map(WebApplication app)
    {
        // Spawn an item into the world (for testing / admin use)
        app.MapPost("/items/spawn", async (SpawnItemRequest request, WorldState world, IDbContextFactory<GameDbContext> dbFactory) =>
        {
            var item = new WorldItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = request.ItemType,
                Position = request.Position,
                RegionId = request.RegionId,
                Quantity = request.Quantity > 0 ? request.Quantity : 1,
                SpawnedAt = DateTimeOffset.UtcNow,
            };

            world.WorldItems[item.Id] = item;

            await using var db = await dbFactory.CreateDbContextAsync();
            db.WorldItems.Add(new Game.Persistence.Entities.WorldItemEntity
            {
                Id = item.Id,
                ItemType = item.ItemType,
                PositionX = item.Position.X,
                PositionY = item.Position.Y,
                PositionZ = item.Position.Z,
                RegionId = item.RegionId,
                Quantity = item.Quantity,
            });
            await db.SaveChangesAsync();

            return Results.Created($"/items/{item.Id}", new
            {
                item_id = item.Id,
                item_type = item.ItemType,
                position = new { x = item.Position.X, y = item.Position.Y, z = item.Position.Z },
                region_id = item.RegionId,
                quantity = item.Quantity,
            });
        });

        // List world items in a region
        app.MapGet("/items", (string? region_id, WorldState world) =>
        {
            var items = world.WorldItems.Values.AsEnumerable();
            if (!string.IsNullOrEmpty(region_id))
                items = items.Where(i => i.RegionId == region_id);

            return Results.Ok(items.Select(i => new
            {
                id = i.Id,
                item_type = i.ItemType,
                position = new { x = i.Position.X, y = i.Position.Y, z = i.Position.Z },
                region_id = i.RegionId,
                quantity = i.Quantity,
            }));
        });

        // Pick up an item (called by Gateway)
        app.MapPost("/items/pickup", async (PickupRequest request, InventoryHandler handler) =>
        {
            var result = await handler.PickupItemAsync(request.PlayerId, request.ItemId);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(new
            {
                picked_up = true,
                item_id = result.Item!.Id,
                item_type = result.Item.ItemType,
                quantity = result.Item.Quantity,
            });
        });

        // Store item in container (called by Gateway)
        app.MapPost("/items/store", async (StoreRequest request, InventoryHandler handler) =>
        {
            var result = await handler.StoreItemAsync(request.PlayerId, request.ContainerId, request.ItemType, request.Quantity);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(new
            {
                stored = true,
                item_type = result.ItemType,
                quantity = result.Quantity,
            });
        });

        // Get player inventory
        app.MapGet("/players/{id}/inventory", async (string id, InventoryHandler handler) =>
        {
            var items = await handler.GetInventoryAsync(id);
            return Results.Ok(items);
        });

        // Create a container attached to a structure
        app.MapPost("/containers", async (CreateContainerRequest request, IDbContextFactory<GameDbContext> dbFactory) =>
        {
            var container = new Game.Persistence.Entities.ContainerEntity
            {
                Id = Guid.NewGuid().ToString(),
                StructureId = request.StructureId,
                RegionId = request.RegionId,
            };

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Containers.Add(container);
            await db.SaveChangesAsync();

            return Results.Created($"/containers/{container.Id}", new
            {
                container_id = container.Id,
                structure_id = container.StructureId,
                region_id = container.RegionId,
            });
        });

        // Open a container (list contents)
        app.MapGet("/containers/{id}", async (string id, IDbContextFactory<GameDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var container = await db.Containers.FindAsync(id);
            if (container == null)
                return Results.NotFound(new { error = "Container not found" });

            var items = await db.ContainerItems
                .Where(ci => ci.ContainerId == id)
                .Select(ci => new { item_type = ci.ItemType, quantity = ci.Quantity })
                .ToListAsync();

            return Results.Ok(new
            {
                container_id = id,
                structure_id = container.StructureId,
                items,
            });
        });
    }
}

public record SpawnItemRequest
{
    public required string ItemType { get; init; }
    public required Vec3 Position { get; init; }
    public required string RegionId { get; init; }
    public int Quantity { get; init; } = 1;
}

public record PickupRequest
{
    public required string PlayerId { get; init; }
    public required string ItemId { get; init; }
}

public record StoreRequest
{
    public required string PlayerId { get; init; }
    public required string ContainerId { get; init; }
    public required string ItemType { get; init; }
    public int Quantity { get; init; } = 1;
}

public record CreateContainerRequest
{
    public required string StructureId { get; init; }
    public required string RegionId { get; init; }
}

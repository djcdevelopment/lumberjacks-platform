using System.Text.Json;
using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.Persistence;
using Game.Persistence.Entities;
using Game.ServiceDefaults;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Handlers;

public class InventoryHandler
{
    private readonly WorldState _world;
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<InventoryHandler> _logger;

    public InventoryHandler(
        WorldState world,
        IDbContextFactory<GameDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<InventoryHandler> logger)
    {
        _world = world;
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<PickupResult> PickupItemAsync(string playerId, string itemId)
    {
        if (!_world.Players.ContainsKey(playerId))
            return PickupResult.Fail("Player not in world");

        if (!_world.WorldItems.TryRemove(itemId, out var item))
            return PickupResult.Fail("Item not found");

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Remove from world_items
        var worldItem = await db.WorldItems.FindAsync(itemId);
        if (worldItem != null)
            db.WorldItems.Remove(worldItem);

        // Add to player inventory
        db.PlayerInventories.Add(new PlayerInventoryEntity
        {
            PlayerId = playerId,
            ItemType = item.ItemType,
            Quantity = item.Quantity,
        });

        await db.SaveChangesAsync();

        _logger.LogInformation("Player {PlayerId} picked up {ItemType} x{Qty}", playerId, item.ItemType, item.Quantity);

        // Public telemetry feed (G4): capture the public-safe projection — item category only,
        // never the player — at the in-process seam, before the out-of-process EventLog POST.
        GameplayEventFeed.Capture(EventType.ItemPickedUp, item.RegionId, item.ItemType);

        // Emit event
        _ = EmitEventAsync(EventType.ItemPickedUp, playerId, item.RegionId, new
        {
            item_id = item.Id,
            item_type = item.ItemType,
            quantity = item.Quantity,
        });

        return PickupResult.Ok(item);
    }

    public async Task<StoreResult> StoreItemAsync(string playerId, string containerId, string itemType, int quantity)
    {
        if (!_world.Players.ContainsKey(playerId))
            return StoreResult.Fail("Player not in world");

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Find the player's inventory slot
        var invSlot = await db.PlayerInventories
            .FirstOrDefaultAsync(i => i.PlayerId == playerId && i.ItemType == itemType);

        if (invSlot == null || invSlot.Quantity < quantity)
            return StoreResult.Fail("Not enough items in inventory");

        // Verify container exists
        var container = await db.Containers.FindAsync(containerId);
        if (container == null)
            return StoreResult.Fail("Container not found");

        // Remove from inventory
        invSlot.Quantity -= quantity;
        if (invSlot.Quantity <= 0)
            db.PlayerInventories.Remove(invSlot);

        // Add to container
        var existing = await db.ContainerItems
            .FirstOrDefaultAsync(c => c.ContainerId == containerId && c.ItemType == itemType);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            db.ContainerItems.Add(new ContainerItemEntity
            {
                ContainerId = containerId,
                ItemType = itemType,
                Quantity = quantity,
            });
        }

        await db.SaveChangesAsync();

        _logger.LogInformation("Player {PlayerId} stored {ItemType} x{Qty} in container {ContainerId}",
            playerId, itemType, quantity, containerId);

        // Public telemetry feed (G4): item category only, never the player. Pre-persistence seam.
        GameplayEventFeed.Capture(EventType.ItemStored, container.RegionId, itemType);

        // Emit event
        _ = EmitEventAsync(EventType.ItemStored, playerId, container.RegionId, new
        {
            container_id = containerId,
            item_type = itemType,
            quantity,
        });

        return StoreResult.Ok(itemType, quantity);
    }

    public async Task<List<InventorySlot>> GetInventoryAsync(string playerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.PlayerInventories
            .Where(i => i.PlayerId == playerId)
            .Select(i => new InventorySlot { ItemType = i.ItemType, Quantity = i.Quantity })
            .ToListAsync();
    }

    private async Task EmitEventAsync(string eventType, string actorId, string regionId, object payload)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var eventLogUrl = _config["ServiceUrls:EventLog"] ?? "http://localhost:4002";
            await client.PostAsJsonAsync($"{eventLogUrl}/events", new
            {
                event_id = Guid.NewGuid().ToString(),
                event_type = eventType,
                occurred_at = DateTimeOffset.UtcNow,
                world_id = "world-default",
                region_id = regionId,
                actor_id = actorId,
                source_service = "simulation",
                schema_version = 1,
                payload,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit {EventType} event", eventType);
        }
    }
}

public record PickupResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public WorldItem? Item { get; init; }

    public static PickupResult Ok(WorldItem item) => new() { Success = true, Item = item };
    public static PickupResult Fail(string error) => new() { Success = false, Error = error };
}

public record StoreResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ItemType { get; init; }
    public int Quantity { get; init; }

    public static StoreResult Ok(string itemType, int qty) => new() { Success = true, ItemType = itemType, Quantity = qty };
    public static StoreResult Fail(string error) => new() { Success = false, Error = error };
}

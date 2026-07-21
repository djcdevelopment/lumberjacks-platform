using System.Text.Json;
using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.Persistence;
using Game.Persistence.Entities;
using Game.ServiceDefaults;
using Game.Simulation.World;
using Microsoft.EntityFrameworkCore;

namespace Game.Simulation.Handlers;

public class PlaceStructureHandler
{
    private readonly WorldState _world;
    private readonly IDbContextFactory<GameDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PlaceStructureHandler> _logger;

    public PlaceStructureHandler(
        WorldState world,
        IDbContextFactory<GameDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<PlaceStructureHandler> logger)
    {
        _world = world;
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<PlaceStructureResult> HandleAsync(PlaceStructureRequest request)
    {
        // Validate region exists
        if (!_world.Regions.TryGetValue(request.RegionId, out var region))
            return PlaceStructureResult.Fail("Region not found");

        // Validate position is within region bounds
        var pos = request.Position;
        if (pos.X < region.BoundsMin.X || pos.X > region.BoundsMax.X ||
            pos.Y < region.BoundsMin.Y || pos.Y > region.BoundsMax.Y ||
            pos.Z < region.BoundsMin.Z || pos.Z > region.BoundsMax.Z)
        {
            return PlaceStructureResult.Fail(
                $"Position ({pos.X},{pos.Y},{pos.Z}) is outside region bounds");
        }

        // Create the structure
        var structure = new Structure
        {
            Id = Guid.NewGuid().ToString(),
            Type = request.StructureType,
            Position = request.Position,
            Rotation = request.Rotation,
            OwnerId = request.PlayerId,
            RegionId = request.RegionId,
            PlacedAt = DateTimeOffset.UtcNow,
            Tags = request.Tags ?? [],
        };

        // Add to in-memory world state
        _world.Structures[structure.Id] = structure;

        // Persist to Postgres
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Structures.Add(new StructureEntity
        {
            Id = structure.Id,
            Type = structure.Type,
            PositionX = structure.Position.X,
            PositionY = structure.Position.Y,
            PositionZ = structure.Position.Z,
            Rotation = structure.Rotation,
            OwnerId = structure.OwnerId,
            RegionId = structure.RegionId,
            PlacedAt = structure.PlacedAt,
            Tags = JsonSerializer.Serialize(structure.Tags),
        });
        await db.SaveChangesAsync();

        _logger.LogInformation("Structure {Id} ({Type}) placed by {Owner} in {Region}",
            structure.Id, structure.Type, structure.OwnerId, structure.RegionId);

        // Public telemetry feed (G4): capture the public-safe, non-identifying projection at the
        // in-process emission seam — structure category as detail, never the owner. Pre-persistence.
        GameplayEventFeed.Capture(EventType.StructurePlaced, structure.RegionId, structure.Type);

        // Fire-and-forget side effects (event emission + progression)
        _ = EmitEventAsync(structure, request.GuildId);
        _ = UpdateProgressionAsync(structure, request.GuildId);

        return PlaceStructureResult.Ok(structure);
    }

    private async Task EmitEventAsync(Structure structure, string? guildId)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var eventLogUrl = _config["ServiceUrls:EventLog"] ?? "http://localhost:4002";

            var gameEvent = new
            {
                event_id = Guid.NewGuid().ToString(),
                event_type = EventType.StructurePlaced,
                occurred_at = structure.PlacedAt,
                world_id = "world-default",
                region_id = structure.RegionId,
                actor_id = structure.OwnerId,
                source_service = "simulation",
                schema_version = 1,
                payload = new
                {
                    structure_id = structure.Id,
                    structure_type = structure.Type,
                    position = new { x = structure.Position.X, y = structure.Position.Y, z = structure.Position.Z },
                    tags = structure.Tags,
                },
            };

            await client.PostAsJsonAsync($"{eventLogUrl}/events", gameEvent);
            _logger.LogDebug("Emitted structure_placed event for {Id}", structure.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit event for structure {Id}", structure.Id);
        }
    }

    private async Task UpdateProgressionAsync(Structure structure, string? guildId)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var progressionUrl = _config["ServiceUrls:Progression"] ?? "http://localhost:4003";

            var request = new
            {
                event_type = EventType.StructurePlaced,
                actor_id = structure.OwnerId,
                guild_id = guildId,
                payload = new { structure_type = structure.Type },
            };

            await client.PostAsJsonAsync($"{progressionUrl}/process-event", request);
            _logger.LogDebug("Updated progression for player {Id} (guild {GuildId})", structure.OwnerId, guildId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update progression for structure {Id}", structure.Id);
        }
    }
}

public record PlaceStructureRequest
{
    public required string PlayerId { get; init; }
    public required string RegionId { get; init; }
    public required string StructureType { get; init; }
    public required Vec3 Position { get; init; }
    public double Rotation { get; init; }
    public List<string>? Tags { get; init; }
    public string? GuildId { get; init; }
}

public record PlaceStructureResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public Structure? Structure { get; init; }

    public static PlaceStructureResult Ok(Structure structure) => new() { Success = true, Structure = structure };
    public static PlaceStructureResult Fail(string error) => new() { Success = false, Error = error };
}

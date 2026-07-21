using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.ServiceDefaults;
using Game.Simulation.World;

namespace Game.Simulation.Handlers;

public class PlayerHandler
{
    private readonly WorldState _world;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PlayerHandler> _logger;

    public PlayerHandler(
        WorldState world,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<PlayerHandler> logger)
    {
        _world = world;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>Bounds inset used to keep spread-spawned players off the region walls.</summary>
    private const double SpawnSpreadInset = 50.0;

    public JoinResult Join(JoinRequest request)
    {
        if (!_world.Regions.TryGetValue(request.RegionId, out var region))
            return JoinResult.Fail("Region not found");

        var spawnPos = _config.GetValue("World:SpawnSpread", false)
            ? RandomSpawnPosition(region)
            : new Vec3(0, 0, 0);

        var player = new Player
        {
            Id = request.PlayerId,
            Name = $"Player-{request.PlayerId[..8]}",
            GuildId = request.GuildId,
            Position = spawnPos,
            RegionId = request.RegionId,
            Connected = true,
            ConnectedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };

        _world.Players[player.Id] = player;
        _world.SpatialGrid.Update(player.Id, spawnPos);

        // Update region player count
        _world.Regions[request.RegionId] = region with
        {
            PlayerCount = _world.Players.Values.Count(p => p.RegionId == request.RegionId && p.Connected),
        };

        // Build world snapshot: all players + structures in this region
        var playerEntities = _world.Players.Values
            .Where(p => p.RegionId == request.RegionId && p.Connected)
            .Select(p => new Dictionary<string, object>
            {
                ["entity_id"] = p.Id,
                ["entity_type"] = "player",
                ["name"] = p.Name,
                ["position"] = new { x = p.Position.X, y = p.Position.Y, z = p.Position.Z },
                ["connected"] = p.Connected,
            })
            .ToList();

        var structureEntities = _world.Structures.Values
            .Where(s => s.RegionId == request.RegionId)
            .Select(s => new Dictionary<string, object>
            {
                ["entity_id"] = s.Id,
                ["entity_type"] = "structure",
                ["type"] = s.Type,
                ["position"] = new { x = s.Position.X, y = s.Position.Y, z = s.Position.Z },
                ["rotation"] = s.Rotation,
                ["owner_id"] = s.OwnerId,
            })
            .ToList();

        var naturalResourceEntities = _world.NaturalResources.Values
            .Where(n => n.RegionId == request.RegionId)
            .Select(n => new Dictionary<string, object>
            {
                ["entity_id"] = n.Id,
                ["entity_type"] = "natural_resource",
                ["type"] = n.Type,
                ["position"] = new { x = n.Position.X, y = n.Position.Y, z = n.Position.Z },
                ["health"] = n.Health,
                ["lean_x"] = n.LeanX,
                ["lean_z"] = n.LeanZ,
                // We skip growth_history in the initial snapshot to keep the message size small.
                // It can be requested on-demand or sent when interacting.
            })
            .ToList();

        var allEntities = playerEntities
            .Concat(structureEntities)
            .Concat(naturalResourceEntities)
            .ToList();

        // Public telemetry feed (G4): a player entered a region — capture the region id ONLY,
        // at the in-process seam. Never the player id/name or spawn position. Pre-persistence.
        GameplayEventFeed.Capture(EventType.PlayerEnteredRegion, regionId: request.RegionId, detail: null, provenance: "observed");

        // Fire-and-forget: emit player_connected event
        _ = EmitPlayerEventAsync(EventType.PlayerConnected, request.PlayerId, request.RegionId,
            new { spawn_position = new { x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z } });

        _logger.LogInformation("Player {PlayerId} joined {RegionId}", request.PlayerId, request.RegionId);

        return JoinResult.Ok(request.RegionId, player.Id, allEntities);
    }

    /// <summary>
    /// Uniform-random XZ position within the region's bounds, inset by <see cref="SpawnSpreadInset"/>
    /// on each side to keep spawned players off the walls. Y is fixed at 0, matching the existing
    /// spawn height. Falls back to the region's bounds midpoint if the region is too small for the
    /// inset (defensive — region-spawn is 1000x1000, never hit today).
    /// </summary>
    private static Vec3 RandomSpawnPosition(Region region)
    {
        var minX = region.BoundsMin.X + SpawnSpreadInset;
        var maxX = region.BoundsMax.X - SpawnSpreadInset;
        var minZ = region.BoundsMin.Z + SpawnSpreadInset;
        var maxZ = region.BoundsMax.Z - SpawnSpreadInset;

        var x = minX <= maxX ? minX + Random.Shared.NextDouble() * (maxX - minX) : (region.BoundsMin.X + region.BoundsMax.X) / 2.0;
        var z = minZ <= maxZ ? minZ + Random.Shared.NextDouble() * (maxZ - minZ) : (region.BoundsMin.Z + region.BoundsMax.Z) / 2.0;

        return new Vec3(x, 0, z);
    }

    public MoveResult Move(MoveRequest request)
    {
        if (!_world.Players.TryGetValue(request.PlayerId, out var player))
            return MoveResult.Fail("Player not in world");

        var pos = request.Position;
        var corrected = false;

        // Bounds check: position must be within the player's region
        if (_world.Regions.TryGetValue(player.RegionId, out var region))
        {
            if (pos.X < region.BoundsMin.X || pos.X > region.BoundsMax.X ||
                pos.Y < region.BoundsMin.Y || pos.Y > region.BoundsMax.Y ||
                pos.Z < region.BoundsMin.Z || pos.Z > region.BoundsMax.Z)
            {
                pos = new Vec3(
                    Math.Clamp(pos.X, region.BoundsMin.X, region.BoundsMax.X),
                    Math.Clamp(pos.Y, region.BoundsMin.Y, region.BoundsMax.Y),
                    Math.Clamp(pos.Z, region.BoundsMin.Z, region.BoundsMax.Z));
                corrected = true;
            }
        }

        // Speed check: max 50 units per move (prevents teleporting)
        const double MaxMoveDistance = 50.0;
        var dx = pos.X - player.Position.X;
        var dy = pos.Y - player.Position.Y;
        var dz = pos.Z - player.Position.Z;
        var distSq = dx * dx + dy * dy + dz * dz;

        if (distSq > MaxMoveDistance * MaxMoveDistance)
        {
            var dist = Math.Sqrt(distSq);
            var scale = MaxMoveDistance / dist;
            pos = new Vec3(
                player.Position.X + dx * scale,
                player.Position.Y + dy * scale,
                player.Position.Z + dz * scale);
            corrected = true;
        }

        var updated = player with
        {
            Position = pos,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
        _world.Players[request.PlayerId] = updated;
        _world.SpatialGrid.Update(request.PlayerId, pos);

        return MoveResult.Ok(request.PlayerId, pos, request.Velocity, player.RegionId, corrected);
    }

    public LeaveResult Leave(LeaveRequest request)
    {
        if (!_world.Players.TryRemove(request.PlayerId, out var player))
            return LeaveResult.NotFound();

        _world.SpatialGrid.Remove(request.PlayerId);

        // Update region player count
        if (_world.Regions.TryGetValue(player.RegionId, out var region))
        {
            _world.Regions[player.RegionId] = region with
            {
                PlayerCount = _world.Players.Values.Count(p => p.RegionId == player.RegionId && p.Connected),
            };
        }

        // Fire-and-forget: emit player_disconnected event
        _ = EmitPlayerEventAsync(EventType.PlayerDisconnected, request.PlayerId, player.RegionId, new { });

        _logger.LogInformation("Player {PlayerId} left {RegionId}", request.PlayerId, player.RegionId);

        return LeaveResult.Ok(request.PlayerId, player.RegionId);
    }

    private async Task EmitPlayerEventAsync(string eventType, string playerId, string regionId, object payload)
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
                actor_id = playerId,
                source_service = "simulation",
                schema_version = 1,
                payload,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit {EventType} event for player {PlayerId}", eventType, playerId);
        }
    }
}

public record JoinResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RegionId { get; init; }
    public string? PlayerId { get; init; }
    public List<Dictionary<string, object>>? Entities { get; init; }

    public static JoinResult Ok(string regionId, string playerId, List<Dictionary<string, object>> entities) =>
        new() { Success = true, RegionId = regionId, PlayerId = playerId, Entities = entities };
    public static JoinResult Fail(string error) => new() { Success = false, Error = error };
}

public record MoveResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? PlayerId { get; init; }
    public Vec3 Position { get; init; }
    public Vec3 Velocity { get; init; }
    public string? RegionId { get; init; }
    public bool Corrected { get; init; }

    public static MoveResult Ok(string playerId, Vec3 position, Vec3 velocity, string regionId, bool corrected) =>
        new() { Success = true, PlayerId = playerId, Position = position, Velocity = velocity, RegionId = regionId, Corrected = corrected };
    public static MoveResult Fail(string error) => new() { Success = false, Error = error };
}

public record LeaveResult
{
    public bool Removed { get; init; }
    public string? PlayerId { get; init; }
    public string? RegionId { get; init; }

    public static LeaveResult Ok(string playerId, string regionId) =>
        new() { Removed = true, PlayerId = playerId, RegionId = regionId };
    public static LeaveResult NotFound() => new() { Removed = false };
}

public record JoinRequest
{
    public required string PlayerId { get; init; }
    public required string RegionId { get; init; }
    public string? GuildId { get; init; }
}

public record MoveRequest
{
    public required string PlayerId { get; init; }
    public required Vec3 Position { get; init; }
    public Vec3 Velocity { get; init; }
}

public record LeaveRequest
{
    public required string PlayerId { get; init; }
}

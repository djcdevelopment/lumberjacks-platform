using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using CommunitySurvival.Networking;

namespace CommunitySurvival.Core;

/// <summary>
/// C# Autoload that mirrors the server's authoritative world state.
/// Parses entity data from snapshots and updates, applies coordinate mapping (ADR 0018),
/// and emits signals for World to spawn/update/remove entity nodes.
/// </summary>
public partial class GameState : Node
{
    [Signal] public delegate void EntityAddedEventHandler(string entityId, string entityType,
        Vector3 position, float heading, Godot.Collections.Dictionary metadata);
    [Signal] public delegate void EntityChangedEventHandler(string entityId, Vector3 position,
        Vector3 velocity, float heading, long tick);
    [Signal] public delegate void EntityDataChangedEventHandler(string entityId, string entityType,
        Godot.Collections.Dictionary metadata, long tick);
    [Signal] public delegate void EntityRemovedEventHandler(string entityId);
    [Signal] public delegate void TerrainReadyEventHandler();

    private SimulationClient _network;
    private readonly Dictionary<string, EntityRecord> _entities = new();

    public string MyPlayerId { get; private set; }
    public string RegionId { get; private set; }
    public long CurrentTick { get; private set; }

    // RegionProfile terrain data (parsed from world_snapshot)
    public double[] AltitudeGrid { get; private set; }
    public int GridWidth { get; private set; }
    public int GridHeight { get; private set; }
    public double TradeWindX { get; private set; }
    public double TradeWindZ { get; private set; }
    public bool HasTerrainData => AltitudeGrid != null;

    public override void _Ready()
    {
        _network = GetNode<SimulationClient>("/root/SimulationClient");

        _network.SessionStarted += OnSessionStarted;
        _network.WorldSnapshotReceived += OnWorldSnapshotReceived;
        _network.EntityUpdated += OnEntityUpdated;
        _network.EntityDataUpdated += OnEntityDataUpdated;
        _network.EntityRemoved += OnEntityRemoved;
    }

    private void OnSessionStarted(string sessionId, string playerId, string worldId, string resumeToken)
    {
        MyPlayerId = playerId;
        GD.Print($"GameState: Session started. MyPlayerId={MyPlayerId}");
    }

    private void OnWorldSnapshotReceived(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            RegionId = root.GetProperty("region_id").GetString();
            CurrentTick = root.TryGetProperty("tick", out var tickEl) ? tickEl.GetInt64() : 0;

            // Parse RegionProfile terrain data if present
            if (root.TryGetProperty("region_profile", out var profile) && profile.ValueKind != JsonValueKind.Null)
            {
                ParseRegionProfile(profile);
            }

            // Clear existing entities
            var oldIds = new List<string>(_entities.Keys);
            foreach (var id in oldIds)
            {
                _entities.Remove(id);
                EmitSignal(SignalName.EntityRemoved, id);
            }

            // Parse entities from snapshot
            if (root.TryGetProperty("entities", out var entitiesArr))
            {
                int count = 0;
                foreach (var entity in entitiesArr.EnumerateArray())
                {
                    ParseAndAddEntity(entity);
                    count++;
                }
                GD.Print($"GameState: Snapshot for {RegionId} — {count} entities loaded");
            }

            // Signal terrain ready after entities are parsed (World needs this to generate mesh)
            if (HasTerrainData)
            {
                EmitSignal(SignalName.TerrainReady);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"GameState: Snapshot parse error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void ParseRegionProfile(JsonElement profile)
    {
        GridWidth = profile.GetProperty("grid_width").GetInt32();
        GridHeight = profile.GetProperty("grid_height").GetInt32();
        TradeWindX = profile.TryGetProperty("trade_wind_x", out var twx) ? twx.GetDouble() : 0;
        TradeWindZ = profile.TryGetProperty("trade_wind_z", out var twz) ? twz.GetDouble() : 0;

        var altArr = profile.GetProperty("altitude_grid");
        AltitudeGrid = new double[altArr.GetArrayLength()];
        int i = 0;
        foreach (var val in altArr.EnumerateArray())
        {
            AltitudeGrid[i++] = val.GetDouble();
        }

        GD.Print($"GameState: Terrain loaded — {GridWidth}x{GridHeight} grid, {AltitudeGrid.Length} altitude points");
    }

    private void ParseAndAddEntity(JsonElement entity)
    {
        var entityId = entity.GetProperty("entity_id").GetString();
        var entityType = entity.TryGetProperty("entity_type", out var etEl) ? etEl.GetString() : "unknown";

        // Normalize: snapshot uses "natural_resource" with sub-type in "type" field
        var subType = entity.TryGetProperty("type", out var stEl) ? stEl.GetString() : null;
        var normalizedType = NormalizeEntityType(entityType, subType);

        // Parse position with coordinate mapping (ADR 0018)
        Vector3 godotPos = Vector3.Zero;
        if (entity.TryGetProperty("position", out var posEl))
        {
            var serverPos = new Game.Contracts.Entities.Vec3(
                posEl.GetProperty("x").GetDouble(),
                posEl.GetProperty("y").GetDouble(),
                posEl.GetProperty("z").GetDouble());
            godotPos = CoordinateMapper.ServerToGodot(serverPos);
        }

        float heading = 0f;
        if (entity.TryGetProperty("heading", out var hEl))
            heading = CoordinateMapper.ServerHeadingToGodot((float)hEl.GetDouble());
        else if (entity.TryGetProperty("rotation", out var rEl))
            heading = CoordinateMapper.ServerHeadingToGodot((float)rEl.GetDouble());

        // Build metadata dictionary for downstream consumers
        var metadata = new Godot.Collections.Dictionary();
        if (subType != null) metadata["type"] = subType;

        // Player-specific
        if (entity.TryGetProperty("name", out var nameEl)) metadata["name"] = nameEl.GetString();
        if (entity.TryGetProperty("player_id", out var pidEl)) metadata["player_id"] = pidEl.GetString();
        if (entity.TryGetProperty("connected", out var connEl)) metadata["connected"] = connEl.GetBoolean();

        // Structure-specific
        if (entity.TryGetProperty("owner_id", out var oidEl)) metadata["owner_id"] = oidEl.GetString();

        // Natural resource-specific
        if (entity.TryGetProperty("health", out var healthEl)) metadata["health"] = healthEl.GetDouble();
        if (entity.TryGetProperty("lean_x", out var lxEl)) metadata["lean_x"] = lxEl.GetDouble();
        if (entity.TryGetProperty("lean_z", out var lzEl)) metadata["lean_z"] = lzEl.GetDouble();
        if (entity.TryGetProperty("stump_health", out var shEl)) metadata["stump_health"] = shEl.GetDouble();
        if (entity.TryGetProperty("regrowth_progress", out var rpEl)) metadata["regrowth_progress"] = rpEl.GetDouble();
        if (entity.TryGetProperty("growth_history", out var ghEl))
            metadata["growth_history"] = ghEl.GetRawText();

        var record = new EntityRecord
        {
            EntityId = entityId,
            EntityType = normalizedType,
            Position = godotPos,
            Heading = heading,
            Metadata = metadata,
        };
        _entities[entityId] = record;

        EmitSignal(SignalName.EntityAdded, entityId, normalizedType, godotPos, heading, metadata);
    }

    /// <summary>
    /// Player entity position/velocity updates (from binary or JSON player-type updates).
    /// </summary>
    private void OnEntityUpdated(string entityId, Vector3 position, Vector3 velocity, float heading, int lastInputSeq, long tick)
    {
        CurrentTick = tick;

        if (!_entities.ContainsKey(entityId))
        {
            // Streaming entity — first seen via update, not snapshot
            var record = new EntityRecord
            {
                EntityId = entityId,
                EntityType = "player",
                Position = position,
                Heading = heading,
            };
            _entities[entityId] = record;

            var meta = new Godot.Collections.Dictionary();
            EmitSignal(SignalName.EntityAdded, entityId, "player", position, heading, meta);
        }
        else
        {
            _entities[entityId].Position = position;
            _entities[entityId].Heading = heading;
        }

        EmitSignal(SignalName.EntityChanged, entityId, position, velocity, heading, tick);
    }

    /// <summary>
    /// Non-player entity data updates (natural resources, structures via JSON).
    /// </summary>
    private void OnEntityDataUpdated(string entityId, string entityType, string jsonData, long tick)
    {
        CurrentTick = tick;

        var normalizedType = NormalizeEntityType(entityType, null);

        // Parse the data JSON for position and metadata
        var metadata = new Godot.Collections.Dictionary();
        Vector3 godotPos = Vector3.Zero;

        try
        {
            using var doc = JsonDocument.Parse(jsonData);
            var data = doc.RootElement;

            if (data.TryGetProperty("position", out var posEl))
            {
                var serverPos = new Game.Contracts.Entities.Vec3(
                    posEl.GetProperty("x").GetDouble(),
                    posEl.GetProperty("y").GetDouble(),
                    posEl.GetProperty("z").GetDouble());
                godotPos = CoordinateMapper.ServerToGodot(serverPos);
            }

            if (data.TryGetProperty("health", out var hEl)) metadata["health"] = hEl.GetDouble();
            if (data.TryGetProperty("stump_health", out var shEl)) metadata["stump_health"] = shEl.GetDouble();
            if (data.TryGetProperty("regrowth_progress", out var rpEl)) metadata["regrowth_progress"] = rpEl.GetDouble();
            if (data.TryGetProperty("lean_x", out var lxEl)) metadata["lean_x"] = lxEl.GetDouble();
            if (data.TryGetProperty("lean_z", out var lzEl)) metadata["lean_z"] = lzEl.GetDouble();
            if (data.TryGetProperty("growth_history", out var ghEl))
                metadata["growth_history"] = ghEl.GetRawText();

            // Structure-specific
            if (data.TryGetProperty("type", out var tEl)) metadata["type"] = tEl.GetString();
            if (data.TryGetProperty("owner_id", out var oidEl)) metadata["owner_id"] = oidEl.GetString();

            // Player-specific (for JSON player updates)
            if (data.TryGetProperty("connected", out var connEl)) metadata["connected"] = connEl.GetBoolean();
            if (data.TryGetProperty("name", out var nameEl)) metadata["name"] = nameEl.GetString();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"GameState: Entity data parse error for {entityId}: {ex.Message}");
        }

        if (!_entities.ContainsKey(entityId))
        {
            // Streaming entity
            var record = new EntityRecord
            {
                EntityId = entityId,
                EntityType = normalizedType,
                Position = godotPos,
            };
            _entities[entityId] = record;

            metadata["entity_type"] = normalizedType;
            EmitSignal(SignalName.EntityAdded, entityId, normalizedType, godotPos, 0f, metadata);
        }
        else
        {
            if (godotPos != Vector3.Zero)
                _entities[entityId].Position = godotPos;
        }

        EmitSignal(SignalName.EntityDataChanged, entityId, normalizedType, metadata, tick);
    }

    private void OnEntityRemoved(string entityId, long tick)
    {
        if (_entities.Remove(entityId))
        {
            EmitSignal(SignalName.EntityRemoved, entityId);
        }
    }

    /// <summary>
    /// Get entity position for HUD/debug display.
    /// </summary>
    public Vector3 GetEntityPosition(string entityId)
    {
        return _entities.TryGetValue(entityId, out var rec) ? rec.Position : Vector3.Zero;
    }

    public int EntityCount => _entities.Count;
    public int PlayerCount
    {
        get
        {
            int count = 0;
            foreach (var e in _entities.Values)
                if (e.EntityType == "player") count++;
            return count;
        }
    }

    /// <summary>
    /// Normalize entity types: "natural_resource" with sub-type "oak_tree" → "oak_tree".
    /// Entity updates send the specific type directly (e.g., "oak_tree").
    /// </summary>
    private static string NormalizeEntityType(string entityType, string subType)
    {
        return entityType switch
        {
            "natural_resource" when !string.IsNullOrEmpty(subType) => subType,
            _ => entityType,
        };
    }

    /// <summary>
    /// Replays all current entities to a late subscriber (e.g., World scene instantiated after snapshot).
    /// </summary>
    /// <summary>
    /// Replays all current entities to a late subscriber (e.g., World scene instantiated after snapshot).
    /// </summary>
    public void ReplayEntities()
    {
        foreach (var rec in _entities.Values)
        {
            EmitSignal(SignalName.EntityAdded, rec.EntityId, rec.EntityType, rec.Position, rec.Heading,
                rec.Metadata ?? new Godot.Collections.Dictionary());
        }

        if (HasTerrainData)
            EmitSignal(SignalName.TerrainReady);
    }

    public void Clear()
    {
        var ids = new List<string>(_entities.Keys);
        foreach (var id in ids)
        {
            _entities.Remove(id);
            EmitSignal(SignalName.EntityRemoved, id);
        }
        AltitudeGrid = null;
    }

    // ---- Replay ingest API (slice 0) -------------------------------------
    //
    // Additive surface for the ReplayLoader. Bypasses SimulationClient
    // (no live network in replay mode) and emits the same EntityAdded/
    // EntityChanged/EntityRemoved signals World.cs already subscribes to.
    // Coordinate mapping is the loader's responsibility — replay coords
    // (WoW yards, normalized then denormalized) are not the same space as
    // CoordinateMapper's server↔Godot mapping.

    public void IngestReplayEntity(string entityId, string entityType, Vector3 position, float heading,
        Godot.Collections.Dictionary metadata)
    {
        var record = new EntityRecord
        {
            EntityId = entityId,
            EntityType = entityType,
            Position = position,
            Heading = heading,
            Metadata = metadata,
        };
        _entities[entityId] = record;
        EmitSignal(SignalName.EntityAdded, entityId, entityType, position, heading, metadata);
    }

    public void IngestReplayPosition(string entityId, Vector3 position, long tickMs)
    {
        if (!_entities.TryGetValue(entityId, out var rec)) return;
        rec.Position = position;
        EmitSignal(SignalName.EntityChanged, entityId, position, Vector3.Zero, rec.Heading, tickMs);
    }

    public void IngestReplayRemove(string entityId)
    {
        if (_entities.Remove(entityId))
            EmitSignal(SignalName.EntityRemoved, entityId);
    }

    private class EntityRecord
    {
        public string EntityId;
        public string EntityType;
        public Vector3 Position;
        public float Heading;
        public Godot.Collections.Dictionary Metadata;
    }
}

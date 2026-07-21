using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using CommunitySurvival.Networking;

namespace CommunitySurvival.Core;

/// <summary>
/// Autoload: mirrors server world state. Parses snapshots, tracks entities, emits signals.
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

    private SimulationClient _net;
    private readonly Dictionary<string, EntityRec> _entities = new();

    public string MyPlayerId { get; private set; }
    public string RegionId { get; private set; }
    public long CurrentTick { get; private set; }

    public double[] AltitudeGrid { get; private set; }
    public int GridWidth { get; private set; }
    public int GridHeight { get; private set; }
    public double TradeWindX { get; private set; }
    public double TradeWindZ { get; private set; }
    public bool HasTerrain => AltitudeGrid != null;

    public override void _Ready()
    {
        _net = GetNode<SimulationClient>("/root/SimulationClient");
        _net.SessionStarted += (pid, _) => { MyPlayerId = pid; GD.Print($"GameState: player={pid}"); };
        _net.WorldSnapshotReceived += OnSnapshot;
        _net.EntityUpdated += OnEntityUpdated;
        _net.EntityDataUpdated += OnEntityDataUpdated;
        _net.EntityRemoved += OnEntityRemoved;
    }

    private void OnSnapshot(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            RegionId = root.GetProperty("region_id").GetString();
            CurrentTick = root.TryGetProperty("tick", out var t) ? t.GetInt64() : 0;

            if (root.TryGetProperty("region_profile", out var rp) && rp.ValueKind != JsonValueKind.Null)
                ParseTerrain(rp);

            // Clear old
            foreach (var id in new List<string>(_entities.Keys))
            {
                _entities.Remove(id);
                EmitSignal(SignalName.EntityRemoved, id);
            }

            int count = 0;
            foreach (var e in root.GetProperty("entities").EnumerateArray())
            {
                ParseEntity(e);
                count++;
            }
            GD.Print($"GameState: snapshot {RegionId} — {count} entities");

            if (HasTerrain) EmitSignal(SignalName.TerrainReady);
        }
        catch (Exception ex) { GD.PrintErr($"GameState snapshot: {ex.Message}\n{ex.StackTrace}"); }
    }

    private void ParseTerrain(JsonElement rp)
    {
        GridWidth = rp.GetProperty("grid_width").GetInt32();
        GridHeight = rp.GetProperty("grid_height").GetInt32();
        TradeWindX = rp.TryGetProperty("trade_wind_x", out var x) ? x.GetDouble() : 0;
        TradeWindZ = rp.TryGetProperty("trade_wind_z", out var z) ? z.GetDouble() : 0;
        var arr = rp.GetProperty("altitude_grid");
        AltitudeGrid = new double[arr.GetArrayLength()];
        int i = 0;
        foreach (var v in arr.EnumerateArray()) AltitudeGrid[i++] = v.GetDouble();
        GD.Print($"GameState: terrain {GridWidth}x{GridHeight}");
    }

    private void ParseEntity(JsonElement e)
    {
        var id = e.GetProperty("entity_id").GetString();
        var et = e.TryGetProperty("entity_type", out var ete) ? ete.GetString() : "unknown";
        var sub = e.TryGetProperty("type", out var st) ? st.GetString() : null;
        var type = et == "natural_resource" && !string.IsNullOrEmpty(sub) ? sub : et;

        Vector3 pos = Vector3.Zero;
        if (e.TryGetProperty("position", out var p))
            pos = CoordinateMapper.ServerToGodot(new Game.Contracts.Entities.Vec3(
                p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), p.GetProperty("z").GetDouble()));

        float heading = 0;
        if (e.TryGetProperty("rotation", out var r)) heading = CoordinateMapper.ServerHeadingToGodot((float)r.GetDouble());

        var meta = new Godot.Collections.Dictionary();
        if (sub != null) meta["type"] = sub;
        if (e.TryGetProperty("name", out var n)) meta["name"] = n.GetString();
        if (e.TryGetProperty("connected", out var c)) meta["connected"] = c.GetBoolean();
        if (e.TryGetProperty("owner_id", out var o)) meta["owner_id"] = o.GetString();
        if (e.TryGetProperty("health", out var h)) meta["health"] = h.GetDouble();
        if (e.TryGetProperty("lean_x", out var lx)) meta["lean_x"] = lx.GetDouble();
        if (e.TryGetProperty("lean_z", out var lz)) meta["lean_z"] = lz.GetDouble();
        if (e.TryGetProperty("stump_health", out var sh)) meta["stump_health"] = sh.GetDouble();
        if (e.TryGetProperty("regrowth_progress", out var rg)) meta["regrowth_progress"] = rg.GetDouble();
        if (e.TryGetProperty("growth_history", out var gh)) meta["growth_history"] = gh.GetRawText();

        _entities[id] = new EntityRec { Id = id, Type = type, Pos = pos, Heading = heading, Meta = meta };
        EmitSignal(SignalName.EntityAdded, id, type, pos, heading, meta);
    }

    private void OnEntityUpdated(string id, Vector3 pos, Vector3 vel, float heading, int seq, long tick)
    {
        CurrentTick = tick;
        if (!_entities.ContainsKey(id))
        {
            _entities[id] = new EntityRec { Id = id, Type = "player", Pos = pos, Heading = heading };
            EmitSignal(SignalName.EntityAdded, id, "player", pos, heading, new Godot.Collections.Dictionary());
        }
        else { _entities[id].Pos = pos; _entities[id].Heading = heading; }
        EmitSignal(SignalName.EntityChanged, id, pos, vel, heading, tick);
    }

    private void OnEntityDataUpdated(string id, string entityType, string jsonData, long tick)
    {
        CurrentTick = tick;
        var meta = new Godot.Collections.Dictionary();
        try
        {
            using var doc = JsonDocument.Parse(jsonData);
            var d = doc.RootElement;
            if (d.TryGetProperty("health", out var h)) meta["health"] = h.GetDouble();
            if (d.TryGetProperty("stump_health", out var sh)) meta["stump_health"] = sh.GetDouble();
            if (d.TryGetProperty("regrowth_progress", out var rg)) meta["regrowth_progress"] = rg.GetDouble();
            if (d.TryGetProperty("lean_x", out var lx)) meta["lean_x"] = lx.GetDouble();
            if (d.TryGetProperty("lean_z", out var lz)) meta["lean_z"] = lz.GetDouble();
            if (d.TryGetProperty("growth_history", out var gh)) meta["growth_history"] = gh.GetRawText();
            if (d.TryGetProperty("type", out var t)) meta["type"] = t.GetString();
            if (d.TryGetProperty("connected", out var c)) meta["connected"] = c.GetBoolean();
        }
        catch (Exception ex) { GD.PrintErr($"GameState data: {ex.Message}"); }

        if (!_entities.ContainsKey(id))
        {
            _entities[id] = new EntityRec { Id = id, Type = entityType, Pos = Vector3.Zero };
            EmitSignal(SignalName.EntityAdded, id, entityType, Vector3.Zero, 0f, meta);
        }
        EmitSignal(SignalName.EntityDataChanged, id, entityType, meta, tick);
    }

    private void OnEntityRemoved(string id, long tick)
    {
        if (_entities.Remove(id)) EmitSignal(SignalName.EntityRemoved, id);
    }

    public Vector3 GetPosition(string id) => _entities.TryGetValue(id, out var r) ? r.Pos : Vector3.Zero;
    public int PlayerCount { get { int n = 0; foreach (var e in _entities.Values) if (e.Type == "player") n++; return n; } }
    public int EntityCount => _entities.Count;

    private static readonly HashSet<string> _treeTypes = new()
        { "tree", "natural_resource", "oak_tree", "pine_tree", "birch_tree" };

    public Godot.Collections.Dictionary GetEntityMeta(string id) =>
        _entities.TryGetValue(id, out var r) ? r.Meta : null;

    public string FindNearestTree(Vector3 pos, float maxDist)
    {
        string nearest = null;
        float bestDist = maxDist * maxDist;
        foreach (var e in _entities.Values)
        {
            if (!_treeTypes.Contains(e.Type)) continue;
            float d = (e.Pos - pos).LengthSquared();
            if (d < bestDist) { bestDist = d; nearest = e.Id; }
        }
        return nearest;
    }

    public void ReplayEntities()
    {
        foreach (var r in _entities.Values)
            EmitSignal(SignalName.EntityAdded, r.Id, r.Type, r.Pos, r.Heading, r.Meta ?? new Godot.Collections.Dictionary());
        if (HasTerrain) EmitSignal(SignalName.TerrainReady);
    }

    public void Clear()
    {
        foreach (var id in new List<string>(_entities.Keys))
        {
            _entities.Remove(id);
            EmitSignal(SignalName.EntityRemoved, id);
        }
        AltitudeGrid = null;
    }

    private class EntityRec { public string Id, Type; public Vector3 Pos; public float Heading; public Godot.Collections.Dictionary Meta; }
}

using Godot;
using System.Collections.Generic;
using CommunitySurvival.Entities;

namespace CommunitySurvival.Core;

/// <summary>
/// 3D world. Spawns typed entity scenes, generates terrain from RegionProfile.
/// </summary>
public partial class World : Node3D
{
    private GameState _state;
    private PackedScene _playerScene;
    private PackedScene _treeScene;
    private MeshInstance3D _ground;
    private readonly Dictionary<string, Node3D> _entities = new();

    private static readonly HashSet<string> TreeTypes = new()
        { "tree", "natural_resource", "oak_tree", "pine_tree", "birch_tree" };
    private int _treeCount, _structCount, _playerCount;

    public override void _Ready()
    {
        _state = GetNode<GameState>("/root/GameState");
        _playerScene = GD.Load<PackedScene>("res://scenes/entities/Player.tscn");
        _treeScene = GD.Load<PackedScene>("res://scenes/entities/Tree.tscn");
        _ground = GetNode<MeshInstance3D>("Ground");

        _state.EntityAdded += OnAdded;
        _state.EntityChanged += OnChanged;
        _state.EntityDataChanged += OnDataChanged;
        _state.EntityRemoved += OnRemoved;
        _state.TerrainReady += OnTerrainReady;

        // Environment: sky + fog + ambient light
        SetupEnvironment();

        // Add tree inspector UI
        AddChild(new UI.TreeInspector());

        GD.Print("World: ready");
        _treeCount = 0; _structCount = 0; _playerCount = 0;
        _state.ReplayEntities();
        GD.Print($"World: spawned {_playerCount} players, {_treeCount} trees, {_structCount} structures");
    }

    public override void _ExitTree()
    {
        _state.EntityAdded -= OnAdded;
        _state.EntityChanged -= OnChanged;
        _state.EntityDataChanged -= OnDataChanged;
        _state.EntityRemoved -= OnRemoved;
        _state.TerrainReady -= OnTerrainReady;
    }

    private void OnTerrainReady()
    {
        if (!_state.HasTerrain) return;
        var mesh = TerrainGenerator.Generate(_state.AltitudeGrid, _state.GridWidth, _state.GridHeight);
        if (mesh != null && _ground != null)
        {
            _ground.Mesh = mesh;
            _ground.MaterialOverride = null;
            GD.Print("World: terrain mesh applied");
        }
    }

    private void OnAdded(string id, string type, Vector3 pos, float heading,
        Godot.Collections.Dictionary meta)
    {
        if (_entities.ContainsKey(id)) return;

        if (type == "player")
        { SpawnPlayer(id, pos, heading, meta); _playerCount++; }
        else if (TreeTypes.Contains(type))
        { SpawnTree(id, pos, heading, meta); _treeCount++; }
        else if (type == "structure")
        { SpawnPlaceholder(id, pos, new Vector3(1f, 1f, 1f), new Color(0.55f, 0.35f, 0.15f)); _structCount++; }
        else
        { SpawnPlaceholder(id, pos, new Vector3(1f, 1f, 1f), new Color(0.5f, 0.5f, 0.5f)); }
    }

    private void SpawnTree(string id, Vector3 pos, float heading, Godot.Collections.Dictionary meta)
    {
        var instance = _treeScene.Instantiate<Node3D>();
        _entities[id] = instance;
        AddChild(instance);

        // Snap tree Y to terrain surface — server stores raw altitude,
        // but terrain mesh scales altitude by 0.3x
        if (_state.HasTerrain)
        {
            float terrainY = TerrainGenerator.GetAltitudeAt(
                _state.AltitudeGrid, _state.GridWidth, _state.GridHeight, pos.X, pos.Z);
            pos = new Vector3(pos.X, terrainY, pos.Z);
        }

        meta["entity_id"] = id;
        if (instance is TreeEntity tree)
            tree.Initialize(pos, heading, meta);
        else
            instance.Position = pos;
    }

    private void SpawnPlayer(string id, Vector3 pos, float heading,
        Godot.Collections.Dictionary meta)
    {
        var instance = _playerScene.Instantiate<Node3D>();
        _entities[id] = instance;
        AddChild(instance);

        // Snap feet to terrain surface
        if (_state.HasTerrain)
        {
            float terrainY = TerrainGenerator.GetAltitudeAt(
                _state.AltitudeGrid, _state.GridWidth, _state.GridHeight, pos.X, pos.Z);
            pos = new Vector3(pos.X, terrainY, pos.Z);
        }

        var re = instance as RemoteEntity;
        re?.Initialize(pos, heading);

        var nameStr = meta.ContainsKey("name") ? (string)meta["name"]
            : id[..System.Math.Min(8, id.Length)];
        if (instance.HasNode("Nametag"))
            instance.GetNode<Label3D>("Nametag").Text = nameStr;

        bool isLocal = id == _state.MyPlayerId;

        if (instance.HasNode("Body"))
        {
            instance.GetNode<MeshInstance3D>("Body").MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = isLocal ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.3f, 0.5f, 0.9f)
            };
        }

        if (isLocal)
        {
            if (instance.HasNode("CameraPivot/Camera3D"))
                instance.GetNode<Camera3D>("CameraPivot/Camera3D").Current = true;
            if (instance.HasNode("Nametag"))
                instance.GetNode<Label3D>("Nametag").Visible = false;

            // Replace static camera with orbitable WoW-style camera
            if (instance.HasNode("CameraPivot"))
            {
                var pivot = instance.GetNode<Node3D>("CameraPivot");
                var camCtrl = new Player.CameraController();
                // Move Camera3D to be child of the controller
                var cam = instance.GetNode<Camera3D>("CameraPivot/Camera3D");
                cam.GetParent().RemoveChild(cam);
                camCtrl.AddChild(cam);
                cam.Position = new Vector3(0, 0, 15); // Distance behind
                pivot.AddChild(camCtrl);
                camCtrl.Name = "CameraController";
            }

            instance.AddChild(new Player.PlayerController());
            GD.Print("World: local player spawned with orbit camera");
        }
        else
        {
            if (instance.HasNode("CameraPivot"))
                instance.GetNode<Node3D>("CameraPivot").Visible = false;
        }
    }

    private void SpawnPlaceholder(string id, Vector3 pos, Vector3 size, Color color)
    {
        var node = new MeshInstance3D();
        node.Mesh = new BoxMesh { Size = size };
        node.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
        AddChild(node);
        node.Position = pos + new Vector3(0, size.Y / 2f, 0);
        _entities[id] = node;
    }

    private void OnChanged(string id, Vector3 pos, Vector3 vel, float heading, long tick)
    {
        if (!_entities.TryGetValue(id, out var node)) return;

        // Snap to terrain surface — feet on ground, not center-of-mass
        if (_state.HasTerrain)
        {
            float terrainY = TerrainGenerator.GetAltitudeAt(
                _state.AltitudeGrid, _state.GridWidth, _state.GridHeight, pos.X, pos.Z);
            pos = new Vector3(pos.X, terrainY, pos.Z);
        }

        if (node is RemoteEntity re)
            re.UpdateFromServer(pos, vel, heading, tick);
        else
            node.GlobalPosition = pos;
    }

    private void OnDataChanged(string id, string type, Godot.Collections.Dictionary meta, long tick)
    {
        if (_entities.TryGetValue(id, out var node) && node is TreeEntity tree)
            tree.UpdateFromServer(meta);
    }

    private void OnRemoved(string id)
    {
        if (_entities.Remove(id, out var node)) node.QueueFree();
    }

    private void SetupEnvironment()
    {
        var sky = new ProceduralSkyMaterial();
        sky.SkyTopColor = new Color(0.35f, 0.55f, 0.85f);
        sky.SkyHorizonColor = new Color(0.6f, 0.7f, 0.85f);
        sky.GroundBottomColor = new Color(0.2f, 0.25f, 0.15f);
        sky.GroundHorizonColor = new Color(0.5f, 0.55f, 0.45f);

        var skyRes = new Sky();
        skyRes.SkyMaterial = sky;

        var env = new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Sky;
        env.Sky = skyRes;

        // Ambient light so shadowed sides aren't pitch black
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.45f, 0.5f, 0.55f);
        env.AmbientLightEnergy = 0.4f;

        // Fog — reinforces AoI, distant things fade
        env.FogEnabled = true;
        env.FogLightColor = new Color(0.6f, 0.7f, 0.8f);
        env.FogDensity = 0.002f;
        env.FogSkyAffect = 0.3f;

        // Tonemap for richer colors
        env.TonemapMode = Godot.Environment.ToneMapper.Aces;

        var worldEnv = new WorldEnvironment();
        worldEnv.Environment = env;
        AddChild(worldEnv);
    }
}

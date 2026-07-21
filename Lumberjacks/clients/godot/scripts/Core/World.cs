using Godot;
using System.Collections.Generic;
using CommunitySurvival.Entities;

namespace CommunitySurvival.Core;

/// <summary>
/// Main 3D world controller.
/// Listens to GameState to spawn/move/remove entity scenes, and generates terrain from RegionProfile.
/// </summary>
public partial class World : Node3D
{
    [Export] public PackedScene PlayerScene;
    [Export] public PackedScene StructureScene;
    [Export] public PackedScene TreeScene;

    private GameState _gameState;
    private readonly Dictionary<string, Node3D> _entities = new();
    private MeshInstance3D _ground;

    // Read-only entity registry — slice 1 raycast helpers iterate this to
    // resolve cursor → entityId without needing physics colliders on orbs.
    public IReadOnlyDictionary<string, Node3D> Entities => _entities;

    // Tree entity types the server may send
    private static readonly HashSet<string> TreeTypes = new()
    {
        "tree", "natural_resource", "oak_tree", "pine_tree", "birch_tree", "basalt_rock"
    };

    public override void _Ready()
    {
        _gameState = GetNode<GameState>("/root/GameState");
        _ground = GetNode<MeshInstance3D>("Ground");

        _gameState.EntityAdded += OnEntityAdded;
        _gameState.EntityChanged += OnEntityChanged;
        _gameState.EntityDataChanged += OnEntityDataChanged;
        _gameState.EntityRemoved += OnEntityRemoved;
        _gameState.TerrainReady += OnTerrainReady;

        // Load scenes if not set in editor
        PlayerScene ??= GD.Load<PackedScene>("res://scenes/entities/player.tscn");
        StructureScene ??= GD.Load<PackedScene>("res://scenes/entities/structure.tscn");
        TreeScene ??= GD.Load<PackedScene>("res://scenes/entities/tree.tscn");

        GD.Print("World: Ready and listening to GameState.");

        // Replay entities that were parsed before World was instantiated
        // (snapshot arrives → GameState parses → Main creates World → World subscribes)
        _gameState.ReplayEntities();
    }

    public override void _ExitTree()
    {
        _gameState.EntityAdded -= OnEntityAdded;
        _gameState.EntityChanged -= OnEntityChanged;
        _gameState.EntityDataChanged -= OnEntityDataChanged;
        _gameState.EntityRemoved -= OnEntityRemoved;
        _gameState.TerrainReady -= OnTerrainReady;
    }

    private void OnEntityAdded(string entityId, string entityType, Vector3 position, float heading,
        Godot.Collections.Dictionary metadata)
    {
        if (_entities.ContainsKey(entityId)) return;

        Node3D instance;

        if (TreeTypes.Contains(entityType))
        {
            instance = TreeScene.Instantiate<Node3D>();
            AddChild(instance);
            _entities[entityId] = instance;

            metadata["entity_id"] = entityId;
            if (instance is TreeEntity tree)
            {
                tree.Initialize(position, heading, metadata);
            }
        }
        else if (entityType == "structure")
        {
            instance = StructureScene.Instantiate<Node3D>();
            AddChild(instance);
            _entities[entityId] = instance;

            if (instance is StructureEntity structure)
            {
                structure.Initialize(position, heading, metadata);
            }
        }
        else
        {
            // Default: player
            instance = PlayerScene.Instantiate<Node3D>();
            AddChild(instance);
            _entities[entityId] = instance;

            // Slice 1 metadata for raycast resolvers (HoverTooltip, SelectionInput).
            // The class_color Variant doesn't survive GetMeta cleanly, so we duplicate
            // the metadata that's safe to round-trip as primitives.
            instance.SetMeta("entity_id", entityId);
            if (metadata.ContainsKey("name")) instance.SetMeta("display_name", (string)metadata["name"]);
            if (metadata.ContainsKey("wow_class")) instance.SetMeta("wow_class", (string)metadata["wow_class"]);
            if (metadata.ContainsKey("wow_role")) instance.SetMeta("wow_role", (string)metadata["wow_role"]);
            if (metadata.ContainsKey("kind")) instance.SetMeta("kind", (string)metadata["kind"]);

            if (instance is RemoteEntity re)
            {
                re.Initialize(position, heading);
            }

            // Replay mode (slice 0): metadata["class_color"] takes over orb
            // coloring; no PlayerController, no camera attachment (replay
            // scene owns the camera). Nametag still uses metadata["name"]
            // like live mode. Returning early skips the live-mode local/
            // remote branches below, which assume a connected SimulationClient.
            if (metadata.ContainsKey("class_color"))
            {
                if (instance.HasNode("MeshInstance3D") && instance.GetNode<MeshInstance3D>("MeshInstance3D") is MeshInstance3D replayMesh)
                {
                    replayMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = (Color)metadata["class_color"] };
                }
                if (instance.HasNode("Nametag") && instance.GetNode<Label3D>("Nametag") is Label3D replayLabel)
                {
                    var replayName = metadata.ContainsKey("name") ? (string)metadata["name"] : entityId[..System.Math.Min(8, entityId.Length)];
                    replayLabel.Text = replayName;
                }
                return;
            }

            // Set nametag
            if (instance.HasNode("Nametag") && instance.GetNode<Label3D>("Nametag") is Label3D label)
            {
                var name = metadata.ContainsKey("name") ? (string)metadata["name"] : entityId[..System.Math.Min(8, entityId.Length)];
                label.Text = name;

                if (entityId == _gameState.MyPlayerId)
                {
                    // Local player: green nametag, add controller, enable camera
                    label.Modulate = new Color(0.2f, 1.0f, 0.2f);
                    label.Visible = false; // Hide own nametag

                    // Color the mesh green
                    if (instance.HasNode("MeshInstance3D") && instance.GetNode<MeshInstance3D>("MeshInstance3D") is MeshInstance3D mesh)
                    {
                        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.8f, 0.2f) };
                    }

                    var controller = new Player.PlayerController();
                    instance.AddChild(controller);

                    if (instance.HasNode("CameraPivot/Camera3D"))
                    {
                        instance.GetNode<Camera3D>("CameraPivot/Camera3D").Current = true;
                    }
                }
                else
                {
                    // Remote player: blue tint
                    if (instance.HasNode("MeshInstance3D") && instance.GetNode<MeshInstance3D>("MeshInstance3D") is MeshInstance3D mesh)
                    {
                        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.5f, 0.9f) };
                    }

                    // Disable camera for remote players
                    if (instance.HasNode("CameraPivot"))
                    {
                        instance.GetNode<Node3D>("CameraPivot").Visible = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Player position/velocity updates (interpolated entities).
    /// </summary>
    private void OnEntityChanged(string entityId, Vector3 position, Vector3 velocity, float heading, long tick)
    {
        if (!_entities.TryGetValue(entityId, out var node)) return;

        if (node is RemoteEntity re)
        {
            re.UpdateFromServer(position, velocity, heading, tick);
        }
    }

    /// <summary>
    /// Non-player entity data updates (natural resources, structures).
    /// </summary>
    private void OnEntityDataChanged(string entityId, string entityType, Godot.Collections.Dictionary metadata, long tick)
    {
        if (!_entities.TryGetValue(entityId, out var node)) return;

        if (node is TreeEntity tree)
        {
            tree.UpdateFromServer(metadata);
        }
    }

    private void OnEntityRemoved(string entityId)
    {
        if (_entities.Remove(entityId, out var node))
        {
            node.QueueFree();
        }
    }

    private void OnTerrainReady()
    {
        if (!_gameState.HasTerrainData) return;

        GD.Print("World: Generating terrain mesh...");
        var terrainMesh = TerrainGenerator.Generate(
            _gameState.AltitudeGrid,
            _gameState.GridWidth,
            _gameState.GridHeight);

        if (terrainMesh != null && _ground != null)
        {
            _ground.Mesh = terrainMesh;
            // Remove the flat green material — terrain generator applies vertex colors or a new material
            _ground.MaterialOverride = null;
            GD.Print("World: Terrain mesh applied.");
        }
    }
}

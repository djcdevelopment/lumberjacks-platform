using Godot;
using System.Collections.Generic;
using CommunitySurvival.Core;

namespace CommunitySurvival.Replay;

/// <summary>
/// Renders a class-colored, pulsing torus ring just below each highlighted
/// entity. Subscribes to SelectionManager — adds/removes ring meshes as
/// selection (and per-entity highlight flag) changes; tracks orb positions
/// in _Process so rings follow movement.
/// </summary>
public partial class HighlightRing : Node3D
{
    [Export] public float InnerRadius = 0.55f;
    [Export] public float OuterRadius = 0.85f;
    [Export] public float HoverHeight = 0.05f;
    [Export] public float PulseHz = 1.0f;
    [Export] public float EmissionMin = 0.4f;
    [Export] public float EmissionMax = 1.6f;

    private SelectionManager _selection;
    private Core.World _world;
    private readonly Dictionary<string, MeshInstance3D> _rings = new();
    private float _pulsePhase = 0f;

    public override void _Ready()
    {
        _selection = GetNode<SelectionManager>("/root/SelectionManager");
        _world = GetParent().GetNodeOrNull<Core.World>("World");
        _selection.SelectionChanged += SyncRings;
        SyncRings();
    }

    public override void _ExitTree()
    {
        if (_selection != null) _selection.SelectionChanged -= SyncRings;
    }

    private void SyncRings()
    {
        var stale = new HashSet<string>(_rings.Keys);

        foreach (var id in _selection.Selected)
        {
            if (!_selection.IsHighlightEnabled(id)) continue;
            stale.Remove(id);
            if (_rings.ContainsKey(id)) continue;
            if (_world == null || !_world.Entities.TryGetValue(id, out var node)) continue;

            var cls = node.HasMeta("wow_class") ? (string)node.GetMeta("wow_class") : "";
            var role = node.HasMeta("wow_role") ? (string)node.GetMeta("wow_role") : "";
            var color = WowClassColors.For(cls, role);

            var mesh = new TorusMesh
            {
                InnerRadius = InnerRadius,
                OuterRadius = OuterRadius,
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = color,
                EmissionEnabled = true,
                Emission = color,
                EmissionEnergyMultiplier = (EmissionMin + EmissionMax) * 0.5f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            };
            var ring = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
            AddChild(ring);
            _rings[id] = ring;
        }

        foreach (var id in stale)
        {
            if (_rings.TryGetValue(id, out var ring))
            {
                ring.QueueFree();
                _rings.Remove(id);
            }
        }
    }

    public override void _Process(double delta)
    {
        _pulsePhase += (float)delta * PulseHz * Mathf.Pi * 2f;
        var pulse01 = 0.5f + 0.5f * Mathf.Sin(_pulsePhase);
        var emission = Mathf.Lerp(EmissionMin, EmissionMax, pulse01);

        foreach (var (id, ring) in _rings)
        {
            if (_world != null && _world.Entities.TryGetValue(id, out var node))
            {
                ring.GlobalPosition = node.GlobalPosition + new Vector3(0, HoverHeight, 0);
            }
            if (ring.MaterialOverride is StandardMaterial3D mat)
            {
                mat.EmissionEnergyMultiplier = emission;
            }
        }
    }
}

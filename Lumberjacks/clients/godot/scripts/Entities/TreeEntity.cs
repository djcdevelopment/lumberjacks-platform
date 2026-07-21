using Godot;
using System;
using System.Text.Json;

namespace CommunitySurvival.Entities;

/// <summary>
/// Tree entity with visual states: healthy, felled (with directional fall), stump, and regrowing sapling.
/// Each tree gets deterministic visual variation from its entity ID hash + growth_history from server.
/// </summary>
public partial class TreeEntity : Node3D
{
    private MeshInstance3D _trunk;
    private MeshInstance3D _canopy;
    private MeshInstance3D _canopy2;
    private MeshInstance3D _canopy3;
    private MeshInstance3D _stump;
    private MeshInstance3D _sapling;

    private string _entityId;
    private double _health = 100;
    private double _stumpHealth = 50;
    private double _regrowthProgress;
    private double _leanX, _leanZ;
    private bool _isFelled;
    private float _fallHeading;

    // Growth history (arrives later via entity_update, or derived from ID hash)
    private float _twist;
    private int _ageYears = 100;
    private bool _fireScars;

    public override void _Ready()
    {
        _trunk = GetNode<MeshInstance3D>("Trunk");
        _canopy = GetNode<MeshInstance3D>("Canopy");
        _canopy2 = GetNode<MeshInstance3D>("Canopy2");
        _canopy3 = GetNode<MeshInstance3D>("Canopy3");
        _stump = GetNode<MeshInstance3D>("Stump");
        _sapling = GetNode<MeshInstance3D>("Sapling");
    }

    public void Initialize(Vector3 position, float heading, Godot.Collections.Dictionary metadata)
    {
        _entityId = metadata.ContainsKey("entity_id") ? (string)metadata["entity_id"] : GetInstanceId().ToString();
        GlobalPosition = position;

        // Parse health/lean from metadata
        _health = metadata.ContainsKey("health") ? (double)metadata["health"] : 100.0;
        _leanX = metadata.ContainsKey("lean_x") ? (double)metadata["lean_x"] : 0;
        _leanZ = metadata.ContainsKey("lean_z") ? (double)metadata["lean_z"] : 0;
        _stumpHealth = metadata.ContainsKey("stump_health") ? (double)metadata["stump_health"] : 50;
        _regrowthProgress = metadata.ContainsKey("regrowth_progress") ? (double)metadata["regrowth_progress"] : 0;

        // Parse growth_history if available
        if (metadata.ContainsKey("growth_history"))
        {
            ParseGrowthHistory((string)metadata["growth_history"]);
        }

        // Apply deterministic visual variation from entity ID hash
        ApplyVisualVariation();
        UpdateVisualState();
    }

    /// <summary>
    /// Called when server sends entity_update with new health/lean/regrowth data.
    /// </summary>
    public void UpdateFromServer(Godot.Collections.Dictionary metadata)
    {
        if (metadata.ContainsKey("health"))
        {
            var oldHealth = _health;
            _health = (double)metadata["health"];

            // Transition to felled state
            if (oldHealth > 0 && _health <= 0 && !_isFelled)
            {
                TriggerFellAnimation();
            }
        }

        if (metadata.ContainsKey("stump_health")) _stumpHealth = (double)metadata["stump_health"];
        if (metadata.ContainsKey("regrowth_progress")) _regrowthProgress = (double)metadata["regrowth_progress"];
        if (metadata.ContainsKey("lean_x")) _leanX = (double)metadata["lean_x"];
        if (metadata.ContainsKey("lean_z")) _leanZ = (double)metadata["lean_z"];

        if (metadata.ContainsKey("growth_history"))
        {
            ParseGrowthHistory((string)metadata["growth_history"]);
            ApplyVisualVariation();
        }

        UpdateVisualState();
    }

    private void ParseGrowthHistory(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("twist", out var twistEl))
                _twist = float.Parse(twistEl.GetString() ?? "0");
            if (root.TryGetProperty("age_years", out var ageEl))
                _ageYears = int.Parse(ageEl.GetString() ?? "100");
            if (root.TryGetProperty("fire_scars", out var fireEl))
                _fireScars = bool.TryParse(fireEl.GetString(), out var fs) && fs;
            if (root.TryGetProperty("fall_heading", out var fallEl))
                _fallHeading = float.Parse(fallEl.GetString() ?? "0");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"TreeEntity: Growth history parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply deterministic visual variation so each tree looks unique.
    /// Uses entity ID hash for consistency (same tree always looks the same).
    /// Growth history overrides when available.
    /// </summary>
    private void ApplyVisualVariation()
    {
        var hash = _entityId?.GetHashCode() ?? 0;
        var rng = new Random(hash);

        // Trunk height variation: 0.85x to 1.15x based on age
        float ageScale = Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp(_ageYears / 200f, 0f, 1f));
        _trunk.Scale = new Vector3(1, ageScale, 1);
        _trunk.Position = new Vector3(0, 2.0f * ageScale, 0);

        // Canopy offsets: each canopy sphere gets a unique rotation and slight scale variation
        float baseRotation = (float)(rng.NextDouble() * Mathf.Tau);
        float twistRad = _twist * 0.175f; // ~10 degrees per unit of twist

        _canopy.RotationDegrees = new Vector3(0, Mathf.RadToDeg(baseRotation + twistRad), 0);
        _canopy.Scale = Vector3.One * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble());
        _canopy.Position = new Vector3(0, 4.2f * ageScale, 0);

        _canopy2.RotationDegrees = new Vector3(0, Mathf.RadToDeg(baseRotation + twistRad + 2.1f), 0);
        float c2scale = 0.8f * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble());
        _canopy2.Scale = Vector3.One * c2scale;
        _canopy2.Position = new Vector3(0.6f, 3.8f * ageScale, 0.4f);

        _canopy3.RotationDegrees = new Vector3(0, Mathf.RadToDeg(baseRotation + twistRad + 4.2f), 0);
        float c3scale = 0.7f * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble());
        _canopy3.Scale = Vector3.One * c3scale;
        _canopy3.Position = new Vector3(-0.5f, 4.8f * ageScale, -0.3f);

        // Canopy color variation: slight hue shift per tree
        float greenShift = Mathf.Lerp(-0.04f, 0.04f, (float)rng.NextDouble());
        var canopyColor = new Color(0.13f + greenShift * 0.5f, 0.4f + greenShift, 0.13f - greenShift * 0.3f);

        // Fire-scarred trees: darker trunk, slightly browner canopy
        Color trunkColor;
        if (_fireScars)
        {
            trunkColor = new Color(0.25f, 0.15f, 0.08f); // Charred
            canopyColor = canopyColor.Lerp(new Color(0.3f, 0.35f, 0.1f), 0.3f); // Slightly yellowed
        }
        else
        {
            trunkColor = new Color(
                0.4f + Mathf.Lerp(-0.05f, 0.05f, (float)rng.NextDouble()),
                0.26f,
                0.13f);
        }

        ApplyColor(_trunk, trunkColor);
        ApplyColor(_canopy, canopyColor);
        ApplyColor(_canopy2, canopyColor);
        ApplyColor(_canopy3, canopyColor);
        ApplyColor(_stump, trunkColor);
    }

    private void UpdateVisualState()
    {
        if (_regrowthProgress > 0 && _regrowthProgress < 1.0)
        {
            // Regrowing sapling
            _trunk.Visible = false;
            _canopy.Visible = false;
            _canopy2.Visible = false;
            _canopy3.Visible = false;
            _stump.Visible = _stumpHealth > 0;
            _sapling.Visible = true;
            float scale = Mathf.Lerp(0.2f, 1.0f, (float)_regrowthProgress);
            _sapling.Scale = Vector3.One * scale;
        }
        else if (_health <= 0 || _isFelled)
        {
            // Felled
            _canopy.Visible = false;
            _canopy2.Visible = false;
            _canopy3.Visible = false;
            _stump.Visible = _stumpHealth > 0;
            _sapling.Visible = false;
            // Trunk visibility handled by fall animation
        }
        else
        {
            // Healthy
            _trunk.Visible = true;
            _canopy.Visible = true;
            _canopy2.Visible = true;
            _canopy3.Visible = true;
            _stump.Visible = false;
            _sapling.Visible = false;
        }
    }

    private void TriggerFellAnimation()
    {
        _isFelled = true;

        // Compute fall direction from accumulated lean + growth history fall_heading
        float fallAngle;
        if (_fallHeading != 0)
        {
            // Server computed the fall heading from lean vectors + trade winds
            fallAngle = Core.CoordinateMapper.ServerHeadingToGodot(_fallHeading);
        }
        else if (Math.Abs(_leanX) > 0.01 || Math.Abs(_leanZ) > 0.01)
        {
            // Fall in direction of accumulated lean (mapped to Godot space)
            fallAngle = Mathf.Atan2((float)_leanX, (float)-_leanZ);
        }
        else
        {
            // Random fall direction from entity hash
            fallAngle = (_entityId?.GetHashCode() ?? 0) % 628 / 100f;
        }

        // Rotate trunk around Y to face fall direction, then tween rotation.x to topple
        _trunk.Rotation = new Vector3(0, fallAngle, 0);

        var tween = CreateTween();
        tween.TweenProperty(_trunk, "rotation:x", Mathf.Pi / 2f, 1.2)
            .SetTrans(Tween.TransitionType.Bounce)
            .SetEase(Tween.EaseType.Out);

        _stump.Visible = _stumpHealth > 0;
    }

    private static void ApplyColor(MeshInstance3D mesh, Color color)
    {
        if (mesh.MaterialOverride is StandardMaterial3D existing)
        {
            existing.AlbedoColor = color;
        }
        else
        {
            mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
        }
    }
}

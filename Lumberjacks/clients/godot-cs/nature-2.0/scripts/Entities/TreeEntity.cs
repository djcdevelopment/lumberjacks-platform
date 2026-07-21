using Godot;
using System;
using System.Text.Json;

namespace CommunitySurvival.Entities;

/// <summary>
/// Tree with visual variation from growth_history and entity ID hash.
/// States: healthy → felled (directional fall) → stump → regrowing sapling.
/// </summary>
public partial class TreeEntity : Node3D
{
    private MeshInstance3D _trunk, _canopy, _canopy2, _canopy3, _stump, _sapling;
    private string _entityId;
    private double _health = 100, _stumpHealth = 50, _regrowth;
    private double _leanX, _leanZ;
    private bool _isFelled;
    private float _twist, _fallHeading;
    private int _age = 100;
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

    public void Initialize(Vector3 pos, float heading, Godot.Collections.Dictionary meta)
    {
        _entityId = meta.ContainsKey("entity_id") ? (string)meta["entity_id"] : GetInstanceId().ToString();
        Position = pos;

        _health = meta.ContainsKey("health") ? (double)meta["health"] : 100;
        _leanX = meta.ContainsKey("lean_x") ? (double)meta["lean_x"] : 0;
        _leanZ = meta.ContainsKey("lean_z") ? (double)meta["lean_z"] : 0;
        _stumpHealth = meta.ContainsKey("stump_health") ? (double)meta["stump_health"] : 50;
        _regrowth = meta.ContainsKey("regrowth_progress") ? (double)meta["regrowth_progress"] : 0;

        if (meta.ContainsKey("growth_history"))
            ParseHistory((string)meta["growth_history"]);

        ApplyVariation();
        UpdateVisuals();
    }

    public void UpdateFromServer(Godot.Collections.Dictionary meta)
    {
        if (meta.ContainsKey("health"))
        {
            double old = _health;
            _health = (double)meta["health"];
            if (old > 0 && _health <= 0 && !_isFelled) TriggerFall();
        }
        if (meta.ContainsKey("stump_health")) _stumpHealth = (double)meta["stump_health"];
        if (meta.ContainsKey("regrowth_progress")) _regrowth = (double)meta["regrowth_progress"];
        if (meta.ContainsKey("lean_x")) _leanX = (double)meta["lean_x"];
        if (meta.ContainsKey("lean_z")) _leanZ = (double)meta["lean_z"];
        if (meta.ContainsKey("growth_history"))
        {
            ParseHistory((string)meta["growth_history"]);
            ApplyVariation();
        }
        UpdateVisuals();
    }

    private void ParseHistory(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("twist", out var tw)) float.TryParse(tw.GetString(), out _twist);
            if (r.TryGetProperty("age_years", out var ag)) int.TryParse(ag.GetString(), out _age);
            if (r.TryGetProperty("fire_scars", out var fs)) bool.TryParse(fs.GetString(), out _fireScars);
            if (r.TryGetProperty("fall_heading", out var fh)) float.TryParse(fh.GetString(), out _fallHeading);
        }
        catch { }
    }

    /// <summary>
    /// Deterministic visual variation from entity ID hash + growth history.
    /// Every tree looks different but consistently so.
    /// </summary>
    private void ApplyVariation()
    {
        var rng = new Random(_entityId?.GetHashCode() ?? 0);

        // Trunk height from age (20-200 years → 0.85-1.15x)
        float ageScale = Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp(_age / 200f, 0f, 1f));
        _trunk.Scale = new Vector3(1, ageScale, 1);
        _trunk.Position = new Vector3(0, 2f * ageScale, 0);

        // Canopy variation: rotation, scale, position shift
        float baseRot = (float)(rng.NextDouble() * Mathf.Tau);
        float twistRad = _twist * 0.175f;

        ApplyCanopy(_canopy, rng, baseRot + twistRad, 0.85f, 1.15f,
            new Vector3(0, 4.2f * ageScale, 0));
        ApplyCanopy(_canopy2, rng, baseRot + twistRad + 2.1f, 0.65f, 0.95f,
            new Vector3(0.6f, 3.8f * ageScale, 0.4f));
        ApplyCanopy(_canopy3, rng, baseRot + twistRad + 4.2f, 0.55f, 0.85f,
            new Vector3(-0.5f, 4.8f * ageScale, -0.3f));

        // Color variation
        float shift = Mathf.Lerp(-0.06f, 0.06f, (float)rng.NextDouble());
        var canopyColor = new Color(0.2f + shift * 0.3f, 0.55f + shift, 0.15f - shift * 0.2f);
        Color trunkColor;

        if (_fireScars)
        {
            trunkColor = new Color(0.3f, 0.18f, 0.1f);
            canopyColor = canopyColor.Lerp(new Color(0.4f, 0.45f, 0.15f), 0.3f);
        }
        else
        {
            trunkColor = new Color(
                0.45f + Mathf.Lerp(-0.05f, 0.05f, (float)rng.NextDouble()), 0.3f, 0.15f);
        }

        SetColor(_trunk, trunkColor);
        SetColor(_canopy, canopyColor);
        SetColor(_canopy2, canopyColor);
        SetColor(_canopy3, canopyColor);
        SetColor(_stump, trunkColor);

        if (_entityId?.GetHashCode() % 100 == 0)
            GD.Print($"Tree: pos={Position}, trunk_vis={_trunk.Visible}, canopy_vis={_canopy.Visible}, canopy_pos={_canopy.Position}, health={_health}");
    }

    private void ApplyCanopy(MeshInstance3D c, Random rng, float rot, float scaleMin, float scaleMax, Vector3 pos)
    {
        c.RotationDegrees = new Vector3(0, Mathf.RadToDeg(rot), 0);
        float s = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
        c.Scale = Vector3.One * s;
        c.Position = pos;
    }

    private void UpdateVisuals()
    {
        if (_regrowth > 0 && _regrowth < 1.0)
        {
            _trunk.Visible = false;
            _canopy.Visible = false; _canopy2.Visible = false; _canopy3.Visible = false;
            _stump.Visible = _stumpHealth > 0;
            _sapling.Visible = true;
            _sapling.Scale = Vector3.One * Mathf.Lerp(0.2f, 1f, (float)_regrowth);
        }
        else if (_health <= 0 || _isFelled)
        {
            _canopy.Visible = false; _canopy2.Visible = false; _canopy3.Visible = false;
            _stump.Visible = _stumpHealth > 0;
            _sapling.Visible = false;
        }
        else
        {
            _trunk.Visible = true;
            _canopy.Visible = true; _canopy2.Visible = true; _canopy3.Visible = true;
            _stump.Visible = false;
            _sapling.Visible = false;
        }
    }

    private void TriggerFall()
    {
        _isFelled = true;
        float angle;
        if (_fallHeading != 0)
            angle = Core.CoordinateMapper.ServerHeadingToGodot(_fallHeading);
        else if (Math.Abs(_leanX) > 0.01 || Math.Abs(_leanZ) > 0.01)
            angle = Mathf.Atan2((float)_leanX, (float)-_leanZ);
        else
            angle = (_entityId?.GetHashCode() ?? 0) % 628 / 100f;

        _trunk.Rotation = new Vector3(0, angle, 0);
        var tween = CreateTween();
        tween.TweenProperty(_trunk, "rotation:x", Mathf.Pi / 2f, 1.2)
            .SetTrans(Tween.TransitionType.Bounce)
            .SetEase(Tween.EaseType.Out);
        _stump.Visible = _stumpHealth > 0;
    }

    private static void SetColor(MeshInstance3D mesh, Color color)
    {
        // Always create a new material per mesh to avoid shared resource mutation
        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
    }
}

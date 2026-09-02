using Godot;
using System;
using System.Security.Cryptography;
using System.Text.Json;
using CommunitySurvival.Forest;

namespace CommunitySurvival.Entities;

/// <summary>
/// Tree with visual variation from growth_history and entity ID hash.
/// States: healthy → felled (directional fall) → stump → regrowing sapling.
/// </summary>
public partial class TreeEntity : Node3D
{
    private Node3D _standing;
    private MeshInstance3D _trunk, _canopy, _canopy2, _canopy3, _stump, _sapling;
    private MeshInstance3D _featuredTree;
    private ForestArchetype _featuredArchetype;
    private float _featuredModelHeight;
    private string _entityId;
    private double _health = 100, _stumpHealth = 50, _regrowth;
    private double _leanX, _leanZ;
    private bool _isFelled, _fallComplete;
    private float _twist, _fallHeading;
    private int _age = 100;
    private bool _fireScars;
    private string _name = "Pine";
    private string _fieldNote = "";

    public override void _Ready()
    {
        _standing = GetNode<Node3D>("Standing");
        _trunk = GetNode<MeshInstance3D>("Standing/Trunk");
        _canopy = GetNode<MeshInstance3D>("Standing/Canopy");
        _canopy2 = GetNode<MeshInstance3D>("Standing/Canopy2");
        _canopy3 = GetNode<MeshInstance3D>("Standing/Canopy3");
        _stump = GetNode<MeshInstance3D>("Stump");
        _sapling = GetNode<MeshInstance3D>("Sapling");
    }

    public void Initialize(Vector3 pos, float heading, Godot.Collections.Dictionary meta,
        ForestAssetLibrary forestAssets = null)
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

        ApplyFeaturedAsset(forestAssets);

        if (_health <= 0)
        {
            _isFelled = true;
            _fallComplete = true;
        }

        ApplyVariation();
        AddFeaturedMarker();
        UpdateVisuals();
    }

    public void UpdateFromServer(Godot.Collections.Dictionary meta)
    {
        double oldHealth = _health;
        if (meta.ContainsKey("health")) _health = (double)meta["health"];
        if (meta.ContainsKey("stump_health")) _stumpHealth = (double)meta["stump_health"];
        if (meta.ContainsKey("regrowth_progress")) _regrowth = (double)meta["regrowth_progress"];
        if (meta.ContainsKey("lean_x")) _leanX = (double)meta["lean_x"];
        if (meta.ContainsKey("lean_z")) _leanZ = (double)meta["lean_z"];
        if (meta.ContainsKey("growth_history"))
        {
            ParseHistory((string)meta["growth_history"]);
            ApplyVariation();
        }
        if (oldHealth > _health && _health > 0) ShowStrike();
        if (oldHealth > 0 && _health <= 0 && !_isFelled)
        {
            TriggerFall();
            return;
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
            if (r.TryGetProperty("name", out var name)) _name = name.GetString() ?? _name;
            if (r.TryGetProperty("field_note", out var note)) _fieldNote = note.GetString() ?? "";
        }
        catch { }
    }

    /// <summary>
    /// Deterministic visual variation from entity ID hash + growth history.
    /// Every tree looks different but consistently so.
    /// </summary>
    private void ApplyVariation()
    {
        var rng = new Random(StableSeed(_entityId));

        // Trunk height from age (20-200 years → 0.85-1.15x)
        float ageScale = Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp(_age / 200f, 0f, 1f));
        _trunk.Scale = new Vector3(1, ageScale, 1);
        _trunk.Position = new Vector3(0, 2f * ageScale, 0);

        // Canopy variation: rotation, scale, position shift
        float baseRot = (float)(rng.NextDouble() * Mathf.Tau);
        float twistRad = _twist * 0.175f;

        if (_featuredTree != null)
        {
            var desiredHeight = 6.8f * ageScale;
            if (_name.Contains("white pine", StringComparison.OrdinalIgnoreCase)) desiredHeight *= 1.28f;
            var scale = desiredHeight / _featuredModelHeight;
            _featuredTree.Scale = Vector3.One * scale;
            _featuredTree.Rotation = new Vector3(0f, baseRot + twistRad, 0f);
            var traits = _featuredArchetype switch
            {
                ForestArchetype.Pine => (Stiffness: 0.86f, Crown: 0.72f),
                ForestArchetype.Common => (Stiffness: 0.66f, Crown: 0.90f),
                _ => (Stiffness: 1.14f, Crown: 1.18f),
            };
            _featuredTree.SetInstanceShaderParameter("use_tree_traits_override", 1f);
            _featuredTree.SetInstanceShaderParameter("tree_traits_override", new Color(
                (float)rng.NextDouble(),
                traits.Stiffness * Mathf.Lerp(0.9f, 1.1f, (float)rng.NextDouble()),
                traits.Crown * Mathf.Lerp(0.9f, 1.1f, (float)rng.NextDouble()),
                Mathf.Lerp(0.48f, 0.92f, (float)rng.NextDouble())));
        }

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

    }

    private void ApplyCanopy(MeshInstance3D c, Random rng, float rot, float scaleMin, float scaleMax, Vector3 pos)
    {
        c.RotationDegrees = new Vector3(0, Mathf.RadToDeg(rot), 0);
        float s = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
        c.Scale = Vector3.One * s;
        c.Position = pos;
    }

    private void ApplyFeaturedAsset(ForestAssetLibrary assets)
    {
        if (assets == null) return;
        var selector = StableSeed(_entityId) % 5;
        _featuredArchetype = _name.Contains("pine", StringComparison.OrdinalIgnoreCase)
            ? ForestArchetype.Pine
            : _name.Contains("oak", StringComparison.OrdinalIgnoreCase) ||
              _name.Contains("birch", StringComparison.OrdinalIgnoreCase)
                ? ForestArchetype.Common
                : selector switch
                {
                    0 or 1 => ForestArchetype.Pine,
                    2 or 3 => ForestArchetype.Common,
                    _ => ForestArchetype.Twisted,
                };
        var asset = assets[_featuredArchetype];
        _featuredModelHeight = asset.ModelHeight;
        _featuredTree = new MeshInstance3D
        {
            Name = "WindTree",
            Mesh = asset.Mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
        _standing.AddChild(_featuredTree);
        _trunk.Visible = false;
        _canopy.Visible = false;
        _canopy2.Visible = false;
        _canopy3.Visible = false;
    }

    private void UpdateVisuals()
    {
        if (_regrowth > 0 && _regrowth < 1.0)
        {
            if (_featuredTree != null) _featuredTree.Visible = false;
            _trunk.Visible = false;
            _canopy.Visible = false; _canopy2.Visible = false; _canopy3.Visible = false;
            _stump.Visible = _stumpHealth > 0;
            _sapling.Visible = true;
            _sapling.Scale = Vector3.One * Mathf.Lerp(0.2f, 1f, (float)_regrowth);
        }
        else if (_isFelled && !_fallComplete)
        {
            _standing.Visible = true;
            if (_featuredTree != null) _featuredTree.Visible = true;
            _stump.Visible = false;
            _sapling.Visible = false;
        }
        else if (_health <= 0 || _isFelled)
        {
            _standing.Visible = false;
            _stump.Visible = _stumpHealth > 0;
            _sapling.Visible = false;
        }
        else
        {
            _standing.Visible = true;
            var fallback = _featuredTree == null;
            if (_featuredTree != null) _featuredTree.Visible = true;
            _trunk.Visible = fallback;
            _canopy.Visible = fallback; _canopy2.Visible = fallback; _canopy3.Visible = fallback;
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
            angle = StableSeed(_entityId) % 628 / 100f;

        _standing.Rotation = new Vector3(0, angle, 0);
        _standing.Visible = true;
        _stump.Visible = false;
        var tween = CreateTween();
        tween.TweenProperty(_standing, "rotation:x", Mathf.Pi / 2f, 1.35)
            .SetTrans(Tween.TransitionType.Bounce)
            .SetEase(Tween.EaseType.Out);
        tween.TweenInterval(0.35);
        tween.TweenCallback(Callable.From(() =>
        {
            _fallComplete = true;
            UpdateVisuals();
        }));
    }

    private void ShowStrike()
    {
        if (_isFelled) return;
        var direction = Mathf.Sign((float)(_leanX + _leanZ));
        if (direction == 0) direction = 1;
        var tween = CreateTween();
        tween.TweenProperty(_standing, "rotation:z", direction * 0.035f, 0.06);
        tween.TweenProperty(_standing, "rotation:z", 0f, 0.16)
            .SetTrans(Tween.TransitionType.Elastic)
            .SetEase(Tween.EaseType.Out);
    }

    private void AddFeaturedMarker()
    {
        if (string.IsNullOrWhiteSpace(_fieldNote)) return;
        var marker = new Label3D
        {
            Text = _name,
            Position = new Vector3(0, 6.6f, 0),
            FontSize = 34,
            OutlineSize = 10,
            Modulate = new Color(0.91f, 0.79f, 0.53f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
        };
        AddChild(marker);
    }

    private static int StableSeed(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }

    private static void SetColor(MeshInstance3D mesh, Color color)
    {
        // Always create a new material per mesh to avoid shared resource mutation
        mesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = color };
    }
}

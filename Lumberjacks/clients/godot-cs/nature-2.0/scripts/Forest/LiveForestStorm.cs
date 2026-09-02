#nullable enable

using System;
using Godot;

namespace CommunitySurvival.Forest;

/// <summary>
/// Visual-only storm controller used by the playable world. Simulation and persistence remain
/// server authoritative; continuous vegetation motion and rain remain entirely on the GPU.
/// </summary>
public partial class LiveForestStorm : Node3D
{
    private ForestStormScenario _scenario = new();
    private ForestAssetLibrary? _assets;
    private DirectionalLight3D? _sun;
    private DirectionalLight3D? _fill;
    private Godot.Environment? _environment;
    private GpuParticles3D? _rain;
    private float _elapsed = 28f;

    public ForestAssetLibrary Assets => _assets ??
        throw new InvalidOperationException("Live forest storm has not been initialized.");

    public void Initialize(DirectionalLight3D sun, DirectionalLight3D fill)
    {
        _sun = sun;
        _fill = fill;
        _scenario = ForestStormScenario.Parse(Godot.FileAccess.GetFileAsString(
            "res://assets/scenarios/forest-storm-default.json"));
        _assets = new ForestAssetLibrary();
    }

    public override void _Ready()
    {
        if (_assets is null || _sun is null || _fill is null)
            throw new InvalidOperationException("Call Initialize before adding LiveForestStorm to the world.");
        BuildEnvironment();
        BuildRain();
    }

    public override void _Process(double delta)
    {
        if (_assets is null) return;
        _elapsed += (float)delta;
        var intensity = _scenario.EvaluateIntensity(_elapsed);
        var radians = Mathf.DegToRad(_scenario.WindDirectionDegrees);
        var direction = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        foreach (var material in _assets.Materials)
        {
            material.SetShaderParameter("storm_time", _elapsed);
            material.SetShaderParameter("storm_intensity", intensity);
            material.SetShaderParameter("wind_direction", direction);
            material.SetShaderParameter("peak_wind_mps", _scenario.PeakWindMetersPerSecond);
            material.SetShaderParameter("turbulence", _scenario.Turbulence);
            material.SetShaderParameter("storm_center", new Vector2(_scenario.CenterX, _scenario.CenterZ));
            material.SetShaderParameter("storm_radius", _scenario.RadiusMeters);
            material.SetShaderParameter("moisture", _scenario.Moisture);
        }

        if (_environment is not null)
        {
            _environment.FogDensity = 0.0035f + intensity * 0.0052f;
            _environment.FogLightColor = new Color(0.31f, 0.37f, 0.37f)
                .Lerp(new Color(0.20f, 0.25f, 0.27f), intensity);
        }
        if (_sun is not null)
        {
            var lightning = intensity > 0.72f
                ? Mathf.Pow(Mathf.Max(0f, Mathf.Sin(_elapsed * 2.71f) - 0.985f) / 0.015f, 3f)
                : 0f;
            _sun.LightEnergy = Mathf.Lerp(1.05f, 0.48f, intensity) + lightning * 2.4f;
            _sun.LightColor = new Color(0.78f, 0.83f, 0.87f)
                .Lerp(new Color(0.63f, 0.71f, 0.78f), intensity);
        }
        if (_fill is not null) _fill.LightEnergy = Mathf.Lerp(0.30f, 0.16f, intensity);
        if (_rain is not null)
        {
            _rain.AmountRatio = Mathf.Clamp((intensity - 0.08f) * 1.45f, 0f, 1f);
            var camera = GetViewport().GetCamera3D();
            if (camera is not null) _rain.GlobalPosition = camera.GlobalPosition + new Vector3(0f, 23f, 0f);
        }
    }

    public void FollowTradeWind(double x, double z)
    {
        if (Math.Abs(x) + Math.Abs(z) < 0.001) return;
        var degrees = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2((float)z, (float)x)), 360f);
        _scenario = _scenario with { WindDirectionDegrees = degrees };
    }

    private void BuildEnvironment()
    {
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.035f, 0.055f, 0.075f),
            SkyHorizonColor = new Color(0.24f, 0.29f, 0.30f),
            GroundBottomColor = new Color(0.018f, 0.025f, 0.022f),
            GroundHorizonColor = new Color(0.12f, 0.17f, 0.15f),
        };
        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMaterial },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.38f, 0.43f, 0.44f),
            AmbientLightEnergy = 0.36f,
            FogEnabled = true,
            FogLightColor = new Color(0.31f, 0.37f, 0.37f),
            FogDensity = 0.0045f,
            FogSkyAffect = 0.72f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
        };
        AddChild(new WorldEnvironment { Environment = _environment });
    }

    private void BuildRain()
    {
        var rainMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.64f, 0.75f, 0.82f, 0.46f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        _rain = new GpuParticles3D
        {
            Amount = 3600,
            Lifetime = 1.7,
            Emitting = true,
            AmountRatio = 0f,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(0.025f, 1.4f),
                Material = rainMaterial,
            },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(38f, 2f, 38f),
                Direction = Vector3.Down,
                Spread = 5f,
                InitialVelocityMin = 30f,
                InitialVelocityMax = 42f,
                Gravity = new Vector3(4.5f, -9.8f, 2.5f),
            },
        };
        AddChild(_rain);
    }
}

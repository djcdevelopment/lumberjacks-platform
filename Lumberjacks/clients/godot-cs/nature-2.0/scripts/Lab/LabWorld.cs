using Godot;
using System;

namespace CommunitySurvival.Lab;

/// <summary>
/// Isolation lab. Tiny world, one tree cluster, player orb.
/// Full telemetry + real-time atmosphere tuning.
///
/// Movement:
///   WASD = camera-relative move, Q/E = strafe
///   Space/Ctrl = up/down, RMB = orbit, LMB+RMB = auto-run, Scroll = zoom
///
/// Atmosphere (hold Tab to show tuning panel, keys while held):
///   1/2 = fog density up/down
///   3/4 = god ray energy up/down
///   5/6 = sun angle up/down
///   7/8 = ambient light up/down
///   9/0 = tree scale up/down
///   -/= = time of day shift
/// </summary>
public partial class LabWorld : Node3D
{
    private const float WorldSize = 40f;
    private const int GridRes = 100;
    private const float MaxAltitude = 6f;
    private const float PlayerRadius = 0.3f;
    private const float MoveSpeed = 5f;

    // Nodes
    private MeshInstance3D _ground;
    private MeshInstance3D _player;
    private Node3D _axe;
    private Camera3D _camera;
    private Node3D _cameraPivot;
    private Label _hud;
    private double[] _altGrid;

    // Trees
    private Node3D _treeCluster;
    private float _treeScale = 1.0f;

    // Lights
    private DirectionalLight3D _sun;
    private float _sunAngle = -40f;
    private float _sunRotation = 30f; // horizontal angle

    // Environment
    private Godot.Environment _env;
    private float _fogDensity = 0.02f;
    private float _ambientEnergy = 0.4f;
    private float _godRayEnergy = 0.0f; // starts off, user dials up

    // Smoke
    private GpuParticles3D _smoke;

    // Terrain shader params
    private ShaderMaterial _terrainMat;
    private float _slopeThreshold = 0.4f;
    private float _noiseScale = 25f;

    // Camera
    private float _camYaw, _camPitch = -20f, _camDist = 10f;
    private bool _lmbHeld, _rmbHeld;

    // Player
    private Vector3 _playerPos;
    private float _playerFacing;

    // Tuning panel
    private TuningPanel _tuningPanel;

    // Tree inspect
    private class TreeInfo { public Node3D Node; public int Age; public bool FireScars; public float Scale; }
    private readonly System.Collections.Generic.List<TreeInfo> _trees = new();
    private Label _inspectLabel;
    private TreeInfo _nearestTree;

    public override void _Ready()
    {
        _altGrid = GenerateHills();

        // Terrain
        _ground = new MeshInstance3D { Mesh = BuildTerrainMesh() };
        AddChild(_ground);

        // Player orb + axe
        BuildPlayer();

        // Tree cluster — a small grove of varied trees
        _treeCluster = new Node3D();
        AddChild(_treeCluster);
        SpawnTree(new Vector3(5f, 0, -3f), 1.0f, 120, false);
        SpawnTree(new Vector3(7f, 0, -1f), 0.7f, 60, false);
        SpawnTree(new Vector3(4f, 0, -6f), 1.3f, 180, true); // fire-scarred old growth
        SpawnTree(new Vector3(8f, 0, -5f), 0.5f, 30, false); // young tree
        SpawnTree(new Vector3(3f, 0, 0f), 1.1f, 140, false);
        SpawnTree(new Vector3(-2f, 0, -4f), 1.5f, 200, true); // ancient
        SpawnTree(new Vector3(6f, 0, -8f), 0.9f, 90, false);

        // Stump (felled tree remains)
        SpawnStump(new Vector3(5.5f, 0, -1f));

        // Woodpile
        SpawnWoodpile(new Vector3(2f, 0, 1f));

        // Campfire with smoke
        SpawnCampfire(new Vector3(1f, 0, 2f));

        // Sun (warm, low angle for god rays through canopy)
        _sun = new DirectionalLight3D();
        _sun.LightEnergy = 1.4f;
        _sun.ShadowEnabled = true;
        _sun.LightColor = new Color(1f, 0.92f, 0.8f);
        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        UpdateSunAngle();
        AddChild(_sun);

        // Fill light (cool)
        var fill = new DirectionalLight3D();
        fill.RotationDegrees = new Vector3(-30, -150, 0);
        fill.LightEnergy = 0.2f;
        fill.ShadowEnabled = false;
        fill.LightColor = new Color(0.6f, 0.7f, 0.9f);
        AddChild(fill);

        // Environment
        SetupEnvironment();

        // Camera
        _cameraPivot = new Node3D();
        AddChild(_cameraPivot);
        _camera = new Camera3D { Fov = 55, Current = true };
        _cameraPivot.AddChild(_camera);
        UpdateCamera();

        // HUD
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label();
        _hud.AnchorLeft = 1.0f; _hud.AnchorRight = 1.0f;
        _hud.OffsetLeft = -350; _hud.OffsetTop = 10; _hud.OffsetRight = -10;
        _hud.HorizontalAlignment = HorizontalAlignment.Right;
        _hud.AddThemeFontSizeOverride("font_size", 13);
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _hud.AddThemeConstantOverride("shadow_offset_x", 1);
        _hud.AddThemeConstantOverride("shadow_offset_y", 1);
        canvas.AddChild(_hud);

        _inspectLabel = new Label();
        _inspectLabel.AnchorLeft = 0.6f; _inspectLabel.AnchorRight = 0.98f;
        _inspectLabel.AnchorTop = 0.1f; _inspectLabel.AnchorBottom = 0.5f;
        _inspectLabel.AddThemeFontSizeOverride("font_size", 14);
        _inspectLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 0.75f));
        _inspectLabel.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _inspectLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _inspectLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        _inspectLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _inspectLabel.Visible = false;
        canvas.AddChild(_inspectLabel);

        // Tuning panel with sliders
        _tuningPanel = new TuningPanel();
        AddChild(_tuningPanel);
        BuildTuningPanel();

        _playerPos = new Vector3(0, GetTerrainY(0, 0) + PlayerRadius, 0);
        _player.Position = _playerPos;

        GD.Print("Lab: ready. Tab=tuning panel, RMB=orbit, WASD=move");
    }

    private void SetupEnvironment()
    {
        var sky = new ProceduralSkyMaterial();
        sky.SkyTopColor = new Color(0.3f, 0.5f, 0.8f);
        sky.SkyHorizonColor = new Color(0.6f, 0.7f, 0.85f);
        sky.GroundBottomColor = new Color(0.15f, 0.2f, 0.1f);
        sky.GroundHorizonColor = new Color(0.45f, 0.5f, 0.4f);
        sky.SunAngleMax = 30f;
        sky.SunCurve = 0.1f;

        var skyRes = new Sky { SkyMaterial = sky };

        _env = new Godot.Environment();
        _env.BackgroundMode = Godot.Environment.BGMode.Sky;
        _env.Sky = skyRes;

        // Ambient
        _env.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
        _env.AmbientLightEnergy = _ambientEnergy;

        // Tonemap
        _env.TonemapMode = Godot.Environment.ToneMapper.Aces;
        _env.TonemapWhite = 6f;

        // Volumetric fog — the key atmosphere feature
        _env.VolumetricFogEnabled = true;
        _env.VolumetricFogDensity = _fogDensity;
        _env.VolumetricFogAlbedo = new Color(0.85f, 0.88f, 0.9f);
        _env.VolumetricFogEmission = new Color(0.1f, 0.1f, 0.12f);
        _env.VolumetricFogEmissionEnergy = 0.5f;
        // VolumetricFogGi not available in Godot 4.6 C# API — skip
        _env.VolumetricFogLength = 60f;
        _env.VolumetricFogDetailSpread = 0.7f;

        // Glow for soft bloom
        _env.GlowEnabled = true;
        _env.GlowIntensity = 0.3f;
        _env.GlowBloom = 0.1f;

        // SSAO for ground contact shadows
        _env.SsaoEnabled = true;
        _env.SsaoRadius = 2f;
        _env.SsaoIntensity = 1.5f;

        var we = new WorldEnvironment { Environment = _env };
        AddChild(we);
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left) _lmbHeld = mb.Pressed;
            if (mb.ButtonIndex == MouseButton.Right) _rmbHeld = mb.Pressed;
            Input.MouseMode = _rmbHeld ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            if (mb.ButtonIndex == MouseButton.WheelUp) _camDist = Mathf.Max(2f, _camDist - 1f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _camDist = Mathf.Min(40f, _camDist + 1f);
            UpdateCamera();
        }
        else if (ev is InputEventMouseMotion mm && _rmbHeld)
        {
            _camYaw -= mm.Relative.X * 0.3f;
            _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * 0.3f, -85f, 85f);
            UpdateCamera();
        }

        // Tab handled by TuningPanel directly
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Tuning handled by slider panel callbacks

        // Movement
        float yawRad = Mathf.DegToRad(_camYaw);
        var camFwd = new Vector3(-Mathf.Sin(yawRad), 0, -Mathf.Cos(yawRad));
        var camRight = new Vector3(camFwd.Z, 0, -camFwd.X);

        var move = Vector3.Zero;
        bool bothMouse = _lmbHeld && _rmbHeld;
        if (bothMouse) { move += camFwd; }
        else
        {
            if (Input.IsActionPressed("move_forward")) move += camFwd;
            if (Input.IsActionPressed("move_back")) move -= camFwd;
            if (Input.IsActionPressed("move_right")) move += camRight;
            if (Input.IsActionPressed("move_left")) move -= camRight;
            if (Input.IsKeyPressed(Key.Q)) move -= camRight;
            if (Input.IsKeyPressed(Key.E)) move += camRight;
        }
        if (move.LengthSquared() > 0.01f)
        {
            move = move.Normalized() * MoveSpeed * dt;
            _playerPos += move;
            _playerPos.X = Mathf.Clamp(_playerPos.X, -WorldSize / 2, WorldSize / 2);
            _playerPos.Z = Mathf.Clamp(_playerPos.Z, -WorldSize / 2, WorldSize / 2);
        }
        if (Input.IsKeyPressed(Key.Space)) _playerPos.Y += MoveSpeed * dt;
        if (Input.IsKeyPressed(Key.Ctrl)) _playerPos.Y -= MoveSpeed * dt;

        float terrainY = GetTerrainY(_playerPos.X, _playerPos.Z);
        if (!Input.IsKeyPressed(Key.Space) && !Input.IsKeyPressed(Key.Ctrl))
            _playerPos.Y = terrainY + PlayerRadius;

        _player.Position = _playerPos;
        if (move.X != 0 || move.Z != 0)
            _playerFacing = Mathf.LerpAngle(_playerFacing, Mathf.Atan2(move.X, move.Z), dt * 12f);
        _player.Rotation = new Vector3(0, _playerFacing, 0);
        _cameraPivot.Position = _playerPos;

        // Nearest tree tracking
        float nearDist = 999f;
        _nearestTree = null;
        foreach (var ti in _trees)
        {
            float d = (_playerPos - ti.Node.GlobalPosition).Length();
            if (d < nearDist) { nearDist = d; _nearestTree = ti; }
        }

        // F key inspect
        if (Input.IsKeyPressed(Key.F) && _nearestTree != null && nearDist < 5f)
        {
            var t = _nearestTree;
            string ageDesc = t.Age > 150 ? $"Ancient — roughly {t.Age} years"
                           : t.Age > 80 ? $"Mature — about {t.Age} years"
                           : $"Young — perhaps {t.Age} years";
            string fire = t.FireScars ? "\nHistory: Bark scarred from a past fire" : "";
            string scaleDesc = t.Scale > 1.2f ? "\nGrowth: Towering, dominant canopy"
                             : t.Scale < 0.6f ? "\nGrowth: Small understory tree"
                             : "\nGrowth: Healthy mid-canopy";
            _inspectLabel.Text = $"=== Oak Tree ===\n\nAge: {ageDesc}\n{scaleDesc}{fire}\n\nDist: {nearDist:F1}m";
            _inspectLabel.Visible = true;
        }
        else if (nearDist > 6f)
        {
            _inspectLabel.Visible = false;
        }

        // HUD
        _hud.Text =
            $"({_playerPos.X:F1}, {_playerPos.Y:F1}, {_playerPos.Z:F1})" +
            $"  Terrain: {terrainY:F2}" +
            (nearDist < 5f ? $"  Tree: {nearDist:F1}m [F]" : "") +
            (bothMouse ? "  AUTO-RUN" : "");
    }

    private void BuildTuningPanel()
    {
        // Atmosphere
        var atmo = _tuningPanel.AddSection("Atmosphere");
        atmo.AddSlider("Fog Density", 0f, 0.15f, _fogDensity, v => { _fogDensity = v; _env.VolumetricFogDensity = v; });
        atmo.AddSlider("Ambient Energy", 0f, 2f, _ambientEnergy, v => { _ambientEnergy = v; _env.AmbientLightEnergy = v; });

        // Lighting
        var light = _tuningPanel.AddSection("Lighting");
        light.AddSlider("Sun Energy", 0f, 5f, _sun.LightEnergy, v => _sun.LightEnergy = v);
        light.AddSlider("Sun Pitch", -89f, -5f, _sunAngle, v => { _sunAngle = v; UpdateSunAngle(); });
        light.AddSlider("Sun Rotation", -180f, 180f, _sunRotation, v => { _sunRotation = v; UpdateSunAngle(); });

        // Terrain
        var terrain = _tuningPanel.AddSection("Terrain");
        terrain.AddSlider("Slope Threshold", 0f, 1f, _slopeThreshold, v =>
        {
            _slopeThreshold = v;
            _terrainMat?.SetShaderParameter("slope_threshold", v);
        });
        terrain.AddSlider("Noise Scale", 1f, 100f, _noiseScale, v =>
        {
            _noiseScale = v;
            _terrainMat?.SetShaderParameter("noise_scale", v);
        });

        // Trees
        var trees = _tuningPanel.AddSection("Trees");
        trees.AddSlider("Scale", 0.3f, 5f, _treeScale, v => { _treeScale = v; UpdateTreeScale(); });
    }

    private void UpdateSunAngle()
    {
        _sun.RotationDegrees = new Vector3(_sunAngle, _sunRotation, 0);
    }

    private void UpdateTreeScale()
    {
        foreach (Node child in _treeCluster.GetChildren())
            if (child is Node3D n) n.Scale = Vector3.One * _treeScale;
    }

    private void UpdateCamera()
    {
        _cameraPivot.RotationDegrees = new Vector3(_camPitch, _camYaw, 0);
        _camera.Position = new Vector3(0, 0, _camDist);
    }

    // ——— Terrain ———

    private float GetTerrainY(float x, float z)
    {
        float gx = (x + WorldSize / 2) / WorldSize * GridRes;
        float gz = (z + WorldSize / 2) / WorldSize * GridRes;
        int ix = Mathf.Clamp((int)gx, 0, GridRes - 2);
        int iz = Mathf.Clamp((int)gz, 0, GridRes - 2);
        float fx = gx - ix, fz = gz - iz;
        int w = GridRes + 1;
        float y00 = (float)_altGrid[iz * w + ix];
        float y10 = (float)_altGrid[iz * w + ix + 1];
        float y01 = (float)_altGrid[(iz + 1) * w + ix];
        float y11 = (float)_altGrid[(iz + 1) * w + ix + 1];
        return Mathf.Lerp(Mathf.Lerp(y00, y10, fx), Mathf.Lerp(y01, y11, fx), fz);
    }

    private double[] GenerateHills()
    {
        int w = GridRes + 1;
        var grid = new double[w * w];
        var rng = new Random(42);
        for (int z = 0; z < w; z++)
            for (int x = 0; x < w; x++)
            {
                double nx = (double)x / GridRes, nz = (double)z / GridRes;
                double h = Math.Sin(nx * Math.PI * 1.5) * Math.Cos(nz * Math.PI) * MaxAltitude * 0.5
                         + Math.Sin(nx * Math.PI * 3 + 1) * Math.Sin(nz * Math.PI * 2.5) * MaxAltitude * 0.2
                         + MaxAltitude * 0.25 + rng.NextDouble() * 0.15;
                grid[z * w + x] = Math.Max(0, h);
            }
        return grid;
    }

    private ArrayMesh BuildTerrainMesh()
    {
        // Indexed mesh: shared vertices → smooth normals across triangles
        int w = GridRes + 1;
        float half = WorldSize / 2, cell = WorldSize / GridRes;

        var vertices = new Vector3[w * w];
        var colors = new Color[w * w];

        for (int z = 0; z < w; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                float y = (float)_altGrid[i];
                vertices[i] = new Vector3(-half + x * cell, y, -half + z * cell);
                colors[i] = HColor(y);
            }

        // Build index buffer: 2 triangles per grid cell
        var indices = new int[GridRes * GridRes * 6];
        int idx = 0;
        for (int z = 0; z < GridRes; z++)
            for (int x = 0; x < GridRes; x++)
            {
                int tl = z * w + x;
                int tr = tl + 1;
                int bl = (z + 1) * w + x;
                int br = bl + 1;
                indices[idx++] = tl; indices[idx++] = tr; indices[idx++] = bl;
                indices[idx++] = tr; indices[idx++] = br; indices[idx++] = bl;
            }

        // Compute smooth normals by averaging face normals per vertex
        var normals = new Vector3[w * w];
        for (int i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];
            var faceNormal = (b - a).Cross(c - a).Normalized();
            normals[indices[i]] += faceNormal;
            normals[indices[i + 1]] += faceNormal;
            normals[indices[i + 2]] += faceNormal;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].Normalized();

        // Build ArrayMesh
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _terrainMat = BuildTerrainShader();
        mesh.SurfaceSetMaterial(0, _terrainMat);
        GD.Print($"Terrain: shader applied, material type={_terrainMat.GetType().Name}, has shader={_terrainMat.Shader != null}");
        return mesh;
    }

    private ShaderMaterial BuildTerrainShader()
    {
        var shader = new Shader();
        shader.Code = @"
shader_type spatial;
render_mode world_vertex_coords;

uniform float slope_threshold : hint_range(0.0, 1.0) = 0.4;
uniform float noise_scale : hint_range(1.0, 100.0) = 25.0;
uniform float altitude_max : hint_range(1.0, 50.0) = 6.0;

// Grass palette — lush greens
const vec3 GRASS_DARK = vec3(0.1, 0.22, 0.05);
const vec3 GRASS_MID = vec3(0.18, 0.38, 0.1);
const vec3 GRASS_LIGHT = vec3(0.3, 0.5, 0.15);
const vec3 MOSS = vec3(0.1, 0.22, 0.06);
const vec3 DIRT = vec3(0.25, 0.18, 0.1);
const vec3 ROCK = vec3(0.32, 0.3, 0.26);
const vec3 ROCK_DARK = vec3(0.2, 0.18, 0.16);

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
        mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x),
        f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 5; i++) {
        v += noise(p) * amp;
        p *= 2.1;
        amp *= 0.45;
    }
    return v;
}

void fragment() {
    float slope = 1.0 - dot(NORMAL, vec3(0.0, 1.0, 0.0));
    slope = clamp(slope * 2.5, 0.0, 1.0);
    float alt = clamp(VERTEX.y / altitude_max, 0.0, 1.0);

    // Multi-scale noise
    vec2 uv = VERTEX.xz;
    float n_large = fbm(uv / noise_scale);           // Broad patches
    float n_med = fbm(uv / (noise_scale * 0.3));     // Medium detail
    float n_fine = fbm(uv / (noise_scale * 0.08));   // Fine grain (grass clumps)

    // Grass base: blend 3 greens by altitude + large noise
    vec3 grass = mix(GRASS_DARK, GRASS_MID, smoothstep(0.0, 0.5, alt + n_large * 0.3));
    grass = mix(grass, GRASS_LIGHT, smoothstep(0.4, 0.8, alt + n_large * 0.2));

    // Moss patches in low, damp areas
    float moss_mask = smoothstep(0.3, 0.5, n_med) * smoothstep(0.4, 0.0, alt);
    grass = mix(grass, MOSS, moss_mask * 0.6);

    // Dirt patches — exposed earth
    float dirt_mask = smoothstep(0.6, 0.75, fbm(uv / (noise_scale * 0.5) + 7.0));
    grass = mix(grass, DIRT, dirt_mask * 0.5);

    // Fine grain variation — subtle per-blade color shift
    grass += (n_fine - 0.5) * 0.06;

    // Rock on slopes
    vec3 rock = mix(ROCK, ROCK_DARK, n_med);
    float slope_mask = smoothstep(slope_threshold - 0.08, slope_threshold + 0.15, slope);
    vec3 col = mix(grass, rock, slope_mask);

    // Slight darkening in crevices (poor man's AO via slope)
    col *= mix(1.0, 0.85, slope * 0.5);

    ALBEDO = col;
    ROUGHNESS = mix(0.8, 0.95, slope_mask + n_fine * 0.1);
    SPECULAR = 0.15;
}
";
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("slope_threshold", _slopeThreshold);
        mat.SetShaderParameter("noise_scale", _noiseScale);
        mat.SetShaderParameter("altitude_max", MaxAltitude);
        return mat;
    }

    private static Color HColor(float y)
    {
        // Vertex colors as fallback / data channel — shader overrides visual
        float n = Mathf.Clamp(y / 5f, 0f, 1f);
        return new Color(
            Mathf.Lerp(0.12f, 0.28f, n),
            Mathf.Lerp(0.28f, 0.38f, n),
            Mathf.Lerp(0.08f, 0.12f, n));
    }

    // ——— Entity builders ———

    private void BuildPlayer()
    {
        _player = new MeshInstance3D();
        var sphere = new SphereMesh { Radius = PlayerRadius, Height = PlayerRadius * 2 };
        _player.Mesh = sphere;
        _player.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.85f, 0.25f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.3f, 0.05f),
            EmissionEnergyMultiplier = 0.2f,
        };

        _axe = new Node3D();
        var handle = new MeshInstance3D();
        handle.Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f, Height = 0.5f };
        handle.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.28f, 0.12f) };
        handle.RotationDegrees = new Vector3(0, 0, 90);
        _axe.AddChild(handle);
        var blade = new MeshInstance3D();
        blade.Mesh = new BoxMesh { Size = new Vector3(0.03f, 0.15f, 0.2f) };
        blade.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.7f, 0.7f), Metallic = 0.8f };
        blade.Position = new Vector3(0.25f, 0, 0);
        _axe.AddChild(blade);
        _axe.Position = new Vector3(0, 0, -PlayerRadius - 0.1f);
        _player.AddChild(_axe);

        AddChild(_player);
    }

    private void SpawnTree(Vector3 pos, float scale, int age, bool fireScars)
    {
        float ty = GetTerrainY(pos.X, pos.Z);
        var root = new Node3D();
        root.Position = new Vector3(pos.X, ty, pos.Z);
        root.Scale = Vector3.One * scale;

        float trunkH = Mathf.Lerp(2f, 5f, age / 200f);
        float trunkR = Mathf.Lerp(0.08f, 0.2f, age / 200f);
        float canopyR = Mathf.Lerp(0.5f, 1.5f, age / 200f);

        // Trunk
        var trunk = new MeshInstance3D();
        trunk.Mesh = new CylinderMesh { TopRadius = trunkR * 0.7f, BottomRadius = trunkR, Height = trunkH };
        var trunkColor = fireScars ? new Color(0.25f, 0.16f, 0.08f) : new Color(0.4f, 0.26f, 0.13f);
        trunk.MaterialOverride = new StandardMaterial3D { AlbedoColor = trunkColor };
        trunk.Position = new Vector3(0, trunkH / 2, 0);
        root.AddChild(trunk);

        // Canopy spheres (3 overlapping for volume)
        var canopyColor = fireScars
            ? new Color(0.2f, 0.4f, 0.12f)
            : new Color(0.15f + age * 0.0003f, 0.5f + age * 0.0005f, 0.12f);
        var rng = new Random(age * 1000 + (int)(pos.X * 100));

        for (int i = 0; i < 3; i++)
        {
            var c = new MeshInstance3D();
            float r = canopyR * Mathf.Lerp(0.6f, 1.0f, (float)rng.NextDouble());
            c.Mesh = new SphereMesh { Radius = r, Height = r * 2 };
            var cColor = canopyColor;
            cColor.G += (float)(rng.NextDouble() - 0.5) * 0.08f;
            c.MaterialOverride = new StandardMaterial3D { AlbedoColor = cColor };
            c.Position = new Vector3(
                (float)(rng.NextDouble() - 0.5) * canopyR * 0.6f,
                trunkH + r * 0.5f + (float)rng.NextDouble() * canopyR * 0.3f,
                (float)(rng.NextDouble() - 0.5) * canopyR * 0.6f);
            root.AddChild(c);
        }

        _treeCluster.AddChild(root);
        _trees.Add(new TreeInfo { Node = root, Age = age, FireScars = fireScars, Scale = scale });
    }

    private void SpawnStump(Vector3 pos)
    {
        float ty = GetTerrainY(pos.X, pos.Z);
        var stump = new MeshInstance3D();
        stump.Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.18f, Height = 0.3f };
        stump.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.22f, 0.1f) };
        stump.Position = new Vector3(pos.X, ty + 0.15f, pos.Z);
        AddChild(stump);
    }

    private void SpawnWoodpile(Vector3 pos)
    {
        float ty = GetTerrainY(pos.X, pos.Z);
        var pile = new Node3D();
        pile.Position = new Vector3(pos.X, ty, pos.Z);
        var rng = new Random(99);
        for (int i = 0; i < 8; i++)
        {
            var log = new MeshInstance3D();
            log.Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.07f, Height = 0.8f };
            log.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.26f, 0.12f) };
            log.RotationDegrees = new Vector3(0, 0, 90);
            log.Position = new Vector3(
                (float)(rng.NextDouble() - 0.5) * 0.3f,
                0.07f + (i / 4) * 0.14f,
                (float)(rng.NextDouble() - 0.5) * 0.2f);
            pile.AddChild(log);
        }
        AddChild(pile);
    }

    private void SpawnCampfire(Vector3 pos)
    {
        float ty = GetTerrainY(pos.X, pos.Z);

        // Stone ring
        for (int i = 0; i < 8; i++)
        {
            var stone = new MeshInstance3D();
            stone.Mesh = new SphereMesh { Radius = 0.08f, Height = 0.12f };
            stone.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.38f, 0.35f) };
            float angle = i * Mathf.Tau / 8;
            stone.Position = new Vector3(pos.X + Mathf.Cos(angle) * 0.3f, ty + 0.04f, pos.Z + Mathf.Sin(angle) * 0.3f);
            AddChild(stone);
        }

        // Smoke particles
        _smoke = new GpuParticles3D();
        _smoke.Amount = 30;
        _smoke.Lifetime = 3f;
        _smoke.Position = new Vector3(pos.X, ty + 0.2f, pos.Z);

        var mat = new ParticleProcessMaterial();
        mat.Direction = new Vector3(0, 1, 0);
        mat.Spread = 15f;
        mat.InitialVelocityMin = 0.3f;
        mat.InitialVelocityMax = 0.8f;
        mat.Gravity = new Vector3(0, 0.1f, 0); // Slight updraft
        mat.DampingMin = 0.3f;
        mat.DampingMax = 0.7f;
        mat.ScaleMin = 0.1f;
        mat.ScaleMax = 0.4f;
        mat.Color = new Color(0.6f, 0.6f, 0.6f, 0.3f);
        _smoke.ProcessMaterial = mat;

        // Smoke mesh (simple quad)
        var smokeMesh = new QuadMesh { Size = new Vector2(1f, 1f) };
        var smokeMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.7f, 0.7f, 0.7f, 0.15f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        smokeMesh.Material = smokeMat;
        _smoke.DrawPass1 = smokeMesh;

        _smoke.Emitting = true;
        AddChild(_smoke);
    }
}

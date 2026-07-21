using Godot;
using System;

namespace CommunitySurvival.Lab;

/// <summary>
/// World generation lab. Interactive terrain with hydraulic erosion,
/// rivers, biomes. All local — no server.
///
/// Controls: WASD move, RMB orbit, scroll zoom, Tab tuning panel
/// </summary>
public partial class WorldGenLab : Node3D
{
    private const int GridSize = 512;
    private const float WorldScale = 500f; // 500x500 units
    private const float HeightScale = 80f; // Tall mountains

    // Terrain data
    private float[] _heightmap;
    private float[] _moisture;
    private float[] _flow; // flow accumulation
    private int[] _flowDir;
    private int _erosionIterations;
    private int _seed = 42;

    // Noise params
    private int _octaves = 6;
    private float _frequency = 2.5f;
    private float _seaLevel = 0.3f;

    // Erosion params
    private float _erosionRate = 0.3f;
    private float _depositionRate = 0.3f;
    private float _evaporation = 0.01f;
    private float _inertia = 0.05f;
    private float _sedimentCapacity = 4f;
    private int _dropletLifetime = 50;

    // Climate
    private float _windAngle = 45f; // degrees
    private float _windStrength = 1f;

    // Slider references (for preset updates)
    private TuningSlider _sliderEroRate, _sliderDeposition, _sliderEvaporation;
    private TuningSlider _sliderInertia, _sliderCapacity, _sliderLife;
    private int _presetErosionIters; // iterations to run when preset applied

    // Display
    private int _vizMode; // 0=shaded, 1=height, 2=moisture, 3=biome, 4=erosion
    private float _verticalExag = 1f;
    private float _riverThreshold = 80f;

    // Nodes
    private MeshInstance3D _terrainMesh;
    private MeshInstance3D _waterPlane;
    private MeshInstance3D _playerOrb;
    private Node3D _cameraPivot;
    private Camera3D _camera;
    private Label _hud;
    private TuningPanel _panel;
    private float[] _originalHeight; // for erosion delta viz

    // Camera
    private float _camYaw, _camPitch = -45f, _camDist = 250f;
    private bool _rmbHeld, _lmbHeld;
    private Vector3 _playerPos;

    public override void _Ready()
    {
        // Generate initial terrain
        _heightmap = new float[GridSize * GridSize];
        _originalHeight = new float[GridSize * GridSize];
        _moisture = new float[GridSize * GridSize];
        _flow = new float[GridSize * GridSize];
        _flowDir = new int[GridSize * GridSize];

        GenerateNoise();
        Array.Copy(_heightmap, _originalHeight, _heightmap.Length);

        // Terrain mesh
        _terrainMesh = new MeshInstance3D();
        AddChild(_terrainMesh);
        RebuildMesh();

        // Water plane
        _waterPlane = new MeshInstance3D();
        var waterMesh = new PlaneMesh { Size = new Vector2(WorldScale, WorldScale) };
        _waterPlane.Mesh = waterMesh;
        _waterPlane.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.3f, 0.5f, 0.6f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.1f,
            Metallic = 0.3f,
        };
        _waterPlane.Position = new Vector3(0, _seaLevel * HeightScale, 0);
        AddChild(_waterPlane);

        // Player orb
        _playerOrb = new MeshInstance3D();
        _playerOrb.Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f };
        _playerOrb.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.1f, 0.9f, 0.2f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.3f, 0.05f),
        };
        AddChild(_playerOrb);
        _playerPos = new Vector3(0, HeightScale * 0.6f, 0);
        _playerOrb.Position = _playerPos;

        // Sun
        var sun = new DirectionalLight3D();
        sun.RotationDegrees = new Vector3(-45, 30, 0);
        sun.LightEnergy = 1.3f;
        sun.ShadowEnabled = true;
        sun.LightColor = new Color(1, 0.95f, 0.9f);
        AddChild(sun);
        var fill = new DirectionalLight3D();
        fill.RotationDegrees = new Vector3(-30, -150, 0);
        fill.LightEnergy = 0.25f;
        fill.ShadowEnabled = false;
        AddChild(fill);

        // Environment
        var env = new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.55f, 0.7f, 0.88f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.45f, 0.5f, 0.55f);
        env.AmbientLightEnergy = 0.5f;
        env.VolumetricFogEnabled = true;
        env.VolumetricFogDensity = 0.001f;
        env.VolumetricFogAlbedo = new Color(0.8f, 0.85f, 0.9f);
        env.VolumetricFogLength = 400f;
        env.TonemapMode = Godot.Environment.ToneMapper.Aces;
        AddChild(new WorldEnvironment { Environment = env });

        // Camera
        _cameraPivot = new Node3D();
        AddChild(_cameraPivot);
        _camera = new Camera3D { Fov = 55, Current = true, Far = 1500f };
        _cameraPivot.AddChild(_camera);
        UpdateCamera();

        // HUD
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label();
        _hud.AnchorLeft = 1; _hud.AnchorRight = 1;
        _hud.OffsetLeft = -300; _hud.OffsetTop = 10; _hud.OffsetRight = -10;
        _hud.HorizontalAlignment = HorizontalAlignment.Right;
        _hud.AddThemeFontSizeOverride("font_size", 13);
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _hud.AddThemeConstantOverride("shadow_offset_x", 1);
        _hud.AddThemeConstantOverride("shadow_offset_y", 1);
        canvas.AddChild(_hud);

        // Tuning panel
        _panel = new TuningPanel();
        AddChild(_panel);
        BuildPanel();

        // Parameter sweep tool (press P to run 500 randomized generation passes)
        AddChild(new ParameterSweep());

        GD.Print("WorldGenLab: ready. Tab=tuning, WASD=move, RMB=orbit, E=erode, R=regen, P=sweep");
    }

    private void BuildPanel()
    {
        var gen = _panel.AddSection("Generation");
        gen.AddSlider("Seed", 1, 999, _seed, v => { _seed = (int)v; Regenerate(); });
        gen.AddSlider("Octaves", 1, 8, _octaves, v => { _octaves = (int)v; Regenerate(); });
        gen.AddSlider("Frequency", 0.5f, 8f, _frequency, v => { _frequency = v; Regenerate(); });
        gen.AddSlider("Sea Level", 0f, 0.8f, _seaLevel, v =>
        {
            _seaLevel = v;
            _waterPlane.Position = new Vector3(0, _seaLevel * HeightScale * _verticalExag, 0);
            RebuildMesh();
        });

        var presets = _panel.AddSection("Biome Presets");
        presets.AddButton("Alpine", () => ApplyPreset(0.04f, 0.79f, 0.0014f, 0.04f, 2.4f, 37, 150000));
        presets.AddButton("Rainforest", () => ApplyPreset(0.18f, 0.10f, 0.040f, 0.32f, 4.7f, 41, 500000));
        presets.AddButton("Desert", () => ApplyPreset(0.04f, 0.69f, 0.033f, 0.05f, 1.1f, 50, 150000));
        presets.AddButton("Rolling Hills", () => ApplyPreset(0.27f, 0.68f, 0.011f, 0.31f, 7.8f, 75, 300000));
        presets.AddButton("Wetlands", () => ApplyPreset(0.22f, 0.23f, 0.040f, 0.14f, 2.7f, 57, 500000));

        var ero = _panel.AddSection("Erosion");
        _sliderEroRate = ero.AddSlider("Erosion Rate", 0f, 1f, _erosionRate, v => _erosionRate = v);
        _sliderDeposition = ero.AddSlider("Deposition", 0f, 1f, _depositionRate, v => _depositionRate = v);
        _sliderEvaporation = ero.AddSlider("Evaporation", 0f, 0.1f, _evaporation, v => _evaporation = v);
        _sliderInertia = ero.AddSlider("Inertia", 0f, 0.5f, _inertia, v => _inertia = v);
        _sliderCapacity = ero.AddSlider("Capacity", 0.5f, 10f, _sedimentCapacity, v => _sedimentCapacity = v);
        _sliderLife = ero.AddSlider("Droplet Life", 10, 100, _dropletLifetime, v => _dropletLifetime = (int)v);

        var climate = _panel.AddSection("Climate");
        climate.AddSlider("Wind Angle", 0, 360, _windAngle, v => { _windAngle = v; ComputeMoisture(); RebuildMesh(); });
        climate.AddSlider("Wind Strength", 0, 3, _windStrength, v => { _windStrength = v; ComputeMoisture(); RebuildMesh(); });
        climate.AddSlider("River Threshold", 10, 500, _riverThreshold, v => { _riverThreshold = v; RebuildMesh(); });

        var disp = _panel.AddSection("Display");
        disp.AddSlider("Mode (0-4)", 0, 4, 0, v => { _vizMode = (int)v; RebuildMesh(); });
        disp.AddSlider("Vert Exag", 0.2f, 3f, _verticalExag, v =>
        {
            _verticalExag = v;
            _waterPlane.Position = new Vector3(0, _seaLevel * HeightScale * _verticalExag, 0);
            RebuildMesh();
        });
    }

    private void ApplyPreset(float eroRate, float deposit, float evap, float inertia, float cap, int life, int iters)
    {
        _erosionRate = eroRate;
        _depositionRate = deposit;
        _evaporation = evap;
        _inertia = inertia;
        _sedimentCapacity = cap;
        _dropletLifetime = life;
        _presetErosionIters = iters;

        // Update sliders to reflect new values
        _sliderEroRate?.SetValue(eroRate);
        _sliderDeposition?.SetValue(deposit);
        _sliderEvaporation?.SetValue(evap);
        _sliderInertia?.SetValue(inertia);
        _sliderCapacity?.SetValue(cap);
        _sliderLife?.SetValue(life);

        // Regenerate and erode with preset
        GenerateNoise();
        Array.Copy(_heightmap, _originalHeight, _heightmap.Length);
        _erosionIterations = 0;
        RunErosion(iters);
        GD.Print($"Preset applied: ero={eroRate} dep={deposit} evap={evap} inertia={inertia} cap={cap} life={life} iters={iters}");
    }

    // ——— Generation ———

    private void Regenerate()
    {
        GenerateNoise();
        Array.Copy(_heightmap, _originalHeight, _heightmap.Length);
        _erosionIterations = 0;
        ComputeFlow();
        ComputeMoisture();
        RebuildMesh();
    }

    private void GenerateNoise()
    {
        var rng = new Random(_seed);
        // Offsets for each octave
        var offsets = new Vector2[_octaves];
        for (int i = 0; i < _octaves; i++)
            offsets[i] = new Vector2((float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 1000));

        for (int z = 0; z < GridSize; z++)
            for (int x = 0; x < GridSize; x++)
            {
                float nx = (float)x / GridSize;
                float nz = (float)z / GridSize;

                float h = 0, amp = 1, freq = _frequency;
                float totalAmp = 0;
                for (int o = 0; o < _octaves; o++)
                {
                    float sx = nx * freq + offsets[o].X;
                    float sz = nz * freq + offsets[o].Y;
                    // Simplex-like noise from sin combinations
                    float n = (float)(Math.Sin(sx * 6.28 + Math.Cos(sz * 4.17)) *
                                     Math.Cos(sz * 6.28 + Math.Sin(sx * 3.71)) * 0.5 + 0.5);
                    h += n * amp;
                    totalAmp += amp;
                    amp *= 0.5f;
                    freq *= 2.1f;
                }
                h /= totalAmp;

                // Island falloff — edges are lower
                float dx = nx - 0.5f, dz = nz - 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dz * dz) * 2f;
                float falloff = Mathf.Clamp(1f - dist * dist, 0f, 1f);
                h *= falloff;

                _heightmap[z * GridSize + x] = h;
            }
    }

    // ——— Hydraulic Erosion ———

    public void RunErosion(int iterations)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(_erosionIterations + _seed * 1000);
        for (int i = 0; i < iterations; i++)
        {
            SimulateDroplet(rng);
            if (i > 0 && i % 100000 == 0)
                GD.Print($"  Erosion: {i / 1000}K / {iterations / 1000}K ...");
        }
        _erosionIterations += iterations;
        sw.Stop();
        GD.Print($"Erosion: {_erosionIterations / 1000}K total in {sw.ElapsedMilliseconds}ms");

        ComputeFlow();
        ComputeMoisture();
        RebuildMesh();
    }

    private void SimulateDroplet(Random rng)
    {
        float posX = (float)(rng.NextDouble() * (GridSize - 2) + 1);
        float posZ = (float)(rng.NextDouble() * (GridSize - 2) + 1);
        float dirX = 0, dirZ = 0;
        float speed = 1;
        float water = 1;
        float sediment = 0;

        for (int step = 0; step < _dropletLifetime; step++)
        {
            int ix = (int)posX, iz = (int)posZ;
            if (ix < 1 || ix >= GridSize - 2 || iz < 1 || iz >= GridSize - 2) break;

            float fx = posX - ix, fz = posZ - iz;

            // Bilinear height and gradient
            float h00 = _heightmap[iz * GridSize + ix];
            float h10 = _heightmap[iz * GridSize + ix + 1];
            float h01 = _heightmap[(iz + 1) * GridSize + ix];
            float h11 = _heightmap[(iz + 1) * GridSize + ix + 1];

            float gradX = (h10 - h00) * (1 - fz) + (h11 - h01) * fz;
            float gradZ = (h01 - h00) * (1 - fx) + (h11 - h10) * fx;
            float height = h00 * (1 - fx) * (1 - fz) + h10 * fx * (1 - fz) + h01 * (1 - fx) * fz + h11 * fx * fz;

            // Update direction with inertia
            dirX = dirX * _inertia - gradX * (1 - _inertia);
            dirZ = dirZ * _inertia - gradZ * (1 - _inertia);
            float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
            if (len > 0.0001f) { dirX /= len; dirZ /= len; }
            else break;

            float newX = posX + dirX;
            float newZ = posZ + dirZ;

            int nix = (int)newX, niz = (int)newZ;
            if (nix < 0 || nix >= GridSize - 1 || niz < 0 || niz >= GridSize - 1) break;

            float nfx = newX - nix, nfz = newZ - niz;
            float newHeight = _heightmap[niz * GridSize + nix] * (1 - nfx) * (1 - nfz)
                            + _heightmap[niz * GridSize + nix + 1] * nfx * (1 - nfz)
                            + _heightmap[(niz + 1) * GridSize + nix] * (1 - nfx) * nfz
                            + _heightmap[(niz + 1) * GridSize + nix + 1] * nfx * nfz;

            float heightDiff = newHeight - height;

            // Sediment capacity
            float capacity = Mathf.Max(-heightDiff * speed * water * _sedimentCapacity, 0.01f);

            if (sediment > capacity || heightDiff > 0)
            {
                // Deposit
                float deposit = (heightDiff > 0)
                    ? Mathf.Min(heightDiff, sediment)
                    : (sediment - capacity) * _depositionRate;
                sediment -= deposit;
                _heightmap[iz * GridSize + ix] += deposit * (1 - fx) * (1 - fz);
                _heightmap[iz * GridSize + ix + 1] += deposit * fx * (1 - fz);
                _heightmap[(iz + 1) * GridSize + ix] += deposit * (1 - fx) * fz;
                _heightmap[(iz + 1) * GridSize + ix + 1] += deposit * fx * fz;
            }
            else
            {
                // Erode
                float erode = Mathf.Min((capacity - sediment) * _erosionRate, -heightDiff);
                sediment += erode;
                // Erode in a small radius
                _heightmap[iz * GridSize + ix] -= erode * (1 - fx) * (1 - fz);
                _heightmap[iz * GridSize + ix + 1] -= erode * fx * (1 - fz);
                _heightmap[(iz + 1) * GridSize + ix] -= erode * (1 - fx) * fz;
                _heightmap[(iz + 1) * GridSize + ix + 1] -= erode * fx * fz;
            }

            speed = Mathf.Sqrt(Mathf.Max(speed * speed - heightDiff, 0.01f));
            water *= (1 - _evaporation);
            posX = newX;
            posZ = newZ;
        }
    }

    // ——— Flow & Moisture ———

    private void ComputeFlow()
    {
        // D8 flow direction + accumulation
        Array.Clear(_flow, 0, _flow.Length);
        Array.Fill(_flowDir, -1);

        // Flow direction: steepest descent to neighbor
        int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dz = { -1, -1, -1, 0, 0, 1, 1, 1 };

        for (int z = 1; z < GridSize - 1; z++)
            for (int x = 1; x < GridSize - 1; x++)
            {
                int idx = z * GridSize + x;
                float h = _heightmap[idx];
                float steepest = 0;
                int bestDir = -1;
                for (int d = 0; d < 8; d++)
                {
                    int ni = (z + dz[d]) * GridSize + (x + dx[d]);
                    float drop = h - _heightmap[ni];
                    if (drop > steepest) { steepest = drop; bestDir = d; }
                }
                _flowDir[idx] = bestDir;
            }

        // Accumulate: process cells from highest to lowest
        var order = new int[GridSize * GridSize];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _heightmap[b].CompareTo(_heightmap[a]));

        for (int i = 0; i < order.Length; i++)
        {
            int idx = order[i];
            _flow[idx] += 1; // each cell contributes 1
            int dir = _flowDir[idx];
            if (dir < 0) continue;
            int x = idx % GridSize, z = idx / GridSize;
            int nx = x + dx[dir], nz = z + dz[dir];
            if (nx >= 0 && nx < GridSize && nz >= 0 && nz < GridSize)
                _flow[nz * GridSize + nx] += _flow[idx];
        }
    }

    private void ComputeMoisture()
    {
        // Simplified orographic rainfall
        float windRad = Mathf.DegToRad(_windAngle);
        float wx = Mathf.Cos(windRad), wz = Mathf.Sin(windRad);

        for (int z = 0; z < GridSize; z++)
            for (int x = 0; x < GridSize; x++)
            {
                int idx = z * GridSize + x;
                float h = _heightmap[idx];

                // Base moisture: higher near sea level, lower at altitude
                float baseMoisture = h < _seaLevel ? 1f : Mathf.Clamp(1f - (h - _seaLevel) * 2f, 0.1f, 1f);

                // Windward side gets more rain (dot product of slope with wind direction)
                float slopeX = 0, slopeZ = 0;
                if (x > 0 && x < GridSize - 1)
                    slopeX = _heightmap[idx + 1] - _heightmap[idx - 1];
                if (z > 0 && z < GridSize - 1)
                    slopeZ = _heightmap[idx + GridSize] - _heightmap[idx - GridSize];

                float windward = (slopeX * wx + slopeZ * wz) * _windStrength;
                float m = baseMoisture + Mathf.Clamp(windward * 3f, -0.5f, 0.5f);

                _moisture[idx] = Mathf.Clamp(m, 0f, 1f);
            }
    }

    // ——— Mesh Building ———

    private void RebuildMesh()
    {
        int w = GridSize;
        float cellSize = WorldScale / (w - 1);
        float half = WorldScale / 2;

        var vertices = new Vector3[w * w];
        var colors = new Color[w * w];
        var normals = new Vector3[w * w];

        for (int z = 0; z < w; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                float h = _heightmap[i];
                float y = h * HeightScale * _verticalExag;
                vertices[i] = new Vector3(-half + x * cellSize, y, -half + z * cellSize);
                colors[i] = GetColor(i, h);
            }

        var indices = new int[(w - 1) * (w - 1) * 6];
        int idx = 0;
        for (int z = 0; z < w - 1; z++)
            for (int x = 0; x < w - 1; x++)
            {
                int tl = z * w + x, tr = tl + 1;
                int bl = (z + 1) * w + x, br = bl + 1;
                indices[idx++] = tl; indices[idx++] = tr; indices[idx++] = bl;
                indices[idx++] = tr; indices[idx++] = br; indices[idx++] = bl;
            }

        // Smooth normals
        for (int i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];
            var fn = (b - a).Cross(c - a).Normalized();
            normals[indices[i]] += fn;
            normals[indices[i + 1]] += fn;
            normals[indices[i + 2]] += fn;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].Normalized();

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.85f,
        });
        _terrainMesh.Mesh = mesh;
    }

    private Color GetColor(int idx, float h)
    {
        bool underwater = h < _seaLevel;

        switch (_vizMode)
        {
            case 1: // Height — grayscale
                return new Color(h, h, h);

            case 2: // Moisture
                float m = _moisture[idx];
                return new Color(0.2f * (1 - m), 0.2f * (1 - m), 0.3f + m * 0.7f);

            case 3: // Biome
                return GetBiomeColor(idx, h);

            case 4: // Erosion delta
                float delta = _heightmap[idx] - _originalHeight[idx];
                if (delta > 0.001f) return new Color(0.2f, 0.5f, 0.8f); // deposition = blue
                if (delta < -0.001f) return new Color(0.8f, 0.2f, 0.1f); // erosion = red
                return new Color(0.4f, 0.4f, 0.4f);

            default: // 0 = Shaded terrain
                if (underwater) return new Color(0.1f, 0.15f, 0.25f);

                float alt = Mathf.Clamp((h - _seaLevel) / (1f - _seaLevel), 0f, 1f);
                float moist = _moisture[idx];

                // River overlay
                if (_flow[idx] > _riverThreshold)
                    return new Color(0.15f, 0.3f, 0.55f);

                // Grass → rock → snow by altitude
                Color grass = new Color(
                    Mathf.Lerp(0.12f, 0.25f, alt),
                    Mathf.Lerp(0.3f + moist * 0.1f, 0.4f, alt),
                    Mathf.Lerp(0.08f, 0.12f, alt));
                Color rock = new Color(0.35f, 0.32f, 0.28f);
                Color snow = new Color(0.9f, 0.92f, 0.95f);

                Color col = grass;
                if (alt > 0.6f) col = grass.Lerp(rock, (alt - 0.6f) / 0.2f);
                if (alt > 0.8f) col = rock.Lerp(snow, (alt - 0.8f) / 0.2f);
                return col;
        }
    }

    private Color GetBiomeColor(int idx, float h)
    {
        if (h < _seaLevel) return new Color(0.1f, 0.2f, 0.4f); // ocean

        float alt = Mathf.Clamp((h - _seaLevel) / (1f - _seaLevel), 0f, 1f);
        float moist = _moisture[idx];

        // Temperature decreases with altitude
        float temp = 1f - alt;

        if (temp > 0.7f && moist > 0.6f) return new Color(0.05f, 0.35f, 0.05f); // tropical forest
        if (temp > 0.7f && moist < 0.3f) return new Color(0.7f, 0.6f, 0.3f); // desert
        if (temp > 0.4f && moist > 0.5f) return new Color(0.1f, 0.4f, 0.1f); // temperate forest
        if (temp > 0.4f && moist < 0.3f) return new Color(0.5f, 0.5f, 0.2f); // grassland
        if (temp > 0.2f && moist > 0.4f) return new Color(0.15f, 0.3f, 0.15f); // boreal forest
        if (temp > 0.2f) return new Color(0.4f, 0.4f, 0.35f); // tundra
        return new Color(0.85f, 0.88f, 0.92f); // snow/ice
    }

    // ——— Input & Update ———

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left) _lmbHeld = mb.Pressed;
            if (mb.ButtonIndex == MouseButton.Right) _rmbHeld = mb.Pressed;
            Input.MouseMode = _rmbHeld ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            if (mb.ButtonIndex == MouseButton.WheelUp) _camDist = Mathf.Max(5f, _camDist - _camDist * 0.1f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _camDist = Mathf.Min(800f, _camDist + _camDist * 0.1f);
            UpdateCamera();
        }
        else if (ev is InputEventMouseMotion mm && _rmbHeld)
        {
            _camYaw -= mm.Relative.X * 0.3f;
            _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * 0.3f, -89f, 89f);
            UpdateCamera();
        }

        // E key = run erosion batch
        if (ev is InputEventKey k && k.Pressed)
        {
            if (k.Keycode == Key.R) { Regenerate(); GD.Print("Regenerated"); }
            if (k.Keycode == Key.E) RunErosion(10000);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        float yawRad = Mathf.DegToRad(_camYaw);
        var camFwd = new Vector3(-Mathf.Sin(yawRad), 0, -Mathf.Cos(yawRad));
        var camRight = new Vector3(camFwd.Z, 0, -camFwd.X);
        float speed = 40f + _camDist * 0.3f; // Faster when zoomed out

        var move = Vector3.Zero;
        bool bothMouse = _lmbHeld && _rmbHeld;
        if (bothMouse) move += camFwd;
        else
        {
            if (Input.IsActionPressed("move_forward")) move += camFwd;
            if (Input.IsActionPressed("move_back")) move -= camFwd;
            if (Input.IsActionPressed("move_right")) move += camRight;
            if (Input.IsActionPressed("move_left")) move -= camRight;
        }
        if (move.LengthSquared() > 0.01f)
            _playerPos += move.Normalized() * speed * dt;
        if (Input.IsKeyPressed(Key.Space)) _playerPos.Y += speed * dt;
        if (Input.IsKeyPressed(Key.Ctrl)) _playerPos.Y -= speed * dt;

        _playerOrb.Position = _playerPos;
        _cameraPivot.Position = _playerPos;

        string vizNames = new[] { "Shaded", "Height", "Moisture", "Biome", "Erosion Δ" }[_vizMode];
        _hud.Text = $"Viz: {vizNames}\n" +
                    $"Erosion: {_erosionIterations / 1000}K iterations\n" +
                    $"[E] +10K erosion  [R] Regenerate";
    }

    private void UpdateCamera()
    {
        _cameraPivot.RotationDegrees = new Vector3(_camPitch, _camYaw, 0);
        _camera.Position = new Vector3(0, 0, _camDist);
    }
}

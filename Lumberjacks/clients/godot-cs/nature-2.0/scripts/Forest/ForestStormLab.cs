#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunitySurvival.Lab;
using Godot;

namespace CommunitySurvival.Forest;

/// <summary>
/// Offline forest/storm tuning sandbox. Continuous tree motion stays in the vertex shader;
/// the worker thread creates deterministic terrain and placement data only.
/// </summary>
public partial class ForestStormLab : Node3D
{
    private const int FeelCount = 2048;
    private const int DefaultCount = 8192;
    private const int StressCount = 32768;

    private ForestStormScenario _scenario = new();
    private ForestAssetLibrary? _assets;
    private Task<ForestGeneration>? _generationTask;
    private int _generationSerial;
    private int _activeGenerationSerial;
    private int _treeCount = DefaultCount;
    private Node3D? _terrainRoot;
    private Node3D? _forestRoot;
    private MeshInstance3D? _water;
    private Camera3D? _camera;
    private DirectionalLight3D? _sun;
    private Godot.Environment? _environment;
    private GpuParticles3D? _rain;
    private Label? _hud;
    private TuningPanel? _panel;
    private readonly List<MultiMeshInstance3D> _chunks = new();
    private readonly List<double> _frameTimes = new();
    private ForestGeneration? _generation;

    private float _stormElapsed;
    private float _runtime;
    private float _timeScale = 1f;
    private float _trunkFrequency = 0.34f;
    private float _detailFrequency = 2.7f;
    private float _trunkAmplitude = 1f;
    private float _detailAmplitude = 1f;
    private float _debugLayer;
    private float _cameraYaw;
    private float _cameraPitch = -0.16f;
    private bool _mouseLook;
    private bool _frozen;
    private bool _shadows = true;
    private bool _rainEnabled = true;
    private bool _visualReady;
    private bool _receiptWritten;
    private int _warmupSeconds = 10;
    private int _captureSeconds;
    private string _receiptPath = string.Empty;
    private string _screenshotPath = string.Empty;
    private bool _screenshotWritten;

    public override void _Ready()
    {
        var args = LabArguments.Parse(OS.GetCmdlineUserArgs());
        _treeCount = args.TreeCount ?? args.Preset switch
        {
            "feel" => FeelCount,
            "stress" => StressCount,
            _ => DefaultCount,
        };
        _warmupSeconds = args.WarmupSeconds;
        _captureSeconds = args.CaptureSeconds;
        _receiptPath = args.ReceiptPath;
        _screenshotPath = args.ScreenshotPath;
        _stormElapsed = args.StartSeconds;

        _scenario = string.IsNullOrWhiteSpace(args.ScenarioPath)
            ? ForestStormScenario.Parse(Godot.FileAccess.GetFileAsString(
                "res://assets/scenarios/forest-storm-default.json"))
            : ForestStormScenario.Load(ResolvePath(args.ScenarioPath));

        if (args.ValidateOnly)
        {
            Callable.From(RunValidation).CallDeferred();
            return;
        }

        _assets = new ForestAssetLibrary();
        BuildEnvironment();
        BuildCamera();
        BuildHudAndTuning();
        StartGeneration();
        GD.Print($"ForestStormLab: loading {_treeCount:N0} trees; Tab=tuning, RMB=look, WASDQE=fly, R=replay, F=freeze");
    }

    public override void _Process(double delta)
    {
        if (_generationTask is { IsCompleted: true }) CompleteGeneration();
        if (!_visualReady) return;

        var dt = (float)delta;
        _runtime += dt;
        if (!_frozen) _stormElapsed += dt * _timeScale;
        MoveCamera(dt);
        UpdateStorm();
        UpdateHud(delta);
        CaptureScreenshot();
        CaptureFrame(delta);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } button)
        {
            _mouseLook = button.Pressed;
            Input.MouseMode = _mouseLook ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventMouseMotion motion && _mouseLook)
        {
            _cameraYaw -= motion.Relative.X * 0.0024f;
            _cameraPitch = Mathf.Clamp(_cameraPitch - motion.Relative.Y * 0.0024f, -1.45f, 1.35f);
            UpdateCameraRotation();
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.R) Replay();
            if (key.Keycode == Key.F) _frozen = !_frozen;
        }
    }

    private void StartGeneration()
    {
        var serial = ++_generationSerial;
        _activeGenerationSerial = serial;
        _visualReady = false;
        _generationTask = Task.Run(() => ForestPlacementGenerator.Generate(_scenario, _treeCount));
        if (_hud is not null) _hud.Text = $"Growing {_treeCount:N0} deterministic trees on a worker thread…";
    }

    private void CompleteGeneration()
    {
        var task = _generationTask!;
        _generationTask = null;
        if (task.IsFaulted)
        {
            GD.PushError(task.Exception?.GetBaseException().Message ?? "Forest generation failed.");
            return;
        }
        if (_activeGenerationSerial != _generationSerial) return;

        _generation = task.Result;
        _terrainRoot?.QueueFree();
        _forestRoot?.QueueFree();
        _chunks.Clear();
        BuildTerrain(_generation);
        BuildForest(_generation);
        _visualReady = true;
        _runtime = 0f;
        _frameTimes.Clear();
        GD.Print($"ForestStormLab: ready trees={_generation.Placements.Count} chunks={_chunks.Count} placement_sha256={_generation.PlacementHash}");
    }

    private void BuildTerrain(ForestGeneration generation)
    {
        _terrainRoot = new Node3D { Name = "Terrain" };
        AddChild(_terrainRoot);
        var meshInstance = new MeshInstance3D { Mesh = CreateTerrainMesh(generation) };
        _terrainRoot.AddChild(meshInstance);

        var waterMesh = new PlaneMesh { Size = new Vector2(generation.WorldSize, generation.WorldSize) };
        _water = new MeshInstance3D
        {
            Mesh = waterMesh,
            Position = new Vector3(0f, generation.SeaLevel * generation.HeightScale + 0.06f, 0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.055f, 0.16f, 0.19f, 0.72f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Metallic = 0.15f,
                Roughness = 0.28f,
            },
        };
        _terrainRoot.AddChild(_water);
    }

    private static ArrayMesh CreateTerrainMesh(ForestGeneration generation)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var n = generation.GridSize;
        var step = generation.WorldSize / (n - 1);
        var half = generation.WorldSize * 0.5f;
        for (var z = 0; z < n - 1; z++)
        for (var x = 0; x < n - 1; x++)
        {
            var v00 = Vertex(x, z);
            var v10 = Vertex(x + 1, z);
            var v01 = Vertex(x, z + 1);
            var v11 = Vertex(x + 1, z + 1);
            Add(v00); Add(v01); Add(v10);
            Add(v10); Add(v01); Add(v11);
        }
        tool.GenerateNormals();
        var mesh = tool.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.94f,
        });
        return mesh;

        (Vector3 Position, Color Color) Vertex(int x, int z)
        {
            var index = z * n + x;
            var height = generation.Heightmap[index];
            var moisture = generation.Moisture[index];
            var position = new Vector3(-half + x * step, height * generation.HeightScale, -half + z * step);
            var low = new Color(0.075f, 0.17f, 0.09f);
            var high = new Color(0.22f, 0.28f, 0.15f);
            var wet = new Color(0.055f, 0.19f, 0.13f);
            var color = low.Lerp(high, Mathf.Clamp(height, 0f, 1f)).Lerp(wet, moisture * 0.42f);
            return (position, color);
        }

        void Add((Vector3 Position, Color Color) vertex)
        {
            tool.SetColor(vertex.Color);
            tool.AddVertex(vertex.Position);
        }
    }

    private void BuildForest(ForestGeneration generation)
    {
        if (_assets is null) throw new InvalidOperationException("Forest assets were not loaded.");
        _forestRoot = new Node3D { Name = "ForestChunks" };
        AddChild(_forestRoot);

        var groups = generation.Placements.GroupBy(p => (p.ChunkX, p.ChunkZ, p.Archetype));
        foreach (var group in groups)
        {
            var placements = group.ToArray();
            var asset = _assets[group.Key.Archetype];
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = false,
                UseCustomData = true,
                Mesh = asset.Mesh,
                InstanceCount = placements.Length,
                VisibleInstanceCount = placements.Length,
            };

            for (var index = 0; index < placements.Length; index++)
            {
                var placement = placements[index];
                var basis = new Basis(Vector3.Up, placement.Yaw).Scaled(Vector3.One * placement.Scale);
                multiMesh.SetInstanceTransform(index, new Transform3D(basis,
                    new Vector3(placement.X, placement.Y, placement.Z)));
                multiMesh.SetInstanceCustomData(index,
                    new Color(placement.Phase, placement.Stiffness, placement.CrownMass, placement.Moisture));
            }

            var instance = new MultiMeshInstance3D
            {
                Name = $"Chunk_{group.Key.ChunkX}_{group.Key.ChunkZ}_{group.Key.Archetype}",
                Multimesh = multiMesh,
                CastShadow = _shadows
                    ? GeometryInstance3D.ShadowCastingSetting.On
                    : GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = 300f,
            };
            _forestRoot.AddChild(instance);
            _chunks.Add(instance);
        }
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
            FogDensity = 0.006f,
            FogSkyAffect = 0.72f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
        };
        AddChild(new WorldEnvironment { Environment = _environment });

        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-38f, 28f, 0f),
            LightEnergy = 1.05f,
            LightColor = new Color(0.78f, 0.83f, 0.87f),
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 180f,
        };
        AddChild(_sun);
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-26f, -145f, 0f),
            LightEnergy = 0.18f,
            LightColor = new Color(0.38f, 0.49f, 0.58f),
        });

        var rainMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.64f, 0.75f, 0.82f, 0.46f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        _rain = new GpuParticles3D
        {
            Amount = 4800,
            Lifetime = 1.7,
            Emitting = true,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.025f, 1.4f), Material = rainMaterial },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(42f, 2f, 42f),
                Direction = Vector3.Down,
                Spread = 5f,
                InitialVelocityMin = 30f,
                InitialVelocityMax = 42f,
                Gravity = new Vector3(4.5f, -9.8f, 2.5f),
            },
        };
        AddChild(_rain);
    }

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Position = new Vector3(0f, 19f, 42f),
            Current = true,
            Fov = 62f,
            Far = 600f,
        };
        AddChild(_camera);
        UpdateCameraRotation();
    }

    private void BuildHudAndTuning()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label
        {
            AnchorLeft = 1f,
            AnchorRight = 1f,
            OffsetLeft = -470f,
            OffsetTop = 12f,
            OffsetRight = -14f,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _hud.AddThemeFontSizeOverride("font_size", 14);
        _hud.AddThemeColorOverride("font_color", new Color(0.91f, 0.94f, 0.88f));
        _hud.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.85f));
        _hud.AddThemeConstantOverride("shadow_offset_x", 2);
        _hud.AddThemeConstantOverride("shadow_offset_y", 2);
        canvas.AddChild(_hud);

        _panel = new TuningPanel();
        AddChild(_panel);
        var storm = _panel.AddSection("Storm");
        storm.AddSlider("Direction", 0f, 359f, _scenario.WindDirectionDegrees,
            value => _scenario = _scenario with { WindDirectionDegrees = value });
        storm.AddSlider("Peak m/s", 0f, 50f, _scenario.PeakWindMetersPerSecond,
            value => _scenario = _scenario with { PeakWindMetersPerSecond = value });
        storm.AddSlider("Turbulence", 0f, 1f, _scenario.Turbulence,
            value => _scenario = _scenario with { Turbulence = value });
        storm.AddSlider("Moisture", 0f, 1f, _scenario.Moisture,
            value => _scenario = _scenario with { Moisture = value });
        storm.AddSlider("Time scale", 0f, 2f, _timeScale, value => _timeScale = value);
        storm.AddButton("Replay [R]", Replay);
        storm.AddButton("Freeze [F]", () => _frozen = !_frozen);

        var motion = _panel.AddSection("Tree motion");
        motion.AddSlider("Trunk freq", 0.08f, 1.2f, _trunkFrequency, value => _trunkFrequency = value);
        motion.AddSlider("Detail freq", 0.6f, 7f, _detailFrequency, value => _detailFrequency = value);
        motion.AddSlider("Trunk amp", 0f, 2f, _trunkAmplitude, value => _trunkAmplitude = value);
        motion.AddSlider("Detail amp", 0f, 2f, _detailAmplitude, value => _detailAmplitude = value);
        motion.AddSlider("Debug 0-3", 0f, 3f, _debugLayer, value => _debugLayer = Mathf.Round(value));

        var forest = _panel.AddSection("Forest");
        forest.AddSlider("Seed", 1f, 999999f, _scenario.Seed,
            value => _scenario = _scenario with { Seed = (uint)value });
        forest.AddButton("Regenerate seed", StartGeneration);
        forest.AddButton("Feel — 2,048", () => SetTreeCount(FeelCount));
        forest.AddButton("Default — 8,192", () => SetTreeCount(DefaultCount));
        forest.AddButton("Stress — 32,768", () => SetTreeCount(StressCount));

        var display = _panel.AddSection("Display");
        display.AddSlider("Fog", 0f, 0.025f, _environment?.FogDensity ?? 0.006f,
            value => { if (_environment is not null) _environment.FogDensity = value; });
        display.AddButton("Toggle rain", () => _rainEnabled = !_rainEnabled);
        display.AddButton("Toggle shadows", ToggleShadows);
    }

    private void UpdateStorm()
    {
        if (_assets is null || _camera is null) return;
        var intensity = _scenario.EvaluateIntensity(_stormElapsed);
        var radians = Mathf.DegToRad(_scenario.WindDirectionDegrees);
        var direction = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        foreach (var material in _assets.Materials)
        {
            material.SetShaderParameter("storm_time", _stormElapsed);
            material.SetShaderParameter("storm_intensity", intensity);
            material.SetShaderParameter("wind_direction", direction);
            material.SetShaderParameter("peak_wind_mps", _scenario.PeakWindMetersPerSecond);
            material.SetShaderParameter("turbulence", _scenario.Turbulence);
            material.SetShaderParameter("storm_center", new Vector2(_scenario.CenterX, _scenario.CenterZ));
            material.SetShaderParameter("storm_radius", _scenario.RadiusMeters);
            material.SetShaderParameter("moisture", _scenario.Moisture);
            material.SetShaderParameter("trunk_frequency", _trunkFrequency);
            material.SetShaderParameter("detail_frequency", _detailFrequency);
            material.SetShaderParameter("trunk_amplitude", _trunkAmplitude);
            material.SetShaderParameter("detail_amplitude", _detailAmplitude);
            material.SetShaderParameter("debug_layer", _debugLayer);
        }

        if (_rain is not null)
        {
            _rain.Position = _camera.Position + new Vector3(0f, 24f, 0f);
            _rain.AmountRatio = _rainEnabled ? Mathf.Clamp(intensity * 1.35f, 0f, 1f) : 0f;
        }
        if (_environment is not null)
            _environment.FogDensity = Math.Max(_environment.FogDensity, 0.001f + intensity * 0.0045f);
        if (_sun is not null)
            _sun.LightEnergy = Mathf.Lerp(1.05f, 0.56f, intensity);
    }

    private void MoveCamera(float delta)
    {
        if (_camera is null) return;
        var input = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) input.Z -= 1f;
        if (Input.IsKeyPressed(Key.S)) input.Z += 1f;
        if (Input.IsKeyPressed(Key.A)) input.X -= 1f;
        if (Input.IsKeyPressed(Key.D)) input.X += 1f;
        if (Input.IsKeyPressed(Key.Q)) input.Y -= 1f;
        if (Input.IsKeyPressed(Key.E)) input.Y += 1f;
        if (input == Vector3.Zero) return;
        var speed = Input.IsKeyPressed(Key.Shift) ? 58f : 22f;
        _camera.Position += _camera.GlobalTransform.Basis * input.Normalized() * speed * delta;
    }

    private void UpdateCameraRotation()
    {
        if (_camera is not null) _camera.Rotation = new Vector3(_cameraPitch, _cameraYaw, 0f);
    }

    private void UpdateHud(double delta)
    {
        if (_hud is null || _generation is null) return;
        var intensity = _scenario.EvaluateIntensity(_stormElapsed);
        _hud.Text = string.Join('\n',
            "LUMBERJACKS — FOREST/STORM LAB",
            $"{_treeCount:N0} trees · {_chunks.Count} spatial chunks · {1.0 / Math.Max(delta, 0.00001):F0} fps",
            $"storm {_stormElapsed % _scenario.DurationSeconds:F1}/{_scenario.DurationSeconds:F0}s · intensity {intensity:F2} · {_scenario.PeakWindMetersPerSecond:F1} m/s",
            $"{RenderingServer.GetVideoAdapterName()} · {RenderingServer.GetCurrentRenderingDriverName()}",
            $"placement {_generation.PlacementHash[..12]} · scenario {_scenario.ContentHash()[..12]}",
            "WASDQE fly · Shift sprint · RMB look · R replay · F freeze · Tab tune");
    }

    private void CaptureFrame(double delta)
    {
        if (_captureSeconds <= 0 || _receiptWritten || _generation is null) return;
        if (_runtime < _warmupSeconds) return;
        _frameTimes.Add(delta * 1000.0);
        if (_runtime < _warmupSeconds + _captureSeconds) return;

        var ordered = _frameTimes.OrderBy(value => value).ToArray();
        var receipt = new
        {
            schema = "lumberjacks.forest-benchmark/v1",
            captured_utc = DateTimeOffset.UtcNow,
            source_revision = System.Environment.GetEnvironmentVariable("LUMBERJACKS_SOURCE_REVISION") ?? "unknown",
            source_dirty = System.Environment.GetEnvironmentVariable("LUMBERJACKS_SOURCE_DIRTY") ?? "unknown",
            godot_version = Engine.GetVersionInfo()["string"].AsString(),
            rendering_driver = RenderingServer.GetCurrentRenderingDriverName(),
            rendering_method = RenderingServer.GetCurrentRenderingMethod(),
            adapter = RenderingServer.GetVideoAdapterName(),
            adapter_api = RenderingServer.GetVideoAdapterApiVersion(),
            asset_set_sha256 = ForestAssetLibrary.AssetSetSha256,
            scenario_sha256 = _scenario.ContentHash(),
            placement_sha256 = _generation.PlacementHash,
            tree_count = _treeCount,
            chunk_count = _chunks.Count,
            viewport = new[] { GetViewport().GetVisibleRect().Size.X, GetViewport().GetVisibleRect().Size.Y },
            warmup_seconds = _warmupSeconds,
            capture_seconds = _captureSeconds,
            frames = ordered.Length,
            p50_ms = Percentile(ordered, 0.50),
            p95_ms = Percentile(ordered, 0.95),
            p99_ms = Percentile(ordered, 0.99),
            max_ms = ordered.Length == 0 ? 0 : ordered[^1],
            over_33ms = ordered.Count(value => value > 33.3),
            over_50ms = ordered.Count(value => value > 50.0),
            over_100ms = ordered.Count(value => value > 100.0),
        };
        var path = string.IsNullOrWhiteSpace(_receiptPath)
            ? Path.Combine(System.Environment.CurrentDirectory, "forest-benchmark.json")
            : ResolvePath(_receiptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        _receiptWritten = true;
        GD.Print($"ForestStormLab: benchmark receipt {path}");
        GetTree().Quit();
    }

    private void CaptureScreenshot()
    {
        if (_screenshotWritten || string.IsNullOrWhiteSpace(_screenshotPath) || _runtime < _warmupSeconds) return;
        var path = ResolvePath(_screenshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        if (error != Error.Ok) throw new IOException($"Could not save forest screenshot: {error}");
        _screenshotWritten = true;
        GD.Print($"ForestStormLab: screenshot {path}");
    }

    private void SetTreeCount(int count)
    {
        if (_treeCount == count) return;
        _treeCount = count;
        StartGeneration();
    }

    private void Replay()
    {
        _stormElapsed = 0f;
        _frozen = false;
    }

    private void ToggleShadows()
    {
        _shadows = !_shadows;
        foreach (var chunk in _chunks)
            chunk.CastShadow = _shadows
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;
        if (_sun is not null) _sun.ShadowEnabled = _shadows;
    }

    private void RunValidation()
    {
        try
        {
            _scenario.Validate();
            var first = ForestPlacementGenerator.Generate(_scenario, 512, 64);
            var second = ForestPlacementGenerator.Generate(_scenario, 512, 64);
            if (!string.Equals(first.PlacementHash, second.PlacementHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Deterministic placement hashes differ.");
            if (_scenario.EvaluateIntensity(0f) != _scenario.IntensityCurve[0])
                throw new InvalidOperationException("Scenario did not start at its first intensity sample.");
            GD.Print($"FOREST_STORM_VALIDATION_OK placement_sha256={first.PlacementHash} scenario_sha256={_scenario.ContentHash()}");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PushError($"FOREST_STORM_VALIDATION_FAILED {ex}");
            GetTree().Quit(2);
        }
    }

    private static string ResolvePath(string path) => path.StartsWith("res://", StringComparison.Ordinal)
        ? ProjectSettings.GlobalizePath(path)
        : Path.GetFullPath(path);

    private static double Percentile(double[] ordered, double fraction)
    {
        if (ordered.Length == 0) return 0;
        var position = fraction * (ordered.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        if (low == high) return Math.Round(ordered[low], 3);
        return Math.Round(ordered[low] + (ordered[high] - ordered[low]) * (position - low), 3);
    }

    private sealed record LabArguments(
        string Preset,
        int? TreeCount,
        string ScenarioPath,
        int WarmupSeconds,
        int CaptureSeconds,
        string ReceiptPath,
        string ScreenshotPath,
        float StartSeconds,
        bool ValidateOnly)
    {
        public static LabArguments Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
                var split = arg[2..].Split('=', 2);
                values[split[0]] = split.Length == 2 ? split[1] : "true";
            }
            int? treeCount = values.TryGetValue("tree-count", out var trees) &&
                int.TryParse(trees, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTrees)
                ? parsedTrees : null;
            return new LabArguments(
                values.GetValueOrDefault("forest-preset", "default"),
                treeCount,
                values.GetValueOrDefault("scenario", string.Empty),
                ParseInt(values, "warmup-seconds", 10, 0, 300),
                ParseInt(values, "capture-seconds", 0, 0, 3600),
                values.GetValueOrDefault("receipt", string.Empty),
                values.GetValueOrDefault("screenshot", string.Empty),
                ParseFloat(values, "storm-start-seconds", 0f, 0f, 3600f),
                values.ContainsKey("validate-only"));
        }

        private static int ParseInt(IReadOnlyDictionary<string, string> values, string key,
            int fallback, int minimum, int maximum)
        {
            if (!values.TryGetValue(key, out var raw) ||
                !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return fallback;
            return Math.Clamp(value, minimum, maximum);
        }

        private static float ParseFloat(IReadOnlyDictionary<string, string> values, string key,
            float fallback, float minimum, float maximum)
        {
            if (!values.TryGetValue(key, out var raw) ||
                !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return fallback;
            return Math.Clamp(value, minimum, maximum);
        }
    }
}

using Godot;
using System;

namespace CommunitySurvival.Lab;

/// <summary>
/// Tree felling physics lab. Interactive tree with notch/back-cut mechanics,
/// hinge analysis, fall dynamics. All local — no server.
///
/// Controls: WASD move, RMB orbit, scroll zoom, Tab tuning panel
/// Space = start/pause fall, R = reset, 1-6 = viz modes
/// </summary>
public partial class TreeFellingLab : Node3D
{
    // --- Simulation ---
    private TreeFellingSim _sim = new();
    private bool _simRunning;
    private float _timeScale = 1f;

    // --- Tree config (mirrored to sim) ---
    private int _speciesIdx; // 0=Oak,1=Pine,2=Ash,3=Birch
    private float _dbh = 18f;
    private float _height = 60f;
    private float _naturalLeanDeg = 3f;
    private float _naturalLeanBearing;
    private float _crownOffset = 0.2f;
    private float _age = 80f;
    private float _greenDry; // 0=green, 1=dry
    private float _twist;

    // --- Notch/cut controls ---
    private float _notchType; // 0=conventional, 1=open-face
    private float _notchDepth = 0.33f;
    private float _notchHeight = 1.5f;
    private float _notchFaceAngle; // degrees, direction to fell toward
    private float _backCutOffset = 2f; // inches above notch floor
    private float _hingeTarget = 10f; // % of DBH

    // --- Axe strike controls ---
    private float _strikeAngle;
    private float _strikeHeight = 1.5f;
    private float _strikePower = 1f;

    // --- Environment ---
    private float _slopeAngle;
    private float _slopeDir;
    private float _windStrength;
    private float _windDir;

    // --- Display ---
    private int _vizMode; // 0=shaded,1=cross-section,2=force,3=stress,4=trajectory,5=side profile

    // --- Nodes ---
    private MeshInstance3D _groundMesh;
    private MeshInstance3D _trunkMesh;
    private MeshInstance3D _canopyMesh;
    private MeshInstance3D _stumpMesh;
    private MeshInstance3D _hingeViz;
    private MeshInstance3D _windArrow;
    private MeshInstance3D _leanArrow;
    private Node3D _treeRoot;
    private Node3D _cameraPivot;
    private Camera3D _camera;
    private Label _hud;
    private Label _crossSectionHud;
    private TuningPanel _panel;

    // --- Player ---
    private MeshInstance3D _playerOrb;
    private Node3D _axeNode;
    private MeshInstance3D _axeHandle;
    private MeshInstance3D _axeBlade;
    private Vector3 _playerPos;
    private float _playerFacing;
    private bool _playerMode = true;
    private float _playerMoveSpeed = 3f;
    private const float PlayerRadius = 0.3f;

    // --- Axe swing ---
    private bool _isSwinging;
    private Tween _swingTween;
    private AxeConfig _axeConfig = AxeConfig.Default;
    private SwingResult _lastStrikeResult;
    private float _playerStrikeHeight = 1.5f;

    // --- Camera ---
    private float _camYaw = 30f, _camPitch = -25f, _camDist = 25f;
    private bool _rmbHeld, _lmbHeld;
    private Vector3 _focusPos = new(0, 3f, 0);

    // --- Constants ---
    private const float FtToUnits = 0.3f; // 1 foot = 0.3 godot units (so 60ft tree = 18 units)
    private const float InToUnits = FtToUnits / 12f;

    public override void _Ready()
    {
        // Initialize sim with default config
        RebuildTree();

        // Ground plane
        _groundMesh = new MeshInstance3D();
        var groundPlaneMesh = new PlaneMesh { Size = new Vector2(60, 60) };
        _groundMesh.Mesh = groundPlaneMesh;
        _groundMesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.25f, 0.35f, 0.15f),
            Roughness = 0.95f,
        };
        AddChild(_groundMesh);

        // Tree root — this rotates during fall
        _treeRoot = new Node3D();
        AddChild(_treeRoot);

        // Player orb with axe
        BuildPlayer();

        // Trunk mesh (built from sim slices)
        _trunkMesh = new MeshInstance3D();
        _treeRoot.AddChild(_trunkMesh);

        // Canopy mesh (simplified crown)
        _canopyMesh = new MeshInstance3D();
        _canopyMesh.Mesh = new SphereMesh { Radius = 2.5f, Height = 5f };
        _canopyMesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.35f, 0.1f),
            Roughness = 0.9f,
        };
        _treeRoot.AddChild(_canopyMesh);

        // Stump mesh (visible after fall)
        _stumpMesh = new MeshInstance3D();
        _stumpMesh.Mesh = new CylinderMesh
        {
            TopRadius = _dbh / 2f * InToUnits,
            BottomRadius = _dbh / 2f * InToUnits * 1.1f,
            Height = _notchHeight * FtToUnits,
        };
        _stumpMesh.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.35f, 0.2f),
            Roughness = 0.9f,
        };
        _stumpMesh.Position = new Vector3(0, _notchHeight * FtToUnits / 2f, 0);
        _stumpMesh.Visible = false;
        AddChild(_stumpMesh);

        // Hinge highlight overlay
        _hingeViz = new MeshInstance3D();
        _treeRoot.AddChild(_hingeViz);

        // Wind direction arrow
        _windArrow = CreateArrow(new Color(0.4f, 0.6f, 0.9f));
        _windArrow.Visible = false;
        AddChild(_windArrow);

        // Lean direction arrow
        _leanArrow = CreateArrow(new Color(0.9f, 0.6f, 0.2f));
        AddChild(_leanArrow);

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
        env.TonemapMode = Godot.Environment.ToneMapper.Aces;
        AddChild(new WorldEnvironment { Environment = env });

        // Camera
        _cameraPivot = new Node3D();
        AddChild(_cameraPivot);
        _camera = new Camera3D { Fov = 55, Current = true, Far = 500f };
        _cameraPivot.AddChild(_camera);
        _cameraPivot.Position = _focusPos;
        UpdateCamera();

        // HUD
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label();
        _hud.AnchorLeft = 1; _hud.AnchorRight = 1;
        _hud.OffsetLeft = -340; _hud.OffsetTop = 10; _hud.OffsetRight = -10;
        _hud.HorizontalAlignment = HorizontalAlignment.Right;
        _hud.AddThemeFontSizeOverride("font_size", 13);
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _hud.AddThemeConstantOverride("shadow_offset_x", 1);
        _hud.AddThemeConstantOverride("shadow_offset_y", 1);
        canvas.AddChild(_hud);

        // Cross-section HUD (bottom-left)
        _crossSectionHud = new Label();
        _crossSectionHud.AnchorTop = 1; _crossSectionHud.AnchorBottom = 1;
        _crossSectionHud.OffsetLeft = 10; _crossSectionHud.OffsetTop = -180; _crossSectionHud.OffsetBottom = -10;
        _crossSectionHud.AddThemeFontSizeOverride("font_size", 11);
        _crossSectionHud.AddThemeColorOverride("font_color", Colors.White);
        _crossSectionHud.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _crossSectionHud.AddThemeConstantOverride("shadow_offset_x", 1);
        _crossSectionHud.AddThemeConstantOverride("shadow_offset_y", 1);
        canvas.AddChild(_crossSectionHud);

        // Tuning panel
        _panel = new TuningPanel();
        AddChild(_panel);
        BuildPanel();

        // Build visuals
        RebuildTrunkMesh();
        UpdateCanopy();
        UpdateIndicators();

        GD.Print("TreeFellingLab: ready. Tab=tuning, WASD=move, RMB=orbit, LMB=chop, Space=start fall, R=reset");
    }

    // ——— Panel ———

    private void BuildPanel()
    {
        var tree = _panel.AddSection("Tree Properties");
        tree.AddSlider("Species", 0, 3, _speciesIdx, v =>
        {
            _speciesIdx = (int)v;
            RebuildTree(); RebuildTrunkMesh(); UpdateCanopy();
        });
        tree.AddSlider("DBH (in)", 8, 48, _dbh, v =>
        {
            _dbh = v;
            RebuildTree(); RebuildTrunkMesh(); UpdateCanopy();
        });
        tree.AddSlider("Height (ft)", 30, 120, _height, v =>
        {
            _height = v;
            RebuildTree(); RebuildTrunkMesh(); UpdateCanopy();
        });
        tree.AddSlider("Lean (deg)", 0, 25, _naturalLeanDeg, v =>
        {
            _naturalLeanDeg = v;
            RebuildTree(); UpdateIndicators();
        });
        tree.AddSlider("Lean Dir", 0, 360, _naturalLeanBearing, v =>
        {
            _naturalLeanBearing = v;
            RebuildTree(); UpdateIndicators();
        });
        tree.AddSlider("Crown Offset", 0, 1, _crownOffset, v =>
        {
            _crownOffset = v;
            RebuildTree(); UpdateCanopy();
        });
        tree.AddSlider("Age (yr)", 20, 300, _age, v => { _age = v; RebuildTree(); });
        tree.AddSlider("Green/Dry", 0, 1, _greenDry, v => { _greenDry = v; RebuildTree(); });
        tree.AddButton("Randomize", () =>
        {
            var rng = new Random();
            _speciesIdx = rng.Next(4);
            _dbh = 10 + (float)rng.NextDouble() * 38;
            _height = 30 + (float)rng.NextDouble() * 90;
            _naturalLeanDeg = (float)rng.NextDouble() * 15;
            _naturalLeanBearing = (float)rng.NextDouble() * 360;
            _crownOffset = (float)rng.NextDouble() * 0.6f;
            _age = 20 + (float)rng.NextDouble() * 280;
            RebuildTree(); RebuildTrunkMesh(); UpdateCanopy(); UpdateIndicators();
        });

        var notch = _panel.AddSection("Notch Control");
        notch.AddSlider("Type (0=conv,1=open)", 0, 1, _notchType, v => _notchType = v);
        notch.AddSlider("Depth (%dia)", 20, 60, _notchDepth * 100, v => _notchDepth = v / 100f);
        notch.AddSlider("Height (ft)", 0.5f, 4, _notchHeight, v => _notchHeight = v);
        notch.AddSlider("Face Angle", 0, 360, _notchFaceAngle, v => _notchFaceAngle = v);
        notch.AddButton("Cut Notch", () =>
        {
            var type = _notchType >= 0.5f ? NotchType.OpenFace : NotchType.Conventional;
            float angleRad = _notchFaceAngle * MathF.PI / 180f;
            _sim.ApplyNotchCut(_notchHeight, angleRad, _notchDepth, type);
            RebuildTrunkMesh();
        });
        notch.AddButton("Clear Cuts", () =>
        {
            _sim.Reset();
            _simRunning = false;
            _treeRoot.Rotation = Vector3.Zero;
            _stumpMesh.Visible = false;
            RebuildTrunkMesh();
        });

        var back = _panel.AddSection("Back Cut");
        back.AddSlider("Height Offset (in)", -2, 6, _backCutOffset, v => _backCutOffset = v);
        back.AddSlider("Hinge Target (%DBH)", 5, 20, _hingeTarget, v => _hingeTarget = v);
        back.AddButton("Make Back Cut", () =>
        {
            // Depth = cut from back until hinge target remains
            float hingeWidthTarget = _hingeTarget / 100f * _dbh; // inches
            float radius = _dbh / 2f;
            // Back cut depth fraction: cut until leaving ~hingeTarget width
            float depthFraction = Math.Max(0.3f, 1f - _notchDepth - hingeWidthTarget / (2f * radius));
            _sim.ApplyBackCut(_backCutOffset, depthFraction);
            RebuildTrunkMesh();
        });
        back.AddButton("Bore Cut", () =>
        {
            float angleRad = _notchFaceAngle * MathF.PI / 180f;
            _sim.ApplyBoreCut(_notchHeight, angleRad, 0.7f);
            RebuildTrunkMesh();
        });

        var axe = _panel.AddSection("Axe Strikes");
        axe.AddSlider("Strike Angle", 0, 360, _strikeAngle, v => _strikeAngle = v);
        axe.AddSlider("Strike Height (ft)", 0.5f, 4, _strikeHeight, v => _strikeHeight = v);
        axe.AddSlider("Power", 0.5f, 2, _strikePower, v => _strikePower = v);
        axe.AddButton("Forehand Strike", () =>
        {
            float angleRad = _strikeAngle * MathF.PI / 180f;
            _sim.ApplyAxeStrike(_strikeHeight, angleRad, _strikePower * 0.08f);
            RebuildTrunkMesh();
        });
        axe.AddButton("Backhand Strike", () =>
        {
            // Backhand hits from the other side of the notch
            float angleRad = (_strikeAngle + 45f) * MathF.PI / 180f;
            _sim.ApplyAxeStrike(_strikeHeight, angleRad, _strikePower * 0.08f * 0.85f);
            RebuildTrunkMesh();
        });

        var player = _panel.AddSection("Player Axe");
        player.AddSlider("Mode (0=orbit,1=walk)", 0, 1, _playerMode ? 1 : 0, v =>
        {
            _playerMode = v >= 0.5f;
            _playerOrb.Visible = _playerMode;
        });
        player.AddSlider("Strike Height (ft)", 0.5f, 4, _playerStrikeHeight, v => _playerStrikeHeight = v);
        player.AddSlider("Axe Mass (kg)", 0.8f, 2.5f, _axeConfig.HeadMassKg, v =>
        {
            var c = _axeConfig; c.HeadMassKg = v; _axeConfig = c;
        });
        player.AddSlider("Wedge Half-Angle (deg)", 6, 18,
            _axeConfig.WedgeHalfAngleRad * 180f / MathF.PI, v =>
        {
            var c = _axeConfig; c.WedgeHalfAngleRad = v * MathF.PI / 180f; _axeConfig = c;
        });
        player.AddSlider("Swing Radius (m)", 0.8f, 1.4f, _axeConfig.SwingRadiusM, v =>
        {
            var c = _axeConfig; c.SwingRadiusM = v; _axeConfig = c;
        });

        var env = _panel.AddSection("Environment");
        env.AddSlider("Slope (deg)", 0, 35, _slopeAngle, v =>
        {
            _slopeAngle = v;
            _sim.SlopeAngle = v;
            UpdateGround();
        });
        env.AddSlider("Slope Dir", 0, 360, _slopeDir, v =>
        {
            _slopeDir = v;
            _sim.SlopeDirection = v;
            UpdateGround();
        });
        env.AddSlider("Wind (mph)", 0, 30, _windStrength, v =>
        {
            _windStrength = v;
            _sim.WindStrength = v;
            UpdateIndicators();
        });
        env.AddSlider("Wind Dir", 0, 360, _windDir, v =>
        {
            _windDir = v;
            _sim.WindDirection = v;
            UpdateIndicators();
        });

        var sim = _panel.AddSection("Simulation");
        sim.AddSlider("Time Scale", 0.1f, 5, _timeScale, v => _timeScale = v);
        sim.AddSlider("Viz Mode (0-5)", 0, 5, _vizMode, v => { _vizMode = (int)v; RebuildTrunkMesh(); });
        sim.AddButton("Start Fall", () => _simRunning = true);
        sim.AddButton("Reset", ResetAll);
        sim.AddButton("Step 0.05s", () =>
        {
            _sim.Advance(0.05f);
            ApplyDynamicsToScene();
        });

        var presets = _panel.AddSection("Presets");
        presets.AddButton("Textbook Fell", () =>
        {
            ResetAll();
            _notchFaceAngle = 0; _notchDepth = 0.33f; _notchHeight = 1.5f;
            _notchType = 1; // open face
            _sim.ApplyNotchCut(1.5f, 0, 0.33f, NotchType.OpenFace);
            _sim.ApplyBackCut(2f, 0.55f);
            RebuildTrunkMesh();
        });
        presets.AddButton("Barber Chair", () =>
        {
            ResetAll();
            _speciesIdx = 2; // Ash (barber-chair prone)
            _naturalLeanDeg = 12f;
            _naturalLeanBearing = 0f;
            RebuildTree();
            _sim.ApplyNotchCut(1.5f, 0, 0.25f, NotchType.Conventional);
            _sim.ApplyBackCut(-1f, 0.7f); // back cut BELOW notch, too deep
            RebuildTrunkMesh(); UpdateCanopy(); UpdateIndicators();
        });
        presets.AddButton("Hillside", () =>
        {
            ResetAll();
            _slopeAngle = 25f; _slopeDir = 90f;
            _sim.SlopeAngle = 25f; _sim.SlopeDirection = 90f;
            UpdateGround();
            _sim.ApplyNotchCut(1.5f, MathF.PI / 2f, 0.33f, NotchType.OpenFace); // fell downhill
            _sim.ApplyBackCut(2f, 0.55f);
            RebuildTrunkMesh();
        });
        presets.AddButton("Against Lean", () =>
        {
            ResetAll();
            _naturalLeanDeg = 8f; _naturalLeanBearing = 180f; // leaning south
            RebuildTree();
            _sim.ApplyNotchCut(1.5f, 0, 0.33f, NotchType.OpenFace); // fell north (against lean)
            _sim.ApplyBoreCut(1.5f, 0, 0.7f);
            _sim.ApplyBackCut(0, 0.4f); // release cut from back
            RebuildTrunkMesh(); UpdateCanopy(); UpdateIndicators();
        });
        presets.AddButton("Big Oak", () =>
        {
            ResetAll();
            _speciesIdx = 0; _dbh = 42f; _height = 100f; _age = 250f;
            _crownOffset = 0.4f; _naturalLeanDeg = 5f;
            RebuildTree(); RebuildTrunkMesh(); UpdateCanopy(); UpdateIndicators();
        });
    }

    // ——— Tree Initialization ———

    private void RebuildTree()
    {
        var config = new TreeConfig
        {
            Species = (TreeSpecies)_speciesIdx,
            DbhInches = _dbh,
            HeightFt = _height,
            NaturalLeanDeg = _naturalLeanDeg,
            NaturalLeanBearing = _naturalLeanBearing,
            CrownOffset = _crownOffset,
            Age = _age,
            IsDry = _greenDry > 0.5f,
            Twist = _twist,
            FireScars = 0,
        };
        _sim.Initialize(config);
    }

    // ——— Trunk Mesh Building ———

    private void RebuildTrunkMesh()
    {
        if (_sim.Slices == null || _sim.SliceCount == 0) return;

        int slices = _sim.SliceCount;
        int sectors = TreeFellingSim.Sectors;
        float baseRadius = _dbh / 2f * InToUnits;

        // Vertices: (slices+1) rings x (sectors+1) verts each (extra for UV seam)
        int ringVerts = sectors + 1;
        int vertCount = (slices + 1) * ringVerts;
        var vertices = new Vector3[vertCount];
        var colors = new Color[vertCount];
        var normals = new Vector3[vertCount];

        for (int si = 0; si <= slices; si++)
        {
            int sliceIdx = Math.Min(si, slices - 1);
            float y = si * _sim.SliceHeight * FtToUnits;
            float[] slice = _sim.Slices[sliceIdx];

            for (int s = 0; s <= sectors; s++)
            {
                int sectorIdx = s % sectors;
                float angle = sectorIdx * TreeFellingSim.SectorAngle;
                float r = slice[sectorIdx] * baseRadius;

                float x = MathF.Cos(angle) * r;
                float z = MathF.Sin(angle) * r;

                int vi = si * ringVerts + s;
                vertices[vi] = new Vector3(x, y, z);
                normals[vi] = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)).Normalized();
                colors[vi] = GetTrunkColor(sliceIdx, sectorIdx, slice[sectorIdx]);
            }
        }

        // Indices: quads between adjacent rings
        int quadCount = slices * sectors;
        var indices = new int[quadCount * 6];
        int idx = 0;
        for (int si = 0; si < slices; si++)
        {
            for (int s = 0; s < sectors; s++)
            {
                int tl = si * ringVerts + s;
                int tr = si * ringVerts + s + 1;
                int bl = (si + 1) * ringVerts + s;
                int br = (si + 1) * ringVerts + s + 1;

                indices[idx++] = tl; indices[idx++] = bl; indices[idx++] = tr;
                indices[idx++] = tr; indices[idx++] = bl; indices[idx++] = br;
            }
        }

        // Recompute normals from faces
        Array.Clear(normals, 0, normals.Length);
        for (int i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];
            var fn = (b - a).Cross(c - a);
            if (fn.LengthSquared() > 0.00001f)
            {
                fn = fn.Normalized();
                normals[indices[i]] += fn;
                normals[indices[i + 1]] += fn;
                normals[indices[i + 2]] += fn;
            }
        }
        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared() > 0.00001f)
                normals[i] = normals[i].Normalized();
            else
                normals[i] = Vector3.Up;
        }

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
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // see inside cuts
        });
        _trunkMesh.Mesh = mesh;
    }

    private Color GetTrunkColor(int sliceIdx, int sectorIdx, float remaining)
    {
        // Check if this sector is part of the hinge zone
        bool isHinge = false;
        if (_sim.HasNotch)
        {
            float sectorAngle = sectorIdx * TreeFellingSim.SectorAngle;
            float faceAngle = _sim.Notch.FaceAngle;
            int faceSector = (int)(faceAngle / TreeFellingSim.SectorAngle) % TreeFellingSim.Sectors;
            int leftCenter = (faceSector + TreeFellingSim.Sectors / 4) % TreeFellingSim.Sectors;
            int rightCenter = (faceSector + 3 * TreeFellingSim.Sectors / 4) % TreeFellingSim.Sectors;

            int distLeft = Math.Min(Math.Abs(sectorIdx - leftCenter),
                                    TreeFellingSim.Sectors - Math.Abs(sectorIdx - leftCenter));
            int distRight = Math.Min(Math.Abs(sectorIdx - rightCenter),
                                     TreeFellingSim.Sectors - Math.Abs(sectorIdx - rightCenter));
            isHinge = (distLeft <= 3 || distRight <= 3) && remaining > 0.01f;
        }

        switch (_vizMode)
        {
            case 3: // Stress map
                if (remaining < 0.01f) return new Color(0.7f, 0.65f, 0.5f); // cut surface
                float stress = _sim.Hinge.FiberStress;
                if (isHinge)
                {
                    // Green → yellow → red based on stress
                    float r = stress;
                    float g = 1f - stress * 0.5f;
                    return new Color(r, g, 0.1f);
                }
                return new Color(0.4f, 0.28f, 0.15f); // normal bark

            default: // shaded and others
                if (remaining < 0.01f)
                    return new Color(0.7f, 0.65f, 0.5f); // exposed wood (cut surface)
                if (remaining < 0.3f)
                    return new Color(0.6f, 0.55f, 0.4f); // partially cut

                if (isHinge)
                    return new Color(0.8f, 0.6f, 0.15f); // hinge zone = orange highlight

                // Normal bark color — varies slightly by species
                return (_sim.Config.Species) switch
                {
                    TreeSpecies.Oak => new Color(0.35f, 0.25f, 0.15f),
                    TreeSpecies.Pine => new Color(0.45f, 0.3f, 0.15f),
                    TreeSpecies.Ash => new Color(0.4f, 0.35f, 0.25f),
                    TreeSpecies.Birch => new Color(0.7f, 0.68f, 0.6f),
                    _ => new Color(0.4f, 0.28f, 0.15f),
                };
        }
    }

    // ——— Canopy & Indicators ———

    private void UpdateCanopy()
    {
        float treeHeight = _height * FtToUnits;
        float canopyRadius = _dbh / 2f * InToUnits * 8f; // crown is ~8x trunk radius
        canopyRadius = Math.Max(1.5f, canopyRadius);

        _canopyMesh.Mesh = new SphereMesh
        {
            Radius = canopyRadius,
            Height = canopyRadius * 1.5f,
        };

        // Position at top of tree, offset by crown asymmetry
        float crownAngleRad = _naturalLeanBearing * MathF.PI / 180f;
        float offsetDist = _crownOffset * canopyRadius * 0.5f;
        _canopyMesh.Position = new Vector3(
            MathF.Cos(crownAngleRad) * offsetDist,
            treeHeight * 0.85f,
            MathF.Sin(crownAngleRad) * offsetDist
        );
    }

    private void UpdateIndicators()
    {
        // Wind arrow
        if (_windStrength > 0.1f)
        {
            float windRad = _windDir * MathF.PI / 180f;
            _windArrow.Visible = true;
            _windArrow.Position = new Vector3(
                MathF.Cos(windRad) * -8f,
                _height * FtToUnits * 0.7f,
                MathF.Sin(windRad) * -8f
            );
            _windArrow.LookAt(new Vector3(0, _height * FtToUnits * 0.7f, 0), Vector3.Up);
            float scale = 0.5f + _windStrength / 30f * 2f;
            _windArrow.Scale = new Vector3(scale, scale, scale);
        }
        else
        {
            _windArrow.Visible = false;
        }

        // Lean arrow (at base, pointing in lean direction)
        if (_naturalLeanDeg > 0.5f)
        {
            float leanRad = _naturalLeanBearing * MathF.PI / 180f;
            _leanArrow.Visible = true;
            _leanArrow.Position = new Vector3(0, 0.5f, 0);
            _leanArrow.LookAt(new Vector3(
                MathF.Cos(leanRad) * 3f,
                0.5f,
                MathF.Sin(leanRad) * 3f
            ), Vector3.Up);
            float scale = 0.3f + _naturalLeanDeg / 25f;
            _leanArrow.Scale = new Vector3(scale, scale, scale);
        }
        else
        {
            _leanArrow.Visible = false;
        }
    }

    private void UpdateGround()
    {
        float slopeRad = _slopeAngle * MathF.PI / 180f;
        float dirRad = _slopeDir * MathF.PI / 180f;
        _groundMesh.Rotation = new Vector3(
            MathF.Cos(dirRad) * slopeRad,
            0,
            MathF.Sin(dirRad) * slopeRad
        );
    }

    private MeshInstance3D CreateArrow(Color color)
    {
        var arrow = new MeshInstance3D();
        // Simple cone as arrow
        arrow.Mesh = new CylinderMesh
        {
            TopRadius = 0f,
            BottomRadius = 0.3f,
            Height = 1.5f,
        };
        arrow.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = color,
            EmissionEnabled = true,
            Emission = color * 0.3f,
        };
        // Rotate so it points forward (-Z)
        arrow.RotationDegrees = new Vector3(90, 0, 0);
        var parent = new MeshInstance3D();
        var parentNode = new Node3D();
        parentNode.AddChild(arrow);

        // Actually, just return a simple node with the cone
        var node = new MeshInstance3D();
        node.Mesh = new CylinderMesh
        {
            TopRadius = 0f,
            BottomRadius = 0.3f,
            Height = 1.5f,
        };
        node.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = color,
            EmissionEnabled = true,
            Emission = color * 0.3f,
        };
        return node;
    }

    // ——— Dynamics ———

    private void ApplyDynamicsToScene()
    {
        var dyn = _sim.Dynamics;
        if (dyn.Phase == FallPhase.Standing) return;

        float tiltRad = dyn.FallTiltDeg * MathF.PI / 180f;
        float dirRad = dyn.FallBearing * MathF.PI / 180f;

        // Rotate tree root around the hinge point (at cut height, perpendicular to fall direction)
        float cutY = _sim.HasNotch ? _sim.Notch.Height * FtToUnits : 1.5f * FtToUnits;

        // Hinge axis is perpendicular to fall direction (horizontal)
        // Fall direction in XZ plane
        float fallX = MathF.Cos(dirRad);
        float fallZ = MathF.Sin(dirRad);

        // Rotate the tree root: pivot point is at cut height
        // Set position so rotation is about the hinge
        _treeRoot.Position = new Vector3(0, 0, 0);
        _treeRoot.Rotation = Vector3.Zero;

        // Apply rotation about hinge axis
        var hingeAxis = new Vector3(-fallZ, 0, fallX).Normalized(); // perpendicular to fall dir
        var pivotPoint = new Vector3(0, cutY, 0);

        // Transform: translate to pivot, rotate, translate back
        _treeRoot.Position = pivotPoint;
        _treeRoot.Rotation = Vector3.Zero;

        // Rotate around hinge axis by tilt angle
        var basis = Basis.Identity.Rotated(hingeAxis, tiltRad);
        _treeRoot.Basis = basis;
        _treeRoot.Position = pivotPoint - basis * pivotPoint;

        // Show stump when tree is falling
        if (dyn.FallTiltDeg > 5f)
        {
            _stumpMesh.Visible = true;
            float stumpH = _sim.HasNotch ? _sim.Notch.Height : 1.5f;
            _stumpMesh.Mesh = new CylinderMesh
            {
                TopRadius = _dbh / 2f * InToUnits * 0.95f,
                BottomRadius = _dbh / 2f * InToUnits * 1.05f,
                Height = stumpH * FtToUnits,
            };
            _stumpMesh.Position = new Vector3(0, stumpH * FtToUnits / 2f, 0);
        }

        // Barber chair visual: scale trunk to simulate split
        if (dyn.Phase == FallPhase.BarberChair)
        {
            float split = dyn.BarberChairProgress;
            // Stretch trunk vertically to simulate the slab splitting upward
            _trunkMesh.Scale = new Vector3(1f - split * 0.3f, 1f + split * 0.5f, 1f);
        }
    }

    private void ResetAll()
    {
        _sim.Reset();
        _simRunning = false;
        _treeRoot.Position = Vector3.Zero;
        _treeRoot.Rotation = Vector3.Zero;
        _treeRoot.Basis = Basis.Identity;
        _trunkMesh.Scale = Vector3.One;
        _stumpMesh.Visible = false;
        _lastStrikeResult = default;
        _isSwinging = false;
        _swingTween?.Kill();
        if (_axeNode != null) _axeNode.RotationDegrees = Vector3.Zero;
        RebuildTrunkMesh();
    }

    // ——— Player ———

    private void BuildPlayer()
    {
        _playerOrb = new MeshInstance3D();
        _playerOrb.Mesh = new SphereMesh { Radius = PlayerRadius, Height = PlayerRadius * 2 };
        _playerOrb.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.15f, 0.85f, 0.25f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.3f, 0.05f),
        };

        // Axe: pivot at the grip end so rotation swings the blade in an arc.
        // _axeNode origin = hand position. Handle extends forward (+Z local),
        // blade at the far end. Tween rotates _axeNode around X → overhead swing.
        _axeNode = new Node3D();
        _axeNode.Position = new Vector3(0, 0.15f, -PlayerRadius - 0.05f); // just in front of orb

        _axeHandle = new MeshInstance3D();
        _axeHandle.Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.025f, Height = 0.6f };
        _axeHandle.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.45f, 0.28f, 0.12f),
            Roughness = 0.9f,
        };
        // Handle along -Z (forward from pivot): rotate so cylinder axis aligns with Z
        _axeHandle.RotationDegrees = new Vector3(90, 0, 0);
        _axeHandle.Position = new Vector3(0, 0, -0.3f); // center of handle, extending forward
        _axeNode.AddChild(_axeHandle);

        _axeBlade = new MeshInstance3D();
        _axeBlade.Mesh = new BoxMesh { Size = new Vector3(0.18f, 0.03f, 0.12f) };
        _axeBlade.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.7f, 0.7f, 0.7f),
            Metallic = 0.8f,
            Roughness = 0.3f,
        };
        _axeBlade.Position = new Vector3(0, 0, -0.6f); // at the far end of the handle
        _axeNode.AddChild(_axeBlade);

        _playerOrb.AddChild(_axeNode);
        AddChild(_playerOrb);

        _playerPos = new Vector3(3f, PlayerRadius, 0);
        _playerOrb.Position = _playerPos;
    }

    private void StartAxeSwing()
    {
        _isSwinging = true;

        // Strike angle: direction from player to tree (tree is at origin)
        float strikeAngle = MathF.Atan2(-_playerPos.Z, -_playerPos.X);
        if (strikeAngle < 0) strikeAngle += 2f * MathF.PI;

        // Animate: wind-up → swing-through → impact callback → recovery
        _swingTween?.Kill();
        _swingTween = CreateTween();

        // Wind-up (0.15s)
        _swingTween.TweenProperty(_axeNode, "rotation_degrees:z", -60f, 0.15)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);

        // Swing through (0.15s)
        _swingTween.TweenProperty(_axeNode, "rotation_degrees:z", 30f, 0.15)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.In);

        // Impact: apply physics strike
        float capturedAngle = strikeAngle;
        float capturedHeight = _playerStrikeHeight;
        _swingTween.TweenCallback(Callable.From(() =>
        {
            _lastStrikeResult = _sim.ApplyPhysicsStrike(
                capturedHeight, capturedAngle, _axeConfig, 1.0f);
            RebuildTrunkMesh();
        }));

        // Recovery (0.1s)
        _swingTween.TweenProperty(_axeNode, "rotation_degrees:z", 0f, 0.1)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.InOut);

        _swingTween.TweenCallback(Callable.From(() => _isSwinging = false));
    }

    // ——— Cross-Section Text Display ———

    private string BuildCrossSectionText()
    {
        if (_sim.Slices == null || !_sim.HasNotch) return "Cut a notch to see cross-section";

        int sliceIdx = Math.Clamp((int)(_notchHeight / _sim.SliceHeight), 0, _sim.SliceCount - 1);
        float[] slice = _sim.Slices[sliceIdx];

        // Build a simple ASCII polar view
        // 7 rows x 13 cols, center = (3,6)
        char[,] grid = new char[7, 13];
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 13; c++)
                grid[r, c] = ' ';

        // Plot each sector
        for (int s = 0; s < TreeFellingSim.Sectors; s++)
        {
            float angle = s * TreeFellingSim.SectorAngle;
            float r = slice[s];
            if (r < 0.05f) continue;

            int px = 6 + (int)(MathF.Cos(angle) * r * 5.5f);
            int py = 3 + (int)(MathF.Sin(angle) * r * 2.8f);
            px = Math.Clamp(px, 0, 12);
            py = Math.Clamp(py, 0, 6);

            // Determine character
            float sectorAngle = s * TreeFellingSim.SectorAngle;
            float faceAngle = _sim.Notch.FaceAngle;
            int faceSector = (int)(faceAngle / TreeFellingSim.SectorAngle) % TreeFellingSim.Sectors;
            int leftCenter = (faceSector + TreeFellingSim.Sectors / 4) % TreeFellingSim.Sectors;
            int rightCenter = (faceSector + 3 * TreeFellingSim.Sectors / 4) % TreeFellingSim.Sectors;
            int distLeft = Math.Min(Math.Abs(s - leftCenter), TreeFellingSim.Sectors - Math.Abs(s - leftCenter));
            int distRight = Math.Min(Math.Abs(s - rightCenter), TreeFellingSim.Sectors - Math.Abs(s - rightCenter));
            bool isHinge = distLeft <= 3 || distRight <= 3;

            grid[py, px] = isHinge ? '#' : 'O';
        }

        // Mark face direction
        float fAngle = _sim.Notch.FaceAngle;
        int fx = 6 + (int)(MathF.Cos(fAngle) * 6f);
        int fy = 3 + (int)(MathF.Sin(fAngle) * 3f);
        fx = Math.Clamp(fx, 0, 12);
        fy = Math.Clamp(fy, 0, 6);
        grid[fy, fx] = '>';

        // Convert to string
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("Cross-Section at Notch Height:");
        for (int r = 0; r < 7; r++)
        {
            for (int c = 0; c < 13; c++)
                lines.Append(grid[r, c]);
            lines.AppendLine();
        }
        lines.AppendLine($"O=wood  #=hinge  >=fall dir");

        return lines.ToString();
    }

    // ——— Input & Update ———

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                _lmbHeld = mb.Pressed;
                if (mb.Pressed && _playerMode && !_isSwinging)
                    StartAxeSwing();
            }
            if (mb.ButtonIndex == MouseButton.Right) _rmbHeld = mb.Pressed;
            Input.MouseMode = _rmbHeld ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            if (mb.ButtonIndex == MouseButton.WheelUp) _camDist = Mathf.Max(2f, _camDist * 0.9f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _camDist = Mathf.Min(100f, _camDist * 1.1f);
            UpdateCamera();
        }
        else if (ev is InputEventMouseMotion mm && _rmbHeld)
        {
            _camYaw -= mm.Relative.X * 0.3f;
            _camPitch = Mathf.Clamp(_camPitch - mm.Relative.Y * 0.3f, -89f, 89f);
            UpdateCamera();
        }

        if (ev is InputEventKey k && k.Pressed)
        {
            switch (k.Keycode)
            {
                case Key.Space:
                    _simRunning = !_simRunning;
                    break;
                case Key.R:
                    ResetAll();
                    GD.Print("Tree reset");
                    break;
                case Key.Key1: _vizMode = 0; RebuildTrunkMesh(); break;
                case Key.Key2: _vizMode = 1; RebuildTrunkMesh(); break;
                case Key.Key3: _vizMode = 2; RebuildTrunkMesh(); break;
                case Key.Key4: _vizMode = 3; RebuildTrunkMesh(); break;
                case Key.Key5: _vizMode = 4; RebuildTrunkMesh(); break;
                case Key.Key6: _vizMode = 5; RebuildTrunkMesh(); break;
            }
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Camera movement
        float yawRad = Mathf.DegToRad(_camYaw);
        var camFwd = new Vector3(-Mathf.Sin(yawRad), 0, -Mathf.Cos(yawRad));
        var camRight = new Vector3(camFwd.Z, 0, -camFwd.X);
        float speed = 5f + _camDist * 0.2f;

        var move = Vector3.Zero;
        bool bothMouse = _lmbHeld && _rmbHeld;
        if (bothMouse) move += camFwd;
        if (Input.IsActionPressed("move_forward")) move += camFwd;
        if (Input.IsActionPressed("move_back")) move -= camFwd;
        if (Input.IsActionPressed("move_right")) move += camRight;
        if (Input.IsActionPressed("move_left")) move -= camRight;

        if (_playerMode)
        {
            // Move player, camera follows
            if (move.LengthSquared() > 0.01f)
            {
                _playerPos += move.Normalized() * _playerMoveSpeed * dt;
                _playerPos.Y = PlayerRadius; // stay on ground
            }
            // Face toward tree (origin)
            var toTree = new Vector3(-_playerPos.X, 0, -_playerPos.Z);
            if (toTree.LengthSquared() > 0.01f)
            {
                _playerFacing = Mathf.Atan2(toTree.X, toTree.Z);
                _playerOrb.Rotation = new Vector3(0, _playerFacing, 0);
            }
            _playerOrb.Position = _playerPos;
            _cameraPivot.Position = _playerPos + new Vector3(0, 1f, 0);
        }
        else
        {
            // Legacy orbit camera
            if (move.LengthSquared() > 0.01f)
                _focusPos += move.Normalized() * speed * dt;
            if (Input.IsKeyPressed(Key.E)) _focusPos.Y += speed * dt;
            if (Input.IsKeyPressed(Key.Q)) _focusPos.Y -= speed * dt;
            _cameraPivot.Position = _focusPos;
        }

        // Run simulation
        if (_simRunning)
        {
            _sim.Advance(dt * _timeScale);
            ApplyDynamicsToScene();

            if (_sim.Dynamics.Phase == FallPhase.Ground)
                _simRunning = false;
        }

        // Update HUD
        var dyn = _sim.Dynamics;
        var hinge = _sim.Hinge;
        string phaseName = dyn.Phase.ToString();
        string speciesName = _sim.Config.Species.ToString();
        string[] vizNames = { "Shaded", "X-Section", "Force", "Stress", "Trajectory", "Side" };
        string vizName = _vizMode < vizNames.Length ? vizNames[_vizMode] : "?";

        float tipping = _sim.HasNotch ? _sim.ComputeTippingMoment() : 0;
        float resisting = _sim.ComputeResistingMoment();
        bool barberChair = _sim.CheckBarberChair();

        _hud.Text = $"Phase: {phaseName}    Tilt: {dyn.FallTiltDeg:F1}deg -> {dyn.FallBearing:F0}deg\n" +
                    $"Hinge: {hinge.WidthInches:F1}\"w x {hinge.DepthInches:F1}\"d ({(1f - hinge.FiberStress) * 100:F0}% strength)\n" +
                    $"Torque: {tipping:F0} tip / {resisting:F0} resist\n" +
                    (barberChair ? "!! BARBER CHAIR RISK !!\n" : "") +
                    $"Wind: {_windStrength:F0} mph {_windDir:F0}deg    Slope: {_slopeAngle:F0}deg\n" +
                    $"DBH: {_dbh:F0}\" {speciesName}    Lean: {_naturalLeanDeg:F1}deg {_naturalLeanBearing:F0}deg\n" +
                    $"Viz: {vizName}\n" +
                    $"[Space] {(_simRunning ? "Pause" : "Start")}  [R] Reset  [Tab] Tuning";

        if (_playerMode)
        {
            float angleDeg = MathF.Atan2(_playerPos.Z, _playerPos.X) * 180f / MathF.PI;
            if (angleDeg < 0) angleDeg += 360f;
            float dist = new Vector2(_playerPos.X, _playerPos.Z).Length();
            _hud.Text += $"\n--- Player Axe ---\n" +
                         $"Dist: {dist:F1}  Angle: {angleDeg:F0}deg\n" +
                         $"Head: {_lastStrikeResult.HeadVelocityMs:F1} m/s  KE: {_lastStrikeResult.KineticEnergyJ:F0}J  FC: {_lastStrikeResult.CentripetalForceN:F0}N\n" +
                         $"Cut: {_lastStrikeResult.PenetrationM * 39.37f:F2}\" into {_sim.Config.Species}\n" +
                         $"Wire: 24 bytes (notch+back+hinge+tilt)\n" +
                         $"[LMB] Swing  [WASD] Move";
        }

        // Cross-section display
        _crossSectionHud.Text = BuildCrossSectionText();
    }

    private void UpdateCamera()
    {
        _cameraPivot.RotationDegrees = new Vector3(_camPitch, _camYaw, 0);
        _camera.Position = new Vector3(0, 0, _camDist);
    }
}

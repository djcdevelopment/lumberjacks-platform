using Godot;
using System;
using System.Collections.Generic;
using NumericsVector3 = System.Numerics.Vector3;

namespace CommunitySurvival.Lab;

public partial class AxeSwingLab
{
    private const float BiteTrunkRadiusMeters = 0.25f;
    private const float BiteTrunkHeightMeters = 2.4f;
    private const float BiteBandWidthMeters = 0.10f;
    private const float BiteEntryDurationSeconds = 0.16f;
    private const float BiteInspectionDurationSeconds = 0.55f;
    private const float BiteWithdrawalDurationSeconds = 0.30f;
    private const float BiteRecoveryDurationSeconds = 0.42f;

    private AxeBiteIntent _biteIntent = AxeBiteIntent.Nominal;
    private float _biteResistanceJoulesPerMeter = 2000f;
    private WoodCutState _biteFreshState;
    private WoodCutState _biteDisplayedState;
    private AxeBiteResult _biteResult;
    private AxeRetentionResult _biteRetention;
    private int _biteTrialIndex = -1;
    private BitePlaybackPhase _bitePhase;
    private float _bitePhaseElapsed;
    private MeshInstance3D _biteTrunk;
    private MeshInstance3D _biteMouth;
    private MeshInstance3D _biteKerfDiagnostic;
    private MeshInstance3D _biteCutMarkDiagnostic;
    private MeshInstance3D _biteGrowthRingsDiagnostic;
    private MeshInstance3D _biteCutEndpointMarker;
    private MeshInstance3D _bitePathDiagnostic;
    private MeshInstance3D _biteAimDiagnostic;
    private MeshInstance3D _biteStopMarker;
    private Label _bitePresetStatus;
    private TuningSlider _biteEffortSlider;
    private TuningSlider _biteAimSlider;
    private TuningSlider _biteResistanceSlider;
    private readonly List<Camera3D> _biteCadCameras = new();

    private void BuildBiteLab()
    {
        EnsureBiteWindowSize();
        _camera.CullMask = 1u;
        _camera.LookAtFromPosition(
            new Vector3(3.5f, 3.2f, 4.8f),
            new Vector3(0.15f, 1.15f, 0f));

        _biteFreshState = WoodCutState.Fresh();
        _biteDisplayedState = _biteFreshState;
        RecalculateBite();
        BuildBiteTrunk();
        BuildBiteDiagnostics();
        BuildBiteSheet();
        BuildBiteTuning();
        RebuildBiteStateVisuals();
        UpdateBitePresetStatus();
    }

    private static void EnsureBiteWindowSize()
    {
        var screen = DisplayServer.WindowGetCurrentScreen();
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        if (usable.Size.X < 800 || usable.Size.Y < 600) return;
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
    }

    private void BuildBiteTrunk()
    {
        var frame = _biteResult.Frame;
        var surface = _shoulderPosition + ToGodot(frame.SurfacePoint);
        var inward = ToGodot(frame.InwardNormal);
        var horizontalCenter = surface + inward * BiteTrunkRadiusMeters;

        _biteTrunk = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = BiteTrunkRadiusMeters,
                BottomRadius = BiteTrunkRadiusMeters * 1.035f,
                Height = BiteTrunkHeightMeters,
                RadialSegments = 64,
                Rings = 8,
            },
            Position = new Vector3(horizontalCenter.X, BiteTrunkHeightMeters * 0.5f, horizontalCenter.Z),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.34f, 0.22f, 0.105f),
                Roughness = 0.92f,
            },
        };
        AddChild(_biteTrunk);

        _biteMouth = new MeshInstance3D
        {
            MaterialOverride = Material(new Color(0.075f, 0.035f, 0.018f), unshaded: true),
            Visible = false,
        };
        AddChild(_biteMouth);
    }

    private void BuildBiteDiagnostics()
    {
        _biteKerfDiagnostic = DiagnosticMesh();
        _biteCutMarkDiagnostic = DiagnosticMesh();
        _biteGrowthRingsDiagnostic = DiagnosticMesh();
        _bitePathDiagnostic = DiagnosticMesh();
        _biteAimDiagnostic = DiagnosticMesh();
        _biteStopMarker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.026f, Height = 0.052f },
            MaterialOverride = DiagnosticMaterial(new Color(1f, 0.24f, 0.12f, 0.95f)),
        };
        DiagnosticOnly(_biteStopMarker);
        _biteCutEndpointMarker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.018f, Height = 0.036f },
            MaterialOverride = DiagnosticMaterial(new Color(1f, 0.93f, 0.62f, 1f)),
            Visible = false,
        };
        DiagnosticOnly(_biteCutEndpointMarker);

        AddChild(_biteKerfDiagnostic);
        AddChild(_biteCutMarkDiagnostic);
        AddChild(_biteGrowthRingsDiagnostic);
        AddChild(_bitePathDiagnostic);
        AddChild(_biteAimDiagnostic);
        AddChild(_biteStopMarker);
        AddChild(_biteCutEndpointMarker);
        _biteGrowthRingsDiagnostic.Mesh = BiteGrowthRingMesh(_biteResult.Frame);
        _biteGrowthRingsDiagnostic.Position = _shoulderPosition;
        RebuildBitePredictionVisuals();
    }

    private void BuildBiteSheet()
    {
        var canvas = new CanvasLayer { Layer = 3 };
        AddChild(canvas);
        var sheet = new VBoxContainer
        {
            AnchorLeft = 0.70f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 4f,
            OffsetTop = 12f,
            OffsetRight = -12f,
            OffsetBottom = -12f,
        };
        sheet.AddThemeConstantOverride("separation", 6);
        canvas.AddChild(sheet);

        var controls = new PanelContainer { CustomMinimumSize = new Vector2(0f, 140f) };
        controls.AddThemeStyleboxOverride("panel", PanelStyle(new Color(0.035f, 0.045f, 0.04f, 0.94f)));
        sheet.AddChild(controls);
        var controlBox = new VBoxContainer();
        controlBox.AddThemeConstantOverride("separation", 3);
        controls.AddChild(controlBox);

        _bitePresetStatus = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _bitePresetStatus.AddThemeFontSizeOverride("font_size", 13);
        _bitePresetStatus.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.82f));
        controlBox.AddChild(_bitePresetStatus);

        var effortRow = new HBoxContainer();
        effortRow.AddThemeConstantOverride("separation", 4);
        AddPresetButton(effortRow, "EFFORT LOW", () => SetBiteEffort(0.75f));
        AddPresetButton(effortRow, "NOMINAL", () => SetBiteEffort(1f));
        AddPresetButton(effortRow, "HARD", () => SetBiteEffort(1.25f));
        controlBox.AddChild(effortRow);

        var aimRow = new HBoxContainer();
        aimRow.AddThemeConstantOverride("separation", 4);
        AddPresetButton(aimRow, "AIM 3 cm", () => SetBiteAim(0.03f));
        AddPresetButton(aimRow, "10 cm", () => SetBiteAim(0.10f));
        AddPresetButton(aimRow, "18 cm", () => SetBiteAim(0.18f));
        controlBox.AddChild(aimRow);

        AddCadPane(sheet, "ELEVATION — SWING PLANE", BiteCadView.Elevation);
        AddCadPane(sheet, "PLAN — LOG AXIS", BiteCadView.Plan);
        AddCadPane(sheet, "END — BARK FACE", BiteCadView.End);
        UpdateBiteCadCameras();
        Callable.From(() => GD.Print(
            $"AxeBiteLab: CAD sheet visible={sheet.Visible} position={sheet.GlobalPosition} size={sheet.Size} " +
            $"viewport={GetViewport().GetVisibleRect().Size} window={DisplayServer.WindowGetSize()} " +
            $"screenDpi={DisplayServer.ScreenGetDpi(DisplayServer.WindowGetCurrentScreen())}"))
            .CallDeferred();
    }

    private void BuildBiteTuning()
    {
        var panel = new TuningPanel { OpenOnRight = true };
        AddChild(panel);
        var intent = panel.AddSection("Bite intent");
        _biteEffortSlider = intent.AddSlider(
            "Effort scale", 0.6f, 1.4f, _biteIntent.EffortMultiplier, value =>
            {
                _biteIntent = _biteIntent with { EffortMultiplier = value };
                RecalculateBiteAndReset();
            });
        _biteAimSlider = intent.AddSlider(
            "Aim through m", 0.02f, 0.20f, _biteIntent.AimThroughMeters, value =>
            {
                _biteIntent = _biteIntent with { AimThroughMeters = value };
                RecalculateBiteAndReset();
            });
        var material = panel.AddSection("Uniform wood");
        _biteResistanceSlider = material.AddSlider(
            "Resistance J/m", 1200f, 3200f, _biteResistanceJoulesPerMeter, value =>
            {
                _biteResistanceJoulesPerMeter = value;
                RecalculateBiteAndReset();
            });
        material.AddButton("Restore bite defaults", ResetBiteLab);
    }

    private void AddCadPane(VBoxContainer sheet, string title, BiteCadView view)
    {
        var panel = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
        };
        panel.AddThemeStyleboxOverride("panel", PanelStyle(new Color(0.025f, 0.032f, 0.03f, 0.96f)));
        sheet.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 1);
        panel.AddChild(box);
        var label = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", new Color(0.65f, 0.86f, 0.82f));
        box.AddChild(label);

        var container = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        box.AddChild(container);
        var viewport = new SubViewport
        {
            Size = new Vector2I(480, 220),
            World3D = GetViewport().World3D,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
        };
        container.AddChild(viewport);
        var camera = new Camera3D
        {
            Current = true,
            Projection = Camera3D.ProjectionType.Orthogonal,
            CullMask = 3u,
        };
        camera.SetMeta("bite_cad_view", (int)view);
        viewport.AddChild(camera);
        _biteCadCameras.Add(camera);
    }

    private void UpdateBiteCadCameras()
    {
        if (_biteResult is null) return;
        var frame = _biteResult.Frame;
        var surface = _shoulderPosition + ToGodot(frame.SurfacePoint);
        var inward = ToGodot(frame.InwardNormal);
        var outward = ToGodot(frame.OutwardNormal);
        var tangent = ToGodot(frame.Tangent);
        var target = surface + inward * 0.10f;

        foreach (var camera in _biteCadCameras)
        {
            var view = (BiteCadView)(int)camera.GetMeta("bite_cad_view");
            switch (view)
            {
                case BiteCadView.Elevation:
                    camera.Size = 1.25f;
                    camera.LookAtFromPosition(target + tangent * 3f, target, Vector3.Up);
                    break;
                case BiteCadView.Plan:
                    camera.Size = 1.15f;
                    camera.LookAtFromPosition(target + Vector3.Up * 3f, target, outward);
                    break;
                case BiteCadView.End:
                    camera.Size = 1.05f;
                    camera.LookAtFromPosition(surface + outward * 3f, target, Vector3.Up);
                    break;
            }
        }
    }

    private void AddPresetButton(HBoxContainer row, string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        button.AddThemeFontSizeOverride("font_size", 10);
        button.Pressed += action;
        row.AddChild(button);
    }

    private void SetBiteEffort(float effort)
    {
        _biteIntent = _biteIntent with { EffortMultiplier = effort };
        _biteEffortSlider?.SetValue(effort);
        RecalculateBiteAndReplay();
    }

    private void SetBiteAim(float aimThrough)
    {
        _biteIntent = _biteIntent with { AimThroughMeters = aimThrough };
        _biteAimSlider?.SetValue(aimThrough);
        RecalculateBiteAndReplay();
    }

    private void RecalculateBiteAndReplay()
    {
        ReplayBite();
    }

    private void RecalculateBiteAndReset()
    {
        _biteTrialIndex = -1;
        RecalculateBite();
        SetBiteReady(clearWood: true);
    }

    private void RecalculateBite()
    {
        var calibration = new AxeBiteCalibration
        {
            UniformResistanceJoulesPerMeter = _biteResistanceJoulesPerMeter,
        };
        _biteResult = AxeBiteSolver.Solve(_profile, _biteFreshState, _biteIntent, calibration);
        var displayedTrial = Math.Max(0, _biteTrialIndex);
        _biteRetention = AxeRetentionModel.Evaluate(
            _biteResult,
            AxeRetentionModel.DeterministicRoll(displayedTrial),
            AxeRetentionContext.Lab04);
        RebuildBitePredictionVisuals();
        UpdateBitePresetStatus();
        UpdateBiteCadCameras();
    }

    private void ReplayBite()
    {
        _biteTrialIndex++;
        RecalculateBite();
        _biteDisplayedState = _biteFreshState;
        _bitePhase = BitePlaybackPhase.FreeSwing;
        _bitePhaseElapsed = 0f;
        _elapsed = 0f;
        RebuildBiteStateVisuals();
        ApplyPose(_profile.Sample(0f));
    }

    private void ResetBiteLab()
    {
        _biteIntent = AxeBiteIntent.Nominal;
        _biteResistanceJoulesPerMeter = 2000f;
        _biteTrialIndex = -1;
        _biteFreshState = WoodCutState.Fresh();
        _biteEffortSlider?.SetValue(_biteIntent.EffortMultiplier);
        _biteAimSlider?.SetValue(_biteIntent.AimThroughMeters);
        _biteResistanceSlider?.SetValue(_biteResistanceJoulesPerMeter);
        RecalculateBite();
        SetBiteReady(clearWood: true);
    }

    private void SetBiteReady(bool clearWood)
    {
        if (clearWood) _biteDisplayedState = _biteFreshState;
        _bitePhase = BitePlaybackPhase.Ready;
        _bitePhaseElapsed = 0f;
        _elapsed = -1f;
        RebuildBiteStateVisuals();
        ApplyPose(_profile.Sample(0f));
    }

    private void ProcessBite(float delta)
    {
        switch (_bitePhase)
        {
            case BitePlaybackPhase.FreeSwing:
                var rate = _elapsed >= _profile.WindupSeconds
                    ? _biteIntent.EffortMultiplier
                    : 1f;
                _elapsed += delta * rate;
                if (_elapsed >= _biteResult.ContactTrajectoryTimeSeconds)
                {
                    _elapsed = _biteResult.ContactTrajectoryTimeSeconds;
                    _bitePhase = BitePlaybackPhase.Penetrating;
                    _bitePhaseElapsed = 0f;
                }
                ApplyPose(_profile.Sample(_elapsed));
                break;

            case BitePlaybackPhase.Penetrating:
                _bitePhaseElapsed += delta;
                var entry = Math.Clamp(_bitePhaseElapsed / BiteEntryDurationSeconds, 0f, 1f);
                var entryEase = 1f - MathF.Pow(1f - entry, 3f);
                _elapsed = Mathf.Lerp(
                    _biteResult.ContactTrajectoryTimeSeconds,
                    _biteResult.StopTrajectoryTimeSeconds,
                    entryEase);
                ApplyPose(_profile.Sample(_elapsed));
                if (entry >= 1f)
                {
                    _elapsed = _biteResult.StopTrajectoryTimeSeconds;
                    _biteDisplayedState = _biteResult.FinalState;
                    _bitePhase = _biteRetention.IsEmbedded
                        ? BitePlaybackPhase.Embedded
                        : BitePlaybackPhase.Inspecting;
                    _bitePhaseElapsed = 0f;
                    RebuildBiteStateVisuals();
                    ApplyPose(_biteResult.StopSample.Pose);
                }
                break;

            case BitePlaybackPhase.Inspecting:
                _bitePhaseElapsed += delta;
                ApplyPose(_biteResult.StopSample.Pose);
                if (_bitePhaseElapsed >= BiteInspectionDurationSeconds)
                {
                    _bitePhase = BitePlaybackPhase.Withdrawing;
                    _bitePhaseElapsed = 0f;
                }
                break;

            case BitePlaybackPhase.Withdrawing:
                _bitePhaseElapsed += delta;
                var withdrawal = Math.Clamp(_bitePhaseElapsed / BiteWithdrawalDurationSeconds, 0f, 1f);
                var withdrawalEase = SmoothStepBite(withdrawal);
                _elapsed = Mathf.Lerp(
                    _biteResult.StopTrajectoryTimeSeconds,
                    _biteResult.ContactTrajectoryTimeSeconds,
                    withdrawalEase);
                ApplyPose(_profile.Sample(_elapsed));
                if (withdrawal >= 1f)
                {
                    _bitePhase = BitePlaybackPhase.Recovering;
                    _bitePhaseElapsed = 0f;
                }
                break;

            case BitePlaybackPhase.Recovering:
                _bitePhaseElapsed += delta;
                var recovery = Math.Clamp(_bitePhaseElapsed / BiteRecoveryDurationSeconds, 0f, 1f);
                ApplyPose(InterpolateBitePose(
                    _biteResult.ContactSample.Pose,
                    _profile.Sample(0f),
                    SmoothStepBite(recovery),
                    AxeSwingPhase.Recovery));
                if (recovery >= 1f)
                    SetBiteReady(clearWood: false);
                break;
        }
    }

    private void BeginBiteWithdrawal()
    {
        if (_bitePhase != BitePlaybackPhase.Embedded) return;
        _bitePhase = BitePlaybackPhase.Withdrawing;
        _bitePhaseElapsed = 0f;
    }

    private void UpdateBiteVisuals(AxeSwingPose pose)
    {
        _ = pose;
        if (_biteStopMarker is not null)
            _biteStopMarker.Position = _shoulderPosition + ToGodot(_biteResult.StopSample.CuttingCenter);
    }

    private void UpdateBiteMainHud(AxeSwingPose pose, string error)
    {
        var phase = _bitePhase switch
        {
            BitePlaybackPhase.FreeSwing => pose.Phase.ToString(),
            BitePlaybackPhase.Penetrating => "Biting — resistance deceleration",
            BitePlaybackPhase.Inspecting => "Free head — inspection before auto recovery",
            BitePlaybackPhase.Embedded => "RETAINED — press F to free axe",
            BitePlaybackPhase.Withdrawing => _biteRetention.IsEmbedded
                ? "Freeing retained head along entry path"
                : "Free head — automatic withdrawal",
            BitePlaybackPhase.Recovering => "Recovery",
            _ => _biteDisplayedState.CutMarkCount > 0
                ? "Ready — retained bite"
                : "Ready — fresh wood",
        };
        var stop = _biteResult.StopReason.ToString().ToUpperInvariant();
        var rings = GrowthRingPattern.ConcentricRadii(BiteTrunkRadiusMeters);
        var ringsCrossed = GrowthRingPattern.CountCrossed(
            BiteTrunkRadiusMeters,
            _biteDisplayedState.CutMarkCount > 0
                ? _biteDisplayedState.CutMarks[^1].PenetrationMeters
                : 0f,
            rings);
        _hud.Text =
            "AXE SWING LAB 04 — ONE FRESH BITE\n" +
            $"Accepted motion  {AxeSwingProfile.AcceptedCalibrationId}\n" +
            "Uniform upright trunk / 5 mm cut field\n\n" +
            $"Phase        {phase}\n" +
            $"Effort       {_biteIntent.EffortMultiplier:F2}x\n" +
            $"Arrival      {_biteResult.ArrivalSpeedMetersPerSecond:F2} m/s\n" +
            $"Aim through  {_biteIntent.AimThroughMeters * 100f:F1} cm\n" +
            $"Actual bite  {_biteResult.PenetrationMeters * 100f:F1} cm\n" +
            $"Stop         {stop}\n\n" +
            $"Energy       {_biteResult.UsedEnergyJoules:F1} / {_biteResult.DeliveredEnergyJoules:F1} J\n" +
            $"Retention    {_biteRetention.Probability * 100f:F0}% / roll {_biteRetention.Roll * 100f:F0}%" +
            $" → {(_biteRetention.IsEmbedded ? "EMBEDDED" : "FREE")}\n" +
            $"Kerf cells   {_biteDisplayedState.KerfCellCount}\n" +
            $"Cut marks    {_biteDisplayedState.CutMarkCount}\n" +
            $"Rings crossed {ringsCrossed} / {rings.Length}\n" +
            $"Released     {_biteDisplayedState.ReleasedCellCount} (disabled in Lab 04)\n\n" +
            "TEAL = accepted free path\n" +
            "GOLD = aim-through plane\n" +
            "RED = solved embedded stop\n" +
            "PALE = retained cut line + endpoint\n" +
            "RINGS = center-dense depth scale\n\n" +
            "[Space/LMB] fresh wood + replay\n" +
            "[F] free axe only when retained\n" +
            "[R] bite defaults   [Tab] fine tune\n\n" +
            "NO CHIP / FALL / DAMAGE / NETWORK\n" +
            (error is null ? string.Empty : $"PROFILE ERROR: {error}");
    }

    private void UpdateBitePresetStatus()
    {
        if (_bitePresetStatus is null || _biteResult is null) return;
        _bitePresetStatus.Text =
            $"EFFORT {_biteIntent.EffortMultiplier:F2}x   •   AIM {_biteIntent.AimThroughMeters * 100f:F0} cm\n" +
            $"PREDICTED {_biteResult.PenetrationMeters * 100f:F1} cm   •   {_biteResult.StopReason.ToString().ToUpperInvariant()}\n" +
            $"RETENTION {_biteRetention.Probability * 100f:F0}% / ROLL {_biteRetention.Roll * 100f:F0}%" +
            $"   •   {(_biteRetention.IsEmbedded ? "EMBEDDED" : "FREE")}";
    }

    private void RebuildBitePredictionVisuals()
    {
        if (_bitePathDiagnostic is null || _biteResult is null) return;
        _bitePathDiagnostic.Mesh = BitePathMesh(_biteResult);
        _bitePathDiagnostic.Position = _shoulderPosition;
        _biteAimDiagnostic.Mesh = BiteAimMesh(_biteResult);
        _biteAimDiagnostic.Position = _shoulderPosition;
        _biteStopMarker.Position = _shoulderPosition + ToGodot(_biteResult.StopSample.CuttingCenter);
        if (_biteDisplayedState is not null)
            RebuildBiteStateVisuals();
    }

    private void RebuildBiteStateVisuals()
    {
        if (_biteKerfDiagnostic is null || _biteDisplayedState is null) return;
        _biteKerfDiagnostic.Mesh = BiteKerfMesh(_biteDisplayedState, _biteResult.Frame);
        _biteKerfDiagnostic.Position = _shoulderPosition;
        _biteCutMarkDiagnostic.Mesh = BiteCutMarkMesh(_biteDisplayedState, _biteResult.Frame);
        _biteCutMarkDiagnostic.Position = _shoulderPosition;
        _biteCutEndpointMarker.Visible = _biteDisplayedState.CutMarkCount > 0;
        if (_biteCutEndpointMarker.Visible)
        {
            var mark = _biteDisplayedState.CutMarks[^1];
            var deepest = DeepestPoint(mark);
            _biteCutEndpointMarker.Position = _shoulderPosition +
                BitePointToGodot(_biteResult.Frame, deepest);
        }
        _biteMouth.Mesh = BiteMouthMesh(_biteResult);
        _biteMouth.Position = _shoulderPosition;
        _biteMouth.Visible = _biteDisplayedState.KerfCellCount > 0;
    }

    private static MeshInstance3D DiagnosticMesh()
    {
        var mesh = new MeshInstance3D();
        DiagnosticOnly(mesh);
        return mesh;
    }

    private static void DiagnosticOnly(VisualInstance3D node)
    {
        node.SetLayerMaskValue(1, false);
        node.SetLayerMaskValue(2, true);
    }

    private static StandardMaterial3D DiagnosticMaterial(Color color) => new()
    {
        AlbedoColor = color,
        EmissionEnabled = true,
        Emission = new Color(color.R, color.G, color.B) * 0.45f,
        Transparency = color.A < 0.999f
            ? BaseMaterial3D.TransparencyEnum.Alpha
            : BaseMaterial3D.TransparencyEnum.Disabled,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        NoDepthTest = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static StyleBoxFlat PanelStyle(Color color) => new()
    {
        BgColor = color,
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        BorderColor = new Color(0.25f, 0.34f, 0.31f, 0.9f),
        ContentMarginLeft = 6f,
        ContentMarginRight = 6f,
        ContentMarginTop = 4f,
        ContentMarginBottom = 4f,
    };

    private static ImmediateMesh BitePathMesh(AxeBiteResult result)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(
            Mesh.PrimitiveType.LineStrip,
            DiagnosticMaterial(new Color(0.3f, 0.95f, 0.86f, 0.95f)));
        foreach (var point in result.Path)
            mesh.SurfaceAddVertex(ToGodot(point.CuttingCenter));
        mesh.SurfaceEnd();
        return mesh;
    }

    private static ImmediateMesh BiteAimMesh(AxeBiteResult result)
    {
        var frame = result.Frame;
        var center = ToGodot(frame.SurfacePoint + frame.InwardNormal * result.Intent.AimThroughMeters);
        var tangent = ToGodot(frame.Tangent) * (BiteBandWidthMeters * 0.8f);
        var vertical = Vector3.Up * 0.26f;
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(
            Mesh.PrimitiveType.Lines,
            DiagnosticMaterial(new Color(1f, 0.76f, 0.18f, 0.95f)));
        AddLine(mesh, center - tangent - vertical, center + tangent - vertical);
        AddLine(mesh, center + tangent - vertical, center + tangent + vertical);
        AddLine(mesh, center + tangent + vertical, center - tangent + vertical);
        AddLine(mesh, center - tangent + vertical, center - tangent - vertical);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static ImmediateMesh BiteGrowthRingMesh(WoodCutFrame frame)
    {
        const int segments = 96;
        var mesh = new ImmediateMesh();
        var center = ToGodot(
            frame.SurfacePoint + frame.InwardNormal * BiteTrunkRadiusMeters);
        var radial = ToGodot(frame.InwardNormal);
        var tangent = ToGodot(frame.Tangent);
        var rings = GrowthRingPattern.ConcentricRadii(BiteTrunkRadiusMeters);
        var material = DiagnosticMaterial(new Color(0.88f, 0.66f, 0.30f, 0.52f));

        foreach (var radius in rings)
        {
            mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, material);
            for (var segment = 0; segment <= segments; segment++)
            {
                var angle = Mathf.Tau * segment / segments;
                mesh.SurfaceAddVertex(center +
                    radial * (MathF.Cos(angle) * radius) +
                    tangent * (MathF.Sin(angle) * radius));
            }
            mesh.SurfaceEnd();
        }
        return mesh;
    }

    private static ArrayMesh BiteCutMarkMesh(WoodCutState state, WoodCutFrame frame)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var tangent = ToGodot(frame.Tangent).Normalized();
        const float halfLineWidth = 0.004f;

        foreach (var mark in state.CutMarks)
        {
            var halfBand = tangent * (mark.CutWidthMeters * 0.52f);
            for (var index = 1; index < mark.Points.Length; index++)
            {
                var start = BitePointToGodot(frame, mark.Points[index - 1]);
                var end = BitePointToGodot(frame, mark.Points[index]);
                var direction = end - start;
                if (direction.LengthSquared() < 0.000000001f) continue;
                direction = direction.Normalized();
                var halfThickness = tangent.Cross(direction).Normalized() * halfLineWidth;

                var startA = start - halfBand - halfThickness;
                var startB = start - halfBand + halfThickness;
                var startC = start + halfBand + halfThickness;
                var startD = start + halfBand - halfThickness;
                var endA = end - halfBand - halfThickness;
                var endB = end - halfBand + halfThickness;
                var endC = end + halfBand + halfThickness;
                var endD = end + halfBand - halfThickness;

                AddQuad(tool, startA, endA, endB, startB);
                AddQuad(tool, startD, startC, endC, endD);
                AddQuad(tool, startA, startD, endD, endA);
                AddQuad(tool, startB, endB, endC, startC);
            }
        }

        tool.GenerateNormals();
        var mesh = tool.Commit();
        if (mesh.GetSurfaceCount() > 0)
            mesh.SurfaceSetMaterial(0, DiagnosticMaterial(new Color(1f, 0.93f, 0.62f, 0.96f)));
        return mesh;
    }

    private static Vector3 BitePointToGodot(WoodCutFrame frame, AxeCutMarkPoint point) =>
        ToGodot(
            frame.SurfacePoint +
            frame.InwardNormal * point.DepthMeters +
            frame.Vertical * point.VerticalMeters);

    private static AxeCutMarkPoint DeepestPoint(AxeCutMark mark)
    {
        var deepest = mark.Points[0];
        foreach (var point in mark.Points)
            if (point.DepthMeters > deepest.DepthMeters) deepest = point;
        return deepest;
    }

    private static ArrayMesh BiteKerfMesh(WoodCutState state, WoodCutFrame frame)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var inward = ToGodot(frame.InwardNormal);
        var vertical = ToGodot(frame.Vertical);
        var tangent = ToGodot(frame.Tangent);
        var origin = ToGodot(frame.SurfacePoint);
        var halfBand = tangent * (BiteBandWidthMeters * 0.5f);

        for (var height = 0; height < state.HeightCells; height++)
        {
            var lowVertical = state.MinimumVerticalMeters + height * state.CellSizeMeters;
            var highVertical = lowVertical + state.CellSizeMeters;
            for (var depth = 0; depth < state.DepthCells; depth++)
            {
                if (state.Cell(depth, height) != WoodCellState.Kerf) continue;
                var nearDepth = depth * state.CellSizeMeters;
                var farDepth = nearDepth + state.CellSizeMeters;
                var nearLow = origin + inward * nearDepth + vertical * lowVertical;
                var nearHigh = origin + inward * nearDepth + vertical * highVertical;
                var farLow = origin + inward * farDepth + vertical * lowVertical;
                var farHigh = origin + inward * farDepth + vertical * highVertical;

                AddQuad(tool, nearLow - halfBand, farLow - halfBand, farHigh - halfBand, nearHigh - halfBand);
                AddQuad(tool, nearLow + halfBand, nearHigh + halfBand, farHigh + halfBand, farLow + halfBand);
                AddQuad(tool, nearLow - halfBand, nearHigh - halfBand, nearHigh + halfBand, nearLow + halfBand);
                AddQuad(tool, farLow - halfBand, farLow + halfBand, farHigh + halfBand, farHigh - halfBand);
            }
        }

        tool.GenerateNormals();
        var mesh = tool.Commit();
        if (mesh.GetSurfaceCount() > 0)
            mesh.SurfaceSetMaterial(0, DiagnosticMaterial(new Color(0.92f, 0.78f, 0.48f, 0.78f)));
        return mesh;
    }

    private static ImmediateMesh BiteMouthMesh(AxeBiteResult result)
    {
        var frame = result.Frame;
        var contact = ToGodot(result.ContactSample.CuttingCenter);
        var stop = ToGodot(result.StopSample.CuttingCenter);
        var path = stop - contact;
        var pathLength = MathF.Max(path.Length(), 0.001f);
        var pathDirection = path / pathLength;
        var tangent = ToGodot(frame.Tangent).Normalized();
        var side = tangent.Cross(pathDirection).Normalized();
        var wedgeHalfAngle = MathF.Atan(
            MathF.Max(0f, 0.085f - 0.008f) * 0.5f / 0.27f);
        var mouthHalfWidth = 0.004f + pathLength * MathF.Tan(wedgeHalfAngle);
        var outwardOffset = ToGodot(frame.OutwardNormal) * 0.004f;
        var halfBand = tangent * (BiteBandWidthMeters * 0.5f);
        var a = contact - side * mouthHalfWidth - halfBand + outwardOffset;
        var b = contact + side * mouthHalfWidth - halfBand + outwardOffset;
        var c = contact + side * mouthHalfWidth + halfBand + outwardOffset;
        var d = contact - side * mouthHalfWidth + halfBand + outwardOffset;

        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, Material(new Color(0.065f, 0.028f, 0.012f), unshaded: true));
        mesh.SurfaceAddVertex(a); mesh.SurfaceAddVertex(b); mesh.SurfaceAddVertex(c);
        mesh.SurfaceAddVertex(a); mesh.SurfaceAddVertex(c); mesh.SurfaceAddVertex(d);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static AxeSwingPose InterpolateBitePose(
        AxeSwingPose from,
        AxeSwingPose to,
        float amount,
        AxeSwingPhase phase) => new(
            phase,
            Mathf.Lerp(from.AngleDegrees, to.AngleDegrees, amount),
            Mathf.Lerp(from.ShoulderAngleDegrees, to.ShoulderAngleDegrees, amount),
            Mathf.Lerp(from.ElbowAngleDegrees, to.ElbowAngleDegrees, amount),
            0f,
            amount < 1f);

    private static float SmoothStepBite(float value) => value * value * (3f - 2f * value);

    private enum BitePlaybackPhase
    {
        Ready,
        FreeSwing,
        Penetrating,
        Inspecting,
        Embedded,
        Withdrawing,
        Recovering,
    }

    private enum BiteCadView
    {
        Elevation,
        Plan,
        End,
    }
}

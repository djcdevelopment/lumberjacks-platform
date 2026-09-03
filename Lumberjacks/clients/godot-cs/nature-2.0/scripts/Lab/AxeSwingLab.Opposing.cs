using Godot;
using System;
using System.Collections.Generic;
using NumericsVector3 = System.Numerics.Vector3;

namespace CommunitySurvival.Lab;

/// <summary>
/// Lab 05 is a human gate, not a wood simulation. It registers one locked
/// downward stroke and one independently tunable upward stroke against the two
/// lips of a neutral witness. Registration translates the complete actor stance.
/// </summary>
public partial class AxeSwingLab
{
    private const float OpposingTrunkRadiusMeters = 0.75f;
    private const float OpposingTrunkHeightMeters = 2.4f;
    private const float OpposingBlobScale = 1.20f;
    private const float OpposingBlobBackoffMeters = 0.06f;
    private const float OpposingBlobBaseHeightMeters = 1.5f * OpposingBlobScale;

    private AxeSwingProfile _opposingDownProfile;
    private AxeSwingProfile _opposingUpProfile;
    private AxeStrokeTargetFrame _opposingTargetFrame;
    private AxeStrokeRegistrationResult _opposingRegistration;
    private AxeCutSense _opposingSense = AxeCutSense.Up;
    private float _opposingMouthHeight = AxeStrokeRegistration.DefaultMouthHeightMeters;
    private bool _opposingAwaitingContinue;
    private bool _opposingUpdatingSliders;
    private bool _opposingUpIsAccepted = true;
    private GroundedBlobScale _opposingBlobScale;
    private MeshInstance3D _opposingWitness;
    private MeshInstance3D _opposingTargetGuide;
    private MeshInstance3D _opposingDownGuide;
    private MeshInstance3D _opposingContactMarker;
    private MeshInstance3D _opposingArrivalGuide;
    private Button _opposingSenseToggle;
    private Label _opposingStatus;
    private TuningSlider _opposingMouthSlider;
    private TuningSlider _opposingPlaneSlider;
    private TuningSlider _opposingRestSlider;
    private TuningSlider _opposingStartSlider;
    private TuningSlider _opposingContactSlider;
    private TuningSlider _opposingFollowSlider;
    private TuningSlider _opposingShoulderRestSlider;
    private TuningSlider _opposingShoulderWindupSlider;
    private TuningSlider _opposingShoulderContactSlider;
    private TuningSlider _opposingShoulderFollowSlider;
    private TuningSlider _opposingElbowRestSlider;
    private TuningSlider _opposingElbowWindupSlider;
    private TuningSlider _opposingElbowContactSlider;
    private TuningSlider _opposingElbowFollowSlider;
    private TuningSlider _opposingWindupSlider;
    private TuningSlider _opposingDriveSlider;
    private TuningSlider _opposingRecoverySlider;
    private readonly List<Camera3D> _opposingCadCameras = new();

    private void BuildOpposingLab()
    {
        EnsureBiteWindowSize();
        _camera.CullMask = 1u;
        _opposingDownProfile = AxeSwingProfile.AcceptedV1();
        _opposingUpProfile = AxeSwingProfile.AcceptedUpV1();
        _opposingTargetFrame = AxeStrokeRegistration.DefaultTargetFrame(_opposingDownProfile);
        _opposingSense = AxeCutSense.Up;
        _profile = _opposingUpProfile;
        ConfigureOpposingBlobShell();

        BuildOpposingWitness();
        BuildOpposingSheet();
        BuildOpposingTuning();
        RefreshOpposingGeometry();

        var target = _shoulderPosition + ToGodot(_opposingTargetFrame.NotchCenter);
        _camera.LookAtFromPosition(target + new Vector3(3.4f, 2.25f, 4.5f), target + Vector3.Up * 0.02f);
    }

    private void ConfigureOpposingBlobShell()
    {
        _bodyBlob.Mesh = new CapsuleMesh
        {
            Radius = 0.24f * OpposingBlobScale,
            Height = OpposingBlobBaseHeightMeters,
        };
        _headBlob.Mesh = new SphereMesh
        {
            Radius = 0.19f * OpposingBlobScale,
            Height = 0.38f * OpposingBlobScale,
        };
    }

    private void BuildOpposingWitness()
    {
        var surface = _shoulderPosition + ToGodot(_opposingTargetFrame.NotchCenter);
        var inward = ToGodot(_opposingTargetFrame.InwardNormal);
        var horizontalCenter = surface + inward * OpposingTrunkRadiusMeters;
        _opposingWitness = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = OpposingTrunkRadiusMeters,
                BottomRadius = OpposingTrunkRadiusMeters,
                Height = OpposingTrunkHeightMeters,
                RadialSegments = 64,
                Rings = 4,
            },
            Position = new Vector3(
                horizontalCenter.X,
                OpposingTrunkHeightMeters * 0.5f,
                horizontalCenter.Z),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.22f, 0.29f, 0.30f, 0.30f),
                EmissionEnabled = true,
                Emission = new Color(0.08f, 0.12f, 0.13f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Roughness = 0.84f,
            },
        };
        AddChild(_opposingWitness);

        _opposingTargetGuide = new MeshInstance3D { Position = _shoulderPosition };
        AddChild(_opposingTargetGuide);
        _opposingDownGuide = new MeshInstance3D
        {
            Position = _shoulderPosition,
            Mesh = TipPathMesh(_opposingDownProfile, includeHead: true, new Color(0.25f, 0.36f, 0.35f)),
        };
        AddChild(_opposingDownGuide);
        _opposingContactMarker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.047f, Height = 0.094f },
            MaterialOverride = DiagnosticMaterial(new Color(1f, 0.32f, 0.12f, 0.96f)),
            Visible = false,
        };
        AddChild(_opposingContactMarker);
        _opposingArrivalGuide = new MeshInstance3D { Position = _shoulderPosition };
        AddChild(_opposingArrivalGuide);
    }

    private void BuildOpposingSheet()
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

        var controls = new PanelContainer { CustomMinimumSize = new Vector2(0f, 126f) };
        controls.AddThemeStyleboxOverride("panel", PanelStyle(new Color(0.035f, 0.045f, 0.04f, 0.94f)));
        sheet.AddChild(controls);
        var controlBox = new VBoxContainer();
        controlBox.AddThemeConstantOverride("separation", 4);
        controls.AddChild(controlBox);
        _opposingSenseToggle = new Button();
        _opposingSenseToggle.AddThemeFontSizeOverride("font_size", 14);
        _opposingSenseToggle.Pressed += ToggleOpposingSense;
        controlBox.AddChild(_opposingSenseToggle);
        _opposingStatus = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _opposingStatus.AddThemeFontSizeOverride("font_size", 12);
        _opposingStatus.AddThemeColorOverride("font_color", new Color(0.88f, 0.92f, 0.84f));
        controlBox.AddChild(_opposingStatus);

        AddOpposingCadPane(sheet, "ELEVATION - SWING PLANE", OpposingCadView.Elevation);
        AddOpposingCadPane(sheet, "PLAN - TRUNK AXIS", OpposingCadView.Plan);
        AddOpposingCadPane(sheet, "END - BARK FACE", OpposingCadView.End);
    }

    private void AddOpposingCadPane(VBoxContainer sheet, string title, OpposingCadView view)
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
            CullMask = 1u,
        };
        camera.SetMeta("opposing_cad_view", (int)view);
        viewport.AddChild(camera);
        _opposingCadCameras.Add(camera);
    }

    private void BuildOpposingTuning()
    {
        var panel = new TuningPanel { OpenOnRight = true };
        AddChild(panel);
        var target = panel.AddSection("Two-lip target");
        _opposingMouthSlider = target.AddSlider(
            "Mouth height m",
            AxeStrokeRegistration.MinimumMouthHeightMeters,
            AxeStrokeRegistration.MaximumMouthHeightMeters,
            _opposingMouthHeight,
            ChangeOpposingMouthHeight);

        var arc = panel.AddSection("UP tool path");
        _opposingPlaneSlider = arc.AddSlider("Plane tilt deg", -60f, -5f, _opposingUpProfile.PlaneTiltDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.PlaneTiltDegrees = value));
        _opposingRestSlider = arc.AddSlider("Rest deg", -60f, 40f, _opposingUpProfile.RestAngleDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.RestAngleDegrees = value));
        _opposingStartSlider = arc.AddSlider("Start deg", 30f, 150f, _opposingUpProfile.StartAngleDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.StartAngleDegrees = value));
        _opposingContactSlider = arc.AddSlider("Contact deg", -50f, 50f, _opposingUpProfile.ContactAngleDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ContactAngleDegrees = value));
        _opposingFollowSlider = arc.AddSlider("Follow deg", -90f, 20f, _opposingUpProfile.FollowThroughAngleDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.FollowThroughAngleDegrees = value));

        var body = panel.AddSection("UP body rotation");
        _opposingShoulderRestSlider = body.AddSlider("Shoulder rest", -145f, 145f, _opposingUpProfile.ShoulderRestDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ShoulderRestDegrees = value));
        _opposingShoulderWindupSlider = body.AddSlider("Shoulder windup", -145f, 145f, _opposingUpProfile.ShoulderWindupDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ShoulderWindupDegrees = value));
        _opposingShoulderContactSlider = body.AddSlider("Shoulder contact", -145f, 145f, _opposingUpProfile.ShoulderContactDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ShoulderContactDegrees = value));
        _opposingShoulderFollowSlider = body.AddSlider("Shoulder follow", -145f, 145f, _opposingUpProfile.ShoulderFollowThroughDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ShoulderFollowThroughDegrees = value));
        _opposingElbowRestSlider = body.AddSlider("Elbow rest", -145f, 145f, _opposingUpProfile.ElbowRestDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ElbowRestDegrees = value));
        _opposingElbowWindupSlider = body.AddSlider("Elbow windup", -145f, 145f, _opposingUpProfile.ElbowWindupDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ElbowWindupDegrees = value));
        _opposingElbowContactSlider = body.AddSlider("Elbow contact", -145f, 145f, _opposingUpProfile.ElbowContactDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ElbowContactDegrees = value));
        _opposingElbowFollowSlider = body.AddSlider("Elbow follow", -145f, 145f, _opposingUpProfile.ElbowFollowThroughDegrees, value => ChangeOpposingCandidate(() => _opposingUpProfile.ElbowFollowThroughDegrees = value));

        var timing = panel.AddSection("UP timing");
        _opposingWindupSlider = timing.AddSlider("Windup sec", 0.05f, 0.8f, _opposingUpProfile.WindupSeconds, value => ChangeOpposingCandidate(() => _opposingUpProfile.WindupSeconds = value));
        _opposingDriveSlider = timing.AddSlider("Drive sec", 0.08f, 0.8f, _opposingUpProfile.DriveSeconds, value => ChangeOpposingCandidate(() => _opposingUpProfile.DriveSeconds = value));
        _opposingRecoverySlider = timing.AddSlider("Recover sec", 0.05f, 1.2f, _opposingUpProfile.RecoverySeconds, value => ChangeOpposingCandidate(() => _opposingUpProfile.RecoverySeconds = value));
        timing.AddButton("Restore accepted UP", ResetOpposingLab);
    }

    private void ToggleOpposingSense()
    {
        _opposingSense = _opposingSense == AxeCutSense.Up ? AxeCutSense.Down : AxeCutSense.Up;
        _profile = _opposingSense == AxeCutSense.Up ? _opposingUpProfile : _opposingDownProfile;
        RefreshOpposingGeometry();
        ReplayOpposingStroke();
    }

    private void ChangeOpposingMouthHeight(float value)
    {
        _opposingMouthHeight = value;
        if (_opposingUpdatingSliders) return;
        RefreshOpposingGeometry();
    }

    private void ChangeOpposingCandidate(Action change)
    {
        change();
        if (_opposingUpdatingSliders) return;
        _opposingUpIsAccepted = false;
        _opposingSense = AxeCutSense.Up;
        _profile = _opposingUpProfile;
        if (_profile.ValidationError is null)
            RefreshOpposingGeometry();
        else
            UpdateOpposingHud(ReadyPose(_profile), _profile.ValidationError);
    }

    private void RefreshOpposingGeometry()
    {
        if (_profile.ValidationError is not null)
        {
            UpdateOpposingHud(ReadyPose(_profile), _profile.ValidationError);
            return;
        }

        try
        {
            _opposingRegistration = AxeStrokeRegistration.Register(
                _profile,
                _opposingTargetFrame,
                _opposingSense,
                _opposingMouthHeight);
        }
        catch (InvalidOperationException ex)
        {
            _elapsed = -1f;
            _heldAtContact = false;
            _opposingAwaitingContinue = false;
            UpdateOpposingHud(ReadyPose(_profile), ex.Message);
            return;
        }
        _actorRoot.Position = ToGodot(_opposingRegistration.StanceTranslation);
        UpdateOpposingBlobStance();
        _elapsed = -1f;
        _heldAtContact = false;
        _contactReleased = false;
        _opposingAwaitingContinue = false;
        RebuildHandle();
        BuildGuides();
        RebuildOpposingTargetVisuals();
        ApplyPose(_profile.Sample(0f));
    }

    private void UpdateOpposingBlobStance()
    {
        _opposingBlobScale = GroundedBlobProjection.Compute(
            OpposingBlobBaseHeightMeters,
            _actorRoot.Position.Y);
        var backoff = ToGodot(_opposingTargetFrame.OutwardNormal) * OpposingBlobBackoffMeters;
        _bodyBlob.Scale = new Vector3(
            _opposingBlobScale.RadialScale,
            _opposingBlobScale.VerticalScale,
            _opposingBlobScale.RadialScale);
        _bodyBlob.Position = new Vector3(
            -0.45f + backoff.X,
            _opposingBlobScale.LocalCenterMeters,
            0.08f + backoff.Z);
        _headBlob.Position = new Vector3(
            -0.45f + backoff.X,
            2.02f,
            0.08f + backoff.Z);
    }

    private void RebuildOpposingTargetVisuals()
    {
        if (_opposingTargetGuide is null) return;
        _opposingTargetGuide.Mesh = OpposingTargetMesh(_opposingTargetFrame, _opposingMouthHeight);
        _opposingDownGuide.Visible = _opposingSense == AxeCutSense.Up;
        _opposingContactMarker.Position = _shoulderPosition + ToGodot(_opposingRegistration.TargetPoint);
        var contact = AxeKinematics.Sample(_profile, _profile.ContactTimeSeconds);
        _opposingArrivalGuide.Mesh = OpposingArrivalMesh(contact, _opposingRegistration.TargetPoint);
        UpdateOpposingCadCameras();
        UpdateOpposingControls();
    }

    private void ReplayOpposingStroke()
    {
        if (_profile.ValidationError is not null) return;
        _elapsed = 0f;
        _heldAtContact = false;
        _contactReleased = false;
        _opposingAwaitingContinue = false;
        _opposingContactMarker.Visible = false;
        ApplyPose(_profile.Sample(0f));
    }

    private void ReplayOpposingStroke(AxeCutSense sense)
    {
        if (_opposingSense != sense)
        {
            _opposingSense = sense;
            _profile = sense == AxeCutSense.Up ? _opposingUpProfile : _opposingDownProfile;
            RefreshOpposingGeometry();
        }
        ReplayOpposingStroke();
    }

    private void ContinueOpposingStroke()
    {
        if (!_opposingAwaitingContinue) return;
        _opposingAwaitingContinue = false;
        _heldAtContact = false;
        _contactReleased = true;
    }

    private void ProcessOpposingStroke(float delta)
    {
        if (_elapsed < 0f || _opposingAwaitingContinue) return;
        var step = AxeStrokePlayback.Advance(
            _elapsed,
            delta,
            _profile.ContactTimeSeconds,
            _contactReleased);
        var next = step.ElapsedSeconds;
        if (step.PauseAtContact)
        {
            next = _profile.ContactTimeSeconds;
            _opposingAwaitingContinue = true;
            _heldAtContact = true;
        }

        _elapsed = next;
        var pose = _profile.Sample(_elapsed);
        ApplyPose(pose);
        if (pose.Phase != AxeSwingPhase.Complete) return;
        if (_loop)
        {
            _elapsed = 0f;
            _contactReleased = false;
        }
        else
            _elapsed = -1f;
    }

    private void ResetOpposingLab()
    {
        _opposingUpProfile = AxeSwingProfile.AcceptedUpV1();
        _opposingMouthHeight = AxeStrokeRegistration.DefaultMouthHeightMeters;
        _opposingSense = AxeCutSense.Up;
        _opposingUpIsAccepted = true;
        _profile = _opposingUpProfile;
        _opposingUpdatingSliders = true;
        _opposingMouthSlider?.SetValue(_opposingMouthHeight);
        _opposingPlaneSlider?.SetValue(_opposingUpProfile.PlaneTiltDegrees);
        _opposingRestSlider?.SetValue(_opposingUpProfile.RestAngleDegrees);
        _opposingStartSlider?.SetValue(_opposingUpProfile.StartAngleDegrees);
        _opposingContactSlider?.SetValue(_opposingUpProfile.ContactAngleDegrees);
        _opposingFollowSlider?.SetValue(_opposingUpProfile.FollowThroughAngleDegrees);
        _opposingShoulderRestSlider?.SetValue(_opposingUpProfile.ShoulderRestDegrees);
        _opposingShoulderWindupSlider?.SetValue(_opposingUpProfile.ShoulderWindupDegrees);
        _opposingShoulderContactSlider?.SetValue(_opposingUpProfile.ShoulderContactDegrees);
        _opposingShoulderFollowSlider?.SetValue(_opposingUpProfile.ShoulderFollowThroughDegrees);
        _opposingElbowRestSlider?.SetValue(_opposingUpProfile.ElbowRestDegrees);
        _opposingElbowWindupSlider?.SetValue(_opposingUpProfile.ElbowWindupDegrees);
        _opposingElbowContactSlider?.SetValue(_opposingUpProfile.ElbowContactDegrees);
        _opposingElbowFollowSlider?.SetValue(_opposingUpProfile.ElbowFollowThroughDegrees);
        _opposingWindupSlider?.SetValue(_opposingUpProfile.WindupSeconds);
        _opposingDriveSlider?.SetValue(_opposingUpProfile.DriveSeconds);
        _opposingRecoverySlider?.SetValue(_opposingUpProfile.RecoverySeconds);
        _opposingUpdatingSliders = false;
        RefreshOpposingGeometry();
        GD.Print("AxeOpposingLab: accepted UP calibration restored");
    }

    private void UpdateOpposingVisuals(AxeSwingPose pose)
    {
        _ = pose;
        if (_opposingContactMarker is null) return;
        _opposingContactMarker.Visible = _opposingAwaitingContinue ||
            (_contactReleased && _elapsed >= _profile.ContactTimeSeconds);
    }

    private void UpdateOpposingHud(AxeSwingPose pose, string error)
    {
        var contact = error is null
            ? AxeKinematics.Sample(_profile, _profile.ContactTimeSeconds)
            : default;
        var velocity = error is null && contact.CuttingCenterSpeedMetersPerSecond > 0f
            ? contact.CuttingCenterVelocityMetersPerSecond / contact.CuttingCenterSpeedMetersPerSecond
            : NumericsVector3.Zero;
        var motion = velocity.Y > 0.01f ? "UPWARD" : velocity.Y < -0.01f ? "DOWNWARD" : "LEVEL";
        var normalSpeed = error is null
            ? NumericsVector3.Dot(
                contact.CuttingCenterVelocityMetersPerSecond,
                _opposingTargetFrame.InwardNormal)
            : 0f;
        var horizontalEdge = error is null
            ? NumericsVector3.Normalize(new NumericsVector3(
                contact.ToolDirection.X,
                0f,
                contact.ToolDirection.Z))
            : NumericsVector3.Zero;
        var edgeAlignment = error is null
            ? MathF.Abs(NumericsVector3.Dot(horizontalEdge, _opposingTargetFrame.Tangent))
            : 0f;
        var phase = _opposingAwaitingContinue ? "CONTACT INSPECTION - press F" : pose.Phase.ToString();
        _hud.Text =
            "AXE SWING LAB 05 - OPPOSING STROKE\n" +
            "MOTION + CONTACT ONLY\n\n" +
            $"DOWN  {AxeSwingProfile.AcceptedCalibrationId}  LOCKED\n" +
            $"UP    {AxeSwingProfile.AcceptedUpCalibrationId}  " +
            $"{(_opposingUpIsAccepted ? "ACCEPTED" : "TUNED - NOT ACCEPTED")}\n\n" +
            $"Selected       {_opposingSense.ToString().ToUpperInvariant()}\n" +
            $"Phase          {phase}\n" +
            $"Mouth height   {_opposingMouthHeight * 100f:F1} cm\n" +
            $"Witness        {OpposingTrunkRadiusMeters * 2f:F2} m diameter / " +
            $"{OpposingTrunkRadiusMeters * 2f / _profile.HeadBladeLengthMeters:F1} edges across\n" +
            $"Target lip     {(_opposingSense == AxeCutSense.Up ? "LOWER" : "UPPER")}\n" +
            $"Stance shift   {_opposingRegistration.StanceTranslation.X:+0.000;-0.000;0.000} / " +
            $"{_opposingRegistration.StanceTranslation.Y:+0.000;-0.000;0.000} / " +
            $"{_opposingRegistration.StanceTranslation.Z:+0.000;-0.000;0.000} m\n" +
            $"Contact error  {_opposingRegistration.ContactErrorMeters * 1000f:F3} mm\n\n" +
            $"Blob shape     {_opposingBlobScale.RadialScale:F2}x wide / " +
            $"{_opposingBlobScale.VerticalScale:F2}x tall (volume held)\n\n" +
            $"Slime shell    {OpposingBlobScale * 100f:F0}% / backed off " +
            $"{OpposingBlobBackoffMeters * 100f:F0} cm\n\n" +
            (error is null
                ? $"Arrival speed  {contact.CuttingCenterSpeedMetersPerSecond:F2} m/s\n" +
                  $"Vertical speed {contact.CuttingCenterVelocityMetersPerSecond.Y:+0.00;-0.00;0.00} m/s ({motion})\n" +
                  $"Inward speed   {normalSpeed:F2} m/s\n" +
                  $"Edge tangent   {edgeAlignment * 100f:F1}% (centered contact)\n" +
                  $"Plane tilt     {_profile.PlaneTiltDegrees:F1} deg\n" +
                  $"Tool angle     {pose.AngleDegrees:F1} deg\n" +
                  $"Shoulder/elbow {pose.ShoulderAngleDegrees:F0} / {pose.ElbowAngleDegrees:F0} deg\n\n"
                : $"INVALID UP PROFILE: {error}\n\n") +
            "TEAL = selected free path\n" +
            "MUTED = locked DOWN path\n" +
            "GOLD = upper/lower targets + 45 deg references\n" +
            "RED = exact declared contact\n\n" +
            "[LMB] under-swing UP   [RMB] top-swing DOWN\n" +
            "[Space] replay selected stroke\n" +
            "[F] continue from contact\n" +
            "[R] restore accepted UP   [Tab] explore UP tuning\n\n" +
            "NO WOOD / FORCE / RETENTION / CHIP / DAMAGE / NETWORK";
        UpdateOpposingControls();
    }

    private void UpdateOpposingControls()
    {
        if (_opposingSenseToggle is null || _opposingStatus is null) return;
        _opposingSenseToggle.Text = _opposingSense == AxeCutSense.Up
            ? _opposingUpIsAccepted
                ? "STROKE: UP - ACCEPTED\nclick for locked DOWN"
                : "STROKE: UP - TUNED / NOT ACCEPTED\nclick for locked DOWN"
            : _opposingUpIsAccepted
                ? "STROKE: DOWN - ACCEPTED / LOCKED\nclick for accepted UP"
                : "STROKE: DOWN - ACCEPTED / LOCKED\nclick for tuned UP";
        _opposingStatus.Text =
            $"MOUTH {_opposingMouthHeight * 100f:F1} cm  -  " +
            $"ERROR {_opposingRegistration.ContactErrorMeters * 1000f:F3} mm\n" +
            (_opposingAwaitingContinue
                ? "PAUSED AT DECLARED CONTACT - F TO FOLLOW THROUGH"
                : "LMB UNDER / RMB TOP - TAB TUNES UP ONLY");
    }

    private void UpdateOpposingCadCameras()
    {
        var surface = _shoulderPosition + ToGodot(_opposingTargetFrame.NotchCenter);
        var inward = ToGodot(_opposingTargetFrame.InwardNormal);
        var outward = ToGodot(_opposingTargetFrame.OutwardNormal);
        var tangent = ToGodot(_opposingTargetFrame.Tangent);
        var target = surface + inward * 0.08f;
        foreach (var camera in _opposingCadCameras)
        {
            var view = (OpposingCadView)(int)camera.GetMeta("opposing_cad_view");
            switch (view)
            {
                case OpposingCadView.Elevation:
                    camera.Size = 1.35f;
                    camera.LookAtFromPosition(target + tangent * 3f, target, Vector3.Up);
                    break;
                case OpposingCadView.Plan:
                    camera.Size = 1.85f;
                    camera.LookAtFromPosition(target + Vector3.Up * 3f, target, outward);
                    break;
                case OpposingCadView.End:
                    camera.Size = 1.05f;
                    camera.LookAtFromPosition(surface + outward * 3f, target, Vector3.Up);
                    break;
            }
        }
    }

    private static AxeSwingPose ReadyPose(AxeSwingProfile profile) => new(
        AxeSwingPhase.Ready,
        profile.RestAngleDegrees,
        profile.ShoulderRestDegrees,
        profile.ElbowRestDegrees,
        0f,
        false);

    private static ImmediateMesh OpposingTargetMesh(AxeStrokeTargetFrame frame, float mouthHeightMeters)
    {
        var center = ToGodot(frame.NotchCenter);
        var vertical = ToGodot(frame.Vertical);
        var outward = ToGodot(frame.OutwardNormal);
        var tangent = ToGodot(frame.Tangent);
        var upper = center + vertical * (mouthHeightMeters * 0.5f);
        var lower = center - vertical * (mouthHeightMeters * 0.5f);
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, DiagnosticMaterial(new Color(1f, 0.78f, 0.20f, 0.95f)));
        AddCross(mesh, center, tangent, vertical, 0.035f);
        AddCross(mesh, upper, tangent, vertical, 0.055f);
        AddCross(mesh, lower, tangent, vertical, 0.055f);
        AddLine(mesh, upper, upper + outward * 0.32f + vertical * 0.32f);
        AddLine(mesh, lower, lower + outward * 0.32f - vertical * 0.32f);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static ImmediateMesh OpposingArrivalMesh(
        AxeKinematicsSample contact,
        NumericsVector3 target)
    {
        var mesh = new ImmediateMesh();
        var speed = contact.CuttingCenterSpeedMetersPerSecond;
        if (speed <= 0f) return mesh;
        var direction = contact.CuttingCenterVelocityMetersPerSecond / speed;
        var point = ToGodot(target);
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, DiagnosticMaterial(new Color(0.24f, 0.92f, 1f, 0.95f)));
        AddLine(mesh, point - ToGodot(direction) * 0.34f, point);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static void AddCross(
        ImmediateMesh mesh,
        Vector3 center,
        Vector3 horizontal,
        Vector3 vertical,
        float radius)
    {
        AddLine(mesh, center - horizontal * radius, center + horizontal * radius);
        AddLine(mesh, center - vertical * radius, center + vertical * radius);
    }

    private enum OpposingCadView
    {
        Elevation,
        Plan,
        End,
    }
}

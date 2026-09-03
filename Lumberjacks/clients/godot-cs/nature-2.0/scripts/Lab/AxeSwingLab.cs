using Godot;
using System;

namespace CommunitySurvival.Lab;

/// <summary>
/// Human-in-the-loop swing lab. It renders a shoulder, two-link arm proxy, rigid handle,
/// chopping head, cutting-edge marker, and driven path. There is no target, collision, wood
/// response, damage, or network traffic in this scene.
/// </summary>
public partial class AxeSwingLab : Node3D
{
    private const float HorizontalFellTiltDegrees = 22f;
    private const float VerticalSplitTiltDegrees = 90f;
    private readonly Vector3 _shoulderPosition = new(-0.30f, 1.32f, 0.30f);
    private AxeSwingProfile _profile = AxeSwingProfile.AcceptedV1();
    private bool _showChoppingHead;
    private bool _contactMode;
    private bool _biteMode;
    private bool _opposingMode;
    private Node3D _actorRoot;
    private MeshInstance3D _bodyBlob;
    private MeshInstance3D _headBlob;
    private Node3D _shoulderPivot;
    private MeshInstance3D _upperArm;
    private Node3D _elbowPivot;
    private MeshInstance3D _forearm;
    private Node3D _handPivot;
    private MeshInstance3D _handGrip;
    private MeshInstance3D _handle;
    private MeshInstance3D _head;
    private MeshInstance3D _cuttingEdge;
    private MeshInstance3D _tip;
    private MeshInstance3D _arcGuide;
    private MeshInstance3D _contactGuide;
    private Camera3D _camera;
    private Label _hud;
    private TuningSlider _restSlider;
    private TuningSlider _startSlider;
    private TuningSlider _contactSlider;
    private TuningSlider _followSlider;
    private TuningSlider _upperArmSlider;
    private TuningSlider _forearmSlider;
    private TuningSlider _headBladeSlider;
    private TuningSlider _headDepthSlider;
    private TuningSlider _headThicknessSlider;
    private TuningSlider _shoulderRestSlider;
    private TuningSlider _shoulderWindupSlider;
    private TuningSlider _shoulderContactSlider;
    private TuningSlider _shoulderFollowSlider;
    private TuningSlider _elbowRestSlider;
    private TuningSlider _elbowWindupSlider;
    private TuningSlider _elbowContactSlider;
    private TuningSlider _elbowFollowSlider;
    private TuningSlider _windupSlider;
    private TuningSlider _driveSlider;
    private TuningSlider _recoverySlider;
    private Button _planeToggle;
    private float _elapsed = -1f;
    private bool _holdAtContact;
    private bool _heldAtContact;
    private bool _loop;

    public override void _Ready()
    {
        var userArguments = OS.GetCmdlineUserArgs();
        _contactMode = Array.Exists(
            userArguments,
            arg => string.Equals(arg, "--lab=axe-contact", StringComparison.OrdinalIgnoreCase));
        _biteMode = Array.Exists(
            userArguments,
            arg => string.Equals(arg, "--lab=axe-bite", StringComparison.OrdinalIgnoreCase));
        _opposingMode = Array.Exists(
            userArguments,
            arg => string.Equals(arg, "--lab=axe-opposing", StringComparison.OrdinalIgnoreCase));
        _showChoppingHead = !Array.Exists(
            userArguments,
            arg => string.Equals(arg, "--lab=axe-arc", StringComparison.OrdinalIgnoreCase));
        BuildStage();
        BuildSwingRig();
        BuildGuides();
        BuildHud();
        if (_opposingMode)
            BuildOpposingLab();
        else if (_biteMode)
            BuildBiteLab();
        else if (_contactMode)
            BuildContactWitness();
        else
        {
            BuildPlaneToggle();
            BuildTuningPanel();
        }
        ApplyPose(_profile.Sample(0f));
        GD.Print(_opposingMode
            ? "AxeOpposingLab: ready; accepted opposing strokes. LMB=under-swing UP, RMB=top-swing DOWN, Space=replay selected, F=continue follow-through, Tab=explore UP tuning"
            : _biteMode
            ? "AxeBiteLab: ready; one accepted swing into fresh uniform wood. Free heads auto-recover; retained heads wait for F"
            : _contactMode
            ? "AxeContactLab: ready; accepted swing against a finite neutral witness, no force/wood/damage. Space or LMB=replay, F=continue follow-through"
            : _showChoppingHead
            ? "AxeHeadLab: ready; accepted articulated swing plus chopping-head geometry, no target/contact/damage. Space or LMB=replay, F=hold contact, Tab=tune"
            : "AxeArcLab: ready; accepted articulated swing only, no head/target/contact/damage. Space or LMB=replay, F=hold contact, Tab=tune");
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { Pressed: true } mouse)
        {
            if (_opposingMode && mouse.ButtonIndex is MouseButton.Left or MouseButton.Right)
                ReplayOpposingStroke(mouse.ButtonIndex == MouseButton.Left
                    ? AxeCutSense.Up
                    : AxeCutSense.Down);
            else if (mouse.ButtonIndex == MouseButton.Left)
                Replay();
        }

        if (ev is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.Space:
                Replay();
                break;
            case Key.F:
                if (_opposingMode)
                {
                    ContinueOpposingStroke();
                }
                else if (_biteMode)
                {
                    BeginBiteWithdrawal();
                }
                else if (_contactMode && _heldAtContact)
                {
                    _heldAtContact = false;
                    _contactReleased = true;
                }
                else if (!_contactMode)
                {
                    _holdAtContact = !_holdAtContact;
                    if (!_holdAtContact && _heldAtContact)
                        _heldAtContact = false;
                }
                break;
            case Key.L:
                _loop = !_loop;
                if (_loop && _elapsed < 0f) Replay();
                break;
            case Key.R:
                ResetProfile();
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_opposingMode)
        {
            ProcessOpposingStroke((float)delta);
            return;
        }

        if (_biteMode)
        {
            ProcessBite((float)delta);
            return;
        }

        if (_elapsed < 0f || _heldAtContact) return;

        var next = _elapsed + (float)delta;
        if (_contactMode && _contactResult.Hit && !_contactReleased &&
            _elapsed < _contactResult.ContactTimeSeconds && next >= _contactResult.ContactTimeSeconds)
        {
            next = _contactResult.ContactTimeSeconds;
            _heldAtContact = true;
        }
        else if (_holdAtContact && _elapsed < _profile.ContactTimeSeconds && next >= _profile.ContactTimeSeconds)
        {
            next = _profile.ContactTimeSeconds;
            _heldAtContact = true;
        }

        _elapsed = next;
        var pose = _profile.Sample(_elapsed);
        ApplyPose(pose);

        if (pose.Phase != AxeSwingPhase.Complete) return;
        if (_loop)
            _elapsed = 0f;
        else
            _elapsed = -1f;
    }

    private void Replay()
    {
        if (_opposingMode)
        {
            ReplayOpposingStroke();
            return;
        }

        if (_biteMode)
        {
            ReplayBite();
            return;
        }

        if (_profile.ValidationError is not null) return;
        _heldAtContact = false;
        _contactReleased = false;
        _elapsed = 0f;
        ApplyPose(_profile.Sample(0f));
    }

    private void BuildStage()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.055f, 0.07f, 0.075f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.78f, 0.82f, 0.76f),
            AmbientLightEnergy = 0.55f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
        };
        AddChild(new WorldEnvironment { Environment = environment });

        var key = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-35f, -25f, 0f),
            LightEnergy = 1.35f,
            ShadowEnabled = true,
            LightColor = new Color(1f, 0.9f, 0.76f),
        };
        AddChild(key);

        var floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(8f, 5f) },
            MaterialOverride = Material(new Color(0.11f, 0.14f, 0.13f)),
        };
        AddChild(floor);

        _actorRoot = new Node3D { Name = "RegisteredActorStance" };
        AddChild(_actorRoot);

        _bodyBlob = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.24f, Height = 1.5f },
            Position = new Vector3(-0.45f, 0.75f, 0.08f),
            MaterialOverride = Material(new Color(0.18f, 0.25f, 0.22f)),
        };
        _actorRoot.AddChild(_bodyBlob);

        _headBlob = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.19f, Height = 0.38f },
            Position = new Vector3(-0.45f, 1.72f, 0.08f),
            MaterialOverride = Material(new Color(0.25f, 0.33f, 0.28f)),
        };
        _actorRoot.AddChild(_headBlob);

        _camera = new Camera3D { Current = true, Fov = 42f };
        AddChild(_camera);
        // Reserve the left third for instruments and use the rest as an uncluttered stage.
        _camera.LookAtFromPosition(new Vector3(-1.1f, 3.25f, 4.8f), new Vector3(-1.1f, 1.15f, 0f));
    }

    private void BuildSwingRig()
    {
        _shoulderPivot = new Node3D { Name = "ShoulderPivot", Position = _shoulderPosition };
        _actorRoot.AddChild(_shoulderPivot);

        var shoulder = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.07f, Height = 0.14f },
            MaterialOverride = Material(new Color(0.72f, 0.82f, 1f), unshaded: true),
        };
        _shoulderPivot.AddChild(shoulder);

        _upperArm = new MeshInstance3D
        {
            MaterialOverride = Material(new Color(0.12f, 0.42f, 0.55f)),
        };
        _shoulderPivot.AddChild(_upperArm);

        _elbowPivot = new Node3D { Name = "ElbowPivot" };
        _shoulderPivot.AddChild(_elbowPivot);

        var elbow = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.058f, Height = 0.116f },
            MaterialOverride = Material(new Color(0.18f, 0.72f, 0.82f), unshaded: true),
        };
        _elbowPivot.AddChild(elbow);

        _forearm = new MeshInstance3D
        {
            MaterialOverride = Material(new Color(0.2f, 0.58f, 0.72f)),
        };
        _elbowPivot.AddChild(_forearm);

        _handPivot = new Node3D { Name = "HandPivot" };
        _elbowPivot.AddChild(_handPivot);

        _handGrip = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.055f, Height = 0.11f },
            MaterialOverride = Material(new Color(0.95f, 0.78f, 0.24f), unshaded: true),
        };
        _handPivot.AddChild(_handGrip);

        _handle = new MeshInstance3D
        {
            MaterialOverride = Material(new Color(0.58f, 0.34f, 0.14f)),
        };
        _handPivot.AddChild(_handle);

        _head = new MeshInstance3D
        {
            MaterialOverride = HeadMaterial(new Color(0.24f, 0.30f, 0.33f)),
        };
        _handPivot.AddChild(_head);

        _cuttingEdge = new MeshInstance3D
        {
            MaterialOverride = HeadMaterial(new Color(0.72f, 0.82f, 0.84f)),
        };
        _handPivot.AddChild(_cuttingEdge);
        _head.Visible = _showChoppingHead;
        _cuttingEdge.Visible = _showChoppingHead;

        _tip = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.038f, Height = 0.076f },
            MaterialOverride = Material(new Color(1f, 0.34f, 0.12f), unshaded: true),
        };
        _handPivot.AddChild(_tip);
        RebuildHandle();
    }

    private void RebuildHandle()
    {
        var upperArmLength = _profile.UpperArmLengthMeters;
        var forearmLength = _profile.ForearmLengthMeters;
        var handleLength = _profile.HandleLengthMeters;
        _upperArm.Mesh = new CylinderMesh
        {
            TopRadius = 0.014f,
            BottomRadius = 0.018f,
            Height = upperArmLength,
        };
        _upperArm.RotationDegrees = new Vector3(0f, 0f, -90f);
        _upperArm.Position = new Vector3(upperArmLength * 0.5f, 0f, 0f);
        _elbowPivot.Position = new Vector3(upperArmLength, 0f, 0f);

        _forearm.Mesh = new CylinderMesh
        {
            TopRadius = 0.013f,
            BottomRadius = 0.017f,
            Height = forearmLength,
        };
        _forearm.RotationDegrees = new Vector3(0f, 0f, -90f);
        _forearm.Position = new Vector3(forearmLength * 0.5f, 0f, 0f);
        _handPivot.Position = new Vector3(forearmLength, 0f, 0f);
        _handGrip.Position = Vector3.Zero;

        _handle.Mesh = new CylinderMesh
        {
            TopRadius = 0.022f,
            BottomRadius = 0.027f,
            Height = handleLength,
        };
        _handle.RotationDegrees = new Vector3(0f, 0f, -90f);
        _handle.Position = new Vector3(handleLength * 0.5f, 0f, 0f);

        _head.Mesh = ChoppingHeadMesh(_profile);
        _head.Position = new Vector3(handleLength, 0f, 0f);
        _cuttingEdge.Mesh = new BoxMesh
        {
            Size = new Vector3(
                _profile.HeadBladeLengthMeters,
                0.012f,
                _profile.HeadThicknessMeters * 0.24f),
        };
        _cuttingEdge.Position = new Vector3(handleLength, -_profile.HeadDepthMeters, 0f);
        _tip.Position = _showChoppingHead
            ? new Vector3(handleLength, -_profile.HeadDepthMeters - 0.008f, 0f)
            : new Vector3(handleLength, 0f, 0f);
    }

    private void BuildGuides()
    {
        _arcGuide?.QueueFree();
        _contactGuide?.QueueFree();

        _arcGuide = new MeshInstance3D
        {
            Mesh = TipPathMesh(_profile, _showChoppingHead, new Color(0.32f, 0.78f, 0.72f)),
            Position = _shoulderPosition + new Vector3(0f, 0f, -0.035f),
        };
        _actorRoot.AddChild(_arcGuide);

        _contactGuide = new MeshInstance3D
        {
            Mesh = ContactLineMesh(_profile, _showChoppingHead, new Color(1f, 0.25f, 0.16f)),
            Position = _shoulderPosition + new Vector3(0f, 0f, -0.04f),
        };
        _actorRoot.AddChild(_contactGuide);
    }

    private void BuildHud()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label
        {
            OffsetLeft = 22f,
            OffsetTop = 20f,
            OffsetRight = 405f,
            OffsetBottom = 700f,
        };
        _hud.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hud.AddThemeFontSizeOverride("font_size", 16);
        _hud.AddThemeColorOverride("font_color", new Color(0.9f, 0.92f, 0.82f));
        _hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _hud.AddThemeConstantOverride("shadow_offset_x", 2);
        _hud.AddThemeConstantOverride("shadow_offset_y", 2);
        canvas.AddChild(_hud);
    }

    private void BuildTuningPanel()
    {
        var panel = new TuningPanel();
        AddChild(panel);

        var arc = panel.AddSection("One fixed drive arc");
        _restSlider = arc.AddSlider("Rest deg", -60f, 40f, _profile.RestAngleDegrees, v => ChangeProfile(() => _profile.RestAngleDegrees = v));
        _startSlider = arc.AddSlider("Start deg", 30f, 150f, _profile.StartAngleDegrees, v => ChangeProfile(() => _profile.StartAngleDegrees = v));
        _contactSlider = arc.AddSlider("Contact deg", -50f, 50f, _profile.ContactAngleDegrees, v => ChangeProfile(() => _profile.ContactAngleDegrees = v));
        _followSlider = arc.AddSlider("Follow deg", -90f, 20f, _profile.FollowThroughAngleDegrees, v => ChangeProfile(() => _profile.FollowThroughAngleDegrees = v));

        var leverage = panel.AddSection("Arm leverage");
        _upperArmSlider = leverage.AddSlider("Upper arm m", 0.15f, 0.55f, _profile.UpperArmLengthMeters, v => ChangeProfile(() => _profile.UpperArmLengthMeters = v));
        _forearmSlider = leverage.AddSlider("Forearm m", 0.15f, 0.55f, _profile.ForearmLengthMeters, v => ChangeProfile(() => _profile.ForearmLengthMeters = v));
        _shoulderRestSlider = leverage.AddSlider("Shoulder rest", -145f, 145f, _profile.ShoulderRestDegrees, v => ChangeProfile(() => _profile.ShoulderRestDegrees = v));
        _shoulderWindupSlider = leverage.AddSlider("Shoulder windup", -145f, 145f, _profile.ShoulderWindupDegrees, v => ChangeProfile(() => _profile.ShoulderWindupDegrees = v));
        _shoulderContactSlider = leverage.AddSlider("Shoulder contact", -145f, 145f, _profile.ShoulderContactDegrees, v => ChangeProfile(() => _profile.ShoulderContactDegrees = v));
        _shoulderFollowSlider = leverage.AddSlider("Shoulder follow", -145f, 145f, _profile.ShoulderFollowThroughDegrees, v => ChangeProfile(() => _profile.ShoulderFollowThroughDegrees = v));
        _elbowRestSlider = leverage.AddSlider("Elbow rest", -145f, 145f, _profile.ElbowRestDegrees, v => ChangeProfile(() => _profile.ElbowRestDegrees = v));
        _elbowWindupSlider = leverage.AddSlider("Elbow windup", -145f, 145f, _profile.ElbowWindupDegrees, v => ChangeProfile(() => _profile.ElbowWindupDegrees = v));
        _elbowContactSlider = leverage.AddSlider("Elbow contact", -145f, 145f, _profile.ElbowContactDegrees, v => ChangeProfile(() => _profile.ElbowContactDegrees = v));
        _elbowFollowSlider = leverage.AddSlider("Elbow follow", -145f, 145f, _profile.ElbowFollowThroughDegrees, v => ChangeProfile(() => _profile.ElbowFollowThroughDegrees = v));

        if (_showChoppingHead)
        {
            var head = panel.AddSection("Chopping head");
            _headBladeSlider = head.AddSlider("Edge length m", 0.18f, 0.55f, _profile.HeadBladeLengthMeters, v => ChangeProfile(() => _profile.HeadBladeLengthMeters = v));
            _headDepthSlider = head.AddSlider("Blade depth m", 0.1f, 0.45f, _profile.HeadDepthMeters, v => ChangeProfile(() => _profile.HeadDepthMeters = v));
            _headThicknessSlider = head.AddSlider("Thickness m", 0.03f, 0.16f, _profile.HeadThicknessMeters, v => ChangeProfile(() => _profile.HeadThicknessMeters = v));
        }

        var timing = panel.AddSection("Timing");
        _windupSlider = timing.AddSlider("Windup sec", 0.05f, 0.8f, _profile.WindupSeconds, v => ChangeProfile(() => _profile.WindupSeconds = v));
        _driveSlider = timing.AddSlider("Drive sec", 0.08f, 0.8f, _profile.DriveSeconds, v => ChangeProfile(() => _profile.DriveSeconds = v));
        _recoverySlider = timing.AddSlider("Recover sec", 0.05f, 1.2f, _profile.RecoverySeconds, v => ChangeProfile(() => _profile.RecoverySeconds = v));

        var playback = panel.AddSection("Playback");
        playback.AddButton("Replay swing", Replay);
        playback.AddButton("Toggle hold at contact", () => _holdAtContact = !_holdAtContact);
        playback.AddButton("Toggle loop", () => { _loop = !_loop; if (_loop && _elapsed < 0f) Replay(); });
    }

    private void BuildPlaneToggle()
    {
        var canvas = new CanvasLayer { Layer = 2 };
        AddChild(canvas);
        _planeToggle = new Button
        {
            AnchorLeft = 1f,
            AnchorRight = 1f,
            OffsetLeft = -310f,
            OffsetTop = 22f,
            OffsetRight = -22f,
            OffsetBottom = 78f,
        };
        _planeToggle.AddThemeFontSizeOverride("font_size", 15);
        _planeToggle.Pressed += ToggleSwingPlane;
        canvas.AddChild(_planeToggle);
        UpdatePlaneToggle();
    }

    private void ToggleSwingPlane()
    {
        var useVertical = _profile.PlaneTiltDegrees < 45f;
        ChangeProfile(() => _profile.PlaneTiltDegrees = useVertical
            ? VerticalSplitTiltDegrees
            : HorizontalFellTiltDegrees);
        UpdatePlaneToggle();
        Replay();
    }

    private void UpdatePlaneToggle()
    {
        var vertical = _profile.PlaneTiltDegrees >= 45f;
        _planeToggle.Text = vertical
            ? "SWING PLANE: VERTICAL SPLIT\nclick for horizontal fell"
            : "SWING PLANE: HORIZONTAL FELL\nclick for vertical split";
    }

    private void ChangeProfile(Action change)
    {
        change();
        _elapsed = -1f;
        _heldAtContact = false;
        RebuildHandle();
        BuildGuides();
        if (_profile.ValidationError is null)
            ApplyPose(_profile.Sample(0f));
        else
            UpdateHud(new AxeSwingPose(
                AxeSwingPhase.Ready,
                _profile.RestAngleDegrees,
                _profile.ShoulderRestDegrees,
                _profile.ElbowRestDegrees,
                0f,
                false));
    }

    private void ResetProfile()
    {
        if (_opposingMode)
        {
            ResetOpposingLab();
            return;
        }

        if (_biteMode)
        {
            ResetBiteLab();
            return;
        }

        if (_contactMode)
        {
            ResetContactWitness();
            return;
        }

        _profile = AxeSwingProfile.AcceptedV1();
        _restSlider.SetValue(_profile.RestAngleDegrees);
        _startSlider.SetValue(_profile.StartAngleDegrees);
        _contactSlider.SetValue(_profile.ContactAngleDegrees);
        _followSlider.SetValue(_profile.FollowThroughAngleDegrees);
        _upperArmSlider.SetValue(_profile.UpperArmLengthMeters);
        _forearmSlider.SetValue(_profile.ForearmLengthMeters);
        _headBladeSlider?.SetValue(_profile.HeadBladeLengthMeters);
        _headDepthSlider?.SetValue(_profile.HeadDepthMeters);
        _headThicknessSlider?.SetValue(_profile.HeadThicknessMeters);
        _shoulderRestSlider.SetValue(_profile.ShoulderRestDegrees);
        _shoulderWindupSlider.SetValue(_profile.ShoulderWindupDegrees);
        _shoulderContactSlider.SetValue(_profile.ShoulderContactDegrees);
        _shoulderFollowSlider.SetValue(_profile.ShoulderFollowThroughDegrees);
        _elbowRestSlider.SetValue(_profile.ElbowRestDegrees);
        _elbowWindupSlider.SetValue(_profile.ElbowWindupDegrees);
        _elbowContactSlider.SetValue(_profile.ElbowContactDegrees);
        _elbowFollowSlider.SetValue(_profile.ElbowFollowThroughDegrees);
        _windupSlider.SetValue(_profile.WindupSeconds);
        _driveSlider.SetValue(_profile.DriveSeconds);
        _recoverySlider.SetValue(_profile.RecoverySeconds);
        _elapsed = -1f;
        _heldAtContact = false;
        RebuildHandle();
        BuildGuides();
        ApplyPose(_profile.Sample(0f));
        UpdatePlaneToggle();
        GD.Print("AxeSwingLab: defaults restored");
    }

    private void ApplyPose(AxeSwingPose pose)
    {
        var direction = ArcDirection(pose.ShoulderAngleDegrees, _profile.PlaneTiltDegrees);
        var planeNormal = Vector3.Right.Cross(ArcPlaneAxis(_profile.PlaneTiltDegrees)).Normalized();
        var localY = planeNormal.Cross(direction).Normalized();
        _shoulderPivot.Basis = new Basis(direction, localY, planeNormal);
        _elbowPivot.RotationDegrees = new Vector3(0f, 0f, pose.ElbowAngleDegrees);
        var wristAngle = pose.AngleDegrees - pose.ShoulderAngleDegrees - pose.ElbowAngleDegrees;
        _handPivot.RotationDegrees = new Vector3(0f, 0f, wristAngle);
        UpdateHud(pose);
        if (_opposingMode)
            UpdateOpposingVisuals(pose);
        else if (_biteMode)
            UpdateBiteVisuals(pose);
        else if (_contactMode)
            UpdateContactWitness(pose);
    }

    private void UpdateHud(AxeSwingPose pose)
    {
        var error = _profile.ValidationError;
        if (_opposingMode)
        {
            UpdateOpposingHud(pose, error);
            return;
        }

        if (_biteMode)
        {
            UpdateBiteMainHud(pose, error);
            return;
        }
        var handReach = _profile.ShoulderToHandReachMeters(pose.ElbowAngleDegrees);
        var headRadius = _showChoppingHead
            ? _profile.ShoulderToHeadRadiusMeters(pose)
            : _profile.ShoulderToHandleTipRadiusMeters(pose);
        var wristAngle = pose.AngleDegrees - pose.ShoulderAngleDegrees - pose.ElbowAngleDegrees;
        var tipSpeed = _showChoppingHead && error is null
            ? AxeKinematics.Sample(_profile, Math.Clamp(_elapsed, 0f, _profile.TotalSeconds))
                .CuttingCenterSpeedMetersPerSecond
            : MathF.Abs(Mathf.DegToRad(pose.AngularVelocityDegreesPerSecond)) * headRadius;
        if (tipSpeed < 0.05f) tipSpeed = 0f;
        var title = _contactMode
            ? "AXE SWING LAB 03 — CONTACT WITNESS\n"
            : _showChoppingHead
            ? "AXE SWING LAB 02 — CHOPPING HEAD\n"
            : "AXE SWING LAB 01 — ARTICULATED ARC\n";
        var headGeometry = _showChoppingHead
            ? $"Head edge/depth {_profile.HeadBladeLengthMeters:F2} / {_profile.HeadDepthMeters:F2} m\n"
            : string.Empty;
        var legend = _showChoppingHead
            ? "WHITE shoulder / TEAL elbow / GOLD hands / ORANGE cutting center\n\n"
            : "WHITE shoulder / TEAL elbow / GOLD hands / ORANGE handle tip\n\n";
        var boundary = _contactMode
            ? "TRANSLUCENT WITNESS / INSPECTION PAUSE ONLY\nCONTACT DOES NOT END THE SWING\nNO FORCE / WOOD / DAMAGE / NETWORK\n\n"
            : _showChoppingHead
            ? "HEAD GEOMETRY / NO WOOD\nNO HIT OR DAMAGE / NO NETWORK\n\n"
            : "NO HEAD / NO WOOD\nNO HIT OR DAMAGE / NO NETWORK\n\n";
        var controls = _contactMode
            ? "\n[Space/LMB] Replay\n[F] Continue follow-through\n[R] Nominal target"
            : $"\n[Space/LMB] Replay\n[F] Hold contact  {OnOff(_holdAtContact)}\n[L] Loop          {OnOff(_loop)}\n[R] Defaults\n[Tab] Tune";
        _hud.Text =
            title +
            $"Upper / forearm  {_profile.UpperArmLengthMeters:F2} / {_profile.ForearmLengthMeters:F2} m\n" +
            $"Physical handle {_profile.HandleLengthMeters:F2} m\n" +
            headGeometry +
            $"Shoulder / elbow {pose.ShoulderAngleDegrees:F0}° / {pose.ElbowAngleDegrees:F0}°\n" +
            $"Wrist / tool     {wristAngle:F0}° / {pose.AngleDegrees:F0}°\n" +
            $"Arm reach        {handReach:F2} m\n" +
            $"Head radius      {headRadius:F2} m\n\n" +
            legend +
            boundary +
            (error is null
                ? $"Phase     {pose.Phase}\n" +
                  $"Angle     {pose.AngleDegrees:F1}°\n" +
                  $"Tip speed {tipSpeed:F2} m/s\n\n" +
                  $"Start     {_profile.StartAngleDegrees:F0}°\n" +
                  $"Contact   {_profile.ContactAngleDegrees:F0}°\n" +
                  $"Follow    {_profile.FollowThroughAngleDegrees:F0}°\n\n" +
                  $"Plane     {(_profile.PlaneTiltDegrees >= 45f ? "VERTICAL SPLIT" : "HORIZONTAL FELL")}\n\n" +
                  $"Windup    {_profile.WindupSeconds:F2} s\n" +
                  $"Drive     {_profile.DriveSeconds:F2} s\n" +
                  $"Recovery  {_profile.RecoverySeconds:F2} s\n"
                : $"INVALID PROFILE: {error}\n") +
            controls +
            (_heldAtContact
                ? _contactMode
                    ? "\nINSPECTION PAUSE AT FIRST CONTACT — press F to follow through"
                    : "\nHELD AT THE RED CONTACT LINE — press F to continue"
                : string.Empty);
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static StandardMaterial3D Material(Color color, bool unshaded = false) => new()
    {
        AlbedoColor = color,
        Roughness = 0.82f,
        ShadingMode = unshaded
            ? BaseMaterial3D.ShadingModeEnum.Unshaded
            : BaseMaterial3D.ShadingModeEnum.PerPixel,
    };

    private static StandardMaterial3D HeadMaterial(Color color) => new()
    {
        AlbedoColor = color,
        Metallic = 0.72f,
        Roughness = 0.3f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static ArrayMesh ChoppingHeadMesh(AxeSwingProfile profile)
    {
        var halfEdge = profile.HeadBladeLengthMeters * 0.5f;
        var halfPoll = profile.HeadBladeLengthMeters * 0.28f;
        var backY = profile.HeadDepthMeters * 0.27f;
        var bladeY = -profile.HeadDepthMeters;
        var halfBackThickness = profile.HeadThicknessMeters * 0.5f;
        var halfEdgeThickness = profile.HeadThicknessMeters * 0.09f;

        var frontPollInner = new Vector3(-halfPoll, backY, halfBackThickness);
        var frontPollOuter = new Vector3(halfPoll, backY, halfBackThickness);
        var frontEdgeOuter = new Vector3(halfEdge, bladeY, halfEdgeThickness);
        var frontEdgeInner = new Vector3(-halfEdge, bladeY, halfEdgeThickness);
        var backPollInner = new Vector3(-halfPoll, backY, -halfBackThickness);
        var backPollOuter = new Vector3(halfPoll, backY, -halfBackThickness);
        var backEdgeOuter = new Vector3(halfEdge, bladeY, -halfEdgeThickness);
        var backEdgeInner = new Vector3(-halfEdge, bladeY, -halfEdgeThickness);

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        AddQuad(tool, frontPollInner, frontEdgeInner, frontEdgeOuter, frontPollOuter);
        AddQuad(tool, backPollInner, backPollOuter, backEdgeOuter, backEdgeInner);
        AddQuad(tool, frontPollInner, frontPollOuter, backPollOuter, backPollInner);
        AddQuad(tool, frontEdgeInner, backEdgeInner, backEdgeOuter, frontEdgeOuter);
        AddQuad(tool, frontPollOuter, frontEdgeOuter, backEdgeOuter, backPollOuter);
        AddQuad(tool, frontPollInner, backPollInner, backEdgeInner, frontEdgeInner);
        tool.GenerateNormals();
        return tool.Commit();
    }

    private static void AddQuad(SurfaceTool tool, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        tool.AddVertex(a);
        tool.AddVertex(b);
        tool.AddVertex(c);
        tool.AddVertex(a);
        tool.AddVertex(c);
        tool.AddVertex(d);
    }

    private static ImmediateMesh TipPathMesh(AxeSwingProfile profile, bool includeHead, Color color)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, Material(color, unshaded: true));
        const int segments = 72;
        for (var index = 0; index <= segments; index++)
        {
            var elapsed = profile.WindupSeconds + profile.DriveSeconds * index / segments;
            mesh.SurfaceAddVertex(TipOffset(profile, profile.Sample(elapsed), includeHead));
        }
        mesh.SurfaceEnd();
        return mesh;
    }

    private static ImmediateMesh ContactLineMesh(AxeSwingProfile profile, bool includeHead, Color color)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, Material(color, unshaded: true));
        mesh.SurfaceAddVertex(Vector3.Zero);
        mesh.SurfaceAddVertex(TipOffset(profile, profile.Sample(profile.ContactTimeSeconds), includeHead) * 1.08f);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static Vector3 TipOffset(AxeSwingProfile profile, AxeSwingPose pose, bool includeHead)
    {
        var upperArm = ArcDirection(pose.ShoulderAngleDegrees, profile.PlaneTiltDegrees)
            * profile.UpperArmLengthMeters;
        var forearm = ArcDirection(
                pose.ShoulderAngleDegrees + pose.ElbowAngleDegrees,
                profile.PlaneTiltDegrees)
            * profile.ForearmLengthMeters;
        var handle = ArcDirection(pose.AngleDegrees, profile.PlaneTiltDegrees)
            * profile.HandleLengthMeters;
        var cuttingDepth = includeHead
            ? ArcLeadingDirection(pose.AngleDegrees, profile.PlaneTiltDegrees) * profile.HeadDepthMeters
            : Vector3.Zero;
        return upperArm + forearm + handle + cuttingDepth;
    }

    private static Vector3 ArcDirection(float angleDegrees, float planeTiltDegrees)
    {
        var angle = Mathf.DegToRad(angleDegrees);
        return (Vector3.Right * MathF.Cos(angle) + ArcPlaneAxis(planeTiltDegrees) * MathF.Sin(angle)).Normalized();
    }

    private static Vector3 ArcLeadingDirection(float angleDegrees, float planeTiltDegrees)
    {
        var angle = Mathf.DegToRad(angleDegrees);
        return (Vector3.Right * MathF.Sin(angle) - ArcPlaneAxis(planeTiltDegrees) * MathF.Cos(angle)).Normalized();
    }

    private static Vector3 ArcPlaneAxis(float planeTiltDegrees)
    {
        var tilt = Mathf.DegToRad(planeTiltDegrees);
        return new Vector3(0f, MathF.Sin(tilt), MathF.Cos(tilt));
    }
}

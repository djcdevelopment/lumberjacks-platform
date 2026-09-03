using Godot;
using System;
using NumericsVector3 = System.Numerics.Vector3;

namespace CommunitySurvival.Lab;

public partial class AxeSwingLab
{
    private const float WitnessOffsetMeters = 0.12f;
    private const float WitnessThicknessMeters = 0.18f;
    private WitnessPreset _witnessPreset;
    private AxeContactTarget _contactTarget;
    private AxeContactResult _contactResult;
    private bool _contactReleased;
    private MeshInstance3D _witnessBlock;
    private MeshInstance3D _witnessOutline;
    private MeshInstance3D _contactMarker;
    private MeshInstance3D _contactVectors;
    private MeshInstance3D _followThroughGuide;
    private MeshInstance3D _followThroughEnd;
    private Label _contactHud;
    private Button _targetToggle;

    private void BuildContactWitness()
    {
        // The swing plane is almost aligned with the original side camera. An oblique
        // contact view makes penetration direction and the two diagnostic vectors visible.
        _camera.LookAtFromPosition(
            new Vector3(3.4f, 3.4f, 4.6f),
            new Vector3(0.15f, 1.15f, 0f));

        _witnessBlock = new MeshInstance3D
        {
            MaterialOverride = WitnessMaterial(),
        };
        AddChild(_witnessBlock);

        _witnessOutline = new MeshInstance3D();
        AddChild(_witnessOutline);

        _contactMarker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.045f, Height = 0.09f },
            MaterialOverride = Material(new Color(1f, 0.26f, 0.12f), unshaded: true),
            Visible = false,
        };
        AddChild(_contactMarker);

        _contactVectors = new MeshInstance3D { Visible = false };
        AddChild(_contactVectors);

        _followThroughGuide = new MeshInstance3D();
        AddChild(_followThroughGuide);

        _followThroughEnd = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.035f, Height = 0.07f },
            MaterialOverride = Material(new Color(0.7f, 1f, 0.2f), unshaded: true),
            Visible = false,
        };
        AddChild(_followThroughEnd);

        var controls = new CanvasLayer { Layer = 2 };
        AddChild(controls);
        _targetToggle = new Button
        {
            AnchorLeft = 1f,
            AnchorRight = 1f,
            OffsetLeft = -330f,
            OffsetTop = 22f,
            OffsetRight = -22f,
            OffsetBottom = 82f,
        };
        _targetToggle.AddThemeFontSizeOverride("font_size", 15);
        _targetToggle.Pressed += CycleWitnessPreset;
        controls.AddChild(_targetToggle);

        _contactHud = new Label
        {
            AnchorLeft = 1f,
            AnchorRight = 1f,
            OffsetLeft = -330f,
            OffsetTop = 165f,
            OffsetRight = -22f,
            OffsetBottom = 500f,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _contactHud.AddThemeFontSizeOverride("font_size", 15);
        _contactHud.AddThemeColorOverride("font_color", new Color(0.88f, 0.92f, 0.9f));
        _contactHud.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _contactHud.AddThemeConstantOverride("shadow_offset_x", 2);
        _contactHud.AddThemeConstantOverride("shadow_offset_y", 2);
        controls.AddChild(_contactHud);

        ConfigureWitnessPreset(replay: false);
    }

    private void CycleWitnessPreset()
    {
        _witnessPreset = (WitnessPreset)(((int)_witnessPreset + 1) % Enum.GetValues<WitnessPreset>().Length);
        ConfigureWitnessPreset(replay: true);
    }

    private void ResetContactWitness()
    {
        _witnessPreset = WitnessPreset.Nominal;
        ConfigureWitnessPreset(replay: true);
    }

    private void ConfigureWitnessPreset(bool replay)
    {
        var target = AxeContactSolver.AcceptedWitnessTarget(_profile);
        target = _witnessPreset switch
        {
            WitnessPreset.Near => target with { Center = target.Center + target.Normal * WitnessOffsetMeters },
            WitnessPreset.Far => target with { Center = target.Center - target.Normal * WitnessOffsetMeters },
            WitnessPreset.High => target with { Center = target.Center + target.Up * WitnessOffsetMeters },
            WitnessPreset.Low => target with { Center = target.Center - target.Up * WitnessOffsetMeters },
            _ => target,
        };

        _contactTarget = target;
        _contactResult = AxeContactSolver.Solve(_profile, target);
        RebuildWitnessVisuals();
        UpdateTargetToggle();
        UpdateContactHud(reached: false);

        GD.Print(
            $"AxeContactLab: target={_witnessPreset} result={_contactResult.Classification} " +
            $"time={_contactResult.ContactTimeSeconds:F4}s speed={_contactResult.SpeedMetersPerSecond:F2}m/s " +
            $"incidence={_contactResult.IncidenceAngleDegrees:F1}deg");

        if (replay)
            Replay();
    }

    private void RebuildWitnessVisuals()
    {
        var normal = ToGodot(_contactTarget.Normal);
        var right = ToGodot(_contactTarget.Right);
        var up = ToGodot(_contactTarget.Up);
        var faceCenter = _shoulderPosition + ToGodot(_contactTarget.Center);

        _witnessBlock.Mesh = new BoxMesh
        {
            Size = new Vector3(
                _contactTarget.HalfWidthMeters * 2f,
                _contactTarget.HalfHeightMeters * 2f,
                WitnessThicknessMeters),
        };
        _witnessBlock.Basis = new Basis(right, up, normal);
        _witnessBlock.Position = faceCenter - normal * (WitnessThicknessMeters * 0.5f);

        _witnessOutline.Mesh = WitnessOutlineMesh(_contactTarget);
        _witnessOutline.Position = _shoulderPosition;

        _contactMarker.Position = _shoulderPosition + ToGodot(_contactResult.ContactPoint);
        _contactVectors.Mesh = ContactVectorMesh(_contactResult);
        _contactVectors.Position = _shoulderPosition;
        _followThroughGuide.Mesh = FollowThroughMesh(_profile, _contactResult);
        _followThroughGuide.Position = _shoulderPosition;
        var driveEnd = _profile.WindupSeconds + _profile.DriveSeconds;
        _followThroughEnd.Position = _shoulderPosition +
            ToGodot(AxeKinematics.Sample(_profile, driveEnd).CuttingCenter);
        _contactMarker.Visible = false;
        _contactVectors.Visible = false;
        _followThroughGuide.Visible = _contactResult.Hit;
        _followThroughEnd.Visible = _contactResult.Hit;
    }

    private void UpdateTargetToggle()
    {
        _targetToggle.Text =
            $"TARGET: {_witnessPreset.ToString().ToUpperInvariant()}\n" +
            "click: nominal / near / far / high / low";
    }

    private void UpdateContactWitness(AxeSwingPose pose)
    {
        _ = pose;
        var reached = _contactResult.Hit &&
            (_heldAtContact || (_contactReleased && _elapsed >= _contactResult.ContactTimeSeconds));
        _contactMarker.Visible = reached;
        _contactVectors.Visible = reached;
        UpdateContactHud(reached);
    }

    private void UpdateContactHud(bool reached)
    {
        var status = !_contactResult.Hit
            ? $"MISS: {_contactResult.Classification.ToString().ToUpperInvariant()}"
            : reached
                ? "INSPECTION PAUSE — FIRST CONTACT"
                : "PREDICTED: CUTTING EDGE HIT";
        var driveEnd = _profile.WindupSeconds + _profile.DriveSeconds;
        var followPath = _contactResult.Hit
            ? AxeKinematics.CuttingCenterPathLength(
                _profile,
                _contactResult.ContactTimeSeconds,
                driveEnd)
            : 0f;
        var contactAngle = AxeKinematics.Sample(_profile, _contactResult.ContactTimeSeconds)
            .Pose.AngleDegrees;
        var followArc = _contactResult.Hit
            ? MathF.Abs(contactAngle - _profile.FollowThroughAngleDegrees)
            : 0f;
        _contactHud.Text =
            $"{status}\n\n" +
            $"Time        {_contactResult.ContactTimeSeconds:F4} s\n" +
            $"Edge speed  {_contactResult.SpeedMetersPerSecond:F2} m/s\n" +
            $"Incidence   {_contactResult.IncidenceAngleDegrees:F1}°\n" +
            $"Follow path {followPath:F2} m\n" +
            $"Follow arc  {followArc:F0}°\n" +
            $"Face X / Y  {_contactResult.FaceHorizontalMeters:+0.000;-0.000;0.000} / " +
            $"{_contactResult.FaceVerticalMeters:+0.000;-0.000;0.000} m\n\n" +
            "RED = first touch\nYELLOW = witness normal\nCYAN = incoming velocity\n" +
            "LIME = committed follow-through\n" +
            (reached ? "\nPress F to continue through" : "\nSpace/LMB replays and auto-pauses");
    }

    private static StandardMaterial3D WitnessMaterial() => new()
    {
        AlbedoColor = new Color(0.19f, 0.24f, 0.27f, 0.34f),
        Roughness = 0.72f,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static ImmediateMesh FollowThroughMesh(
        AxeSwingProfile profile,
        AxeContactResult result)
    {
        var mesh = new ImmediateMesh();
        if (!result.Hit)
            return mesh;

        const int segments = 48;
        var driveEnd = profile.WindupSeconds + profile.DriveSeconds;
        var previous = ToGodot(AxeKinematics.Sample(profile, result.ContactTimeSeconds).CuttingCenter);
        mesh.SurfaceBegin(
            Mesh.PrimitiveType.Lines,
            Material(new Color(0.7f, 1f, 0.2f), unshaded: true));
        for (var index = 1; index <= segments; index++)
        {
            var elapsed = result.ContactTimeSeconds +
                (driveEnd - result.ContactTimeSeconds) * index / segments;
            var current = ToGodot(AxeKinematics.Sample(profile, elapsed).CuttingCenter);
            AddLine(mesh, previous, current);
            previous = current;
        }
        mesh.SurfaceEnd();
        return mesh;
    }

    private static ImmediateMesh WitnessOutlineMesh(AxeContactTarget target)
    {
        var center = ToGodot(target.Center);
        var right = ToGodot(target.Right) * target.HalfWidthMeters;
        var up = ToGodot(target.Up) * target.HalfHeightMeters;
        var normal = ToGodot(target.Normal) * 0.003f;
        var topLeft = center - right + up + normal;
        var topRight = center + right + up + normal;
        var bottomRight = center + right - up + normal;
        var bottomLeft = center - right - up + normal;

        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, Material(new Color(0.94f, 0.76f, 0.2f), unshaded: true));
        AddLine(mesh, topLeft, topRight);
        AddLine(mesh, topRight, bottomRight);
        AddLine(mesh, bottomRight, bottomLeft);
        AddLine(mesh, bottomLeft, topLeft);
        AddLine(mesh, center - ToGodot(target.Right) * 0.055f + normal, center + ToGodot(target.Right) * 0.055f + normal);
        AddLine(mesh, center - ToGodot(target.Up) * 0.055f + normal, center + ToGodot(target.Up) * 0.055f + normal);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static ImmediateMesh ContactVectorMesh(AxeContactResult result)
    {
        var point = ToGodot(result.ContactPoint);
        var normal = ToGodot(result.TargetNormal);
        var velocity = result.SpeedMetersPerSecond > 0f
            ? ToGodot(NumericsVector3.Normalize(result.CuttingCenterVelocityMetersPerSecond))
            : Vector3.Zero;
        var mesh = new ImmediateMesh();

        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, Material(new Color(1f, 0.82f, 0.16f), unshaded: true));
        AddLine(mesh, point, point + normal * 0.28f);
        mesh.SurfaceEnd();

        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, Material(new Color(0.2f, 0.9f, 1f), unshaded: true));
        AddLine(mesh, point - velocity * 0.34f, point);
        mesh.SurfaceEnd();
        return mesh;
    }

    private static void AddLine(ImmediateMesh mesh, Vector3 from, Vector3 to)
    {
        mesh.SurfaceAddVertex(from);
        mesh.SurfaceAddVertex(to);
    }

    private static Vector3 ToGodot(NumericsVector3 value) => new(value.X, value.Y, value.Z);

    private enum WitnessPreset
    {
        Nominal,
        Near,
        Far,
        High,
        Low,
    }
}

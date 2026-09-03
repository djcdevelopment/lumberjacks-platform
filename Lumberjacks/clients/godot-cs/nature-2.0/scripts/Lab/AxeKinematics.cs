#nullable enable

using System;
using System.Numerics;

namespace CommunitySurvival.Lab;

/// <summary>
/// Pure, deterministic forward kinematics for the accepted articulated axe rig.
/// Coordinates are shoulder-relative and match the Godot lab: +X is radial from
/// the player and the swing-plane axis blends +Y into +Z.
/// </summary>
public static class AxeKinematics
{
    private const float VelocitySampleSeconds = 0.00025f;

    public static float CuttingCenterPathLength(
        AxeSwingProfile profile,
        float fromSeconds,
        float toSeconds,
        int segments = 256)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.EnsureValid();
        if (segments <= 0)
            throw new ArgumentOutOfRangeException(nameof(segments), "Path segments must be positive.");

        var from = Math.Clamp(fromSeconds, 0f, profile.TotalSeconds);
        var to = Math.Clamp(toSeconds, 0f, profile.TotalSeconds);
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(toSeconds), "Path end cannot precede path start.");
        if (to == from)
            return 0f;

        var previous = Sample(profile, from).CuttingCenter;
        var length = 0f;
        for (var index = 1; index <= segments; index++)
        {
            var elapsed = from + (to - from) * index / segments;
            var current = Sample(profile, elapsed).CuttingCenter;
            length += Vector3.Distance(previous, current);
            previous = current;
        }

        return length;
    }

    public static AxeKinematicsSample Sample(AxeSwingProfile profile, float elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.EnsureValid();

        var elapsed = Math.Clamp(elapsedSeconds, 0f, profile.TotalSeconds);
        var geometry = SampleGeometry(profile, elapsed);
        var beforeTime = Math.Max(0f, elapsed - VelocitySampleSeconds);
        var afterTime = Math.Min(profile.TotalSeconds, elapsed + VelocitySampleSeconds);
        var duration = afterTime - beforeTime;
        var velocity = duration > 0f
            ? (SampleGeometry(profile, afterTime).CuttingCenter -
               SampleGeometry(profile, beforeTime).CuttingCenter) / duration
            : Vector3.Zero;

        return new AxeKinematicsSample(
            elapsed,
            geometry.Pose,
            geometry.Elbow,
            geometry.Hand,
            geometry.HeadMount,
            geometry.CuttingCenter,
            geometry.EdgeInner,
            geometry.EdgeOuter,
            geometry.ToolDirection,
            geometry.LeadingDirection,
            velocity);
    }

    private static AxeGeometry SampleGeometry(AxeSwingProfile profile, float elapsedSeconds)
    {
        var pose = profile.Sample(elapsedSeconds);
        var shoulderDirection = ArcDirection(pose.ShoulderAngleDegrees, profile.PlaneTiltDegrees);
        var forearmDirection = ArcDirection(
            pose.ShoulderAngleDegrees + pose.ElbowAngleDegrees,
            profile.PlaneTiltDegrees);
        var toolDirection = ArcDirection(pose.AngleDegrees, profile.PlaneTiltDegrees);
        var leadingDirection = ArcLeadingDirection(pose.AngleDegrees, profile.PlaneTiltDegrees);

        var elbow = shoulderDirection * profile.UpperArmLengthMeters;
        var hand = elbow + forearmDirection * profile.ForearmLengthMeters;
        var headMount = hand + toolDirection * profile.HandleLengthMeters;
        var cuttingCenter = headMount + leadingDirection * profile.HeadDepthMeters;
        var halfEdge = toolDirection * (profile.HeadBladeLengthMeters * 0.5f);

        return new AxeGeometry(
            pose,
            elbow,
            hand,
            headMount,
            cuttingCenter,
            cuttingCenter - halfEdge,
            cuttingCenter + halfEdge,
            toolDirection,
            leadingDirection);
    }

    private static Vector3 ArcDirection(float angleDegrees, float planeTiltDegrees)
    {
        var angle = DegreesToRadians(angleDegrees);
        return Vector3.Normalize(
            Vector3.UnitX * MathF.Cos(angle) +
            ArcPlaneAxis(planeTiltDegrees) * MathF.Sin(angle));
    }

    private static Vector3 ArcLeadingDirection(float angleDegrees, float planeTiltDegrees)
    {
        var angle = DegreesToRadians(angleDegrees);
        return Vector3.Normalize(
            Vector3.UnitX * MathF.Sin(angle) -
            ArcPlaneAxis(planeTiltDegrees) * MathF.Cos(angle));
    }

    private static Vector3 ArcPlaneAxis(float planeTiltDegrees)
    {
        var tilt = DegreesToRadians(planeTiltDegrees);
        return new Vector3(0f, MathF.Sin(tilt), MathF.Cos(tilt));
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;

    private readonly record struct AxeGeometry(
        AxeSwingPose Pose,
        Vector3 Elbow,
        Vector3 Hand,
        Vector3 HeadMount,
        Vector3 CuttingCenter,
        Vector3 EdgeInner,
        Vector3 EdgeOuter,
        Vector3 ToolDirection,
        Vector3 LeadingDirection);
}

public readonly record struct AxeKinematicsSample(
    float ElapsedSeconds,
    AxeSwingPose Pose,
    Vector3 Elbow,
    Vector3 Hand,
    Vector3 HeadMount,
    Vector3 CuttingCenter,
    Vector3 EdgeInner,
    Vector3 EdgeOuter,
    Vector3 ToolDirection,
    Vector3 LeadingDirection,
    Vector3 CuttingCenterVelocityMetersPerSecond)
{
    public float CuttingCenterSpeedMetersPerSecond => CuttingCenterVelocityMetersPerSecond.Length();
}

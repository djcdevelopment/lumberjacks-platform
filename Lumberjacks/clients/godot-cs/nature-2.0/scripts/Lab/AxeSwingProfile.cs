#nullable enable

using System;
using System.IO;

namespace CommunitySurvival.Lab;

/// <summary>
/// One deliberately small experiment: an articulated two-link arm driving a rigid handle
/// and chopping-head geometry. It models leverage, arc, timing, and head proportions only.
/// Contact, wood response, and damage do not belong here; those are later labs once a human
/// accepts this motion and the head orientation.
/// </summary>
public sealed class AxeSwingProfile
{
    public const string AcceptedCalibrationId = "accepted-axe-v1";

    public float RestAngleDegrees { get; set; } = -18f;
    public float StartAngleDegrees { get; set; } = 108f;
    public float ContactAngleDegrees { get; set; } = -4f;
    public float FollowThroughAngleDegrees { get; set; } = -38f;
    public float PlaneTiltDegrees { get; set; } = 22f;
    public float WindupSeconds { get; set; } = 0.24f;
    public float DriveSeconds { get; set; } = 0.28f;
    public float RecoverySeconds { get; set; } = 0.42f;
    public float HandleLengthMeters { get; set; } = 0.76f;
    public float HeadBladeLengthMeters { get; set; } = 0.34f;
    public float HeadDepthMeters { get; set; } = 0.27f;
    public float HeadThicknessMeters { get; set; } = 0.085f;
    public float UpperArmLengthMeters { get; set; } = 0.28f;
    public float ForearmLengthMeters { get; set; } = 0.32f;
    public float ShoulderRestDegrees { get; set; } = -8f;
    public float ShoulderWindupDegrees { get; set; } = 65f;
    public float ShoulderContactDegrees { get; set; } = -18f;
    public float ShoulderFollowThroughDegrees { get; set; } = -45f;
    public float ElbowRestDegrees { get; set; } = -12f;
    public float ElbowWindupDegrees { get; set; } = 78f;
    public float ElbowContactDegrees { get; set; } = 8f;
    public float ElbowFollowThroughDegrees { get; set; } = -16f;

    public float TotalSeconds => WindupSeconds + DriveSeconds + RecoverySeconds;

    public static AxeSwingProfile AcceptedV1() => new();

    public float ContactTimeSeconds
    {
        get
        {
            EnsureValid();
            var driveFraction = (StartAngleDegrees - ContactAngleDegrees)
                / (StartAngleDegrees - FollowThroughAngleDegrees);
            return WindupSeconds + MathF.Sqrt(driveFraction) * DriveSeconds;
        }
    }

    public string? ValidationError
    {
        get
        {
            if (!AllFinite()) return "All profile values must be finite.";
            if (HandleLengthMeters is < 0.2f or > 1.5f)
                return "Handle length must remain between 0.2 m and 1.5 m.";
            if (HeadBladeLengthMeters is < 0.18f or > 0.55f)
                return "Head blade length must remain between 0.18 m and 0.55 m.";
            if (HeadDepthMeters is < 0.1f or > 0.45f)
                return "Head depth must remain between 0.1 m and 0.45 m.";
            if (HeadThicknessMeters is < 0.03f or > 0.16f)
                return "Head thickness must remain between 0.03 m and 0.16 m.";
            if (UpperArmLengthMeters is < 0.15f or > 0.55f || ForearmLengthMeters is < 0.15f or > 0.55f)
                return "Upper-arm and forearm proxies must remain between 0.15 m and 0.55 m.";
            if (!JointAnglesInRange())
                return "Shoulder and elbow joint values must remain between -145 and 145 degrees.";
            if (PlaneTiltDegrees is < 0f or > 90f)
                return "Swing-plane tilt must remain between horizontal (0) and vertical (90).";
            if (WindupSeconds <= 0.01f || DriveSeconds <= 0.01f || RecoverySeconds <= 0.01f)
                return "Every phase must last more than 0.01 seconds.";
            if (!(StartAngleDegrees > ContactAngleDegrees &&
                  ContactAngleDegrees > FollowThroughAngleDegrees))
                return "The fixed drive arc must cross start > contact > follow-through.";
            if (!(StartAngleDegrees > RestAngleDegrees &&
                  RestAngleDegrees > FollowThroughAngleDegrees))
                return "The resting angle must sit inside the drive arc.";
            return null;
        }
    }

    public AxeSwingPose Sample(float elapsedSeconds)
    {
        EnsureValid();

        var elapsed = Math.Clamp(elapsedSeconds, 0f, TotalSeconds);
        if (elapsed <= 0f)
            return new AxeSwingPose(
                AxeSwingPhase.Ready,
                RestAngleDegrees,
                ShoulderRestDegrees,
                ElbowRestDegrees,
                0f,
                false);

        if (elapsed < WindupSeconds)
        {
            var t = elapsed / WindupSeconds;
            return new AxeSwingPose(
                AxeSwingPhase.Windup,
                Lerp(RestAngleDegrees, StartAngleDegrees, SmoothStep(t)),
                Lerp(ShoulderRestDegrees, ShoulderWindupDegrees, SmoothStep(t)),
                Lerp(ElbowRestDegrees, ElbowWindupDegrees, SmoothStep(t)),
                (StartAngleDegrees - RestAngleDegrees) * SmoothStepDerivative(t) / WindupSeconds,
                false);
        }

        var driveEnd = WindupSeconds + DriveSeconds;
        if (elapsed < driveEnd)
        {
            var t = (elapsed - WindupSeconds) / DriveSeconds;
            var driveProgress = t * t;
            var angle = Lerp(StartAngleDegrees, FollowThroughAngleDegrees, driveProgress);
            var velocity = (FollowThroughAngleDegrees - StartAngleDegrees) * 2f * t / DriveSeconds;
            return new AxeSwingPose(
                AxeSwingPhase.Drive,
                angle,
                JointAtDriveProgress(
                    ShoulderWindupDegrees,
                    ShoulderContactDegrees,
                    ShoulderFollowThroughDegrees,
                    driveProgress),
                JointAtDriveProgress(
                    ElbowWindupDegrees,
                    ElbowContactDegrees,
                    ElbowFollowThroughDegrees,
                    driveProgress),
                velocity,
                elapsed >= ContactTimeSeconds);
        }

        if (elapsed < TotalSeconds)
        {
            var t = (elapsed - driveEnd) / RecoverySeconds;
            return new AxeSwingPose(
                AxeSwingPhase.Recovery,
                Lerp(FollowThroughAngleDegrees, RestAngleDegrees, SmoothStep(t)),
                Lerp(ShoulderFollowThroughDegrees, ShoulderRestDegrees, SmoothStep(t)),
                Lerp(ElbowFollowThroughDegrees, ElbowRestDegrees, SmoothStep(t)),
                (RestAngleDegrees - FollowThroughAngleDegrees) * SmoothStepDerivative(t) / RecoverySeconds,
                true);
        }

        return new AxeSwingPose(
            AxeSwingPhase.Complete,
            RestAngleDegrees,
            ShoulderRestDegrees,
            ElbowRestDegrees,
            0f,
            true);
    }

    public float ShoulderToHandReachMeters(float elbowAngleDegrees) =>
        LinkReach(UpperArmLengthMeters, ForearmLengthMeters, elbowAngleDegrees);

    public float ShoulderToHandleTipRadiusMeters(AxeSwingPose pose)
    {
        var shoulder = DegreesToRadians(pose.ShoulderAngleDegrees);
        var forearm = DegreesToRadians(pose.ShoulderAngleDegrees + pose.ElbowAngleDegrees);
        var handle = DegreesToRadians(pose.AngleDegrees);
        var x = UpperArmLengthMeters * MathF.Cos(shoulder)
            + ForearmLengthMeters * MathF.Cos(forearm)
            + HandleLengthMeters * MathF.Cos(handle);
        var y = UpperArmLengthMeters * MathF.Sin(shoulder)
            + ForearmLengthMeters * MathF.Sin(forearm)
            + HandleLengthMeters * MathF.Sin(handle);
        return MathF.Sqrt(x * x + y * y);
    }

    public float ShoulderToHeadRadiusMeters(AxeSwingPose pose)
    {
        var shoulder = DegreesToRadians(pose.ShoulderAngleDegrees);
        var forearm = DegreesToRadians(pose.ShoulderAngleDegrees + pose.ElbowAngleDegrees);
        var handle = DegreesToRadians(pose.AngleDegrees);
        var x = UpperArmLengthMeters * MathF.Cos(shoulder)
            + ForearmLengthMeters * MathF.Cos(forearm)
            + HandleLengthMeters * MathF.Cos(handle)
            + HeadDepthMeters * MathF.Sin(handle);
        var y = UpperArmLengthMeters * MathF.Sin(shoulder)
            + ForearmLengthMeters * MathF.Sin(forearm)
            + HandleLengthMeters * MathF.Sin(handle)
            - HeadDepthMeters * MathF.Cos(handle);
        return MathF.Sqrt(x * x + y * y);
    }

    public void EnsureValid()
    {
        if (ValidationError is { } error)
            throw new InvalidDataException(error);
    }

    private bool AllFinite() =>
        float.IsFinite(RestAngleDegrees) &&
        float.IsFinite(StartAngleDegrees) &&
        float.IsFinite(ContactAngleDegrees) &&
        float.IsFinite(FollowThroughAngleDegrees) &&
        float.IsFinite(PlaneTiltDegrees) &&
        float.IsFinite(WindupSeconds) &&
        float.IsFinite(DriveSeconds) &&
        float.IsFinite(RecoverySeconds) &&
        float.IsFinite(HandleLengthMeters) &&
        float.IsFinite(HeadBladeLengthMeters) &&
        float.IsFinite(HeadDepthMeters) &&
        float.IsFinite(HeadThicknessMeters) &&
        float.IsFinite(UpperArmLengthMeters) &&
        float.IsFinite(ForearmLengthMeters) &&
        float.IsFinite(ShoulderRestDegrees) &&
        float.IsFinite(ShoulderWindupDegrees) &&
        float.IsFinite(ShoulderContactDegrees) &&
        float.IsFinite(ShoulderFollowThroughDegrees) &&
        float.IsFinite(ElbowRestDegrees) &&
        float.IsFinite(ElbowWindupDegrees) &&
        float.IsFinite(ElbowContactDegrees) &&
        float.IsFinite(ElbowFollowThroughDegrees);

    private bool JointAnglesInRange() =>
        InRange(ShoulderRestDegrees, -145f, 145f) &&
        InRange(ShoulderWindupDegrees, -145f, 145f) &&
        InRange(ShoulderContactDegrees, -145f, 145f) &&
        InRange(ShoulderFollowThroughDegrees, -145f, 145f) &&
        InRange(ElbowRestDegrees, -145f, 145f) &&
        InRange(ElbowWindupDegrees, -145f, 145f) &&
        InRange(ElbowContactDegrees, -145f, 145f) &&
        InRange(ElbowFollowThroughDegrees, -145f, 145f);

    private float JointAtDriveProgress(float windup, float contact, float followThrough, float driveProgress)
    {
        var contactProgress = (StartAngleDegrees - ContactAngleDegrees)
            / (StartAngleDegrees - FollowThroughAngleDegrees);
        if (driveProgress <= contactProgress)
        {
            var beforeContact = SmoothStep(driveProgress / contactProgress);
            return Lerp(windup, contact, beforeContact);
        }

        var afterContact = SmoothStep((driveProgress - contactProgress) / (1f - contactProgress));
        return Lerp(contact, followThrough, afterContact);
    }

    private static float LinkReach(float firstLength, float secondLength, float bendDegrees)
    {
        var bendRadians = bendDegrees * MathF.PI / 180f;
        return MathF.Sqrt(
            firstLength * firstLength +
            secondLength * secondLength +
            2f * firstLength * secondLength * MathF.Cos(bendRadians));
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;
    private static bool InRange(float value, float minimum, float maximum) => value >= minimum && value <= maximum;
    private static float SmoothStep(float t) => t * t * (3f - 2f * t);
    private static float SmoothStepDerivative(float t) => 6f * t * (1f - t);
}

public enum AxeSwingPhase
{
    Ready,
    Windup,
    Drive,
    Recovery,
    Complete,
}

public readonly record struct AxeSwingPose(
    AxeSwingPhase Phase,
    float AngleDegrees,
    float ShoulderAngleDegrees,
    float ElbowAngleDegrees,
    float AngularVelocityDegreesPerSecond,
    bool ContactCrossed);

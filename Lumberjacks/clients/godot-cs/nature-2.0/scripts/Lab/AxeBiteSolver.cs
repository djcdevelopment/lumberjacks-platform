#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;

namespace CommunitySurvival.Lab;

/// <summary>
/// Frame-independent first-bite model. The accepted free trajectory supplies
/// geometry and arrival speed. Uniform wood work and commanded aim-through are
/// independent limits on the embedded pose.
/// </summary>
public static class AxeBiteSolver
{
    private const int PathSegments = 4096;

    public static AxeBiteResult Solve(
        AxeSwingProfile profile,
        WoodCutState cutState,
        AxeBiteIntent intent,
        AxeBiteCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(cutState);
        profile.EnsureValid();
        intent.EnsureValid();
        calibration ??= AxeBiteCalibration.AcceptedV1();
        calibration.EnsureValid();

        var contactTime = profile.ContactTimeSeconds;
        var driveEnd = profile.WindupSeconds + profile.DriveSeconds;
        var contact = AxeKinematics.Sample(profile, contactTime);
        var frame = WoodCutFrame.FromAcceptedContact(contact);
        var arrivalVelocity = contact.CuttingCenterVelocityMetersPerSecond * intent.EffortMultiplier;
        var arrivalSpeed = arrivalVelocity.Length();
        var deliveredEnergy = 0.5f * calibration.EffectiveHeadMassKilograms *
            arrivalSpeed * arrivalSpeed;
        var usedEnergy = 0f;
        var path = new List<AxeBitePathPoint>(256)
        {
            PathPoint(frame, contact),
        };

        var previous = contact;
        var previousPoint = path[0];
        var stopReason = AxeBiteStopReason.PathExhausted;
        var stopSample = contact;

        for (var index = 1; index <= PathSegments; index++)
        {
            var elapsed = contactTime + (driveEnd - contactTime) * index / PathSegments;
            var current = AxeKinematics.Sample(profile, elapsed);
            var currentPoint = PathPoint(frame, current);
            var segmentLength = Vector3.Distance(previous.CuttingCenter, current.CuttingCenter);
            var midDepth = (previousPoint.DepthMeters + currentPoint.DepthMeters) * 0.5f;
            var midVertical = (previousPoint.VerticalMeters + currentPoint.VerticalMeters) * 0.5f;
            var materialMultiplier = cutState.ResistanceMultiplierAt(midDepth, midVertical);
            var wedgeGrowth = 1f + calibration.WedgeGrowthFactor *
                Math.Clamp(MathF.Max(0f, midDepth) / profile.HeadDepthMeters, 0f, 1f);
            var segmentWork = calibration.UniformResistanceJoulesPerMeter *
                segmentLength * materialMultiplier * wedgeGrowth;

            var aimFraction = CrossingFraction(
                previousPoint.DepthMeters,
                currentPoint.DepthMeters,
                intent.AimThroughMeters);
            var energyFraction = segmentWork > 0f && usedEnergy + segmentWork >= deliveredEnergy
                ? Math.Clamp((deliveredEnergy - usedEnergy) / segmentWork, 0f, 1f)
                : float.PositiveInfinity;

            if (aimFraction <= 1f || energyFraction <= 1f)
            {
                var fraction = MathF.Min(aimFraction, energyFraction);
                var stopTime = previous.ElapsedSeconds +
                    (current.ElapsedSeconds - previous.ElapsedSeconds) * fraction;
                stopSample = AxeKinematics.Sample(profile, stopTime);
                var finalPoint = PathPoint(frame, stopSample);
                path.Add(finalPoint);
                usedEnergy += segmentWork * fraction;
                stopReason = energyFraction <= aimFraction
                    ? AxeBiteStopReason.EnergyLimited
                    : AxeBiteStopReason.CommitmentLimited;
                break;
            }

            usedEnergy += segmentWork;
            path.Add(currentPoint);
            stopSample = current;
            previous = current;
            previousPoint = currentPoint;
        }

        var wedgeHalfAngle = MathF.Atan(
            MathF.Max(0f, profile.HeadThicknessMeters - calibration.EdgeThicknessMeters) * 0.5f /
            profile.HeadDepthMeters);
        var finalState = cutState.ApplyKerf(
            path,
            calibration.EdgeThicknessMeters,
            wedgeHalfAngle,
            calibration.CutWidthMeters);
        var finalPointInFrame = path[^1];
        return new AxeBiteResult(
            frame,
            intent,
            stopReason,
            contactTime,
            stopSample.ElapsedSeconds,
            contact,
            stopSample,
            arrivalVelocity,
            deliveredEnergy,
            MathF.Min(usedEnergy, deliveredEnergy),
            MathF.Max(0f, finalPointInFrame.DepthMeters),
            path.ToArray(),
            finalState);
    }

    private static AxeBitePathPoint PathPoint(WoodCutFrame frame, AxeKinematicsSample sample)
    {
        var offset = sample.CuttingCenter - frame.SurfacePoint;
        return new AxeBitePathPoint(
            sample.ElapsedSeconds,
            sample.CuttingCenter,
            Vector3.Dot(offset, frame.InwardNormal),
            Vector3.Dot(offset, frame.Vertical));
    }

    private static float CrossingFraction(float from, float to, float target)
    {
        if (from >= target) return 0f;
        if (to < target || to <= from) return float.PositiveInfinity;
        return Math.Clamp((target - from) / (to - from), 0f, 1f);
    }
}

public readonly record struct AxeBiteIntent(float EffortMultiplier, float AimThroughMeters)
{
    public static AxeBiteIntent Nominal => new(1f, 0.10f);

    public void EnsureValid()
    {
        if (!float.IsFinite(EffortMultiplier) || EffortMultiplier is < 0.6f or > 1.4f)
            throw new ArgumentOutOfRangeException(nameof(EffortMultiplier));
        if (!float.IsFinite(AimThroughMeters) || AimThroughMeters is < 0.02f or > 0.20f)
            throw new ArgumentOutOfRangeException(nameof(AimThroughMeters));
    }
}

public sealed class AxeBiteCalibration
{
    public float EffectiveHeadMassKilograms { get; init; } = 1.5f;
    public float UniformResistanceJoulesPerMeter { get; init; } = 2000f;
    public float WedgeGrowthFactor { get; init; } = 1.5f;
    public float EdgeThicknessMeters { get; init; } = 0.008f;
    public float CutWidthMeters { get; init; } = 0.10f;

    public static AxeBiteCalibration AcceptedV1() => new();

    public void EnsureValid()
    {
        if (!float.IsFinite(EffectiveHeadMassKilograms) || EffectiveHeadMassKilograms <= 0f)
            throw new ArgumentOutOfRangeException(nameof(EffectiveHeadMassKilograms));
        if (!float.IsFinite(UniformResistanceJoulesPerMeter) || UniformResistanceJoulesPerMeter <= 0f)
            throw new ArgumentOutOfRangeException(nameof(UniformResistanceJoulesPerMeter));
        if (!float.IsFinite(WedgeGrowthFactor) || WedgeGrowthFactor < 0f)
            throw new ArgumentOutOfRangeException(nameof(WedgeGrowthFactor));
        if (!float.IsFinite(EdgeThicknessMeters) || EdgeThicknessMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(EdgeThicknessMeters));
        if (!float.IsFinite(CutWidthMeters) || CutWidthMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(CutWidthMeters));
    }
}

public readonly record struct WoodCutFrame(
    Vector3 SurfacePoint,
    Vector3 OutwardNormal,
    Vector3 InwardNormal,
    Vector3 Vertical,
    Vector3 Tangent)
{
    public static WoodCutFrame FromAcceptedContact(AxeKinematicsSample contact)
    {
        var horizontalVelocity = new Vector3(
            contact.CuttingCenterVelocityMetersPerSecond.X,
            0f,
            contact.CuttingCenterVelocityMetersPerSecond.Z);
        if (horizontalVelocity.LengthSquared() < 0.000001f)
            throw new ArgumentException("The accepted contact must have horizontal motion.", nameof(contact));

        var inward = Vector3.Normalize(horizontalVelocity);
        var outward = -inward;
        var vertical = Vector3.UnitY;
        var tangent = Vector3.Normalize(Vector3.Cross(vertical, outward));
        return new WoodCutFrame(contact.CuttingCenter, outward, inward, vertical, tangent);
    }
}

public readonly record struct AxeBitePathPoint(
    float TrajectoryTimeSeconds,
    Vector3 CuttingCenter,
    float DepthMeters,
    float VerticalMeters);

public enum AxeBiteStopReason
{
    EnergyLimited,
    CommitmentLimited,
    PathExhausted,
    Miss,
}

public sealed record AxeBiteResult(
    WoodCutFrame Frame,
    AxeBiteIntent Intent,
    AxeBiteStopReason StopReason,
    float ContactTrajectoryTimeSeconds,
    float StopTrajectoryTimeSeconds,
    AxeKinematicsSample ContactSample,
    AxeKinematicsSample StopSample,
    Vector3 ArrivalVelocityMetersPerSecond,
    float DeliveredEnergyJoules,
    float UsedEnergyJoules,
    float PenetrationMeters,
    AxeBitePathPoint[] Path,
    WoodCutState FinalState)
{
    public float ArrivalSpeedMetersPerSecond => ArrivalVelocityMetersPerSecond.Length();
    public float UnusedEnergyJoules => MathF.Max(0f, DeliveredEnergyJoules - UsedEnergyJoules);
}

/// <summary>
/// Separates the solved bite from whether the head is retained by the resulting
/// cut. The caller supplies an explicit roll so playback remains reproducible.
/// </summary>
public static class AxeRetentionModel
{
    public static AxeRetentionResult Evaluate(
        AxeBiteResult bite,
        float roll,
        AxeRetentionContext context)
    {
        ArgumentNullException.ThrowIfNull(bite);
        if (!float.IsFinite(roll) || roll is < 0f or >= 1f)
            throw new ArgumentOutOfRangeException(nameof(roll));
        context.EnsureValid();

        var depthFactor = Math.Clamp(bite.PenetrationMeters / 0.10f, 0f, 1f);
        var effortFactor = Math.Clamp(
            (bite.Intent.EffortMultiplier - 0.6f) / 0.8f,
            0f,
            1f);
        var chance = 0.08f +
            depthFactor * 0.30f +
            effortFactor * 0.18f +
            (context.ChipReleased ? -0.18f : 0.15f) +
            (1f - context.AimAlignment) * 0.25f;
        chance = Math.Clamp(chance, 0.03f, 0.92f);
        return new AxeRetentionResult(chance, roll, roll < chance);
    }

    public static float DeterministicRoll(int trialIndex)
    {
        if (trialIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(trialIndex));
        const double goldenFraction = 0.6180339887498949;
        const double seed = 0.37;
        return (float)((seed + trialIndex * goldenFraction) % 1d);
    }
}

public readonly record struct AxeRetentionContext(bool ChipReleased, float AimAlignment)
{
    public static AxeRetentionContext Lab04 => new(false, 1f);

    public void EnsureValid()
    {
        if (!float.IsFinite(AimAlignment) || AimAlignment is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(AimAlignment));
    }
}

public readonly record struct AxeRetentionResult(
    float Probability,
    float Roll,
    bool IsEmbedded);

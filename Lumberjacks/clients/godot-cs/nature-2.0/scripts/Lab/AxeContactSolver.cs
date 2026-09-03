#nullable enable

using System;
using System.Numerics;

namespace CommunitySurvival.Lab;

/// <summary>
/// Finds the first swept cutting-edge crossing against a finite witness face.
/// It searches the accepted drive independently of render frames, then bisects
/// the crossing to sub-millisecond precision.
/// </summary>
public static class AxeContactSolver
{
    private const int SearchSegments = 2048;
    private const int BisectionIterations = 18;

    public static AxeContactTarget AcceptedWitnessTarget(
        AxeSwingProfile profile,
        float widthMeters = 0.64f,
        float heightMeters = 0.58f)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var sample = AxeKinematics.Sample(profile, profile.ContactTimeSeconds);
        var normal = Vector3.Normalize(-sample.LeadingDirection);
        var right = Vector3.Normalize(sample.ToolDirection);
        var up = Vector3.Normalize(Vector3.Cross(normal, right));
        return new AxeContactTarget(
            sample.CuttingCenter,
            normal,
            right,
            up,
            widthMeters * 0.5f,
            heightMeters * 0.5f);
    }

    public static AxeContactResult Solve(AxeSwingProfile profile, AxeContactTarget target)
    {
        ArgumentNullException.ThrowIfNull(profile);
        target.EnsureValid();
        profile.EnsureValid();

        var start = profile.WindupSeconds;
        var end = profile.WindupSeconds + profile.DriveSeconds;
        var previousTime = start;
        var previous = AxeKinematics.Sample(profile, previousTime);
        var previousDistance = SignedDistance(previous.CuttingCenter, target);
        var closest = ClosestResult(previous, target, previousDistance);

        for (var index = 1; index <= SearchSegments; index++)
        {
            var currentTime = start + (end - start) * index / SearchSegments;
            var current = AxeKinematics.Sample(profile, currentTime);
            var currentDistance = SignedDistance(current.CuttingCenter, target);
            if (MathF.Abs(currentDistance) < MathF.Abs(closest.SignedPlaneDistanceMeters))
                closest = ClosestResult(current, target, currentDistance);

            if (previousDistance > 0f && currentDistance <= 0f)
            {
                var crossing = RefineCrossing(profile, target, previousTime, currentTime);
                return ClassifyCrossing(crossing, target);
            }

            previousTime = currentTime;
            previousDistance = currentDistance;
        }

        var classification = closest.SignedPlaneDistanceMeters > 0f
            ? AxeContactClassification.Short
            : AxeContactClassification.Overreach;
        return closest with { Classification = classification };
    }

    private static AxeKinematicsSample RefineCrossing(
        AxeSwingProfile profile,
        AxeContactTarget target,
        float before,
        float after)
    {
        for (var iteration = 0; iteration < BisectionIterations; iteration++)
        {
            var middle = (before + after) * 0.5f;
            var sample = AxeKinematics.Sample(profile, middle);
            if (SignedDistance(sample.CuttingCenter, target) > 0f)
                before = middle;
            else
                after = middle;
        }

        return AxeKinematics.Sample(profile, after);
    }

    private static AxeContactResult ClassifyCrossing(
        AxeKinematicsSample sample,
        AxeContactTarget target)
    {
        var edge = sample.EdgeOuter - sample.EdgeInner;
        var edgeLengthSquared = edge.LengthSquared();
        var alongEdge = edgeLengthSquared > 0f
            ? Math.Clamp(Vector3.Dot(target.Center - sample.EdgeInner, edge) / edgeLengthSquared, 0f, 1f)
            : 0.5f;
        var point = sample.EdgeInner + edge * alongEdge;
        point -= target.Normal * SignedDistance(point, target);

        var horizontal = Vector3.Dot(point - target.Center, target.Right);
        var vertical = Vector3.Dot(point - target.Center, target.Up);
        var classification = ClassifyFacePosition(horizontal, vertical, target);
        var velocity = sample.CuttingCenterVelocityMetersPerSecond;
        var speed = velocity.Length();
        var incidence = speed > 0f
            ? RadiansToDegrees(MathF.Acos(Math.Clamp(
                Vector3.Dot(Vector3.Normalize(-velocity), target.Normal),
                -1f,
                1f)))
            : 0f;

        return new AxeContactResult(
            classification,
            sample.ElapsedSeconds,
            point,
            sample.CuttingCenter,
            velocity,
            target.Normal,
            horizontal,
            vertical,
            SignedDistance(sample.CuttingCenter, target),
            incidence);
    }

    private static AxeContactResult ClosestResult(
        AxeKinematicsSample sample,
        AxeContactTarget target,
        float signedDistance)
    {
        var point = sample.CuttingCenter - target.Normal * signedDistance;
        var horizontal = Vector3.Dot(point - target.Center, target.Right);
        var vertical = Vector3.Dot(point - target.Center, target.Up);
        return new AxeContactResult(
            ClassifyFacePosition(horizontal, vertical, target),
            sample.ElapsedSeconds,
            point,
            sample.CuttingCenter,
            sample.CuttingCenterVelocityMetersPerSecond,
            target.Normal,
            horizontal,
            vertical,
            signedDistance,
            0f);
    }

    private static AxeContactClassification ClassifyFacePosition(
        float horizontal,
        float vertical,
        AxeContactTarget target)
    {
        if (vertical > target.HalfHeightMeters) return AxeContactClassification.High;
        if (vertical < -target.HalfHeightMeters) return AxeContactClassification.Low;
        if (horizontal > target.HalfWidthMeters) return AxeContactClassification.Right;
        if (horizontal < -target.HalfWidthMeters) return AxeContactClassification.Left;
        return AxeContactClassification.Hit;
    }

    private static float SignedDistance(Vector3 point, AxeContactTarget target) =>
        Vector3.Dot(point - target.Center, target.Normal);

    private static float RadiansToDegrees(float radians) => radians * 180f / MathF.PI;
}

public readonly record struct AxeContactTarget(
    Vector3 Center,
    Vector3 Normal,
    Vector3 Right,
    Vector3 Up,
    float HalfWidthMeters,
    float HalfHeightMeters)
{
    public void EnsureValid()
    {
        if (!IsFinite(Center) || !IsFinite(Normal) || !IsFinite(Right) || !IsFinite(Up))
            throw new ArgumentException("Witness target vectors must be finite.");
        if (HalfWidthMeters <= 0f || HalfHeightMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(HalfWidthMeters), "Witness dimensions must be positive.");
        if (MathF.Abs(Normal.Length() - 1f) > 0.001f ||
            MathF.Abs(Right.Length() - 1f) > 0.001f ||
            MathF.Abs(Up.Length() - 1f) > 0.001f)
            throw new ArgumentException("Witness target axes must be normalized.");
        if (MathF.Abs(Vector3.Dot(Normal, Right)) > 0.001f ||
            MathF.Abs(Vector3.Dot(Normal, Up)) > 0.001f ||
            MathF.Abs(Vector3.Dot(Right, Up)) > 0.001f)
            throw new ArgumentException("Witness target axes must be orthogonal.");
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public enum AxeContactClassification
{
    Hit,
    Short,
    Overreach,
    High,
    Low,
    Left,
    Right,
}

public readonly record struct AxeContactResult(
    AxeContactClassification Classification,
    float ContactTimeSeconds,
    Vector3 ContactPoint,
    Vector3 CuttingCenter,
    Vector3 CuttingCenterVelocityMetersPerSecond,
    Vector3 TargetNormal,
    float FaceHorizontalMeters,
    float FaceVerticalMeters,
    float SignedPlaneDistanceMeters,
    float IncidenceAngleDegrees)
{
    public bool Hit => Classification == AxeContactClassification.Hit;
    public float SpeedMetersPerSecond => CuttingCenterVelocityMetersPerSecond.Length();
}

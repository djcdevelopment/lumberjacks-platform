#nullable enable

using System;
using System.Numerics;

namespace CommunitySurvival.Lab;

public enum AxeCutSense
{
    Down,
    Up,
}

/// <summary>
/// Registers a shoulder-relative accepted or exploratory stroke onto one lip of
/// a fixed notch witness by translating the entire actor stance. Articulation
/// and contact geometry are never distorted to hide targeting error.
/// </summary>
public static class AxeStrokeRegistration
{
    public const float DefaultMouthHeightMeters = 0.10f;
    public const float MinimumMouthHeightMeters = 0.04f;
    public const float MaximumMouthHeightMeters = 0.20f;

    public static AxeStrokeTargetFrame DefaultTargetFrame(AxeSwingProfile acceptedDownProfile)
    {
        ArgumentNullException.ThrowIfNull(acceptedDownProfile);
        acceptedDownProfile.EnsureValid();
        var contact = AxeKinematics.Sample(
            acceptedDownProfile,
            acceptedDownProfile.ContactTimeSeconds);
        var vertical = Vector3.UnitY;
        var tangent = new Vector3(contact.ToolDirection.X, 0f, contact.ToolDirection.Z);
        if (tangent.LengthSquared() < 0.000001f)
            throw new ArgumentException(
                "The accepted cutting edge must have a horizontal span.",
                nameof(acceptedDownProfile));
        tangent = Vector3.Normalize(tangent);
        var inward = Vector3.Normalize(Vector3.Cross(vertical, tangent));
        var horizontalVelocity = new Vector3(
            contact.CuttingCenterVelocityMetersPerSecond.X,
            0f,
            contact.CuttingCenterVelocityMetersPerSecond.Z);
        if (Vector3.Dot(inward, horizontalVelocity) < 0f)
        {
            inward = -inward;
            tangent = -tangent;
        }
        var outward = -inward;
        var notchCenter = contact.CuttingCenter -
            vertical * (DefaultMouthHeightMeters * 0.5f);
        return new AxeStrokeTargetFrame(
            notchCenter,
            outward,
            inward,
            vertical,
            tangent);
    }

    public static AxeStrokeRegistrationResult Register(
        AxeSwingProfile profile,
        AxeStrokeTargetFrame targetFrame,
        AxeCutSense sense,
        float mouthHeightMeters = DefaultMouthHeightMeters,
        float faceOffsetMeters = 0f)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.EnsureValid();
        targetFrame.EnsureValid();
        if (!float.IsFinite(mouthHeightMeters) ||
            mouthHeightMeters is < MinimumMouthHeightMeters or > MaximumMouthHeightMeters)
            throw new ArgumentOutOfRangeException(nameof(mouthHeightMeters));
        if (!float.IsFinite(faceOffsetMeters))
            throw new ArgumentOutOfRangeException(nameof(faceOffsetMeters));
        if (!Enum.IsDefined(sense))
            throw new ArgumentOutOfRangeException(nameof(sense));

        var contact = AxeKinematics.Sample(profile, profile.ContactTimeSeconds);
        var verticalSpeed = Vector3.Dot(
            contact.CuttingCenterVelocityMetersPerSecond,
            targetFrame.Vertical);
        if (sense == AxeCutSense.Down && verticalSpeed >= -0.001f)
            throw new InvalidOperationException("A DOWN registration requires downward motion at contact.");
        if (sense == AxeCutSense.Up && verticalSpeed <= 0.001f)
            throw new InvalidOperationException("An UP registration requires upward motion at contact.");
        var lipOffset = targetFrame.Vertical * (mouthHeightMeters * 0.5f) *
            (sense == AxeCutSense.Down ? 1f : -1f);
        var faceOffset = targetFrame.Tangent * faceOffsetMeters;
        var target = targetFrame.NotchCenter + lipOffset + faceOffset;
        var stanceTranslation = target - contact.CuttingCenter;
        var registeredCenter = contact.CuttingCenter + stanceTranslation;
        return new AxeStrokeRegistrationResult(
            sense,
            target,
            stanceTranslation,
            registeredCenter,
            Vector3.Distance(target, registeredCenter));
    }
}

public readonly record struct AxeStrokeTargetFrame(
    Vector3 NotchCenter,
    Vector3 OutwardNormal,
    Vector3 InwardNormal,
    Vector3 Vertical,
    Vector3 Tangent)
{
    public void EnsureValid()
    {
        if (!Finite(NotchCenter) || !Finite(OutwardNormal) || !Finite(InwardNormal) ||
            !Finite(Vertical) || !Finite(Tangent))
            throw new ArgumentException("Target frame vectors must be finite.");
        if (MathF.Abs(OutwardNormal.Length() - 1f) > 0.001f ||
            MathF.Abs(InwardNormal.Length() - 1f) > 0.001f ||
            MathF.Abs(Vertical.Length() - 1f) > 0.001f ||
            MathF.Abs(Tangent.Length() - 1f) > 0.001f)
            throw new ArgumentException("Target frame axes must be normalized.");
        if (Vector3.Dot(OutwardNormal, InwardNormal) > -0.999f ||
            MathF.Abs(Vector3.Dot(OutwardNormal, Vertical)) > 0.001f ||
            MathF.Abs(Vector3.Dot(OutwardNormal, Tangent)) > 0.001f ||
            MathF.Abs(Vector3.Dot(Vertical, Tangent)) > 0.001f)
            throw new ArgumentException("Target frame axes must be orthogonal and inward/outward opposed.");
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public readonly record struct AxeStrokeRegistrationResult(
    AxeCutSense Sense,
    Vector3 TargetPoint,
    Vector3 StanceTranslation,
    Vector3 RegisteredContactCenter,
    float ContactErrorMeters);

/// <summary>
/// Frame-rate-independent clamp at the declared contact sample. The lab uses
/// the returned pause as a human inspection gate; it never infers collision.
/// </summary>
public static class AxeStrokePlayback
{
    public static AxeStrokePlaybackStep Advance(
        float elapsedSeconds,
        float deltaSeconds,
        float contactTimeSeconds,
        bool contactReleased)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!float.IsFinite(contactTimeSeconds) || contactTimeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(contactTimeSeconds));

        var next = elapsedSeconds + deltaSeconds;
        if (!contactReleased && elapsedSeconds < contactTimeSeconds && next >= contactTimeSeconds)
            return new AxeStrokePlaybackStep(contactTimeSeconds, true);
        return new AxeStrokePlaybackStep(next, false);
    }
}

public readonly record struct AxeStrokePlaybackStep(float ElapsedSeconds, bool PauseAtContact);

/// <summary>
/// Presentation-only grounding for a squash-and-stretch blob. Its base remains
/// on the floor while vertical stance translation changes height. Reciprocal
/// radial scaling keeps the three-axis scale determinant at one.
/// </summary>
public static class GroundedBlobProjection
{
    public static GroundedBlobScale Compute(
        float baseHeightMeters,
        float stanceVerticalMeters,
        float minimumHeightMeters = 0.55f)
    {
        if (!float.IsFinite(baseHeightMeters) || baseHeightMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(baseHeightMeters));
        if (!float.IsFinite(stanceVerticalMeters))
            throw new ArgumentOutOfRangeException(nameof(stanceVerticalMeters));
        if (!float.IsFinite(minimumHeightMeters) ||
            minimumHeightMeters <= 0f || minimumHeightMeters > baseHeightMeters)
            throw new ArgumentOutOfRangeException(nameof(minimumHeightMeters));

        var visibleHeight = MathF.Max(minimumHeightMeters, baseHeightMeters + stanceVerticalMeters);
        var verticalScale = visibleHeight / baseHeightMeters;
        var radialScale = 1f / MathF.Sqrt(verticalScale);
        var localCenter = visibleHeight * 0.5f - stanceVerticalMeters;
        return new GroundedBlobScale(visibleHeight, verticalScale, radialScale, localCenter);
    }
}

public readonly record struct GroundedBlobScale(
    float VisibleHeightMeters,
    float VerticalScale,
    float RadialScale,
    float LocalCenterMeters)
{
    public float ScaleDeterminant => VerticalScale * RadialScale * RadialScale;
}

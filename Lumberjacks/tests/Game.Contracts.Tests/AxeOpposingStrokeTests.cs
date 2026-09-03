using System;
using System.Numerics;
using CommunitySurvival.Lab;
using Xunit;

namespace Game.Contracts.Tests;

public sealed class AxeOpposingStrokeTests
{
    [Fact]
    public void AcceptedUpIsIndependentAndPreservesLockedPhysicalCalibration()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var up = AxeSwingProfile.AcceptedUpV1();

        Assert.Equal("accepted-axe-v1", AxeSwingProfile.AcceptedCalibrationId);
        Assert.Equal("accepted-axe-up-v1", AxeSwingProfile.AcceptedUpCalibrationId);
        Assert.Equal(22f, down.PlaneTiltDegrees);
        Assert.Equal(-22f, up.PlaneTiltDegrees);
        Assert.Equal(down.HandleLengthMeters, up.HandleLengthMeters);
        Assert.Equal(down.HeadBladeLengthMeters, up.HeadBladeLengthMeters);
        Assert.Equal(down.HeadDepthMeters, up.HeadDepthMeters);
        Assert.Equal(down.HeadThicknessMeters, up.HeadThicknessMeters);
        Assert.Equal(down.UpperArmLengthMeters, up.UpperArmLengthMeters);
        Assert.Equal(down.ForearmLengthMeters, up.ForearmLengthMeters);
        Assert.Equal(down.StartAngleDegrees, up.StartAngleDegrees);
        Assert.Equal(down.ContactAngleDegrees, up.ContactAngleDegrees);
        Assert.Equal(down.FollowThroughAngleDegrees, up.FollowThroughAngleDegrees);
        Assert.Equal(down.WindupSeconds, up.WindupSeconds);
        Assert.Equal(down.DriveSeconds, up.DriveSeconds);
        Assert.Equal(down.RecoverySeconds, up.RecoverySeconds);

        up.ShoulderContactDegrees += 12f;
        Assert.Equal(-18f, down.ShoulderContactDegrees);
    }

    [Fact]
    public void SignedPlaneLimitsPermitUpwardCandidateAndRejectEitherExtreme()
    {
        AxeSwingProfile.AcceptedUpV1().EnsureValid();
        Assert.ThrowsAny<Exception>(() => new AxeSwingProfile { PlaneTiltDegrees = -90.01f }.EnsureValid());
        Assert.ThrowsAny<Exception>(() => new AxeSwingProfile { PlaneTiltDegrees = 90.01f }.EnsureValid());
    }

    [Fact]
    public void DefaultRegistrationKeepsLockedDownStanceAndSeparatesLipTargets()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var up = AxeSwingProfile.AcceptedUpV1();
        var frame = AxeStrokeRegistration.DefaultTargetFrame(down);
        var downRegistration = AxeStrokeRegistration.Register(down, frame, AxeCutSense.Down);
        var upRegistration = AxeStrokeRegistration.Register(up, frame, AxeCutSense.Up);

        AssertVectorNear(Vector3.Zero, downRegistration.StanceTranslation);
        AssertVectorNear(downRegistration.TargetPoint, downRegistration.RegisteredContactCenter);
        AssertVectorNear(upRegistration.TargetPoint, upRegistration.RegisteredContactCenter);
        Assert.True(upRegistration.StanceTranslation.Length() > 0.01f);
        Assert.InRange(
            Vector3.Distance(downRegistration.TargetPoint, upRegistration.TargetPoint),
            0.09999f,
            0.10001f);
    }

    [Fact]
    public void DefaultWitnessIsTangentToTheCuttingEdgeForCenteredContact()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var contact = AxeKinematics.Sample(down, down.ContactTimeSeconds);
        var frame = AxeStrokeRegistration.DefaultTargetFrame(down);
        var horizontalEdge = Vector3.Normalize(new Vector3(
            contact.ToolDirection.X,
            0f,
            contact.ToolDirection.Z));
        var innerDepth = Vector3.Dot(contact.EdgeInner - contact.CuttingCenter, frame.InwardNormal);
        var outerDepth = Vector3.Dot(contact.EdgeOuter - contact.CuttingCenter, frame.InwardNormal);

        Assert.InRange(MathF.Abs(Vector3.Dot(horizontalEdge, frame.Tangent)), 0.99999f, 1f);
        Assert.InRange(MathF.Abs(innerDepth), 0f, 0.00001f);
        Assert.InRange(MathF.Abs(outerDepth), 0f, 0.00001f);
        Assert.True(Vector3.Dot(contact.CuttingCenterVelocityMetersPerSecond, frame.InwardNormal) > 0f);
    }

    [Fact]
    public void AcceptedAndCandidateArriveInGenuinelyOpposingVerticalDirections()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var up = AxeSwingProfile.AcceptedUpV1();
        var frame = AxeStrokeRegistration.DefaultTargetFrame(down);
        var downContact = AxeKinematics.Sample(down, down.ContactTimeSeconds);
        var upContact = AxeKinematics.Sample(up, up.ContactTimeSeconds);
        var downVertical = Vector3.Dot(downContact.CuttingCenterVelocityMetersPerSecond, frame.Vertical);
        var upVertical = Vector3.Dot(upContact.CuttingCenterVelocityMetersPerSecond, frame.Vertical);

        Assert.True(downVertical < -0.001f);
        Assert.True(upVertical > 0.001f);
        Assert.Equal(MathF.Abs(downVertical), MathF.Abs(upVertical), 3);
    }

    [Fact]
    public void FaceOffsetMovesTargetOnlyAlongBarkTangent()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var frame = AxeStrokeRegistration.DefaultTargetFrame(down);
        var nominal = AxeStrokeRegistration.Register(down, frame, AxeCutSense.Down);
        var offset = AxeStrokeRegistration.Register(down, frame, AxeCutSense.Down, faceOffsetMeters: 0.08f);
        var delta = offset.TargetPoint - nominal.TargetPoint;

        Assert.InRange(Vector3.Dot(delta, frame.Tangent), 0.07999f, 0.08001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(delta, frame.Vertical)), 0f, 0.00001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(delta, frame.InwardNormal)), 0f, 0.00001f);
    }

    [Fact]
    public void InvalidTargetInputsCannotBeRegistered()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var frame = AxeStrokeRegistration.DefaultTargetFrame(down);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AxeStrokeRegistration.Register(down, frame, AxeCutSense.Down, 0.039f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AxeStrokeRegistration.Register(down, frame, AxeCutSense.Down, 0.201f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AxeStrokeRegistration.Register(down, frame, AxeCutSense.Down, faceOffsetMeters: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AxeStrokeRegistration.Register(down, frame, (AxeCutSense)99));
        Assert.Throws<ArgumentException>(() =>
            AxeStrokeRegistration.Register(
                down,
                frame with { Tangent = Vector3.One },
                AxeCutSense.Down));
    }

    [Fact]
    public void SenseCannotRelabelMotionThatTravelsTheWrongWay()
    {
        var down = AxeSwingProfile.AcceptedV1();
        var up = AxeSwingProfile.AcceptedUpV1();
        var frame = AxeStrokeRegistration.DefaultTargetFrame(down);

        Assert.Throws<InvalidOperationException>(() =>
            AxeStrokeRegistration.Register(down, frame, AxeCutSense.Up));
        Assert.Throws<InvalidOperationException>(() =>
            AxeStrokeRegistration.Register(up, frame, AxeCutSense.Down));
    }

    [Theory]
    [InlineData(1f / 30f)]
    [InlineData(1f / 60f)]
    [InlineData(1f / 144f)]
    public void PlaybackClampsExactlyAtContactRegardlessOfFrameRate(float delta)
    {
        var contact = AxeSwingProfile.AcceptedUpV1().ContactTimeSeconds;
        var elapsed = 0f;
        AxeStrokePlaybackStep step;
        do
        {
            step = AxeStrokePlayback.Advance(elapsed, delta, contact, contactReleased: false);
            elapsed = step.ElapsedSeconds;
        }
        while (!step.PauseAtContact);

        Assert.Equal(contact, elapsed);
        var continued = AxeStrokePlayback.Advance(elapsed, delta, contact, contactReleased: true);
        Assert.False(continued.PauseAtContact);
        Assert.True(continued.ElapsedSeconds > contact);
    }

    [Theory]
    [InlineData(-0.30f)]
    [InlineData(0f)]
    [InlineData(0.30f)]
    public void GroundedBlobAnchorsItsBaseAndPreservesScaledVolume(float stanceVertical)
    {
        const float baseHeight = 1.5f;
        var blob = GroundedBlobProjection.Compute(baseHeight, stanceVertical);
        var worldCenter = stanceVertical + blob.LocalCenterMeters;
        var worldBottom = worldCenter - baseHeight * blob.VerticalScale * 0.5f;

        Assert.InRange(MathF.Abs(worldBottom), 0f, 0.00001f);
        Assert.InRange(MathF.Abs(blob.ScaleDeterminant - 1f), 0f, 0.00001f);
        if (stanceVertical < 0f)
        {
            Assert.True(blob.VerticalScale < 1f);
            Assert.True(blob.RadialScale > 1f);
        }
        else if (stanceVertical > 0f)
        {
            Assert.True(blob.VerticalScale > 1f);
            Assert.True(blob.RadialScale < 1f);
        }
    }

    [Fact]
    public void GroundedBlobRetainsMinimumVisibleHeightUnderExtremeCompression()
    {
        var blob = GroundedBlobProjection.Compute(1.5f, -4f, 0.55f);

        Assert.Equal(0.55f, blob.VisibleHeightMeters);
        Assert.InRange(MathF.Abs(blob.ScaleDeterminant - 1f), 0f, 0.00001f);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 0.00001f);
    }
}

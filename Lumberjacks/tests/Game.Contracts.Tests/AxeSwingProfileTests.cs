using System;
using System.IO;
using CommunitySurvival.Lab;
using Xunit;

namespace Game.Contracts.Tests;

public sealed class AxeSwingProfileTests
{
    [Fact]
    public void AcceptedV1PinsTheHumanReviewedCalibration()
    {
        var profile = AxeSwingProfile.AcceptedV1();

        Assert.Equal("accepted-axe-v1", AxeSwingProfile.AcceptedCalibrationId);
        Assert.Equal(-18f, profile.RestAngleDegrees);
        Assert.Equal(108f, profile.StartAngleDegrees);
        Assert.Equal(-4f, profile.ContactAngleDegrees);
        Assert.Equal(-38f, profile.FollowThroughAngleDegrees);
        Assert.Equal(22f, profile.PlaneTiltDegrees);
        Assert.Equal(0.24f, profile.WindupSeconds);
        Assert.Equal(0.28f, profile.DriveSeconds);
        Assert.Equal(0.42f, profile.RecoverySeconds);
        Assert.Equal(0.28f, profile.UpperArmLengthMeters);
        Assert.Equal(0.32f, profile.ForearmLengthMeters);
        Assert.Equal(0.76f, profile.HandleLengthMeters);
        Assert.Equal(-8f, profile.ShoulderRestDegrees);
        Assert.Equal(65f, profile.ShoulderWindupDegrees);
        Assert.Equal(-18f, profile.ShoulderContactDegrees);
        Assert.Equal(-45f, profile.ShoulderFollowThroughDegrees);
        Assert.Equal(-12f, profile.ElbowRestDegrees);
        Assert.Equal(78f, profile.ElbowWindupDegrees);
        Assert.Equal(8f, profile.ElbowContactDegrees);
        Assert.Equal(-16f, profile.ElbowFollowThroughDegrees);
        Assert.Equal(0.34f, profile.HeadBladeLengthMeters);
        Assert.Equal(0.27f, profile.HeadDepthMeters);
        Assert.Equal(0.085f, profile.HeadThicknessMeters);
    }

    [Fact]
    public void DefaultProfileCrossesTheDeclaredContactOnOneMonotonicDriveArc()
    {
        var profile = new AxeSwingProfile();
        profile.EnsureValid();
        Assert.InRange(profile.PlaneTiltDegrees, 0f, 44.999f);

        var atStart = profile.Sample(profile.WindupSeconds);
        var atContact = profile.Sample(profile.ContactTimeSeconds);
        var atEnd = profile.Sample(profile.WindupSeconds + profile.DriveSeconds);

        Assert.Equal(AxeSwingPhase.Drive, atStart.Phase);
        Assert.Equal(profile.StartAngleDegrees, atStart.AngleDegrees, 4);
        Assert.Equal(profile.ContactAngleDegrees, atContact.AngleDegrees, 4);
        Assert.Equal(profile.ShoulderContactDegrees, atContact.ShoulderAngleDegrees, 4);
        Assert.Equal(profile.ElbowContactDegrees, atContact.ElbowAngleDegrees, 4);
        Assert.True(atContact.ContactCrossed);
        Assert.Equal(AxeSwingPhase.Recovery, atEnd.Phase);
        Assert.Equal(profile.FollowThroughAngleDegrees, atEnd.AngleDegrees, 4);

        var previous = profile.StartAngleDegrees;
        for (var sample = 1; sample < 100; sample++)
        {
            var elapsed = profile.WindupSeconds + profile.DriveSeconds * sample / 100f;
            var pose = profile.Sample(elapsed);
            Assert.True(pose.AngleDegrees < previous);
            Assert.True(pose.AngularVelocityDegreesPerSecond < 0f);
            previous = pose.AngleDegrees;
        }
    }

    [Fact]
    public void SampleClampsBeforeAndAfterTheAnimation()
    {
        var profile = new AxeSwingProfile();

        var before = profile.Sample(-10f);
        var after = profile.Sample(profile.TotalSeconds + 10f);

        Assert.Equal(AxeSwingPhase.Ready, before.Phase);
        Assert.Equal(profile.RestAngleDegrees, before.AngleDegrees);
        Assert.Equal(AxeSwingPhase.Complete, after.Phase);
        Assert.Equal(profile.RestAngleDegrees, after.AngleDegrees);
        Assert.True(after.ContactCrossed);
    }

    [Fact]
    public void InvalidOrNonFiniteProfilesCannotRun()
    {
        var reversed = new AxeSwingProfile { ContactAngleDegrees = 120f };
        Assert.Throws<InvalidDataException>(reversed.EnsureValid);

        var nonFinite = new AxeSwingProfile { DriveSeconds = float.NaN };
        Assert.Throws<InvalidDataException>(nonFinite.EnsureValid);

        var impossiblePlane = new AxeSwingProfile { PlaneTiltDegrees = 91f };
        Assert.Throws<InvalidDataException>(impossiblePlane.EnsureValid);

        var impossibleReach = new AxeSwingProfile { UpperArmLengthMeters = 0.14f };
        Assert.Throws<InvalidDataException>(impossibleReach.EnsureValid);

        var impossibleHead = new AxeSwingProfile { HeadDepthMeters = 0.09f };
        Assert.Throws<InvalidDataException>(impossibleHead.EnsureValid);
    }

    [Fact]
    public void ContactTimeMovesPredictablyWithoutChangingTheArcPlane()
    {
        var early = new AxeSwingProfile { ContactAngleDegrees = 40f };
        var late = new AxeSwingProfile { ContactAngleDegrees = -20f };

        Assert.True(early.ContactTimeSeconds < late.ContactTimeSeconds);
        Assert.InRange(early.ContactTimeSeconds, early.WindupSeconds, early.WindupSeconds + early.DriveSeconds);
        Assert.InRange(late.ContactTimeSeconds, late.WindupSeconds, late.WindupSeconds + late.DriveSeconds);
    }

    [Fact]
    public void ElbowExtensionLengthensHandReachAtContact()
    {
        var profile = new AxeSwingProfile();
        var windup = profile.Sample(profile.WindupSeconds);
        var contact = profile.Sample(profile.ContactTimeSeconds);

        var windupReach = profile.ShoulderToHandReachMeters(windup.ElbowAngleDegrees);
        var contactReach = profile.ShoulderToHandReachMeters(contact.ElbowAngleDegrees);

        Assert.True(contactReach > windupReach);
        Assert.True(profile.ShoulderToHeadRadiusMeters(contact) > contactReach);
    }

    [Fact]
    public void ToolArcIsDistributedAcrossShoulderElbowAndWrist()
    {
        var profile = new AxeSwingProfile();
        var windup = profile.Sample(profile.WindupSeconds);
        var contact = profile.Sample(profile.ContactTimeSeconds);

        Assert.True(
            MathF.Abs(profile.ShoulderWindupDegrees - profile.ShoulderFollowThroughDegrees) <
            MathF.Abs(profile.StartAngleDegrees - profile.FollowThroughAngleDegrees));
        Assert.NotEqual(windup.ShoulderAngleDegrees, contact.ShoulderAngleDegrees);
        Assert.NotEqual(windup.ElbowAngleDegrees, contact.ElbowAngleDegrees);

        var windupWrist = windup.AngleDegrees - windup.ShoulderAngleDegrees - windup.ElbowAngleDegrees;
        var contactWrist = contact.AngleDegrees - contact.ShoulderAngleDegrees - contact.ElbowAngleDegrees;
        Assert.NotEqual(windupWrist, contactWrist);
    }
}

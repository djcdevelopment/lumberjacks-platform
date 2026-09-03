using System;
using System.Numerics;
using CommunitySurvival.Lab;
using Xunit;

namespace Game.Contracts.Tests;

public sealed class AxeContactSolverTests
{
    [Fact]
    public void KinematicsExposeTheActualCuttingEdgeAndVelocity()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var sample = AxeKinematics.Sample(profile, profile.ContactTimeSeconds);

        Assert.Equal(profile.HeadBladeLengthMeters, Vector3.Distance(sample.EdgeInner, sample.EdgeOuter), 4);
        Assert.Equal(profile.HeadDepthMeters, Vector3.Distance(sample.HeadMount, sample.CuttingCenter), 4);
        Assert.InRange(sample.CuttingCenterSpeedMetersPerSecond, 1f, 40f);
        Assert.InRange(MathF.Abs(Vector3.Dot(sample.ToolDirection, sample.LeadingDirection)), 0f, 0.0001f);
    }

    [Fact]
    public void ContactLeavesAMeasurableCurvedFollowThroughPath()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var contact = AxeContactSolver.Solve(profile, AxeContactSolver.AcceptedWitnessTarget(profile));
        var driveEnd = profile.WindupSeconds + profile.DriveSeconds;

        var remainingPath = AxeKinematics.CuttingCenterPathLength(
            profile,
            contact.ContactTimeSeconds,
            driveEnd);
        var straightLine = Vector3.Distance(
            AxeKinematics.Sample(profile, contact.ContactTimeSeconds).CuttingCenter,
            AxeKinematics.Sample(profile, driveEnd).CuttingCenter);

        Assert.True(contact.Hit);
        Assert.True(remainingPath > 0f);
        Assert.True(remainingPath > straightLine);
        Assert.Equal(0f, AxeKinematics.CuttingCenterPathLength(profile, driveEnd, driveEnd));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AxeKinematics.CuttingCenterPathLength(profile, driveEnd, contact.ContactTimeSeconds));
    }

    [Fact]
    public void AcceptedWitnessFindsTheDeclaredContactWithoutRenderFrames()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var target = AxeContactSolver.AcceptedWitnessTarget(profile);

        var result = AxeContactSolver.Solve(profile, target);

        Assert.True(result.Hit);
        Assert.Equal(AxeContactClassification.Hit, result.Classification);
        Assert.InRange(MathF.Abs(result.ContactTimeSeconds - profile.ContactTimeSeconds), 0f, 0.0005f);
        Assert.InRange(MathF.Abs(result.SignedPlaneDistanceMeters), 0f, 0.00001f);
        Assert.InRange(result.IncidenceAngleDegrees, 0f, 45f);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void PlaybackClampsToTheSamePrecomputedContactAtEveryRenderRate(int framesPerSecond)
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var result = AxeContactSolver.Solve(profile, AxeContactSolver.AcceptedWitnessTarget(profile));
        var elapsed = 0f;
        var frameSeconds = 1f / framesPerSecond;

        while (elapsed < result.ContactTimeSeconds)
            elapsed = Math.Min(elapsed + frameSeconds, result.ContactTimeSeconds);

        var displayed = AxeKinematics.Sample(profile, elapsed);
        Assert.Equal(result.ContactTimeSeconds, displayed.ElapsedSeconds);
        Assert.InRange(Vector3.Distance(displayed.CuttingCenter, result.CuttingCenter), 0f, 0.0001f);
    }

    [Fact]
    public void FiniteWitnessReportsVerticalMissesInsteadOfPlaneHits()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var target = AxeContactSolver.AcceptedWitnessTarget(profile);

        var highTarget = target with { Center = target.Center + target.Up * 0.5f };
        var lowTarget = target with { Center = target.Center - target.Up * 0.5f };

        Assert.Equal(AxeContactClassification.Low, AxeContactSolver.Solve(profile, highTarget).Classification);
        Assert.Equal(AxeContactClassification.High, AxeContactSolver.Solve(profile, lowTarget).Classification);
    }

    [Fact]
    public void TwelveCentimeterWitnessPresetsRemainHitsAndOrderNearBeforeFar()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var target = AxeContactSolver.AcceptedWitnessTarget(profile);
        var nominal = AxeContactSolver.Solve(profile, target);
        var near = AxeContactSolver.Solve(
            profile,
            target with { Center = target.Center + target.Normal * 0.12f });
        var far = AxeContactSolver.Solve(
            profile,
            target with { Center = target.Center - target.Normal * 0.12f });
        var high = AxeContactSolver.Solve(
            profile,
            target with { Center = target.Center + target.Up * 0.12f });
        var low = AxeContactSolver.Solve(
            profile,
            target with { Center = target.Center - target.Up * 0.12f });

        Assert.True(near.Hit);
        Assert.True(far.Hit);
        Assert.True(high.Hit);
        Assert.True(low.Hit);
        Assert.True(near.ContactTimeSeconds < nominal.ContactTimeSeconds);
        Assert.True(nominal.ContactTimeSeconds < far.ContactTimeSeconds);
        Assert.Equal(-0.12f, high.FaceVerticalMeters, 3);
        Assert.Equal(0.12f, low.FaceVerticalMeters, 3);
    }

    [Fact]
    public void UnreachableAndAlreadyPassedPlanesHaveDifferentFailures()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var target = AxeContactSolver.AcceptedWitnessTarget(profile);

        var beyondReach = target with { Center = target.Center - target.Normal * 2f };
        var behindTheSwing = target with { Center = target.Center + target.Normal * 2f };

        Assert.Equal(AxeContactClassification.Short, AxeContactSolver.Solve(profile, beyondReach).Classification);
        Assert.Equal(AxeContactClassification.Overreach, AxeContactSolver.Solve(profile, behindTheSwing).Classification);
    }

    [Fact]
    public void InvalidWitnessAxesAreRejected()
    {
        var target = new AxeContactTarget(
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitX,
            Vector3.UnitY,
            0.2f,
            0.2f);

        Assert.Throws<ArgumentException>(target.EnsureValid);
    }
}

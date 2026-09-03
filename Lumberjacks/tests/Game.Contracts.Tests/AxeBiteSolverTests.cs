using System;
using System.Numerics;
using CommunitySurvival.Lab;
using Xunit;

namespace Game.Contracts.Tests;

public sealed class AxeBiteSolverTests
{
    [Fact]
    public void FreshWoodFieldIsDeterministicAndUsesFiveMillimeterCells()
    {
        var first = WoodCutState.Fresh();
        var second = WoodCutState.Fresh();

        Assert.Equal(0.005f, first.CellSizeMeters, 4);
        Assert.Equal(100, first.DepthCells);
        Assert.Equal(140, first.HeightCells);
        Assert.Equal(0, first.KerfCellCount);
        Assert.Equal(0, first.ReleasedCellCount);
        Assert.Equal(0, first.CutMarkCount);
        Assert.Equal(first.StableHash(), second.StableHash());
    }

    [Fact]
    public void NominalAcceptedSwingStopsNearFiveCentimetersInUniformWood()
    {
        var result = Solve(AxeBiteIntent.Nominal);

        Assert.Equal(AxeBiteStopReason.EnergyLimited, result.StopReason);
        Assert.InRange(result.PenetrationMeters, 0.045f, 0.055f);
        Assert.InRange(result.ArrivalSpeedMetersPerSecond, 12.5f, 13.5f);
        Assert.InRange(result.DeliveredEnergyJoules, 120f, 135f);
        Assert.InRange(result.UnusedEnergyJoules, 0f, 0.001f);
        Assert.True(result.FinalState.KerfCellCount > 0);
        Assert.Equal(1, result.FinalState.StrikeCount);
        Assert.Equal(1, result.FinalState.CutMarkCount);
        Assert.Equal(0.10f, result.FinalState.CutMarks[0].CutWidthMeters, 4);
        Assert.Equal(
            result.PenetrationMeters,
            result.FinalState.CutMarks[0].PenetrationMeters,
            4);
        Assert.Equal(0, result.FinalState.ReleasedCellCount);
    }

    [Fact]
    public void RetainedCutMarkIsDeterministicAndChangesTheMaterialStateHash()
    {
        var fresh = WoodCutState.Fresh();
        var first = AxeBiteSolver.Solve(
            AxeSwingProfile.AcceptedV1(), fresh, AxeBiteIntent.Nominal);
        var repeatedFromFresh = AxeBiteSolver.Solve(
            AxeSwingProfile.AcceptedV1(), fresh, AxeBiteIntent.Nominal);

        Assert.NotEqual(fresh.StableHash(), first.FinalState.StableHash());
        Assert.Equal(first.FinalState.StableHash(), repeatedFromFresh.FinalState.StableHash());
        Assert.Equal(first.FinalState.CutMarks[0].Points, repeatedFromFresh.FinalState.CutMarks[0].Points);
    }

    [Fact]
    public void OpposingCutMustReachTheRetainedLineBeforeItCanIntersect()
    {
        var retained = new AxeCutMark(new[]
        {
            new AxeCutMarkPoint(0f, 0.04f),
            new AxeCutMarkPoint(0.08f, -0.04f),
        });
        var crossing = new AxeCutMark(new[]
        {
            new AxeCutMarkPoint(0f, -0.04f),
            new AxeCutMarkPoint(0.08f, 0.04f),
        });
        var tooShallow = new AxeCutMark(new[]
        {
            new AxeCutMarkPoint(0f, -0.04f),
            new AxeCutMarkPoint(0.03f, -0.01f),
        });
        var reachesWithinOneCell = new AxeCutMark(new[]
        {
            new AxeCutMarkPoint(0f, -0.04f),
            new AxeCutMarkPoint(0.038f, -0.001f),
        });

        Assert.True(retained.Intersects(crossing, 0f));
        Assert.False(retained.Intersects(tooShallow, WoodCutState.DefaultCellSizeMeters));
        Assert.True(retained.Intersects(reachesWithinOneCell, WoodCutState.DefaultCellSizeMeters));
    }

    [Fact]
    public void DiagnosticGrowthRingsAreConcentricAndDenserTowardTheCore()
    {
        const float radius = WoodCutState.DefaultTrunkDiameterMeters * 0.5f;
        var rings = GrowthRingPattern.ConcentricRadii(radius);

        Assert.Equal(14, rings.Length);
        Assert.Equal(radius, rings[^1], 5);
        for (var index = 1; index < rings.Length; index++)
            Assert.True(rings[index] > rings[index - 1]);
        Assert.True(rings[1] - rings[0] < rings[^1] - rings[^2]);
        Assert.InRange(GrowthRingPattern.CountCrossed(radius, 0.052f, rings), 1, rings.Length);
    }

    [Fact]
    public void HarderDeeperBitesAreMoreLikelyToRetainTheHead()
    {
        var low = Solve(new AxeBiteIntent(0.75f, 0.18f));
        var nominal = Solve(new AxeBiteIntent(1f, 0.18f));
        var hard = Solve(new AxeBiteIntent(1.25f, 0.18f));

        var lowRetention = AxeRetentionModel.Evaluate(low, 0.5f, AxeRetentionContext.Lab04);
        var nominalRetention = AxeRetentionModel.Evaluate(nominal, 0.5f, AxeRetentionContext.Lab04);
        var hardRetention = AxeRetentionModel.Evaluate(hard, 0.5f, AxeRetentionContext.Lab04);

        Assert.True(lowRetention.Probability < nominalRetention.Probability);
        Assert.True(nominalRetention.Probability < hardRetention.Probability);
    }

    [Fact]
    public void PoorAlignmentAndAnUnreleasedChipIncreaseRetentionRisk()
    {
        var bite = Solve(AxeBiteIntent.Nominal);
        var cleanRelease = AxeRetentionModel.Evaluate(
            bite, 0.5f, new AxeRetentionContext(true, 1f));
        var poorUnreleased = AxeRetentionModel.Evaluate(
            bite, 0.5f, new AxeRetentionContext(false, 0.25f));

        Assert.True(cleanRelease.Probability < poorUnreleased.Probability);
    }

    [Fact]
    public void RetentionEventUsesAnExplicitRepeatableRoll()
    {
        var bite = Solve(AxeBiteIntent.Nominal);
        var firstRoll = AxeRetentionModel.DeterministicRoll(0);
        var nextRoll = AxeRetentionModel.DeterministicRoll(1);
        var retained = AxeRetentionModel.Evaluate(bite, firstRoll, AxeRetentionContext.Lab04);
        var free = AxeRetentionModel.Evaluate(bite, nextRoll, AxeRetentionContext.Lab04);

        Assert.Equal(0.37f, firstRoll, 4);
        Assert.NotEqual(firstRoll, nextRoll);
        Assert.True(retained.IsEmbedded);
        Assert.False(free.IsEmbedded);
        Assert.Equal(retained, AxeRetentionModel.Evaluate(
            bite, firstRoll, AxeRetentionContext.Lab04));
    }

    [Fact]
    public void RepeatedSameDirectionCutsDoNotInventAChip()
    {
        var marks = new AxeCutMark[6];
        for (var index = 0; index < marks.Length; index++)
        {
            var offset = index * 0.002f;
            marks[index] = Cut(
                new AxeCutMarkPoint(0f, 0.05f + offset),
                new AxeCutMarkPoint(0.08f, -0.03f + offset),
                width: 0.10f);
        }

        Assert.Empty(PotentialChipSolver.FindCandidates(marks));
    }

    [Fact]
    public void OpposingCutsDerivePotentialChipFromBoundedAreaAndOverlappingWidth()
    {
        var downward = Cut(
            new AxeCutMarkPoint(0f, 0.05f),
            new AxeCutMarkPoint(0.10f, -0.05f),
            width: 0.12f,
            faceCenter: 0f);
        var upward = Cut(
            new AxeCutMarkPoint(0f, -0.05f),
            new AxeCutMarkPoint(0.10f, 0.05f),
            width: 0.08f,
            faceCenter: 0.01f);

        var chip = Assert.Single(PotentialChipSolver.FindCandidates(new[] { downward, upward }));

        Assert.Equal(0.05f, chip.DeepestIntersection.DepthMeters, 4);
        Assert.Equal(0f, chip.DeepestIntersection.VerticalMeters, 4);
        Assert.Equal(0.0025f, chip.CrossSectionAreaSquareMeters, 5);
        Assert.Equal(0.08f, chip.FaceWidthMeters, 4);
        Assert.Equal(0.0002f, chip.VolumeCubicMeters, 6);
        Assert.Equal(3, chip.Boundary.Length);
    }

    [Fact]
    public void CutLinesWithoutFaceWidthOverlapCannotBoundTheSameChip()
    {
        var downward = Cut(
            new AxeCutMarkPoint(0f, 0.05f),
            new AxeCutMarkPoint(0.10f, -0.05f),
            width: 0.04f,
            faceCenter: -0.05f);
        var upward = Cut(
            new AxeCutMarkPoint(0f, -0.05f),
            new AxeCutMarkPoint(0.10f, 0.05f),
            width: 0.04f,
            faceCenter: 0.05f);

        Assert.Empty(PotentialChipSolver.FindCandidates(new[] { downward, upward }));
    }

    [Fact]
    public void PotentialChipVolumeIncreasesWithCutDepthAndWidth()
    {
        var shallow = PotentialChipSolver.FindCandidates(new[]
        {
            Cut(new AxeCutMarkPoint(0f, 0.04f), new AxeCutMarkPoint(0.08f, -0.04f), 0.06f),
            Cut(new AxeCutMarkPoint(0f, -0.04f), new AxeCutMarkPoint(0.08f, 0.04f), 0.06f),
        });
        var deepWide = PotentialChipSolver.FindCandidates(new[]
        {
            Cut(new AxeCutMarkPoint(0f, 0.07f), new AxeCutMarkPoint(0.16f, -0.07f), 0.12f),
            Cut(new AxeCutMarkPoint(0f, -0.07f), new AxeCutMarkPoint(0.16f, 0.07f), 0.12f),
        });

        Assert.True(Assert.Single(shallow).VolumeCubicMeters < Assert.Single(deepWide).VolumeCubicMeters);
    }

    [Fact]
    public void EffortChangesEnergyLimitedDepthWithoutChangingAimThrough()
    {
        var low = Solve(new AxeBiteIntent(0.75f, 0.18f));
        var nominal = Solve(new AxeBiteIntent(1f, 0.18f));
        var hard = Solve(new AxeBiteIntent(1.25f, 0.18f));

        Assert.Equal(AxeBiteStopReason.EnergyLimited, low.StopReason);
        Assert.Equal(AxeBiteStopReason.EnergyLimited, nominal.StopReason);
        Assert.Equal(AxeBiteStopReason.EnergyLimited, hard.StopReason);
        Assert.True(low.PenetrationMeters < nominal.PenetrationMeters);
        Assert.True(nominal.PenetrationMeters < hard.PenetrationMeters);
        Assert.Equal(0.18f, low.Intent.AimThroughMeters);
        Assert.Equal(0.18f, hard.Intent.AimThroughMeters);
    }

    [Fact]
    public void ShortAimThroughLimitsCommitmentWithoutInventingPenetrationEnergy()
    {
        var shortAim = Solve(new AxeBiteIntent(1f, 0.03f));
        var nominalAim = Solve(new AxeBiteIntent(1f, 0.10f));
        var deepAim = Solve(new AxeBiteIntent(1f, 0.18f));

        Assert.Equal(AxeBiteStopReason.CommitmentLimited, shortAim.StopReason);
        Assert.Equal(0.03f, shortAim.PenetrationMeters, 3);
        Assert.True(shortAim.UnusedEnergyJoules > 0f);
        Assert.Equal(AxeBiteStopReason.EnergyLimited, nominalAim.StopReason);
        Assert.Equal(AxeBiteStopReason.EnergyLimited, deepAim.StopReason);
        Assert.Equal(nominalAim.PenetrationMeters, deepAim.PenetrationMeters, 4);
    }

    [Fact]
    public void ExistingKerfCostsLessWorkButDoesNotMutateThePriorState()
    {
        var profile = AxeSwingProfile.AcceptedV1();
        var fresh = WoodCutState.Fresh();
        var first = AxeBiteSolver.Solve(profile, fresh, AxeBiteIntent.Nominal);
        var repeated = AxeBiteSolver.Solve(profile, first.FinalState, AxeBiteIntent.Nominal);

        Assert.Equal(0, fresh.KerfCellCount);
        Assert.True(first.FinalState.KerfCellCount > 0);
        Assert.True(repeated.PenetrationMeters >= first.PenetrationMeters);
        Assert.Equal(2, repeated.FinalState.StrikeCount);
    }

    [Fact]
    public void TrunkFrameIsOrthonormalAndKeepsVerticalWorldUp()
    {
        var result = Solve(AxeBiteIntent.Nominal);
        var frame = result.Frame;

        Assert.Equal(Vector3.UnitY, frame.Vertical);
        Assert.InRange(MathF.Abs(Vector3.Dot(frame.OutwardNormal, frame.InwardNormal) + 1f), 0f, 0.0001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(frame.OutwardNormal, frame.Vertical)), 0f, 0.0001f);
        Assert.InRange(MathF.Abs(Vector3.Dot(frame.Tangent, frame.Vertical)), 0f, 0.0001f);
        Assert.InRange(MathF.Abs(frame.Tangent.Length() - 1f), 0f, 0.0001f);
    }

    [Theory]
    [InlineData(0.59f, 0.1f)]
    [InlineData(1.41f, 0.1f)]
    [InlineData(1f, 0.019f)]
    [InlineData(1f, 0.201f)]
    public void IntentRejectsValuesOutsideTheLabEnvelope(float effort, float aimThrough)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Solve(new AxeBiteIntent(effort, aimThrough)));
    }

    private static AxeBiteResult Solve(AxeBiteIntent intent) =>
        AxeBiteSolver.Solve(
            AxeSwingProfile.AcceptedV1(),
            WoodCutState.Fresh(),
            intent);

    private static AxeCutMark Cut(
        AxeCutMarkPoint entry,
        AxeCutMarkPoint end,
        float width,
        float faceCenter = 0f) =>
        new(new[] { entry, end }, width, faceCenter);
}

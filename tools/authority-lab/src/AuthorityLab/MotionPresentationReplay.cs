namespace AuthorityLab;

public sealed record MotionReplaySample(
    int Sequence,
    double ProducedMilliseconds,
    double ArrivedMilliseconds,
    double X,
    double Y);

public sealed record MotionPresentationReplayMetrics(
    string Policy,
    int RenderedFrames,
    int AppliedFrames,
    int StaleFrames,
    int FinalSequence,
    double TimelineDelayMilliseconds,
    double MeanCurrentTruthErrorMeters,
    double MeanTimelineErrorMeters,
    double FinalErrorMeters,
    double MeanStepMeters,
    double MaximumStepMeters,
    double MeanStepChangeMeters,
    double MaximumStepChangeMeters,
    int LargeStepFrames,
    int StalledWhileTimelineMovingFrames,
    int DiscontinuityGuardFrames,
    int InterpolationBoundsViolations,
    bool Finite);

public static class MotionPresentationReplay
{
    public const string ChaseLatest = "chase_latest";
    public const string BufferedInterpolation = "buffered_interpolation";

    public static MotionPresentationReplayMetrics Evaluate(
        IReadOnlyList<MotionReplaySample> input,
        string policy,
        int frameRateHz,
        int freshnessMilliseconds,
        double convergenceRate,
        int interpolationDelayMilliseconds,
        double discontinuityMeters,
        double largeStepMeters)
    {
        if (input.Count < 2) throw new ArgumentException("At least two motion samples are required.", nameof(input));
        if (frameRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(frameRateHz));
        if (freshnessMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(freshnessMilliseconds));
        if (convergenceRate <= 0) throw new ArgumentOutOfRangeException(nameof(convergenceRate));
        if (interpolationDelayMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(interpolationDelayMilliseconds));
        if (discontinuityMeters <= 0) throw new ArgumentOutOfRangeException(nameof(discontinuityMeters));
        if (largeStepMeters <= 0) throw new ArgumentOutOfRangeException(nameof(largeStepMeters));
        if (policy is not ChaseLatest and not BufferedInterpolation)
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown presentation policy.");

        var source = input.OrderBy(sample => sample.ProducedMilliseconds).ThenBy(sample => sample.Sequence).ToArray();
        var arrivals = input.OrderBy(sample => sample.ArrivedMilliseconds).ThenBy(sample => sample.Sequence).ToArray();
        var accepted = new List<MotionReplaySample>(source.Length);
        var arrivalIndex = 0;
        var newestSequence = 0;
        var latestArrivalMilliseconds = double.NegativeInfinity;
        var frameMilliseconds = 1000.0 / frameRateHz;
        var frameAlpha = 1.0 - Math.Exp(-convergenceRate / frameRateHz);
        var policyDelay = policy == BufferedInterpolation ? interpolationDelayMilliseconds : 0;
        // Every policy receives the same wall-clock observation window for a given
        // arrival profile. A larger buffer must not earn extra convergence time.
        var endMilliseconds =
            arrivals[^1].ArrivedMilliseconds + Math.Min(250, freshnessMilliseconds);

        var hasOutput = false;
        var output = (x: 0d, y: 0d);
        var priorOutput = output;
        var priorStep = (x: 0d, y: 0d);
        var currentErrorTotal = 0d;
        var timelineErrorTotal = 0d;
        var finalError = 0d;
        var stepTotal = 0d;
        var maximumStep = 0d;
        var stepChangeTotal = 0d;
        var maximumStepChange = 0d;
        var renderedFrames = 0;
        var appliedFrames = 0;
        var staleFrames = 0;
        var largeStepFrames = 0;
        var stalledFrames = 0;
        var discontinuityGuardFrames = 0;
        var interpolationBoundsViolations = 0;
        var finite = true;

        for (var frameTime = 0d; frameTime <= endMilliseconds + 0.000001; frameTime += frameMilliseconds)
        {
            while (arrivalIndex < arrivals.Length &&
                   arrivals[arrivalIndex].ArrivedMilliseconds <= frameTime + 0.000001)
            {
                var arrived = arrivals[arrivalIndex++];
                if (arrived.Sequence <= newestSequence) continue;
                newestSequence = arrived.Sequence;
                latestArrivalMilliseconds = frameTime;
                accepted.Add(arrived);
            }

            if (accepted.Count == 0) continue;
            renderedFrames++;
            if (frameTime - latestArrivalMilliseconds > freshnessMilliseconds)
            {
                staleFrames++;
                continue;
            }

            var targetTime = Math.Max(source[0].ProducedMilliseconds, frameTime - policyDelay);
            var timelineTruth = PositionAt(source, targetTime);
            var currentTruth = PositionAt(source, frameTime);
            var target = policy == ChaseLatest
                ? (accepted[^1].X, accepted[^1].Y)
                : InterpolationTarget(
                    accepted,
                    targetTime,
                    discontinuityMeters,
                    ref discontinuityGuardFrames,
                    ref interpolationBoundsViolations);

            if (!hasOutput)
            {
                output = target;
                priorOutput = output;
                hasOutput = true;
            }
            else
            {
                output = policy == ChaseLatest
                    ? (
                        output.x + (target.Item1 - output.x) * frameAlpha,
                        output.y + (target.Item2 - output.y) * frameAlpha)
                    : target;
            }

            var step = (x: output.x - priorOutput.x, y: output.y - priorOutput.y);
            var stepMeters = Length(step);
            var stepChangeMeters = Length((step.x - priorStep.x, step.y - priorStep.y));
            var timelineMovement = Length((
                timelineTruth.x - PositionAt(source, Math.Max(source[0].ProducedMilliseconds, targetTime - frameMilliseconds)).x,
                timelineTruth.y - PositionAt(source, Math.Max(source[0].ProducedMilliseconds, targetTime - frameMilliseconds)).y));

            currentErrorTotal += Distance(output, currentTruth);
            timelineErrorTotal += Distance(output, timelineTruth);
            finalError = Distance(output, source[^1]);
            stepTotal += stepMeters;
            maximumStep = Math.Max(maximumStep, stepMeters);
            stepChangeTotal += stepChangeMeters;
            maximumStepChange = Math.Max(maximumStepChange, stepChangeMeters);
            if (stepMeters > largeStepMeters) largeStepFrames++;
            if (timelineMovement > 0.0001 && stepMeters <= 0.0001) stalledFrames++;
            finite &= double.IsFinite(output.x) && double.IsFinite(output.y);
            appliedFrames++;
            priorOutput = output;
            priorStep = step;
        }

        return new(
            policy,
            renderedFrames,
            appliedFrames,
            staleFrames,
            newestSequence,
            policyDelay,
            Divide(currentErrorTotal, appliedFrames),
            Divide(timelineErrorTotal, appliedFrames),
            finalError,
            Divide(stepTotal, appliedFrames),
            maximumStep,
            Divide(stepChangeTotal, appliedFrames),
            maximumStepChange,
            largeStepFrames,
            stalledFrames,
            discontinuityGuardFrames,
            interpolationBoundsViolations,
            finite);
    }

    private static (double x, double y) InterpolationTarget(
        IReadOnlyList<MotionReplaySample> accepted,
        double targetMilliseconds,
        double discontinuityMeters,
        ref int discontinuityGuardFrames,
        ref int boundsViolations)
    {
        if (accepted.Count == 1 || targetMilliseconds <= accepted[0].ProducedMilliseconds)
            return (accepted[0].X, accepted[0].Y);
        if (targetMilliseconds >= accepted[^1].ProducedMilliseconds)
            return (accepted[^1].X, accepted[^1].Y);

        for (var index = 1; index < accepted.Count; index++)
        {
            var newer = accepted[index];
            if (newer.ProducedMilliseconds < targetMilliseconds) continue;
            var older = accepted[index - 1];
            if (Distance(older, newer) > discontinuityMeters)
            {
                discontinuityGuardFrames++;
                return (older.X, older.Y);
            }

            var range = newer.ProducedMilliseconds - older.ProducedMilliseconds;
            var alpha = range <= 0 ? 1 : (targetMilliseconds - older.ProducedMilliseconds) / range;
            if (alpha < -0.000001 || alpha > 1.000001) boundsViolations++;
            alpha = Math.Clamp(alpha, 0, 1);
            return (
                older.X + (newer.X - older.X) * alpha,
                older.Y + (newer.Y - older.Y) * alpha);
        }
        return (accepted[^1].X, accepted[^1].Y);
    }

    private static (double x, double y) PositionAt(
        IReadOnlyList<MotionReplaySample> source,
        double milliseconds)
    {
        if (milliseconds <= source[0].ProducedMilliseconds) return (source[0].X, source[0].Y);
        if (milliseconds >= source[^1].ProducedMilliseconds) return (source[^1].X, source[^1].Y);
        for (var index = 1; index < source.Count; index++)
        {
            var newer = source[index];
            if (newer.ProducedMilliseconds < milliseconds) continue;
            var older = source[index - 1];
            var range = newer.ProducedMilliseconds - older.ProducedMilliseconds;
            var alpha = range <= 0 ? 1 : Math.Clamp((milliseconds - older.ProducedMilliseconds) / range, 0, 1);
            return (
                older.X + (newer.X - older.X) * alpha,
                older.Y + (newer.Y - older.Y) * alpha);
        }
        return (source[^1].X, source[^1].Y);
    }

    private static double Divide(double value, int count) => count <= 0 ? 0 : value / count;
    private static double Length((double x, double y) value) => Math.Sqrt(value.x * value.x + value.y * value.y);
    private static double Distance((double x, double y) left, (double x, double y) right) =>
        Length((left.x - right.x, left.y - right.y));
    private static double Distance(MotionReplaySample left, MotionReplaySample right) =>
        Distance((left.X, left.Y), (right.X, right.Y));
    private static double Distance((double x, double y) left, MotionReplaySample right) =>
        Distance(left, (right.X, right.Y));
}

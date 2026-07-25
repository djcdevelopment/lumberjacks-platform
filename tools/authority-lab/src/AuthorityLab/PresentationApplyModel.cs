namespace AuthorityLab;

public sealed record PresentationApplyModelPoint(
    int FrameRateHz,
    int RemoteEntities,
    int SnapshotRateHz,
    int InboundSnapshotsPerSecond,
    int RenderApplyCallsPerSecond,
    double ApplyCallsPerSnapshot,
    int StaleTailApplyUpperBound,
    double FrameAlpha,
    double SendIntervalConvergence,
    double ConvergenceTimeConstantMilliseconds);

public static class PresentationApplyModel
{
    public static PresentationApplyModelPoint Evaluate(
        int frameRateHz,
        int remoteEntities,
        int snapshotRateHz,
        int freshnessMilliseconds,
        double convergenceRate)
    {
        if (frameRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(frameRateHz));
        if (remoteEntities <= 0) throw new ArgumentOutOfRangeException(nameof(remoteEntities));
        if (snapshotRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(snapshotRateHz));
        if (freshnessMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(freshnessMilliseconds));
        if (convergenceRate <= 0) throw new ArgumentOutOfRangeException(nameof(convergenceRate));

        var frameAlpha = 1.0 - Math.Exp(-convergenceRate / frameRateHz);
        var sendIntervalSeconds = 1.0 / snapshotRateHz;
        var convergenceOverSendInterval = 1.0 - Math.Exp(-convergenceRate * sendIntervalSeconds);
        var staleTailFramesPerEntity =
            (int)Math.Floor(freshnessMilliseconds / 1000.0 * frameRateHz) + 1;

        return new(
            frameRateHz,
            remoteEntities,
            snapshotRateHz,
            snapshotRateHz * remoteEntities,
            frameRateHz * remoteEntities,
            frameRateHz / (double)snapshotRateHz,
            staleTailFramesPerEntity * remoteEntities,
            frameAlpha,
            convergenceOverSendInterval,
            1000.0 / convergenceRate);
    }
}

namespace AuthorityLab;

public sealed record PresentationSample(
    string SampleId,
    string SourceId,
    ushort Sequence,
    long ProducedMilliseconds,
    long ArrivedMilliseconds,
    double Position);

public sealed record PresentationConsumerDecision(
    PresentationSample Sample,
    string Consumer,
    string Disposition,
    long ApplyMilliseconds,
    long AgeMilliseconds)
{
    public bool Applied => Disposition == "applied";
}

public sealed record PresentationConsumerResult(
    string Consumer,
    IReadOnlyList<PresentationConsumerDecision> Decisions,
    IReadOnlyDictionary<string, ushort> FinalAppliedSequences)
{
    public int Count(string disposition) =>
        Decisions.Count(decision =>
            string.Equals(decision.Disposition, disposition, StringComparison.Ordinal));
}

public static class PresentationConsumerPolicy
{
    public static PresentationConsumerResult Direct(
        IReadOnlyList<PresentationSample> samples) =>
        Evaluate(samples, "direct", drainIntervalMilliseconds: null, expiryMilliseconds: null);

    public static PresentationConsumerResult LatestWins(
        IReadOnlyList<PresentationSample> samples,
        int drainIntervalMilliseconds,
        int? expiryMilliseconds = null)
    {
        if (drainIntervalMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(drainIntervalMilliseconds));
        if (expiryMilliseconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiryMilliseconds));

        return Evaluate(
            samples,
            expiryMilliseconds.HasValue ? "latest_wins_expiry" : "latest_wins",
            drainIntervalMilliseconds,
            expiryMilliseconds);
    }

    private static PresentationConsumerResult Evaluate(
        IReadOnlyList<PresentationSample> samples,
        string consumer,
        int? drainIntervalMilliseconds,
        int? expiryMilliseconds)
    {
        var ordered = samples
            .Select((sample, index) => (sample, index))
            .OrderBy(item => item.sample.ArrivedMilliseconds)
            .ThenBy(item => item.index)
            .ToArray();
        var lastSeen = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var finalApplied = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var decisions = new PresentationConsumerDecision?[samples.Count];

        if (!drainIntervalMilliseconds.HasValue)
        {
            foreach (var (sample, index) in ordered)
            {
                if (!TryAdvance(lastSeen, sample.SourceId, sample.Sequence))
                {
                    decisions[index] = Decision(sample, consumer, "stale", sample.ArrivedMilliseconds);
                    continue;
                }

                decisions[index] = Decision(sample, consumer, "applied", sample.ArrivedMilliseconds);
                finalApplied[sample.SourceId] = sample.Sequence;
            }

            return Result(consumer, decisions, finalApplied);
        }

        var pending = new Dictionary<(string Source, long Drain), (PresentationSample Sample, int Index)>();
        foreach (var (sample, index) in ordered)
        {
            var drain = NextDrain(sample.ArrivedMilliseconds, drainIntervalMilliseconds.Value);
            if (!TryAdvance(lastSeen, sample.SourceId, sample.Sequence))
            {
                decisions[index] = Decision(sample, consumer, "stale", drain);
                continue;
            }

            var key = (sample.SourceId, drain);
            if (pending.TryGetValue(key, out var prior))
                decisions[prior.Index] = Decision(prior.Sample, consumer, "coalesced", drain);
            pending[key] = (sample, index);
        }

        foreach (var item in pending.Values
                     .OrderBy(value => NextDrain(
                         value.Sample.ArrivedMilliseconds,
                         drainIntervalMilliseconds.Value))
                     .ThenBy(value => value.Index))
        {
            var drain = NextDrain(item.Sample.ArrivedMilliseconds, drainIntervalMilliseconds.Value);
            var age = Math.Max(0, drain - item.Sample.ProducedMilliseconds);
            var disposition = expiryMilliseconds.HasValue && age > expiryMilliseconds.Value
                ? "expired"
                : "applied";
            decisions[item.Index] = Decision(item.Sample, consumer, disposition, drain);
            if (disposition == "applied")
                finalApplied[item.Sample.SourceId] = item.Sample.Sequence;
        }

        return Result(consumer, decisions, finalApplied);
    }

    private static bool TryAdvance(
        IDictionary<string, ushort> lastSeen,
        string source,
        ushort sequence)
    {
        if (!lastSeen.TryGetValue(source, out var prior))
        {
            lastSeen[source] = sequence;
            return true;
        }

        var delta = unchecked((ushort)(sequence - prior));
        if (delta == 0 || delta >= 0x8000)
            return false;

        lastSeen[source] = sequence;
        return true;
    }

    private static long NextDrain(long arrivedMilliseconds, int intervalMilliseconds) =>
        arrivedMilliseconds == 0
            ? 0
            : ((arrivedMilliseconds + intervalMilliseconds - 1) / intervalMilliseconds) *
              intervalMilliseconds;

    private static PresentationConsumerDecision Decision(
        PresentationSample sample,
        string consumer,
        string disposition,
        long applyMilliseconds) =>
        new(
            sample,
            consumer,
            disposition,
            applyMilliseconds,
            Math.Max(0, applyMilliseconds - sample.ProducedMilliseconds));

    private static PresentationConsumerResult Result(
        string consumer,
        IReadOnlyList<PresentationConsumerDecision?> decisions,
        IReadOnlyDictionary<string, ushort> finalApplied) =>
        new(
            consumer,
            decisions.Select(decision =>
                decision ?? throw new InvalidOperationException("presentation decision was not classified"))
                .ToArray(),
            finalApplied);
}

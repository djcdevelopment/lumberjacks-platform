using System.Diagnostics.Metrics;

namespace Game.Simulation.Tick;

/// <summary>
/// Per-tick timing instrumentation for the 20Hz tick loop. Two output paths, both DB-less:
///
///  1. A <see cref="System.Diagnostics.Metrics.Histogram{T}"/> ("game.tick.duration",
///     tagged by phase) — picked up automatically by OpenTelemetry (AddMeter) or
///     dotnet-counters if a listener is attached; effectively a no-op otherwise.
///  2. A rolling window of 100 ticks (~5s at 20Hz) reduced to per-phase p50/p99/max:
///     logged as one line per window and exposed as <see cref="LastWindow"/> for /tick.
///
/// Phases: total, interval (time between tick starts — catches timer starvation even
/// when tick work is fast), sim (input apply + physics), hash, broadcast (whole
/// broadcaster call), interest (AoI filtering), send (per-entity serialization +
/// socket writes), housekeeping.
///
/// Hot-path cost per tick: a few Stopwatch timestamps, array writes, and histogram
/// records — no allocation, no locks. The window reduction (sorting ~100 doubles per
/// phase) runs once per ~5s.
///
/// Threading: RecordBroadcastPhases and RecordTick are called only from the tick
/// loop's sequential flow. Concurrent readers get an immutable snapshot via LastWindow.
/// </summary>
public sealed class TickMetrics : IDisposable
{
    public const string MeterName = "Game.Simulation.Tick";

    /// <summary>Tick budget at 20Hz — a tick slower than this delays the next one.</summary>
    public const double TickBudgetMs = 50.0;

    /// <summary>Window length in ticks (~5s at 20Hz).</summary>
    public const int WindowTicks = 100;

    private static readonly string[] PhaseNames =
        ["total", "interval", "sim", "hash", "broadcast", "interest", "send", "housekeeping"];
    private const int TotalIdx = 0, IntervalIdx = 1, SimIdx = 2, HashIdx = 3,
        BroadcastIdx = 4, InterestIdx = 5, SendIdx = 6, HousekeepingIdx = 7;

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _overrunCounter;
    private readonly KeyValuePair<string, object?>[] _phaseTags;
    private readonly ILogger<TickMetrics> _logger;

    // Window sample buffers, written only from the tick loop thread.
    private readonly double[][] _samples;
    private int _sampleCount;
    private int _windowOverruns;

    // Broadcast sub-phase timings for the in-flight tick; folded into the next RecordTick.
    private double _pendingInterestMs;
    private double _pendingSendMs;

    // Replication counters for the in-flight tick; folded into the next RecordTick.
    private long _pendingSent;
    private long _pendingCulled;

    // Send-loop rework (phase 2) counters for the in-flight tick; folded into the next
    // RecordTick. Deadline aborts and degrade are both 0/false whenever their respective
    // Replication:BroadcastDeadlineMs / Replication:AdaptiveDegrade knobs are off.
    private int _pendingDeadlineAborts;
    private bool _pendingDegraded;

    // Window totals (sums, not percentiles) for entity-updates sent vs. culled by the
    // active InterestManager policy. Reset when the window closes.
    private long _windowSent;
    private long _windowCulled;

    // Window totals for the send-loop rework knobs. DeadlineAborts sums sessions aborted
    // across the window; DegradedTicks counts how many of the window's ticks ran with
    // adaptive degrade active. Reset when the window closes.
    private long _windowDeadlineAborts;
    private int _windowDegradedTicks;

    // Set once at startup by the broadcaster (post-auto-resolve — see SendFanOut.ResolveWorkerCount).
    private int _effectiveSendWorkers = 1;

    // Set once at startup by the broadcaster (post-auto-resolve — see SendFanOut.ResolveUdpSocketCount).
    // Phase 3a: the effective Replication:UdpSockets count UdpTransport is using.
    private int _effectiveUdpSockets = 1;

    // Set once at startup by the broadcaster; "unknown" until then.
    private string _replicationPolicy = "unknown";

    private volatile TickTimingSnapshot? _lastWindow;

    public TickMetrics(IMeterFactory meterFactory, ILogger<TickMetrics> logger)
    {
        _logger = logger;
        _meter = meterFactory.Create(MeterName);
        _duration = _meter.CreateHistogram<double>(
            "game.tick.duration", unit: "ms",
            description: "Duration of tick loop phases (tagged by phase)");
        _overrunCounter = _meter.CreateCounter<long>(
            "game.tick.overruns",
            description: $"Ticks whose total duration exceeded the {TickBudgetMs}ms budget");
        _phaseTags = PhaseNames
            .Select(p => new KeyValuePair<string, object?>("phase", p))
            .ToArray();
        _samples = new double[PhaseNames.Length][];
        for (var i = 0; i < _samples.Length; i++)
            _samples[i] = new double[WindowTicks];
    }

    /// <summary>Latest completed window, or null until the first window closes.</summary>
    public TickTimingSnapshot? LastWindow => _lastWindow;

    /// <summary>
    /// Called by the tick broadcaster (on ticks where it runs) with its sub-phase
    /// timings. Ticks with no broadcast report 0 for both phases.
    /// </summary>
    public void RecordBroadcastPhases(double interestMs, double sendMs)
    {
        _pendingInterestMs = interestMs;
        _pendingSendMs = sendMs;
    }

    /// <summary>Called once at startup by the broadcaster to tag the active replication policy.</summary>
    public void SetReplicationPolicy(string policyName) => _replicationPolicy = policyName;

    /// <summary>
    /// Called once at startup by the broadcaster to tag the EFFECTIVE send-worker count
    /// (Replication:SendWorkers after auto-resolution — see SendFanOut.ResolveWorkerCount).
    /// </summary>
    public void SetSendWorkers(int workers) => _effectiveSendWorkers = workers;

    /// <summary>
    /// Called once at startup by the broadcaster to tag the EFFECTIVE UDP send-socket count
    /// (Replication:UdpSockets after auto-resolution — see SendFanOut.ResolveUdpSocketCount).
    /// </summary>
    public void SetUdpSockets(int sockets) => _effectiveUdpSockets = sockets;

    /// <summary>
    /// Called by the tick broadcaster (on ticks where it runs) with the number of sessions
    /// aborted this tick because their send raced the Replication:BroadcastDeadlineMs
    /// deadline. Ticks with no broadcast, or with deadline shedding off, report 0.
    /// </summary>
    public void RecordDeadlineAborts(int aborts) => _pendingDeadlineAborts = aborts;

    /// <summary>
    /// Called by the tick broadcaster (on ticks where it runs) to flag whether THIS tick's
    /// broadcast ran in adaptive-degrade mode (Replication:AdaptiveDegrade, triggered by the
    /// previous tick's broadcast wall time exceeding budget).
    /// </summary>
    public void RecordDegraded(bool degraded) => _pendingDegraded = degraded;

    /// <summary>
    /// Called by the tick broadcaster (on ticks where it runs) with the total entity-updates
    /// sent vs. culled by InterestManager, summed across every observer this tick. Ticks with
    /// no broadcast report 0 for both.
    /// </summary>
    public void RecordReplication(long sent, long culled)
    {
        _pendingSent = sent;
        _pendingCulled = culled;
    }

    /// <summary>Called by TickLoop once per tick after all phases complete.</summary>
    public void RecordTick(
        long tick, double totalMs, double intervalMs,
        double simMs, double hashMs, double broadcastMs, double housekeepingMs)
    {
        var interestMs = _pendingInterestMs;
        var sendMs = _pendingSendMs;
        _pendingInterestMs = 0;
        _pendingSendMs = 0;

        var sent = _pendingSent;
        var culled = _pendingCulled;
        _pendingSent = 0;
        _pendingCulled = 0;
        _windowSent += sent;
        _windowCulled += culled;

        var deadlineAborts = _pendingDeadlineAborts;
        _pendingDeadlineAborts = 0;
        _windowDeadlineAborts += deadlineAborts;

        var wasDegraded = _pendingDegraded;
        _pendingDegraded = false;
        if (wasDegraded) _windowDegradedTicks++;

        _duration.Record(totalMs, _phaseTags[TotalIdx]);
        if (intervalMs > 0)
            _duration.Record(intervalMs, _phaseTags[IntervalIdx]);
        _duration.Record(simMs, _phaseTags[SimIdx]);
        _duration.Record(hashMs, _phaseTags[HashIdx]);
        _duration.Record(broadcastMs, _phaseTags[BroadcastIdx]);
        _duration.Record(interestMs, _phaseTags[InterestIdx]);
        _duration.Record(sendMs, _phaseTags[SendIdx]);
        _duration.Record(housekeepingMs, _phaseTags[HousekeepingIdx]);

        var slot = _sampleCount++;
        _samples[TotalIdx][slot] = totalMs;
        _samples[IntervalIdx][slot] = intervalMs;
        _samples[SimIdx][slot] = simMs;
        _samples[HashIdx][slot] = hashMs;
        _samples[BroadcastIdx][slot] = broadcastMs;
        _samples[InterestIdx][slot] = interestMs;
        _samples[SendIdx][slot] = sendMs;
        _samples[HousekeepingIdx][slot] = housekeepingMs;

        if (totalMs > TickBudgetMs)
        {
            _windowOverruns++;
            _overrunCounter.Add(1);
        }

        if (_sampleCount >= WindowTicks)
            CloseWindow(tick);
    }

    private void CloseWindow(long tick)
    {
        var n = _sampleCount;
        var phases = new Dictionary<string, PhaseStats>(PhaseNames.Length);
        for (var i = 0; i < PhaseNames.Length; i++)
        {
            var sorted = new double[n];
            Array.Copy(_samples[i], sorted, n);
            Array.Sort(sorted);
            phases[PhaseNames[i]] = new PhaseStats(
                P50Ms: Percentile(sorted, 0.50),
                P99Ms: Percentile(sorted, 0.99),
                MaxMs: sorted[n - 1]);
        }

        var replication = new ReplicationWindowStats(
            _replicationPolicy, _windowSent, _windowCulled,
            _effectiveSendWorkers, _effectiveUdpSockets, _windowDeadlineAborts, _windowDegradedTicks);

        _lastWindow = new TickTimingSnapshot(
            WindowEndTick: tick,
            SampleCount: n,
            Overruns: _windowOverruns,
            BudgetMs: TickBudgetMs,
            Phases: phases,
            Replication: replication,
            CapturedAt: DateTimeOffset.UtcNow);

        // Disable by setting logging category "Game.Simulation.Tick.TickMetrics" above Information.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Tick timing (last {Count} ticks): total p50={TotalP50:F2}ms p99={TotalP99:F2}ms max={TotalMax:F2}ms overruns={Overruns} (budget {BudgetMs}ms) | interval p99={IntervalP99:F1}ms | sim p99={SimP99:F2}ms hash p99={HashP99:F2}ms broadcast p99={BroadcastP99:F2}ms (interest p99={InterestP99:F2}ms send p99={SendP99:F2}ms) housekeeping p99={HousekeepingP99:F2}ms | repl policy={Policy} sent={Sent} culled={Culled} sendWorkers={SendWorkers} udpSockets={UdpSockets} deadlineAborts={DeadlineAborts} degradedTicks={DegradedTicks}",
                n,
                phases["total"].P50Ms, phases["total"].P99Ms, phases["total"].MaxMs,
                _windowOverruns, TickBudgetMs,
                phases["interval"].P99Ms,
                phases["sim"].P99Ms, phases["hash"].P99Ms,
                phases["broadcast"].P99Ms, phases["interest"].P99Ms, phases["send"].P99Ms,
                phases["housekeeping"].P99Ms,
                replication.Policy, replication.Sent, replication.Culled,
                replication.SendWorkers, replication.UdpSockets, replication.DeadlineAborts, replication.DegradedTicks);
        }

        _sampleCount = 0;
        _windowOverruns = 0;
        _windowSent = 0;
        _windowCulled = 0;
        _windowDeadlineAborts = 0;
        _windowDegradedTicks = 0;
    }

    /// <summary>Nearest-rank percentile on an ascending-sorted array.</summary>
    private static double Percentile(double[] sortedAscending, double quantile)
    {
        var n = sortedAscending.Length;
        var rank = (int)Math.Ceiling(quantile * n);
        return sortedAscending[Math.Clamp(rank, 1, n) - 1];
    }

    public void Dispose() => _meter.Dispose();
}

/// <summary>p50/p99/max for one phase over one window, in milliseconds.</summary>
public sealed record PhaseStats(double P50Ms, double P99Ms, double MaxMs);

/// <summary>
/// Entity-update replication totals for one window (~5s), summed (not percentiled) across
/// every observer/tick in the window. Sent = updates actually dispatched; Culled = updates
/// evaluated by InterestManager but dropped by the active policy.
///
/// SendWorkers/UdpSockets/DeadlineAborts/DegradedTicks are the send-loop rework (phase 2) and
/// per-worker-socket (phase 3a) knobs: SendWorkers is the EFFECTIVE (post-auto-resolve)
/// Replication:SendWorkers value; UdpSockets is the EFFECTIVE (post-auto-resolve)
/// Replication:UdpSockets value (see SendFanOut.ResolveUdpSocketCount) — both constant for the
/// process's lifetime, not really "window" stats but exposed here alongside the others they
/// explain. DeadlineAborts sums Replication:BroadcastDeadlineMs aborts across the window;
/// DegradedTicks counts how many of the window's ticks ran with Replication:AdaptiveDegrade
/// active. Under SendWorkers>1, the "send"/"interest" phase timings above (from
/// RecordBroadcastPhases) are SUMS across concurrent worker chunks, not wall time — the
/// "broadcast" phase (measured once around the whole call, in TickLoop) is the actual
/// tick-budget truth. The gap between the broadcast wall time and the interest+send sum is the
/// parallelism-efficiency signal: a wide gap means the workers meaningfully overlapped; a
/// near-zero gap means little real concurrency was achieved (the phase-3a UdpSockets
/// experiment exists to test whether a shared UdpClient explains a near-zero gap).
/// </summary>
public sealed record ReplicationWindowStats(
    string Policy, long Sent, long Culled,
    int SendWorkers, int UdpSockets, long DeadlineAborts, int DegradedTicks);

/// <summary>Per-phase tick timing stats for the most recent completed window (~5s).</summary>
public sealed record TickTimingSnapshot(
    long WindowEndTick,
    int SampleCount,
    int Overruns,
    double BudgetMs,
    IReadOnlyDictionary<string, PhaseStats> Phases,
    ReplicationWindowStats Replication,
    DateTimeOffset CapturedAt);

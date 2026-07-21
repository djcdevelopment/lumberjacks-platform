using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.Contracts.Protocol;
using Game.Contracts.Protocol.Binary;
using Game.ServiceDefaults;
using Game.Simulation.Tick;
using Game.Simulation.World;

namespace Game.Gateway.WebSocket;

/// <summary>
/// Broadcasts authoritative tick state to connected clients.
/// Called by TickLoop (via ITickBroadcaster) after each simulation step for changed entities only.
///
/// Uses InterestManager for per-player AoI filtering, per the active ReplicationPolicy
/// (env Replication__Policy, default "tiered" — see ReplicationOptions):
///   Tiered (default) — Near (0–NearRadius) every tick, Mid (NearRadius–MidRadius) every
///                       MidTickInterval-th tick, Far dropped. 100/300/4 by default.
///   Full              — no filtering; every observer gets every changed entity every tick.
///   Radius            — hard cutoff at NearRadius; inside every tick, outside dropped.
///
/// Sends binary frames to binary-mode sessions, JSON to JSON-mode sessions.
///
/// Send-loop rework (phase 2): the per-region session list is split into
/// Replication:SendWorkers contiguous chunks (default 1 — exactly the original serial
/// foreach) and rotated by tick number first (Replication:SendWorkers-independent — see
/// <see cref="SendFanOut.RotateOffset"/>) so no session is systematically served last. A
/// session appears in exactly one chunk per tick, so per-socket send serialization
/// (WebSocket.SendAsync allows only one outstanding send per socket) is preserved even
/// though chunks run concurrently.
///
/// Phase 3a′ (Follow-up F): SendWorkers&gt;1 chunks are dispatched via
/// Parallel.ForEachAsync(MaxDegreeOfParallelism = SendWorkers) instead of an inline
/// async-method-call + Task.WhenAll, so they genuinely run on distinct thread-pool threads
/// — the original shape looked parallel but every chunk's awaits completed synchronously
/// (sync UDP Send, inline-completing small-frame LAN WS sends), so it ran inline-serial on
/// the tick thread. SendWorkers&lt;=1 (the default) is untouched: still the exact original
/// direct `await` on chunk 0, no Task/Parallel overhead at all.
///
/// Deadline shedding (Replication:BroadcastDeadlineMs, default 0/off): one
/// CancellationTokenSource per broadcast call, shared by every send this tick. A session
/// whose send is still in flight (or hasn't started) when the deadline fires gets
/// OperationCanceledException, its socket is Abort()'d (a mid-frame cancel corrupts the WS
/// stream — never keep using it) and counted, and the loop moves on: the tick must end
/// within budget even if that means shedding the slowest sessions this tick. The existing
/// per-send try/catch + SessionManager's stale-session cleanup reaps the aborted sessions.
///
/// Adaptive degrade (Replication:AdaptiveDegrade, default false — ADR-0011 "reduce
/// frequency before dropping"), v2 (burst-aligned, see AdaptiveDegrade class doc for why v1
/// mistimed tiered): for the tiered policy, suppression is decided from the LAST BURST
/// TICK's broadcast wall time (<see cref="_lastBurstBroadcastWallMs"/>, updated only on
/// ticks where InterestManager.IsBurstTick is true) and only ever applies on the CURRENT
/// tick if it is itself a burst tick — see AdaptiveDegrade.ShouldSuppressMidBand. Radius/full
/// (no mid band) keep the v1 next-tick-aligned alternating-skip rule, driven by
/// <see cref="_lastBroadcastWallMs"/> (every tick's own wall time, as before). Both trackers
/// are self-measured — the same interval TickLoop measures around this whole call, since
/// nothing else runs between them — and both lift the instant the relevant broadcast fits
/// inside budget again: no cooldown, no hysteresis.
/// </summary>
public class TickBroadcaster : ITickBroadcaster
{
    private readonly SessionManager _sessions;
    private readonly InterestManager _interest;
    private readonly UdpTransport? _udpTransport;
    private readonly ILogger<TickBroadcaster> _logger;
    private readonly TickMetrics? _metrics;
    private readonly int _sendWorkers;
    private readonly int _deadlineMs;
    private readonly bool _adaptiveDegrade;

    // interest_subscription_changed evidence feed (Replication:SubscriptionEvents, default off —
    // see InterestSubscriptionTracker). All of this is dormant unless explicitly enabled: when
    // _subscriptionEvents is false the sampling branch in BroadcastTickAsync is never taken, so
    // there is zero added cost on the default/benchmark path.
    private readonly bool _subscriptionEvents;
    private readonly int _subscriptionSampleTicks;
    private readonly double _subscriptionRadius;
    private readonly bool _subscriptionPolicyObservable; // false for Full (no interest filtering to observe)
    private readonly IHttpClientFactory? _httpFactory;
    private readonly string _eventLogUrl;
    private readonly InterestSubscriptionTracker _subscriptionTracker = new();

    // Guards against overlapping off-tick-thread emit passes (0 = idle, 1 = a pass is running).
    // A sample is skipped if the previous pass hasn't finished — samples are spaced by
    // _subscriptionSampleTicks so this is rare, and skipping is the right call (the next sample
    // is a fresh full snapshot anyway; no state is lost).
    private int _subscriptionEmitInFlight;

    // Hard ceiling on events emitted from a single sample — a safety valve against a pathological
    // burst (e.g. a whole region's worth of first-time subscriptions at once). Sampling already
    // bounds the RATE; this bounds the size of any one pass.
    private const int SubscriptionMaxEventsPerSample = 500;

    // Mutable, but only ever touched from the tick loop's sequential flow — BroadcastTickAsync
    // calls never overlap (TickLoop awaits each one before starting the next), so no locking.
    // v1 tracker: every tick's own broadcast wall time — still drives radius/full's alternating
    // skip (see AdaptiveDegrade.ShouldDegrade / ShouldSkipAlternating).
    private double _lastBroadcastWallMs;

    // v2 tracker (phase 3a): the wall time of the LAST tick that was itself a burst tick (see
    // InterestManager.IsBurstTick) — updated ONLY on burst ticks, so an overrun on an
    // intervening non-burst tick can never leak into the next burst tick's suppress decision.
    // Drives the tiered policy's mid-band suppression (see AdaptiveDegrade.ShouldSuppressMidBand).
    private double _lastBurstBroadcastWallMs;

    public TickBroadcaster(
        SessionManager sessions,
        WorldState world,
        IConfiguration config,
        ILogger<TickBroadcaster> logger,
        UdpTransport? udpTransport = null,
        TickMetrics? metrics = null,
        IHttpClientFactory? httpFactory = null)
    {
        _sessions = sessions;
        var replicationOptions = ReplicationOptions.FromConfiguration(
            config, warning => logger.LogWarning("Replication config: {Warning}", warning));
        _interest = new InterestManager(world.SpatialGrid, replicationOptions);
        _udpTransport = udpTransport;
        _logger = logger;
        _metrics = metrics;
        _metrics?.SetReplicationPolicy(replicationOptions.PolicyName);

        _sendWorkers = SendFanOut.ResolveWorkerCount(replicationOptions.SendWorkers, Environment.ProcessorCount);
        _deadlineMs = replicationOptions.BroadcastDeadlineMs;
        _adaptiveDegrade = replicationOptions.AdaptiveDegrade;
        _metrics?.SetSendWorkers(_sendWorkers);

        // interest_subscription_changed feed (off by default). Full policy has no interest
        // filtering, so there is no subscription boundary to observe — disable there even if the
        // flag is set (SubscriptionRadius is +inf for Full).
        _subscriptionEvents = replicationOptions.SubscriptionEvents;
        _subscriptionSampleTicks = Math.Max(1, replicationOptions.SubscriptionSampleTicks);
        _subscriptionRadius = _interest.SubscriptionRadius;
        _subscriptionPolicyObservable = !double.IsPositiveInfinity(_subscriptionRadius);
        _httpFactory = httpFactory;
        _eventLogUrl = (config["ServiceUrls:EventLog"] ?? "http://localhost:4002").TrimEnd('/');
        if (_subscriptionEvents && (!_subscriptionPolicyObservable || _httpFactory == null))
        {
            _logger.LogInformation(
                "Replication:SubscriptionEvents requested but inactive (policy={Policy} observable={Observable} httpFactory={HasFactory}) — no interest_subscription_changed events will be emitted",
                replicationOptions.PolicyName, _subscriptionPolicyObservable, _httpFactory != null);
        }
        else if (_subscriptionEvents)
        {
            _logger.LogInformation(
                "Replication:SubscriptionEvents on — sampling interest subscriptions every {SampleTicks} tick(s) at radius {Radius}",
                _subscriptionSampleTicks, _subscriptionRadius);
        }

        // Phase 3a: UdpTransport resolves its own effective socket count at construction time
        // (pure config + processor-count math — see UdpTransport.SocketCount), independent of
        // whether its BackgroundService has started yet, so it's safe to read here. No UDP
        // transport (e.g. standalone Simulation service) reports 1 — the single-socket default.
        var udpSockets = _udpTransport?.SocketCount ?? 1;
        _metrics?.SetUdpSockets(udpSockets);

        _logger.LogInformation(
            "Replication policy={Policy} nearRadius={NearRadius} midRadius={MidRadius} midTickInterval={MidTickInterval} sendWorkers={SendWorkers} udpSockets={UdpSockets} deadlineMs={DeadlineMs} adaptive={Adaptive}",
            replicationOptions.PolicyName, replicationOptions.NearRadius, replicationOptions.MidRadius, replicationOptions.MidTickInterval,
            _sendWorkers, udpSockets, _deadlineMs, _adaptiveDegrade);
    }

    public async Task BroadcastTickAsync(
        IReadOnlyDictionary<string, Player> players,
        IReadOnlyDictionary<string, Region> regions,
        IReadOnlyDictionary<string, NaturalResource> resources,
        HashSet<string> changedPlayerIds,
        HashSet<string> changedResourceIds,
        long tick,
        uint stateHash)
    {
        var wallStart = Stopwatch.GetTimestamp();

        // 1. Prepare Player data
        var playerData = new Dictionary<string, (string RegionId, Player Player)>();
        foreach (var playerId in changedPlayerIds)
        {
            if (!players.TryGetValue(playerId, out var player))
                continue;
            playerData[playerId] = (player.RegionId, player);
        }

        // 2. Prepare Resource data
        var resourceData = new Dictionary<string, (string RegionId, NaturalResource Resource)>();
        foreach (var resourceId in changedResourceIds)
        {
            if (!resources.TryGetValue(resourceId, out var resource))
                continue;
            resourceData[resourceId] = (resource.RegionId, resource);
        }

        // 3. Group all changes by region
        var regionIds = playerData.Values.Select(u => u.RegionId)
            .Concat(resourceData.Values.Select(r => r.RegionId))
            .Distinct();

        // Raw Stopwatch tick accumulators for the interest and send sub-phases. Under
        // SendWorkers>1 these are SUMS across worker chunks (each chunk accumulates locally,
        // no Interlocked needed — one accumulator instance per task, summed at Task.WhenAll
        // join). Broadcast WALL time (measured by TickLoop around the whole
        // BroadcastTickAsync call) is the tick-budget truth; the gap between that wall time
        // and this interest+send SUM is the parallelism-efficiency signal (wide gap = good
        // overlap across workers, near-equal = no effective parallelism).
        // "send" includes per-entity serialization (stackalloc/JSON) — socket writes dominate.
        long interestElapsed = 0, sendElapsed = 0;

        // Replication counters: how many player-update candidates InterestManager evaluated
        // per observer (regionPlayerChanges.Count) vs. how many it let through. Resource
        // broadcasts are out of policy scope (region-wide, always sent) and excluded here.
        long entitiesSent = 0, entitiesCulled = 0;

        // Deadline shedding: off (the default) means no CTS at all, so every send below runs
        // with CancellationToken.None — zero behavior change from before this feature. UDP
        // sends are sync (TrySendUdpEntityUpdate) and unaffected either way.
        var deadlineAborts = 0;
        using var deadlineCts = BroadcastDeadline.IsEnabled(_deadlineMs) ? new CancellationTokenSource() : null;
        deadlineCts?.CancelAfter(_deadlineMs);
        var deadlineToken = deadlineCts?.Token ?? CancellationToken.None;

        // Adaptive degrade: decided ONCE for the whole tick. Off (the default) leaves both
        // enabled checks false, so suppressMidBand/suppressAlternating below are never true
        // and every session gets its normal update — zero behavior change.
        //
        // v1 (radius/full, no mid band): stateless beyond the PREVIOUS tick's own wall time.
        var isTiered = _interest.Policy == ReplicationPolicy.Tiered;
        var suppressAlternating = !isTiered && AdaptiveDegrade.ShouldDegrade(_adaptiveDegrade, _lastBroadcastWallMs);

        // v2 (tiered, burst-aligned): stateless beyond the LAST BURST TICK's wall time —
        // suppression only ever applies when THIS tick is itself a burst tick (see
        // AdaptiveDegrade class doc for why v1's next-tick alignment mistimed tiered).
        var isBurstTick = _interest.IsBurstTick(tick);
        var suppressMidBand = isTiered && AdaptiveDegrade.ShouldSuppressMidBand(_adaptiveDegrade, isBurstTick, _lastBurstBroadcastWallMs);

        var degraded = suppressMidBand || suppressAlternating;

        foreach (var regionId in regionIds)
        {
            var sessions = _sessions.GetByRegion(regionId).ToList();
            if (sessions.Count == 0) continue;

            var regionPlayerChanges = changedPlayerIds
                .Where(id => playerData.TryGetValue(id, out var u) && u.RegionId == regionId)
                .ToHashSet();

            var regionResourceChanges = changedResourceIds
                .Where(id => resourceData.TryGetValue(id, out var r) && r.RegionId == regionId)
                .ToHashSet();

            // Fairness rotation: always on, independent of SendWorkers. Rotates the starting
            // point through the session list each tick so early-connected sessions don't
            // absorb the whole send-phase tail every single tick — session order was never a
            // guaranteed fairness contract. No list copy: RotatedIndex maps a chunk-local
            // position back to the real index below.
            var offset = SendFanOut.RotateOffset(tick, sessions.Count);
            var chunks = SendFanOut.Chunk(sessions.Count, _sendWorkers);

            if (_sendWorkers <= 1)
            {
                // Default: exactly today's serial behavior, no Task/array overhead. Chunk
                // index 0 — with UdpSockets resolving to 1 (its own default), this always
                // picks the one bound socket, same as before phase 3a existed.
                var acc = new SendAccumulator();
                var (start, length) = chunks[0];
                await SendChunkAsync(
                    sessions, offset, start, length, chunkIndex: 0,
                    regionPlayerChanges, regionResourceChanges, playerData, resourceData, players,
                    tick, stateHash, deadlineToken, suppressMidBand, suppressAlternating, acc);
                interestElapsed += acc.InterestTicks;
                sendElapsed += acc.SendTicks;
                entitiesSent += acc.Sent;
                entitiesCulled += acc.Culled;
                deadlineAborts += acc.Aborts;
            }
            else
            {
                // Phase 3a′ (Follow-up F): directly invoking the async SendChunkAsync method
                // N times and Task.WhenAll'ing the results LOOKED parallel but wasn't — UDP
                // Socket.Send is synchronous and small-frame WS SendAsync completes inline on
                // LAN, so every await inside SendChunkAsync returned an already-completed
                // Task and every chunk ran inline-serial on the calling (tick) thread. Measured
                // proof: broadcast wall ~= interest+send summed across workers (send:wall ratio
                // ~0.9) with ~3/24 cores busy while p99 blew budget.
                //
                // Parallel.ForEachAsync(dop=_sendWorkers) fixes this: it spins up
                // MaxDegreeOfParallelism worker loops via Task.Run (always queues to the thread
                // pool, never runs inline), each pulling chunks from the shared source. Since
                // Chunk() always returns exactly _sendWorkers chunks, this is a 1:1
                // chunk-to-pool-thread mapping — the CPU-bound per-entity serialization now
                // actually runs concurrently across idle cores instead of queueing behind
                // itself on one thread.
                //
                // ParallelOptions.CancellationToken is deliberately left unset (default/none).
                // The broadcast deadline still flows in as deadlineToken, passed straight
                // through to SendChunkAsync exactly as in the serial path — each chunk catches
                // OperationCanceledException per-send and keeps going (see SendChunkAsync).
                // Wiring deadlineToken into ParallelOptions too would make Parallel.ForEachAsync
                // itself throw and unwind the whole fan-out the instant the deadline fires,
                // instead of letting each chunk shed its own slow sessions independently.
                var accumulators = new SendAccumulator[chunks.Count];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, chunks.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = _sendWorkers },
                    async (i, _) =>
                    {
                        var (start, length) = chunks[i];
                        var acc = new SendAccumulator();
                        accumulators[i] = acc;
                        await SendChunkAsync(
                            sessions, offset, start, length, chunkIndex: i,
                            regionPlayerChanges, regionResourceChanges, playerData, resourceData, players,
                            tick, stateHash, deadlineToken, suppressMidBand, suppressAlternating, acc);
                    });

                foreach (var acc in accumulators)
                {
                    interestElapsed += acc.InterestTicks;
                    sendElapsed += acc.SendTicks;
                    entitiesSent += acc.Sent;
                    entitiesCulled += acc.Culled;
                    deadlineAborts += acc.Aborts;
                }
            }
        }

        // Broadcast WALL time — measured here around the whole call, NOT summed across worker
        // chunks — is the tick-budget truth (the same interval TickLoop measures). Deliberately
        // recorded even when AdaptiveDegrade is off, so flipping it on mid-run has a real
        // previous value to work from immediately rather than a cold-start 0.
        var wallMs = Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds;
        _lastBroadcastWallMs = wallMs; // v1 tracker — feeds radius/full's NEXT-tick decision.
        if (isBurstTick)
            _lastBurstBroadcastWallMs = wallMs; // v2 tracker — updated ONLY on burst ticks.

        _metrics?.RecordBroadcastPhases(
            Stopwatch.GetElapsedTime(0, interestElapsed).TotalMilliseconds,
            Stopwatch.GetElapsedTime(0, sendElapsed).TotalMilliseconds);
        _metrics?.RecordReplication(entitiesSent, entitiesCulled);
        _metrics?.RecordDeadlineAborts(deadlineAborts);
        _metrics?.RecordDegraded(degraded);

        // Deadline aborts get an immediate debug-level breadcrumb too (in addition to the
        // windowed count above) since an abort is a live socket getting force-closed — worth
        // seeing as it happens, not just in the ~5s rollup. Adaptive degrade deliberately logs
        // nothing per tick (see class doc) — the windowed degradedTicks count is enough.
        if (deadlineAborts > 0)
            _logger.LogDebug("Broadcast deadline shed {Aborts} session(s) this tick (tick {Tick})", deadlineAborts, tick);

        // interest_subscription_changed evidence feed — sampled, and dispatched OFF the tick
        // thread so it never inflates the broadcast wall time TickLoop measures around this call
        // (the very number replication-policy experiments care about). No-op unless enabled.
        MaybeSampleSubscriptions(players, tick);
    }

    /// <summary>
    /// On a sample tick (and only when enabled + observable + wired to an EventLog), snapshot player
    /// positions cheaply on the tick thread, then hand the O(n²) diff + fire-and-forget emit to a
    /// background task. The tick thread does O(n) snapshot work and returns immediately; nothing
    /// here touches the send path's state or the SpatialGrid, so it cannot race the broadcast.
    /// </summary>
    private void MaybeSampleSubscriptions(IReadOnlyDictionary<string, Player> players, long tick)
    {
        if (!_subscriptionEvents || !_subscriptionPolicyObservable || _httpFactory == null)
            return;
        if (tick % _subscriptionSampleTicks != 0)
            return;

        // Skip if a previous pass is still running — samples are spaced by _subscriptionSampleTicks,
        // so this only trips under genuine overload, and skipping loses nothing (the next sample is
        // a fresh full snapshot). Prevents overlapping passes from racing the tracker's state.
        if (Interlocked.CompareExchange(ref _subscriptionEmitInFlight, 1, 0) != 0)
            return;

        // Snapshot on the tick thread (cheap, O(n), no distance math) so the background pass works
        // from a stable copy rather than the live, mutating player dictionary.
        var snapshot = new List<InterestSubscription.PlayerSnapshot>(players.Count);
        foreach (var (id, p) in players)
        {
            if (!p.Connected) continue;
            snapshot.Add(new InterestSubscription.PlayerSnapshot(id, p.RegionId, p.Position.X, p.Position.Z));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ComputeAndEmitSubscriptionsAsync(snapshot, tick);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "interest_subscription_changed sample failed (tick {Tick})", tick);
            }
            finally
            {
                Volatile.Write(ref _subscriptionEmitInFlight, 0);
            }
        });
    }

    private async Task ComputeAndEmitSubscriptionsAsync(
        IReadOnlyList<InterestSubscription.PlayerSnapshot> snapshot, long tick)
    {
        var current = InterestSubscription.ComputeSubscriptions(snapshot, _subscriptionRadius);
        var changes = _subscriptionTracker.DiffAll(current);
        if (changes.Count == 0)
            return;

        var emitted = 0;
        foreach (var change in changes)
        {
            if (emitted >= SubscriptionMaxEventsPerSample)
            {
                _logger.LogDebug(
                    "interest_subscription_changed sample capped at {Cap} events (tick {Tick}, {Total} observers changed)",
                    SubscriptionMaxEventsPerSample, tick, changes.Count);
                break;
            }
            await EmitInterestSubscriptionEventAsync(change, tick);
            emitted++;
        }
    }

    private async Task EmitInterestSubscriptionEventAsync(SubscriptionChange change, long tick)
    {
        // Public telemetry feed (G4): capture a public-safe projection at the in-process seam,
        // before the out-of-process EventLog POST. detail is the tier-transition magnitude
        // (+added/-removed counts) — never the observer id or the added/removed player-id lists.
        GameplayEventFeed.Capture(
            EventType.InterestSubscriptionChanged,
            change.RegionId,
            $"+{change.Added.Count}/-{change.Removed.Count}");

        try
        {
            var client = _httpFactory!.CreateClient();
            await client.PostAsJsonAsync($"{_eventLogUrl}/events", new
            {
                event_id = Guid.NewGuid().ToString(),
                event_type = EventType.InterestSubscriptionChanged,
                occurred_at = DateTimeOffset.UtcNow,
                world_id = "world-default",
                region_id = change.RegionId,
                actor_id = change.ObserverId,
                source_service = "gateway",
                schema_version = 1,
                payload = new
                {
                    tick,
                    subscribed_count = change.SubscribedCount,
                    added = change.Added,
                    removed = change.Removed,
                    added_count = change.Added.Count,
                    removed_count = change.Removed.Count,
                    subscription_radius = _subscriptionRadius,
                    policy = _interest.Policy.ToString().ToLowerInvariant(),
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit interest_subscription_changed for observer {ObserverId}", change.ObserverId);
        }
    }

    /// <summary>
    /// Per-worker accumulator for one chunk's send loop. Each concurrent chunk task gets its
    /// own instance (see the fan-out loop above), so no Interlocked/locking is needed — the
    /// caller sums them after Task.WhenAll.
    /// </summary>
    private sealed class SendAccumulator
    {
        public long InterestTicks;
        public long SendTicks;
        public long Sent;
        public long Culled;
        public int Aborts;
    }

    /// <summary>
    /// Sends the full per-session body (player entity updates + resource updates) for a
    /// contiguous, rotated slice of <paramref name="sessions"/>. Safe to run concurrently
    /// against other chunks of the SAME sessions list because rotation+chunking guarantees a
    /// session appears in exactly one chunk per tick — WebSocket.SendAsync allows only one
    /// outstanding send per socket, but concurrent sends to DIFFERENT sockets are safe.
    /// <paramref name="chunkIndex"/> is this chunk's position among the tick's chunks (0 for
    /// the serial SendWorkers&lt;=1 path) — passed through to UdpTransport.TrySend so phase 3a's
    /// UdpSockets experiment can pick a per-chunk send socket deterministically.
    /// </summary>
    private async Task SendChunkAsync(
        IReadOnlyList<GameSession> sessions,
        int rotationOffset,
        int chunkStart,
        int chunkLength,
        int chunkIndex,
        HashSet<string> regionPlayerChanges,
        HashSet<string> regionResourceChanges,
        Dictionary<string, (string RegionId, Player Player)> playerData,
        Dictionary<string, (string RegionId, NaturalResource Resource)> resourceData,
        IReadOnlyDictionary<string, Player> players,
        long tick,
        uint stateHash,
        CancellationToken deadlineToken,
        bool suppressMidBand,
        bool suppressAlternating,
        SendAccumulator acc)
    {
        for (var i = 0; i < chunkLength; i++)
        {
            var rotatedPos = chunkStart + i;
            var index = SendFanOut.RotatedIndex(rotatedPos, rotationOffset, sessions.Count);
            var session = sessions[index];

            if (session.Socket.State != WebSocketState.Open) continue;

            // Adaptive degrade halving for radius/full (no mid band to suppress instead):
            // skip this session's ENTIRE update for this tick. Position is in rotated order,
            // so which physical sessions get skipped shifts every degraded tick along with
            // the fairness rotation above — no session is skipped every time.
            if (suppressAlternating && AdaptiveDegrade.ShouldSkipAlternating(rotatedPos)) continue;

            var isBinary = session.Protocol == ProtocolMode.Binary;

            // --- Player Updates ---
            var tInterest = Stopwatch.GetTimestamp();
            var visiblePlayers = _interest.FilterForObserver(session.PlayerId, regionPlayerChanges, players, tick, suppressMidBand);
            acc.InterestTicks += Stopwatch.GetTimestamp() - tInterest;

            acc.Sent += visiblePlayers.Count;
            acc.Culled += regionPlayerChanges.Count - visiblePlayers.Count;

            var tSend = Stopwatch.GetTimestamp();
            var aborted = false;
            foreach (var playerId in visiblePlayers)
            {
                if (!playerData.TryGetValue(playerId, out var data)) continue;
                try
                {
                    if (isBinary)
                    {
                        if (!TrySendUdpEntityUpdate(session, playerId, data.Player, tick, stateHash, chunkIndex))
                            await SendBinaryEntityUpdate(session, playerId, data.Player, tick, stateHash, deadlineToken);
                    }
                    else
                    {
                        await SendJsonEntityUpdate(session, playerId, data.Player, tick, stateHash, deadlineToken);
                    }
                }
                catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
                {
                    // Broadcast deadline fired mid-send — a canceled WS send corrupts the
                    // stream, so this socket can never be used again this tick (or ever).
                    // Abort it, count it, and move on: the tick must end.
                    AbortSession(session, acc);
                    aborted = true;
                    break;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to send player update"); }
            }

            // --- Resource Updates (Nature 2.0) ---
            // For now, simpler AoI for resources: everyone in region gets them if they change (trees are big)
            if (!aborted)
            {
                foreach (var resourceId in regionResourceChanges)
                {
                    if (!resourceData.TryGetValue(resourceId, out var data)) continue;
                    try
                    {
                        if (!isBinary) // Binary path for resources can be added later if needed
                        {
                            await SendJsonNaturalResourceUpdate(session, resourceId, data.Resource, tick, stateHash, deadlineToken);
                        }
                    }
                    catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
                    {
                        AbortSession(session, acc);
                        break;
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to send resource update"); }
                }
            }
            acc.SendTicks += Stopwatch.GetTimestamp() - tSend;
        }
    }

    /// <summary>
    /// Broadcast deadline exceeded mid-send for this session: the WS stream is corrupted
    /// (SendAsync doesn't support a clean retry after cancellation), so abort the socket
    /// outright rather than try to keep using it. The existing session cleanup path (stale
    /// socket state checks) reaps it; the client reconnects.
    /// </summary>
    private void AbortSession(GameSession session, SendAccumulator acc)
    {
        try { session.Socket.Abort(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Abort() on an already-faulted socket — ignoring"); }
        acc.Aborts++;
        _logger.LogDebug("Aborted session {SessionId} — broadcast deadline exceeded mid-send", session.SessionId);
    }

    private bool TrySendUdpEntityUpdate(
        GameSession session, string entityId, Player player, long tick, uint stateHash, int chunkIndex)
    {
        if (_udpTransport == null || session.UdpEndpoint == null)
        {
            // No UDP channel bound — caller falls back to a WebSocket send,
            // which records the actual delivery path (binary_ws / json_ws).
            return false;
        }

        Span<byte> payloadBuf = stackalloc byte[128];
        var payloadLen = PayloadSerializers.WriteEntityUpdate(
            payloadBuf, entityId,
            player.Position, player.Velocity,
            player.Heading, player.LastInputSeq,
            (uint)tick, stateHash);

        Span<byte> frameBuf = stackalloc byte[BinaryEnvelope.HeaderBytes + payloadLen];
        BinaryEnvelope.Write(
            frameBuf,
            version: 1,
            MessageTypeId.EntityUpdate,
            DeliveryLane.Datagram,
            seq: 0,
            payloadBuf[..payloadLen]);

        // On success TrySend records RecordDelivery("udp"); on failure the caller
        // falls back to a WebSocket send which records its own delivery path. chunkIndex
        // picks this tick's deterministic send socket under the UdpSockets experiment
        // (see UdpTransport.TrySend) — a no-op selector when UdpSockets resolves to 1.
        return _udpTransport.TrySend(session, frameBuf, chunkIndex);
    }

    private static async Task SendBinaryEntityUpdate(
        GameSession session, string entityId, Player player, long tick, uint stateHash, CancellationToken ct)
    {
        // Serialize payload
        Span<byte> payloadBuf = stackalloc byte[128];
        var payloadLen = PayloadSerializers.WriteEntityUpdate(
            payloadBuf, entityId,
            player.Position, player.Velocity,
            player.Heading, player.LastInputSeq,
            (uint)tick, stateHash);

        // Wrap in binary envelope
        Span<byte> frameBuf = stackalloc byte[BinaryEnvelope.HeaderBytes + payloadLen];
        var frameLen = BinaryEnvelope.Write(
            frameBuf,
            version: 1,
            MessageTypeId.EntityUpdate,
            DeliveryLane.Datagram,
            seq: 0,
            payloadBuf[..payloadLen]);

        await session.Socket.SendAsync(
            frameBuf[..frameLen].ToArray(),
            WebSocketMessageType.Binary,
            true,
            ct);

        LumberjacksTelemetry.RecordDelivery("binary_ws");
    }

    private static async Task SendJsonEntityUpdate(
        GameSession session, string entityId, Player player, long tick, uint stateHash, CancellationToken ct)
    {
        var updateData = new
        {
            entity_id = entityId,
            entity_type = "player",
            data = new Dictionary<string, object>
            {
                ["player_id"] = entityId,
                ["position"] = new { x = player.Position.X, y = player.Position.Y, z = player.Position.Z },
                ["velocity"] = new { x = player.Velocity.X, y = player.Velocity.Y, z = player.Velocity.Z },
                ["heading"] = player.Heading,
                ["last_input_seq"] = player.LastInputSeq,
            },
            tick,
            state_hash = stateHash,
        };

        var env = EnvelopeFactory.Create(MessageType.EntityUpdate, updateData);
        var json = EnvelopeFactory.Serialize(env);
        await session.Socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            ct);

        LumberjacksTelemetry.RecordDelivery("json_ws");
    }

    private static async Task SendJsonNaturalResourceUpdate(
        GameSession session, string resourceId, NaturalResource resource, long tick, uint stateHash, CancellationToken ct)
    {
        var updateData = new
        {
            entity_id = resourceId,
            entity_type = resource.Type, // e.g. "oak_tree"
            data = new Dictionary<string, object>
            {
                ["position"] = new { x = resource.Position.X, y = resource.Position.Y, z = resource.Position.Z },
                ["health"] = resource.Health,
                ["stump_health"] = resource.StumpHealth,
                ["regrowth_progress"] = resource.RegrowthProgress,
                ["lean_x"] = resource.LeanX,
                ["lean_z"] = resource.LeanZ,
                ["growth_history"] = resource.GrowthHistory
            },
            tick,
            state_hash = stateHash,
        };

        var env = EnvelopeFactory.Create(MessageType.EntityUpdate, updateData);
        var json = EnvelopeFactory.Serialize(env);
        await session.Socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            ct);
    }
}

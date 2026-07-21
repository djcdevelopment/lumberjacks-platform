using System.Diagnostics;
using Game.Contracts.Protocol;
using Game.Simulation.World;

namespace Game.Simulation.Tick;

public class TickLoop : BackgroundService
{
    private readonly WorldState _world;
    private readonly InputQueue _inputQueue;
    private readonly ITickBroadcaster _broadcaster;
    private readonly ILogger<TickLoop> _logger;
    private readonly TickMetrics? _metrics;
    private const int TickMs = 50; // 20 Hz
    private static readonly TimeSpan StalePlayerThreshold = TimeSpan.FromMinutes(5);
    private const int StaleCheckIntervalTicks = 200; // every 10 seconds at 20Hz
    private const int RegionReconcileIntervalTicks = 100; // every 5 seconds
    private const int InputPurgeIntervalTicks = 60; // every 3 seconds

    public TickLoop(WorldState world, InputQueue inputQueue, ITickBroadcaster broadcaster, ILogger<TickLoop> logger, TickMetrics? metrics = null)
    {
        _world = world;
        _inputQueue = inputQueue;
        _broadcaster = broadcaster;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Tick loop started at {TickRate}Hz (input-driven simulation)", 1000 / TickMs);
        _world.StartedAt = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));

        long prevTickStart = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var tickStart = Stopwatch.GetTimestamp();

            var tick = Interlocked.Increment(ref _tickBacking);
            _world.CurrentTick = tick;

            // === CORE SIMULATION STEP ===
            // Drain input queue → apply physics → get changed entities
            var (changedPlayers, changedResources) = SimulationStep.Execute(_world, _inputQueue, tick);
            var afterSim = Stopwatch.GetTimestamp();

            // Compute deterministic state hash
            var stateHash = StateHasher.ComputeHash(_world);
            _world.LastStateHash = stateHash;
            var afterHash = Stopwatch.GetTimestamp();

            // Broadcast authoritative state for changed entities
            if (changedPlayers.Count > 0 || changedResources.Count > 0)
            {
                await _broadcaster.BroadcastTickAsync(
                    _world.Players, _world.Regions, _world.NaturalResources,
                    changedPlayers, changedResources, tick, stateHash);
            }
            var afterBroadcast = Stopwatch.GetTimestamp();

            // === HOUSEKEEPING ===

            // Stale player cleanup
            if (tick % StaleCheckIntervalTicks == 0)
            {
                CleanStalePlayers();
            }

            // Reconcile region player counts
            if (tick % RegionReconcileIntervalTicks == 0)
            {
                ReconcileRegionCounts();
            }

            // Purge stale inputs from the queue
            if (tick % InputPurgeIntervalTicks == 0)
            {
                _inputQueue.PurgeStale(tick);
            }

            if (_metrics != null)
            {
                var tickEnd = Stopwatch.GetTimestamp();
                _metrics.RecordTick(
                    tick,
                    totalMs: Stopwatch.GetElapsedTime(tickStart, tickEnd).TotalMilliseconds,
                    intervalMs: prevTickStart == 0 ? 0 : Stopwatch.GetElapsedTime(prevTickStart, tickStart).TotalMilliseconds,
                    simMs: Stopwatch.GetElapsedTime(tickStart, afterSim).TotalMilliseconds,
                    hashMs: Stopwatch.GetElapsedTime(afterSim, afterHash).TotalMilliseconds,
                    broadcastMs: Stopwatch.GetElapsedTime(afterHash, afterBroadcast).TotalMilliseconds,
                    housekeepingMs: Stopwatch.GetElapsedTime(afterBroadcast, tickEnd).TotalMilliseconds);
            }
            prevTickStart = tickStart;
        }

        _logger.LogInformation("Tick loop stopped at tick {Tick}", _world.CurrentTick);
    }

    private long _tickBacking;

    private void CleanStalePlayers()
    {
        var cutoff = DateTimeOffset.UtcNow - StalePlayerThreshold;
        var stale = _world.Players.Values
            .Where(p => p.Connected && p.LastActivityAt < cutoff)
            .ToList();

        foreach (var player in stale)
        {
            if (_world.Players.TryRemove(player.Id, out _))
            {
                _logger.LogWarning(
                    "Removed stale player {PlayerId} from {RegionId} (last activity {LastActivity})",
                    player.Id, player.RegionId, player.LastActivityAt);
            }
        }

        if (stale.Count > 0)
        {
            ReconcileRegionCounts();
        }
    }

    private void ReconcileRegionCounts()
    {
        // Build accurate counts from current player state
        var counts = _world.Players.Values
            .Where(p => p.Connected)
            .GroupBy(p => p.RegionId)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (regionId, region) in _world.Regions)
        {
            var actualCount = counts.GetValueOrDefault(regionId, 0);
            if (region.PlayerCount != actualCount)
            {
                _world.Regions[regionId] = region with { PlayerCount = actualCount };
            }
        }
    }
}


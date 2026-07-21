using System.Collections.Concurrent;
using Game.Contracts.Protocol;

namespace Game.Simulation.Tick;

/// <summary>
/// Per-player input queue that buffers client inputs for tick-aligned execution.
/// Inputs arrive asynchronously from the Gateway and are consumed by the TickLoop
/// on the target tick.
///
/// Thread-safe: Gateway writes (Enqueue) from async I/O threads,
/// TickLoop reads (DrainForTick) from the simulation thread.
/// </summary>
public class InputQueue
{
    /// <summary>How many ticks ahead inputs are buffered for. Inputs for past ticks are assigned to now+1.</summary>
    public const int BufferDepthTicks = 3;

    // Key: target tick → list of inputs for that tick
    private readonly ConcurrentDictionary<long, ConcurrentBag<QueuedInput>> _queue = new();

    /// <summary>Total inputs currently buffered (for diagnostics).</summary>
    public int PendingCount => _queue.Values.Sum(bag => bag.Count);

    /// <summary>
    /// Enqueue a player input for processing on a future tick.
    /// If the input's target tick is in the past or unspecified, it's assigned to currentTick + 1.
    /// </summary>
    public void Enqueue(string playerId, PlayerInputMessage input, long currentTick)
    {
        var targetTick = currentTick + 1; // process on the very next tick

        var queued = new QueuedInput
        {
            PlayerId = playerId,
            Input = input,
            TargetTick = targetTick,
        };

        var bag = _queue.GetOrAdd(targetTick, _ => new ConcurrentBag<QueuedInput>());
        bag.Add(queued);
    }

    /// <summary>
    /// Drain all inputs scheduled for the given tick.
    /// Returns inputs grouped by player (last input wins if multiple per player per tick).
    /// </summary>
    public Dictionary<string, QueuedInput> DrainForTick(long tick)
    {
        var result = new Dictionary<string, QueuedInput>();

        if (_queue.TryRemove(tick, out var bag))
        {
            foreach (var input in bag)
            {
                // Last-write-wins: if a player sent multiple inputs for the same tick,
                // use the one with the highest input sequence number
                if (!result.TryGetValue(input.PlayerId, out var existing)
                    || input.Input.InputSeq > existing.Input.InputSeq)
                {
                    result[input.PlayerId] = input;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Purge any stale queued inputs for ticks that have already passed.
    /// Called periodically to prevent memory leaks from abandoned inputs.
    /// </summary>
    public int PurgeStale(long currentTick)
    {
        var staleKeys = _queue.Keys.Where(t => t < currentTick - BufferDepthTicks).ToList();
        int purged = 0;
        foreach (var key in staleKeys)
        {
            if (_queue.TryRemove(key, out var bag))
                purged += bag.Count;
        }
        return purged;
    }
}

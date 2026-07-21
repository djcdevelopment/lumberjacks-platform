using System.Collections.Concurrent;
using Game.Simulation.Tick;
using Xunit;

namespace Game.Simulation.Tests;

/// <summary>
/// Phase 3a′ (Follow-up F): TickBroadcaster's SendWorkers&gt;1 path now dispatches chunk
/// bodies via Parallel.ForEachAsync(MaxDegreeOfParallelism = workers) instead of directly
/// invoking the async SendChunkAsync method N times and Task.WhenAll'ing the results — the
/// latter looked parallel but wasn't, because every await inside SendChunkAsync resolved
/// synchronously (sync UDP Socket.Send, inline-completing small-frame WS SendAsync on LAN),
/// so every chunk ran inline-serial on the calling thread.
///
/// TickBroadcaster's real chunk body (SendChunkAsync/SendAccumulator) is private and needs a
/// live GameSession/WebSocket, so it isn't independently unit-testable from here. These tests
/// instead exercise the exact dispatch SHAPE TickBroadcaster now uses — SendFanOut.Chunk +
/// Parallel.ForEachAsync + per-chunk-local accumulator summed at the join — against a
/// synthetic per-item accumulator, to lock down:
///   1. the workers=1 path stays a single chunk covering everything (no dispatch at all —
///      that's literally what TickBroadcaster's `if (_sendWorkers &lt;= 1)` branch does, no
///      Parallel.ForEachAsync involved);
///   2. under Parallel.ForEachAsync, every session index across all chunks is visited exactly
///      once (chunking correctness survives the switch from Task.WhenAll);
///   3. per-chunk-local accumulators (sent/culled/aborts) sum correctly at the join, mirroring
///      TickBroadcaster's SendAccumulator pattern;
///   4. MaxDegreeOfParallelism>1 chunks actually run concurrently on distinct pool threads —
///      the thing the phase 3a bug got wrong — using a Barrier so an inline-serial dispatch
///      would deadlock/time out instead of silently passing.
/// </summary>
public class ParallelSendDispatchTests
{
    private sealed class Accumulator
    {
        public long Sent;
        public long Culled;
        public int Aborts;
    }

    [Fact]
    public void SerialPath_SendWorkersOne_IsASingleChunkNoDispatch()
    {
        // Mirrors TickBroadcaster's `_sendWorkers <= 1` branch: exactly one chunk, spanning
        // every session, handled with a direct call — no Task/Parallel machinery at all.
        const int sessionCount = 37;
        var chunks = SendFanOut.Chunk(sessionCount, workers: 1);
        Assert.Single(chunks);
        Assert.Equal((0, sessionCount), chunks[0]);

        var acc = new Accumulator();
        var (start, length) = chunks[0];
        for (var i = 0; i < length; i++)
        {
            acc.Sent += 1;
            if (i % 5 == 0) acc.Culled += 1;
        }

        Assert.Equal(sessionCount, acc.Sent);
        Assert.Equal(8, acc.Culled); // indices 0,5,10,...,35 -> 8 hits
    }

    [Theory]
    [InlineData(100, 4)]
    [InlineData(400, 8)]
    [InlineData(1, 4)]   // workers > sessions — trailing chunks are empty, must contribute zero
    [InlineData(0, 4)]   // no sessions at all
    [InlineData(37, 6)]  // uneven split (37 / 6 = 6 remainder 1)
    public async Task ParallelDispatch_CoversEverySessionExactlyOnce_AndAccumulatorsSumCorrectly(
        int sessionCount, int workers)
    {
        var chunks = SendFanOut.Chunk(sessionCount, workers);
        var accumulators = new Accumulator[chunks.Count];
        var seen = new ConcurrentDictionary<int, byte>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (i, _) =>
            {
                var (start, length) = chunks[i];
                var acc = new Accumulator();
                accumulators[i] = acc;
                for (var pos = start; pos < start + length; pos++)
                {
                    Assert.True(seen.TryAdd(pos, 0), $"index {pos} visited by more than one chunk");
                    acc.Sent += 1;
                    if (pos % 3 == 0) acc.Culled += 1;
                    if (pos % 11 == 0) acc.Aborts += 1;
                }
                await Task.Yield();
            });

        // Every session index visited exactly once, across however many chunks it took.
        Assert.Equal(sessionCount, seen.Count);

        long totalSent = 0, totalCulled = 0;
        var totalAborts = 0;
        foreach (var acc in accumulators)
        {
            totalSent += acc.Sent;
            totalCulled += acc.Culled;
            totalAborts += acc.Aborts;
        }

        Assert.Equal(sessionCount, totalSent);
        Assert.Equal(Enumerable.Range(0, sessionCount).Count(i => i % 3 == 0), totalCulled);
        Assert.Equal(Enumerable.Range(0, sessionCount).Count(i => i % 11 == 0), totalAborts);
    }

    [Fact]
    public async Task ParallelDispatch_WithMultipleWorkers_ActuallyRunsConcurrently()
    {
        // Directly reproduces the phase 3a bug as a regression guard: if chunk bodies ran
        // inline-serial (the old "await SendChunkAsync(...) directly, then Task.WhenAll" shape,
        // where every await resolves synchronously), the first participant to reach the
        // barrier would block waiting for the other three — which would never arrive, because
        // execution wouldn't move on to the next chunk until the current one (blocked on the
        // barrier) finished. Parallel.ForEachAsync's Task.Run-per-worker dispatch means all
        // `workers` chunks are in flight on distinct thread-pool threads at once, so the
        // barrier releases promptly for every participant.
        const int workers = 4;
        var chunks = SendFanOut.Chunk(count: workers, workers); // exactly 1 item per chunk
        using var barrier = new Barrier(workers);
        var released = new bool[workers];

        var dispatch = Parallel.ForEachAsync(
            Enumerable.Range(0, chunks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            (i, _) =>
            {
                released[i] = barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return ValueTask.CompletedTask;
            });

        var completed = await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(dispatch, completed); // didn't hit the outer timeout
        await dispatch; // propagate any fault (e.g. a broken barrier) instead of swallowing it

        Assert.All(released, Assert.True);
    }

    // ── SocketForChunk distinctness under true concurrency (phase 3a′ UDP-safety guarantee) ──

    [Theory]
    [InlineData(4, 4)]   // socketCount == workers — the auto-resolve default
    [InlineData(8, 8)]
    [InlineData(4, 8)]   // socketCount > workers — still distinct, just sparse
    public void SocketForChunk_GivesDistinctSocketsToEveryConcurrentChunk_WhenSocketCountAtLeastWorkers(
        int workers, int socketCount)
    {
        var seen = new HashSet<int>();
        for (var chunkIndex = 0; chunkIndex < workers; chunkIndex++)
            Assert.True(seen.Add(SendFanOut.SocketForChunk(chunkIndex, socketCount)));
        Assert.Equal(workers, seen.Count);
    }
}

using Game.Simulation.Tick;
using Xunit;

namespace Game.Simulation.Tests;

public class SendFanOutTests
{
    // ── ResolveWorkerCount ──

    [Theory]
    [InlineData(1, 4, 1)]      // default config (1) = today's serial behavior regardless of cores
    [InlineData(4, 16, 4)]     // explicit value passes through as-is
    [InlineData(-3, 4, 1)]     // guard against bad config — never non-positive
    [InlineData(0, 4, 4)]      // auto = min(AutoWorkerCap, processorCount)
    [InlineData(0, 16, 8)]     // auto capped at AutoWorkerCap (8)
    [InlineData(0, 1, 1)]      // auto on a single-core box
    public void ResolveWorkerCountHandlesAutoAndExplicit(int configured, int processorCount, int expected)
        => Assert.Equal(expected, SendFanOut.ResolveWorkerCount(configured, processorCount));

    // ── RotateOffset ──

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(5, 10, 5)]
    [InlineData(10, 10, 0)]    // wraps exactly
    [InlineData(15, 10, 5)]
    [InlineData(3, 0, 0)]      // empty session list — no divide by zero, offset is 0
    [InlineData(3, 1, 0)]      // single session — offset always 0
    public void RotateOffsetWrapsWithinCount(long tick, int count, int expected)
        => Assert.Equal(expected, SendFanOut.RotateOffset(tick, count));

    // ── Chunk: coverage ──

    [Theory]
    [InlineData(10, 1)]
    [InlineData(10, 3)]
    [InlineData(10, 4)]
    [InlineData(1, 4)]     // workers > sessions
    [InlineData(0, 4)]     // no sessions at all
    [InlineData(7, 7)]     // exact match
    [InlineData(7, 100)]   // workers >> sessions
    [InlineData(400, 8)]   // realistic: 400-bot region, auto-resolved 8 workers
    public void ChunkCoversEveryIndexExactlyOnce(int count, int workers)
    {
        var chunks = SendFanOut.Chunk(count, workers);
        Assert.Equal(workers, chunks.Count);

        var seen = new int[count];
        var totalLength = 0;
        foreach (var (start, length) in chunks)
        {
            Assert.True(length >= 0);
            Assert.True(start >= 0);
            for (var i = 0; i < length; i++)
                seen[start + i]++;
            totalLength += length;
        }

        Assert.Equal(count, totalLength);
        Assert.All(seen, c => Assert.Equal(1, c));
    }

    [Fact]
    public void ChunkSizesDifferByAtMostOne()
    {
        var chunks = SendFanOut.Chunk(10, 3); // 10 / 3 = 3 remainder 1 → sizes 4,3,3
        var lengths = chunks.Select(c => c.Length).ToList();
        Assert.Equal(new[] { 4, 3, 3 }, lengths);
    }

    [Fact]
    public void SingleWorkerProducesOneChunkCoveringEverything()
    {
        // workers=1 path identical: no fan-out, a single chunk spans the whole list.
        var chunks = SendFanOut.Chunk(37, 1);
        Assert.Single(chunks);
        Assert.Equal((0, 37), chunks[0]);
    }

    [Fact]
    public void ChunkRejectsNonPositiveWorkers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SendFanOut.Chunk(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SendFanOut.Chunk(10, -1));
    }

    // ── RotatedIndex + Chunk + RotateOffset composed: full coverage of the ORIGINAL list ──

    [Theory]
    [InlineData(10, 3, 7)]
    [InlineData(10, 4, 23)]
    [InlineData(1, 4, 5)]
    [InlineData(7, 100, 250)]
    [InlineData(400, 8, 12345)]
    public void RotationPlusChunkingCoversOriginalIndicesExactlyOnce(int count, int workers, long tick)
    {
        var offset = SendFanOut.RotateOffset(tick, count);
        var chunks = SendFanOut.Chunk(count, workers);

        var seen = new int[count];
        foreach (var (start, length) in chunks)
        {
            for (var i = 0; i < length; i++)
            {
                var originalIndex = SendFanOut.RotatedIndex(start + i, offset, count);
                seen[originalIndex]++;
            }
        }

        Assert.All(seen, c => Assert.Equal(1, c));
    }

    [Fact]
    public void RotatedIndexHandlesEmptyList()
    {
        Assert.Equal(0, SendFanOut.RotatedIndex(0, 0, 0));
    }

    // ── ResolveUdpSocketCount (phase 3a) ──

    [Theory]
    [InlineData(1, 4, 1)]      // default config (1) = today's exact behavior regardless of workers
    [InlineData(1, 1, 1)]      // default config, single-worker process
    [InlineData(4, 16, 4)]     // explicit value passes through as-is, independent of workers
    [InlineData(-3, 4, 1)]     // guard against bad config — never non-positive
    [InlineData(0, 4, 4)]      // auto = the ALREADY-resolved SendWorkers count
    [InlineData(0, 8, 8)]      // auto = resolved SendWorkers count (post auto-resolve, e.g. AutoWorkerCap)
    [InlineData(0, 1, 1)]      // auto, serial send loop — one socket
    public void ResolveUdpSocketCountHandlesAutoAndExplicit(int configured, int resolvedSendWorkers, int expected)
        => Assert.Equal(expected, SendFanOut.ResolveUdpSocketCount(configured, resolvedSendWorkers));

    [Fact]
    public void ResolveUdpSocketCountAutoTracksResolvedNotRawSendWorkers()
    {
        // Auto (configured=0) must use the ALREADY-resolved worker count (post
        // ResolveWorkerCount), not the raw Replication:SendWorkers value — e.g. raw
        // SendWorkers=0 resolves to 8 on a big box, and UdpSockets=0 must follow that 8,
        // not the raw 0 it was derived from.
        var resolvedWorkers = SendFanOut.ResolveWorkerCount(configured: 0, processorCount: 16);
        Assert.Equal(8, resolvedWorkers); // sanity: AutoWorkerCap
        Assert.Equal(8, SendFanOut.ResolveUdpSocketCount(configured: 0, resolvedWorkers));
    }

    // ── SocketForChunk (phase 3a): deterministic chunk→socket mapping ──

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(5, 1, 0)]      // single socket — everything maps to 0
    [InlineData(0, 4, 0)]
    [InlineData(1, 4, 1)]
    [InlineData(3, 4, 3)]
    [InlineData(4, 4, 0)]      // wraps exactly
    [InlineData(9, 4, 1)]
    public void SocketForChunkWrapsWithinSocketCount(int chunkIndex, int socketCount, int expected)
        => Assert.Equal(expected, SendFanOut.SocketForChunk(chunkIndex, socketCount));

    [Fact]
    public void SocketForChunkDefaultsToZeroForNonPositiveSocketCount()
    {
        Assert.Equal(0, SendFanOut.SocketForChunk(chunkIndex: 3, socketCount: 0));
        Assert.Equal(0, SendFanOut.SocketForChunk(chunkIndex: 3, socketCount: -1));
    }

    [Fact]
    public void SocketForChunkNeverAssignsTwoChunksToTheSameSocketWithinOneRoundOfWorkersEqualToSockets()
    {
        // When socket count == worker count, each chunk index in [0, workers) must map to a
        // DISTINCT socket — no socket is shared by two chunks in the same tick.
        const int workers = 8;
        var seen = new HashSet<int>();
        for (var chunkIndex = 0; chunkIndex < workers; chunkIndex++)
            Assert.True(seen.Add(SendFanOut.SocketForChunk(chunkIndex, socketCount: workers)));
        Assert.Equal(workers, seen.Count);
    }

    [Fact]
    public void SocketForChunkSpreadsEvenlyWhenChunksOutnumberSockets()
    {
        // 8 worker chunks, 4 sockets: each socket gets exactly 2 chunks, and no chunk pairing
        // is haphazard — it's chunkIndex % socketCount.
        var counts = new int[4];
        for (var chunkIndex = 0; chunkIndex < 8; chunkIndex++)
            counts[SendFanOut.SocketForChunk(chunkIndex, socketCount: 4)]++;
        Assert.All(counts, c => Assert.Equal(2, c));
    }
}

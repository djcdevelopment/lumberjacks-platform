using Game.Contracts.Protocol;
using Game.Simulation.Tick;
using Xunit;

namespace Game.Simulation.Tests;

public class InputQueueTests
{
    [Fact]
    public void EnqueueAndDrain()
    {
        var queue = new InputQueue();
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 10, SpeedPercent = 50, InputSeq = 1 }, currentTick: 5);

        var inputs = queue.DrainForTick(6); // enqueue at tick 5 → target tick 6

        Assert.Single(inputs);
        Assert.True(inputs.ContainsKey("player-1"));
        Assert.Equal(10, inputs["player-1"].Input.Direction);
    }

    [Fact]
    public void DrainEmptyTickReturnsEmpty()
    {
        var queue = new InputQueue();
        var inputs = queue.DrainForTick(100);
        Assert.Empty(inputs);
    }

    [Fact]
    public void LastWriteWins_HigherSeqTakesPriority()
    {
        var queue = new InputQueue();
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 10, SpeedPercent = 50, InputSeq = 1 }, currentTick: 0);
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 20, SpeedPercent = 80, InputSeq = 5 }, currentTick: 0);
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 15, SpeedPercent = 60, InputSeq = 3 }, currentTick: 0);

        var inputs = queue.DrainForTick(1);

        Assert.Single(inputs);
        Assert.Equal(5, inputs["player-1"].Input.InputSeq);
        Assert.Equal(20, inputs["player-1"].Input.Direction);
    }

    [Fact]
    public void PendingCountReflectsQueuedInputs()
    {
        var queue = new InputQueue();
        Assert.Equal(0, queue.PendingCount);

        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 0, SpeedPercent = 50, InputSeq = 1 }, currentTick: 0);
        queue.Enqueue("player-2", new PlayerInputMessage { Direction = 0, SpeedPercent = 50, InputSeq = 1 }, currentTick: 0);

        Assert.Equal(2, queue.PendingCount);

        queue.DrainForTick(1);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void PurgeStaleRemovesOldTicks()
    {
        var queue = new InputQueue();
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 0, SpeedPercent = 50, InputSeq = 1 }, currentTick: 0);  // target tick 1
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 0, SpeedPercent = 50, InputSeq = 2 }, currentTick: 1);  // target tick 2
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 0, SpeedPercent = 50, InputSeq = 3 }, currentTick: 10); // target tick 11

        // Purge stale at tick 10 — ticks older than 10 - BufferDepthTicks(3) = 7 are stale
        var purged = queue.PurgeStale(10);

        Assert.Equal(2, purged); // tick 1 and tick 2 are stale
    }

    [Fact]
    public void MultiplePlayersOnSameTick()
    {
        var queue = new InputQueue();
        queue.Enqueue("player-1", new PlayerInputMessage { Direction = 10, SpeedPercent = 50, InputSeq = 1 }, currentTick: 0);
        queue.Enqueue("player-2", new PlayerInputMessage { Direction = 20, SpeedPercent = 50, InputSeq = 1 }, currentTick: 0);

        var inputs = queue.DrainForTick(1);

        Assert.Equal(2, inputs.Count);
        Assert.Equal(10, inputs["player-1"].Input.Direction);
        Assert.Equal(20, inputs["player-2"].Input.Direction);
    }
}

using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Contract shakedown for the ADR-0013 co-presence fan-out, driven entirely by synthetic producer
/// traffic — no Valheim client, no live window. Where <see cref="ValheimRecipientIsolationTests"/>
/// proves isolation when each recipient gets a DISTINCT envelope, these prove the fan-out shape: ONE
/// logical ZDO (same uid/prefab) copied into EVERY in-range observer's partition. That is the "one
/// object, N readers" property area co-presence rests on — each observer drains its own read copy,
/// acks independently, no cross-consumption, and WAL replay reconstructs all N partitions holding
/// copies of the same logical object. This is the gateway-side de-risking of the fan-out before the
/// mod ever emits it.
/// </summary>
public sealed class ValheimCoPresenceFanoutTests
{
    private const string Window = "copresence-fanout";

    // One shared-area building, as the mod would serialize it: identical logical identity across
    // every fanned copy. Only the per-partition Seq differs (the producer's global counter).
    private const long BuildingUidUser = 76561190000000007L;
    private const long BuildingUidId = 4242L;
    private const int BuildingPrefab = 98765;

    private static ValheimZdoRedirectEnvelope FannedCopy(string recipient, long seq) => new()
    {
        Seq = seq,
        RecipientId = recipient,
        UidUser = BuildingUidUser,
        UidId = BuildingUidId,
        Prefab = BuildingPrefab,
        DataRev = 3,
        OwnerRev = 1,
        Pos = [100.0, 32.0, -50.0],
        PriorityTier = "critical",
        BodyB64 = "QUJD", // "ABC"
    };

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void OneLogicalZdo_FannedToEveryObserver_EachDrainsItsOwnCopy(int observerCount)
    {
        var service = new ValheimZdoRedirectService();
        var observers = Enumerable.Range(0, observerCount).Select(i => "obs-" + i).ToArray();

        // Fan the SAME building out to every observer's partition (one global seq per copy).
        foreach (var (recipient, index) in observers.Select((r, i) => (r, i)))
        {
            service.RecordEnvelopes(Window, "producer", [FannedCopy(recipient, index + 1)]);
        }

        // Read-only pass: every observer sees exactly ONE copy — the SAME logical ZDO — and no other
        // observer can consume it. No real acks here, so every partition stays pending throughout.
        foreach (var (recipient, index) in observers.Select((r, i) => (r, i)))
        {
            var copy = Assert.Single(service.Pending(Window, recipient, 64));
            Assert.Equal(index + 1L, copy.Seq);
            Assert.Equal(BuildingUidUser, copy.UidUser);
            Assert.Equal(BuildingUidId, copy.UidId);
            Assert.Equal(BuildingPrefab, copy.Prefab);

            // A neighbour cannot ack this observer's seq — it isn't in the neighbour's partition.
            var neighbour = observers[(index + 1) % observerCount];
            var forged = service.Acknowledge(Window, neighbour, [index + 1L]);
            Assert.Equal(0, forged.Acknowledged);
            Assert.Equal(1, forged.Unknown);
            Assert.Equal(1, service.GetStatus(Window, recipient).Pending);
        }

        // Independent acks: draining obs-0 leaves every other partition untouched.
        Assert.Equal(1, service.Acknowledge(Window, observers[0], [1]).Acknowledged);
        Assert.Equal(0, service.GetStatus(Window, observers[0]).Pending);
        foreach (var recipient in observers.Skip(1))
        {
            Assert.Equal(1, service.GetStatus(Window, recipient).Pending);
        }

        // Drain the rest; the window conserves exactly the N fanned copies.
        foreach (var (recipient, index) in observers.Select((r, i) => (r, i)).Skip(1))
        {
            Assert.Equal(1, service.Acknowledge(Window, recipient, [index + 1L]).Acknowledged);
        }

        var aggregate = service.GetStatus(Window);
        Assert.Equal(observerCount, aggregate.Receipts);
        Assert.Equal(observerCount, aggregate.Acknowledged);
        Assert.Equal(0, aggregate.Pending);
    }

    [Fact]
    public void FannedZdo_SurvivesWalReplay_AllPartitionsReconstructed()
    {
        var walPath = Path.Combine(
            Path.GetTempPath(), "cns-fanout-" + Guid.NewGuid().ToString("N") + ".wal");
        try
        {
            const int observerCount = 4;
            var observers = Enumerable.Range(0, observerCount).Select(i => "obs-" + i).ToArray();

            // Producer fans the building out, then obs-0 acks its copy — both must survive a restart.
            var before = new ValheimZdoRedirectService(walPath);
            foreach (var (recipient, index) in observers.Select((r, i) => (r, i)))
            {
                before.RecordEnvelopes(Window, "producer", [FannedCopy(recipient, index + 1)]);
            }
            Assert.Equal(1, before.Acknowledge(Window, observers[0], [1]).Acknowledged);

            // Gateway restart: a fresh service replays the WAL from disk (no state carried in memory).
            var after = new ValheimZdoRedirectService(walPath);

            // obs-0's ack persisted: drained, nothing pending.
            var acked = after.GetStatus(Window, observers[0]);
            Assert.Equal(1, acked.Acknowledged);
            Assert.Equal(0, acked.Pending);

            // Every un-acked partition still holds its copy of the SAME logical ZDO after replay.
            foreach (var recipient in observers.Skip(1))
            {
                var copy = Assert.Single(after.Pending(Window, recipient, 64));
                Assert.Equal(BuildingUidUser, copy.UidUser);
                Assert.Equal(BuildingUidId, copy.UidId);
                Assert.Equal(BuildingPrefab, copy.Prefab);
            }
        }
        finally
        {
            if (File.Exists(walPath)) File.Delete(walPath);
        }
    }

    [Fact]
    public void FanOut_IsIdempotent_ReDeliveringTheSameCopyIsDeduped()
    {
        // The producer fan-out is at-least-once (retries on a failed batch), so re-delivering the same
        // (recipient, seq) copy must dedupe — exactly-once per partition, the property the queue is
        // trusted for, must hold under the extra fan-out traffic too.
        var service = new ValheimZdoRedirectService();

        service.RecordEnvelopes(Window, "producer", [FannedCopy("obs-0", 1)]);
        service.RecordEnvelopes(Window, "producer", [FannedCopy("obs-0", 1)]);

        var status = service.GetStatus(Window, "obs-0");
        Assert.Equal(1, status.DistinctSeq);
        Assert.Equal(1, status.Duplicates);
        Assert.Equal(1, status.Pending);
        Assert.Single(service.Pending(Window, "obs-0", 64));
    }
}

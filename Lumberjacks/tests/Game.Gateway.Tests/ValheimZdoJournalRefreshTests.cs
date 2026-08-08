using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Regression: a refresh must actually re-issue the world snapshot.
///
/// The client discards its entire inbound delivery queue on a ZNet teardown
/// (ZdoJournalCutoverRunner.ResetClientEpochState) and then re-registers with
/// Refresh = true. But the recipient id is the logical peer id, which survives the
/// teardown, so the Gateway still held a _pending queue for it. HasPending then
/// skipped every object "already queued" — deduping against deliveries the client
/// had thrown away and could never receive.
///
/// The world was therefore issued exactly once. Observed 2026-08-08 on AM4 as
/// snapshot_count 1230 -> 9 -> 1 -> 0 across one run, with 16 journal-cutover events
/// where a healthy run shows ~42,000. Latent since 2026-07-30; it only became visible
/// when the server widened to zdoRedirectPrefabs=*, because under a scoped redirect the
/// world still arrived over the native ZDO path and the lost snapshot cost a few
/// mushrooms rather than the entire world.
/// </summary>
public sealed class ValheimZdoJournalRefreshTests
{
    private const string Recipient = "peer-logical-1";
    private const string Epoch = "epoch-1";
    private const string Run = "r42-test";

    private static ValheimZdoJournalInterest Interest(bool refresh) => new()
    {
        RecipientId = Recipient,
        RunId = Run,
        WorldEpoch = Epoch,
        ZoneX = 0,
        ZoneY = 0,
        RadiusZones = 1,
        Refresh = refresh,
    };

    private static ValheimZdoJournalService SeededWorld(int objectCount)
    {
        var journal = new ValheimZdoJournalService(walPath: null);
        journal.ActivateWorldEpoch(Epoch);
        for (var i = 1; i <= objectCount; i++)
        {
            var accepted = journal.Record(new ValheimZdoJournalObject
            {
                RunId = Run,
                WorldEpoch = Epoch,
                SourceSequence = i,
                ObjectRevision = 1,
                UidUser = 42,
                UidId = i,
                Prefab = 1,
                ZoneX = 0,
                ZoneY = 0,
                BodyBase64 = "AA==",
            }).Accepted;
            Assert.True(accepted);
        }
        return journal;
    }

    [Fact]
    public void RefreshReissuesTheWorldAfterTheClientDiscardsItsInboundQueue()
    {
        const int worldSize = 50;
        var journal = SeededWorld(worldSize);

        // First join: the client registers and the whole world is queued for it.
        var first = journal.RegisterInterest(Recipient, Interest(refresh: false));
        Assert.True(first.Accepted);
        Assert.Equal(worldSize, first.SnapshotCount);

        // The client tears down ZNet and drops every one of those deliveries. The
        // Gateway is told nothing, and the logical peer id is unchanged.

        // Second join: same interest, Refresh = true. Before the fix this returned
        // SnapshotCount 0, because HasPending matched the stranded queue.
        var second = journal.RegisterInterest(Recipient, Interest(refresh: true));
        Assert.True(second.Accepted);
        Assert.Equal(worldSize, second.SnapshotCount);

        // And the client can actually collect it.
        var pending = journal.Pending(Recipient, Epoch, limit: worldSize * 2);
        Assert.Equal(worldSize, pending.Count);
    }

    [Fact]
    public void RefreshedDeliveriesTakeFreshSequencesAndDoNotCollideWithTheDiscardedOnes()
    {
        const int worldSize = 10;
        var journal = SeededWorld(worldSize);

        journal.RegisterInterest(Recipient, Interest(refresh: false));
        var before = journal.Pending(Recipient, Epoch, limit: worldSize * 2);
        var highestDiscarded = before.Max(delivery => delivery.Sequence);

        journal.RegisterInterest(Recipient, Interest(refresh: true));
        var after = journal.Pending(Recipient, Epoch, limit: worldSize * 2);

        // _nextSequence is per-recipient and tracked independently of _pending, so a
        // dropped queue must not rewind it — otherwise an ack for a discarded delivery
        // would silently retire a live one carrying the same sequence.
        Assert.Equal(worldSize, after.Count);
        Assert.All(after, delivery => Assert.True(
            delivery.Sequence > highestDiscarded,
            $"re-snapshot reused sequence {delivery.Sequence}; " +
            $"highest discarded was {highestDiscarded}"));
    }

    [Fact]
    public void UnchangedInterestWithoutRefreshStillDedupsAgainstALiveQueue()
    {
        const int worldSize = 10;
        var journal = SeededWorld(worldSize);

        journal.RegisterInterest(Recipient, Interest(refresh: false));

        // No refresh and no interest change: the recipient's queue is presumed live,
        // so re-registering must NOT duplicate the world. The fix must not widen into
        // "every registration re-sends everything".
        var repeat = journal.RegisterInterest(Recipient, Interest(refresh: false));
        Assert.True(repeat.Accepted);
        Assert.Equal(0, repeat.SnapshotCount);
        Assert.Equal(worldSize, journal.Pending(Recipient, Epoch, limit: worldSize * 2).Count);
    }
}

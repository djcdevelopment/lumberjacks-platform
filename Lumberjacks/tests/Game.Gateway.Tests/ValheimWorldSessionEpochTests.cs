using System.Text.Json;
using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimWorldSessionEpochTests
{
    private const long WorldUid = 0x1234;
    private const long SessionA = 0x1001;
    private const long SessionB = 0x1002;

    [Fact]
    public void DescriptorIdentityBindsStableWorldToServerSession()
    {
        var stable = ValheimWorldEpoch.StableWorld(WorldUid);
        var epochA = ValheimWorldEpoch.Compose(WorldUid, SessionA);
        var epochB = ValheimWorldEpoch.Compose(WorldUid, SessionB);

        Assert.Equal("world-0000000000001234", stable);
        Assert.NotEqual(epochA, epochB);
        Assert.True(ValheimWorldEpoch.IsConsistent(
            epochA,
            stable,
            ValheimWorldEpoch.ServerSession(SessionA),
            WorldUid));
        Assert.False(ValheimWorldEpoch.IsConsistent(
            epochA,
            stable,
            ValheimWorldEpoch.ServerSession(SessionB),
            WorldUid));
    }

    [Fact]
    public void GatewayRestartKeepsSameSessionButServerRestartInvalidatesBank()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "lumberjacks-session-epoch-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "zdo-journal.jsonl");
        var epochA = ValheimWorldEpoch.Compose(WorldUid, SessionA);
        var epochB = ValheimWorldEpoch.Compose(WorldUid, SessionB);
        try
        {
            var firstGateway = new ValheimZdoJournalService(path);
            Assert.False(firstGateway.ActivateWorldEpoch(epochA).Changed);
            Assert.True(firstGateway.Record(Mutation(epochA, 41)).Accepted);
            Assert.Equal(1, firstGateway.Status().DurableObjects);

            // A Gateway-only restart replays the bank because the dedicated server session did
            // not change.
            var restartedGateway = new ValheimZdoJournalService(path);
            Assert.Equal(epochA, restartedGateway.Status().ActiveWorldEpoch);
            Assert.Equal(1, restartedGateway.Status().DurableObjects);
            Assert.False(restartedGateway.ActivateWorldEpoch(epochA).Changed);
            Assert.Equal(1, restartedGateway.Status().DurableObjects);

            var interestA = restartedGateway.RegisterInterest(
                "client-a",
                Interest(epochA));
            Assert.True(interestA.Accepted);
            Assert.Equal(1, interestA.SnapshotCount);
            Assert.Single(restartedGateway.Pending("client-a", epochA, 16));

            // A new dedicated-server descriptor changes only the session component. The old
            // objects, client interest, and queued delivery disappear in one transition.
            var transition = restartedGateway.ActivateWorldEpoch(epochB);
            Assert.True(transition.Changed);
            Assert.Equal(1, transition.RemovedObjects);
            Assert.Equal(1, transition.RemovedInterests);
            Assert.Equal(1, transition.RemovedPending);
            Assert.Equal(epochB, restartedGateway.Status().ActiveWorldEpoch);
            Assert.Equal(0, restartedGateway.Status().DurableObjects);
            Assert.Empty(restartedGateway.Pending("client-a", epochA, 16));

            var staleInterest = restartedGateway.RegisterInterest(
                "client-a",
                Interest(epochA));
            Assert.False(staleInterest.Accepted);
            Assert.Equal("world_epoch_not_active", staleInterest.Result);
            Assert.False(restartedGateway.Record(Mutation(epochA, 42)).Accepted);

            Assert.True(restartedGateway.RegisterInterest(
                "client-b",
                Interest(epochB)).Accepted);
            Assert.True(restartedGateway.Record(Mutation(epochB, 43)).Accepted);

            // The compacted WAL can no longer resurrect session A on a later Gateway restart.
            var afterSecondGatewayRestart = new ValheimZdoJournalService(path);
            Assert.Equal(epochB, afterSecondGatewayRestart.Status().ActiveWorldEpoch);
            Assert.Equal(1, afterSecondGatewayRestart.Status().DurableObjects);
            Assert.DoesNotContain(
                File.ReadLines(path),
                line => JsonDocument.Parse(line).RootElement
                    .GetProperty("world_epoch").GetString() == epochA);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ValheimZdoJournalObject Mutation(string worldEpoch, long uidId) => new()
    {
        RunId = "epoch-test",
        WorldEpoch = worldEpoch,
        ZoneEpoch = 1,
        SourceSequence = uidId,
        ObjectRevision = 1,
        UidUser = 1,
        UidId = uidId,
        Prefab = 7,
        DataRevision = 1,
        BodyBase64 = "AA==",
    };

    private static ValheimZdoJournalInterest Interest(string worldEpoch) => new()
    {
        RecipientId = "client-a",
        RunId = "epoch-test",
        WorldEpoch = worldEpoch,
        ZoneEpoch = 1,
        ZoneX = 0,
        ZoneY = 0,
        RadiusZones = 2,
        Refresh = true,
    };
}

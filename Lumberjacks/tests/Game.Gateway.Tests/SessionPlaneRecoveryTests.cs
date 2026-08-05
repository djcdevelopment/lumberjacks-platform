using System.Net.WebSockets;
using Game.Contracts.Protocol;
using Game.Gateway.Valheim;
using Game.Gateway.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Locks down the r42 session-plane fixes from the 2026-08-05 plan: resume-evict of zombie
/// sessions, the stalled-session abort, paced journal redelivery, per-recipient degrade on
/// motion resync, owner-liveness in window completeness, provisional replayed partitions,
/// and the bounded redirect queue.
/// </summary>
public sealed class SessionPlaneRecoveryTests
{
    // --- Fix 1: resume must succeed against a zombie session -------------------------------

    [Fact]
    public void ResumeEvictsLiveZombieAndCarriesReliableState()
    {
        var sessions = new SessionManager();
        var zombieSocket = new FakeWebSocket();
        var zombie = sessions.Create(zombieSocket);
        zombie.ValheimRole = "client";
        zombie.ValheimLogicalPeerId = "peer-zombie";
        zombie.RegionId = "region-spawn";
        Assert.True(zombie.Reliable.TryEnqueue("test_frame", new { n = 1 }, out _, out _));
        Assert.True(zombie.Reliable.TryEnqueue("test_frame", new { n = 2 }, out _, out _));
        var token = zombie.ResumeToken;
        var epochBefore = zombie.ResumeEpoch;

        var resumed = sessions.TryResume(token, new FakeWebSocket(), out var evicted);

        Assert.NotNull(resumed);
        Assert.True(evicted);
        Assert.True(zombieSocket.Aborted);
        Assert.Equal(zombie.PlayerId, resumed!.PlayerId);
        Assert.Equal("peer-zombie", resumed.ValheimLogicalPeerId);
        Assert.Equal("region-spawn", resumed.RegionId);
        // Reliable state (pending frames, sequences) carried into the new incarnation.
        Assert.Same(zombie.Reliable, resumed.Reliable);
        Assert.Equal(2, resumed.Reliable.PendingCount);
        Assert.Equal(epochBefore + 1, resumed.ResumeEpoch);
        Assert.NotEqual(token, resumed.ResumeToken);
        // The zombie is gone, so the duplicate logical-peer rejection cannot fire.
        Assert.DoesNotContain(sessions.GetAll(), s => s.SessionId == zombie.SessionId);
        Assert.Contains(sessions.GetAll(), s => s.SessionId == resumed.SessionId);
    }

    [Fact]
    public void ResumeFromDetachedStillWorksAndUnknownTokenCreatesNothing()
    {
        var sessions = new SessionManager();
        var original = sessions.Create(new FakeWebSocket());
        original.ValheimLogicalPeerId = "peer-detached";
        var token = original.ResumeToken;
        sessions.Detach(original);

        var resumed = sessions.TryResume(token, new FakeWebSocket(), out var evicted);
        Assert.NotNull(resumed);
        Assert.False(evicted);
        Assert.Equal(original.PlayerId, resumed!.PlayerId);

        Assert.Null(sessions.TryResume("no-such-token", new FakeWebSocket(), out _));
    }

    // --- Fix 2: stalled-session abort ------------------------------------------------------

    [Fact]
    public void PendingStalledForTracksAckProgressAndResetsOnResume()
    {
        var reliable = new ReliableGameSessionState("server-1", "world-1");
        var now = DateTime.UtcNow;
        Assert.Equal(TimeSpan.Zero, reliable.PendingStalledFor(now));

        Assert.True(reliable.TryEnqueue("test_frame", new { n = 1 }, out var frame, out _));
        Assert.True(reliable.PendingStalledFor(now.AddSeconds(11)) >= TimeSpan.FromSeconds(10));

        // ACK progress that drains the queue stops the clock entirely.
        Assert.Equal(1, reliable.AcknowledgeThrough(frame.Sequence));
        Assert.Equal(TimeSpan.Zero, reliable.PendingStalledFor(now.AddHours(1)));

        // A resumed incarnation earns its own stall verdict.
        Assert.True(reliable.TryEnqueue("test_frame", new { n = 2 }, out _, out _));
        reliable.AdvanceResume();
        Assert.True(reliable.PendingStalledFor(DateTime.UtcNow) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void StallMonitorAbortsOnlyOpenSessionsPastTheThreshold()
    {
        var sessions = new SessionManager();
        var stalledSocket = new FakeWebSocket();
        var stalled = sessions.Create(stalledSocket);
        Assert.True(stalled.Reliable.TryEnqueue("test_frame", new { n = 1 }, out _, out _));

        var healthySocket = new FakeWebSocket();
        var healthy = sessions.Create(healthySocket);

        var monitor = new SessionPlaneMonitor(
            sessions, new ValheimZdoRedirectService(walPath: null),
            NullLogger<SessionPlaneMonitor>.Instance);
        var later = DateTime.UtcNow + SessionPlaneMonitor.StallAbortAfter +
            TimeSpan.FromSeconds(1);

        Assert.Equal(1, monitor.SweepOnce(later));
        Assert.True(stalledSocket.Aborted);
        Assert.False(healthySocket.Aborted);

        // An already-closed socket is not re-aborted on the next sweep.
        Assert.False(SessionPlaneMonitor.ShouldAbortStalledSession(stalled, later));
    }

    // --- Fix 2b: motion resync degrades per recipient --------------------------------------

    [Fact]
    public async Task MotionResyncSkipsRecipientWithFullQueueInsteadOfThrowing()
    {
        var sessions = new SessionManager();
        var router = NewRouter(sessions, new ValheimZdoJournalService(walPath: null));

        var publisher = sessions.Create(new FakeWebSocket());
        publisher.ValheimRole = "client";
        publisher.ValheimLogicalPeerId = "peer-publisher";
        publisher.RegionId = "region-spawn";

        var wedged = sessions.Create(new FakeWebSocket());
        wedged.ValheimRole = "client";
        wedged.ValheimLogicalPeerId = "peer-wedged";
        wedged.RegionId = "region-spawn";
        while (wedged.Reliable.TryEnqueue("filler", new { }, out _, out _)) { }
        Assert.Equal(
            ReliableGameSessionState.MaxPendingFrames, wedged.Reliable.PendingCount);

        var healthy = sessions.Create(new FakeWebSocket());
        healthy.ValheimRole = "client";
        healthy.ValheimLogicalPeerId = "peer-healthy";
        healthy.RegionId = "region-spawn";

        // Pre-fix this throw took the whole publisher processing loop down with the one
        // wedged recipient (candidate-8 signature at MessageRouter's resync fan-out).
        await router.RouteAsync(publisher, EnvelopeFactory.Create(
            MessageType.ValheimMotionResyncPublish,
            new
            {
                run_id = "r42-test",
                action_id = "resync-1",
                source_motion_sequence = 10,
                gap_start_sequence = 8,
                gap_end_sequence = 9,
                zdo_user_id = 42L,
                zdo_id = 7U,
                x = 1.0,
                y = 2.0,
                z = 3.0,
                velocity_x = 0.0,
                velocity_y = 0.0,
                velocity_z = 0.0,
                yaw = 90.0,
                sent_milliseconds = 123U,
                reason = "gap_recovery",
            }));

        // The healthy recipient still got the resync; the wedged one was skipped.
        Assert.Equal(1, healthy.Reliable.PendingCount);
        Assert.Equal(
            ReliableGameSessionState.MaxPendingFrames, wedged.Reliable.PendingCount);
    }

    // --- Fix 3: paced journal redelivery ----------------------------------------------------

    [Fact]
    public async Task JournalRedeliveryRefillsToTheBulkCapNotTheControlHeadroom()
    {
        var journal = new ValheimZdoJournalService(walPath: null);
        var sessions = new SessionManager();
        var router = NewRouter(sessions, journal);

        var client = sessions.Create(new FakeWebSocket());
        client.ValheimRole = "client";
        client.ValheimLogicalPeerId = "peer-slow";

        journal.ActivateWorldEpoch("epoch-1");
        for (var i = 1; i <= 200; i++)
        {
            var result = journal.Record(new ValheimZdoJournalObject
            {
                RunId = "r42-test",
                WorldEpoch = "epoch-1",
                SourceSequence = i,
                ObjectRevision = 1,
                UidUser = 42,
                UidId = i,
                Prefab = 1,
                ZoneX = 0,
                ZoneY = 0,
                BodyBase64 = "AA==",
            });
            Assert.True(result.Accepted);
        }
        var interest = journal.RegisterInterest("peer-slow", new ValheimZdoJournalInterest
        {
            RecipientId = "peer-slow",
            RunId = "r42-test",
            WorldEpoch = "epoch-1",
            ZoneX = 0,
            ZoneY = 0,
            RadiusZones = 1,
        });
        Assert.True(interest.Accepted);
        Assert.True(interest.PendingCount >= 200);

        await router.RouteAsync(client, EnvelopeFactory.Create(
            MessageType.ReliableAck, new { through_sequence = 1 }));

        // 200 deliveries are pending in the journal, but the reconnected peer only ever
        // holds the bulk in-flight cap unACKed — not the 224-frame headroom that buried
        // candidate 8's i5 client after the Gateway restart.
        Assert.True(client.Reliable.PendingCount is > 0 and <= 64,
            $"expected 0 < pending <= 64, got {client.Reliable.PendingCount}");
    }

    // --- Fix 4: owner-liveness in window completeness ---------------------------------------

    [Fact]
    public void DeadReplayedPartitionCannotVetoWindowCompleteness()
    {
        var service = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService(walPath: null);
        var consumers = new ValheimZdoConsumerTelemetryService();
        var activity = new ValheimWindowActivityService();
        var now = DateTime.UtcNow;

        // Live partition: fully drained and acknowledged.
        redirects.RecordEnvelopes("window-1", "server", new[]
        {
            new ValheimZdoRedirectEnvelope { Seq = 1, BodyB64 = "AA==" },
        }, "recipient-live");
        redirects.Acknowledge("window-1", "recipient-live", new[] { 1L });
        activity.Touch("window-1", "recipient-live", now);

        // Dead partition: pending forever, its consumer never re-attached (the replayed
        // journal state that starved the 2026-08-05 human session with heartbeat 409s).
        redirects.RecordEnvelopes("window-1", "server", new[]
        {
            new ValheimZdoRedirectEnvelope { Seq = 1, BodyB64 = "AA==" },
        }, "recipient-dead");

        consumers.Record(new ValheimZdoConsumerHeartbeat
        {
            WindowId = "window-1",
            ConsumerId = "recipient-live",
            Applied = 1,
            Acknowledged = 1,
        });

        // Without liveness the dead partition vetoes completeness permanently.
        Assert.False(service.IsAuthoritativeComplete("window-1", redirects, consumers));
        // With liveness only the live partition counts, and it is complete.
        Assert.True(service.IsAuthoritativeComplete(
            "window-1", redirects, consumers, activity, now));
    }

    [Fact]
    public void ReplayedPartitionWithoutConsumerIsPurgedAfterTtlAndStaysGone()
    {
        var walPath = Path.Combine(
            Path.GetTempPath(), "r42-provisional-" + Guid.NewGuid().ToString("N"),
            "redirect.wal");
        try
        {
            var writer = new ValheimZdoRedirectService(walPath);
            writer.RecordEnvelopes("window-1", "server", new[]
            {
                new ValheimZdoRedirectEnvelope { Seq = 1, BodyB64 = "AA==" },
            }, "recipient-gone");

            var replayed = new ValheimZdoRedirectService(walPath);
            Assert.Equal(1, replayed.GetStatus("window-1", "recipient-gone").Pending);

            // Before the TTL nothing is dropped.
            Assert.Equal(0, replayed.PurgeStaleReplayedPartitions(DateTime.UtcNow));
            // Past the TTL the never-reattached partition is dropped.
            Assert.Equal(1, replayed.PurgeStaleReplayedPartitions(
                DateTime.UtcNow + ValheimZdoRedirectService.ReplayedPartitionTtl +
                TimeSpan.FromSeconds(1)));
            Assert.Equal(0, replayed.GetStatus("window-1", "recipient-gone").Pending);

            // The drop is WAL-durable: another restart does not resurrect the partition.
            var restarted = new ValheimZdoRedirectService(walPath);
            Assert.Equal(0, restarted.GetStatus("window-1", "recipient-gone").Pending);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(walPath)!, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void ConsumerPollMarksReplayedPartitionAttachedAndExemptFromPurge()
    {
        var walPath = Path.Combine(
            Path.GetTempPath(), "r42-attached-" + Guid.NewGuid().ToString("N"),
            "redirect.wal");
        try
        {
            var writer = new ValheimZdoRedirectService(walPath);
            writer.RecordEnvelopes("window-1", "server", new[]
            {
                new ValheimZdoRedirectEnvelope { Seq = 1, BodyB64 = "AA==" },
            }, "recipient-back");

            var replayed = new ValheimZdoRedirectService(walPath);
            Assert.Single(replayed.Pending("window-1", "recipient-back", 10));

            Assert.Equal(0, replayed.PurgeStaleReplayedPartitions(
                DateTime.UtcNow + ValheimZdoRedirectService.ReplayedPartitionTtl +
                TimeSpan.FromSeconds(1)));
            Assert.Equal(1, replayed.GetStatus("window-1", "recipient-back").Pending);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(walPath)!, recursive: true); }
            catch { }
        }
    }

    // --- WAL/pending bound ------------------------------------------------------------------

    [Fact]
    public void UnconsumedPartitionShedsOldestPastTheBoundInsteadOfGrowingForever()
    {
        var redirects = new ValheimZdoRedirectService(
            walPath: null, maxPendingPerPartition: 100);
        for (var seq = 1; seq <= 250; seq++)
            redirects.RecordEnvelopes("window-1", "server", new[]
            {
                new ValheimZdoRedirectEnvelope { Seq = seq, BodyB64 = "AA==" },
            }, "recipient-none");

        var status = redirects.GetStatus("window-1", "recipient-none");
        Assert.True(status.Pending <= 100,
            $"pending {status.Pending} exceeds the partition bound");
        Assert.Equal(250, status.Receipts);
        Assert.Equal(250 - status.Pending, status.Shed);
        // Oldest envelopes were shed; the newest survive.
        Assert.Contains(redirects.Pending("window-1", "recipient-none", 1024),
            envelope => envelope.Seq == 250);
    }

    static MessageRouter NewRouter(SessionManager sessions, ValheimZdoJournalService journal) =>
        new(
            sessions,
            inputQueue: null!,
            world: null!,
            playerHandler: null!,
            placeStructureHandler: null!,
            inventoryHandler: null!,
            zdoJournal: journal,
            ownershipLeases: null!,
            worldZones: null!,
            NullLogger<MessageRouter>.Instance);

    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        public bool Aborted { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State =>
            Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => Aborted = true;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

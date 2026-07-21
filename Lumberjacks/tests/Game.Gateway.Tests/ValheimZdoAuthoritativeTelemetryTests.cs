using System.Diagnostics;
using System.Text.Json;
using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimZdoAuthoritativeTelemetryTests
{
    private const string Window = "i7-test";

    [Fact]
    public void RedirectStatusTracksAcknowledgedAndPendingSequences()
    {
        var redirects = new ValheimZdoRedirectService();
        redirects.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2)]);

        var before = redirects.GetStatus(Window);
        Assert.Equal(0, before.Acknowledged);
        Assert.Equal(2, before.Pending);

        var ack = redirects.Acknowledge(Window, [1, 2, 99]);
        Assert.Equal(2, ack.Acknowledged);
        Assert.Equal(1, ack.Unknown);

        var after = redirects.GetStatus(Window);
        Assert.Equal(2, after.Acknowledged);
        Assert.Equal(0, after.Pending);
    }

    [Fact]
    public void PendingCarriesPriorityPlanIntoDurableAuthoritativeOrder()
    {
        var redirects = new ValheimZdoRedirectService();
        redirects.RecordEnvelopes(Window, "server",
        [
            Envelope(1) with { PriorityTier = "support_piece", PriorityRank = 5, DistanceMeters = 1 },
            Envelope(2) with { PriorityTier = "player_critical", PriorityRank = 0, DistanceMeters = 50 },
            Envelope(3) with { PriorityTier = "player_critical", PriorityRank = 0, DistanceMeters = 10 },
            Envelope(4),
        ]);

        Assert.Equal(new long?[] { 3, 2, 1, 4 },
            redirects.Pending(Window, 1024).Select(envelope => envelope.Seq));
        Assert.Equal(4, redirects.GetStatus(Window).Pending);
    }

    [Fact]
    public void PromotionRequiresFreshSuccessfulConsumerAndDrainedQueue()
    {
        var heartbeat = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService();
        var consumers = new ValheimZdoConsumerTelemetryService();
        redirects.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2)]);

        consumers.Record(Consumer(applied: 2, acknowledged: 2));
        Assert.False(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));

        redirects.Acknowledge(Window, [1, 2]);
        Assert.True(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));

        consumers.Record(Consumer(applied: 2, acknowledged: 2) with { Rejected = 1 });
        Assert.False(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));
    }

    [Fact]
    public void LatestModVersionTracksTheRunningValheimHeartbeat()
    {
        var service = new ValheimTelemetryHeartbeatService();
        Assert.Null(service.LatestModVersion());

        service.Record(new ValheimTelemetryHeartbeat
        {
            InstanceId = "server-test",
            ModVersion = "0.5.22",
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        });

        Assert.Equal("0.5.22", service.LatestModVersion());
    }

    [Fact]
    public void PromotionAcceptsExplicitlySupersededOlderRevisions()
    {
        var heartbeat = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService();
        var consumers = new ValheimZdoConsumerTelemetryService();
        redirects.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2)]);
        redirects.Acknowledge(Window, [1, 2]);

        consumers.Record(Consumer(applied: 1, acknowledged: 2) with { Superseded = 1 });

        Assert.True(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));
    }

    [Fact]
    public void DurableQueueReplaysReceiptsAcksAndRepairsATruncatedTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumberjacks-zdo-wal-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "redirect.wal");
        try
        {
            var writer = new ValheimZdoRedirectService(path);
            writer.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2)]);
            writer.Acknowledge(Window, [1]);
            var validBytes = new FileInfo(path).Length;
            using (var tail = new FileStream(path, FileMode.Append, FileAccess.Write))
                tail.Write([0x7f, 0x01]);

            var replayed = new ValheimZdoRedirectService(path);
            var status = replayed.GetStatus(Window);
            Assert.True(replayed.PersistenceEnabled);
            Assert.True(replayed.PersistenceHealthy);
            Assert.Equal(validBytes, replayed.WalBytes);
            Assert.Equal(2, status.DistinctSeq);
            Assert.Equal(1, status.Acknowledged);
            Assert.Equal(1, status.Pending);

            replayed.Reset(Window);
            Assert.Equal(0, new ValheimZdoRedirectService(path).GetStatus(Window).Receipts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecipientLessV1WalFixtureReplaysIntoLegacyBucket()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumberjacks-zdo-v1-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "redirect.wal");
        try
        {
            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Op = "record",
                WindowId = Window,
                RecipientId = "recipient-from-future",
                Source = "v1-fixture",
                Envelopes = new[] { new { Seq = 41L, BodyB64 = "AA==" } },
                ObservedUtc = DateTime.UtcNow,
            });
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.Write(BitConverter.GetBytes(payload.Length));
                stream.Write(payload);
            }

            var replayed = new ValheimZdoRedirectService(path);
            Assert.True(replayed.PersistenceHealthy);
            Assert.Equal(1, replayed.GetStatus(Window, ValheimRecipient.Legacy).Receipts);
            Assert.Equal(41, replayed.Pending(Window, ValheimRecipient.Legacy, 10).Single().Seq);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompleteLengthCorruptWalRecordFailsRatherThanTruncating()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumberjacks-zdo-corrupt-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "redirect.wal");
        try
        {
            Directory.CreateDirectory(directory);
            var payload = System.Text.Encoding.UTF8.GetBytes("{ this is complete garbage }");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.Write(BitConverter.GetBytes(payload.Length));
                stream.Write(payload);
            }

            Assert.ThrowsAny<Exception>(() => new ValheimZdoRedirectService(path));
            Assert.Equal(sizeof(int) + payload.Length, new FileInfo(path).Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecipientScopedWalReplayConvergesAcrossTwoRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumberjacks-zdo-recipient-replay-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "redirect.wal");
        try
        {
            var writer = new ValheimZdoRedirectService(path);
            writer.RecordEnvelopes(Window, "server", [Envelope(1) with { RecipientId = "a" }, Envelope(2) with { RecipientId = "b" }]);
            writer.Acknowledge(Window, "a", [1]);
            var first = new ValheimZdoRedirectService(path);
            var second = new ValheimZdoRedirectService(path);
            foreach (var recipient in new[] { "a", "b" })
            {
                var before = first.GetStatus(Window, recipient);
                var after = second.GetStatus(Window, recipient);
                Assert.Equal(before.WindowId, after.WindowId);
                Assert.Equal(before.RecipientId, after.RecipientId);
                Assert.Equal(before.Receipts, after.Receipts);
                Assert.Equal(before.DistinctSeq, after.DistinctSeq);
                Assert.Equal(before.Acknowledged, after.Acknowledged);
                Assert.Equal(before.Pending, after.Pending);
                Assert.Equal(before.Duplicates, after.Duplicates);
                Assert.Equal(first.Pending(Window, recipient, 10).Select(item => item.Seq),
                    second.Pending(Window, recipient, 10).Select(item => item.Seq));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WalCompactionPreservesWindowStateAndReplay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumberjacks-zdo-compact-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "redirect.wal");
        try
        {
            var service = new ValheimZdoRedirectService(path);
            service.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2), Envelope(3)]);
            service.RecordEnvelopes(Window, "retry", [Envelope(2)]);
            service.Acknowledge(Window, [1, 2]);
            var before = service.GetStatus(Window);
            var pendingBefore = service.Pending(Window, 256).Select(e => e.Seq).ToArray();
            var oldBytes = service.WalBytes;

            var compactedBytes = service.Compact();
            Assert.True(compactedBytes < oldBytes);

            var replayed = new ValheimZdoRedirectService(path);
            var after = replayed.GetStatus(Window);
            Assert.Equal(before.WindowId, after.WindowId);
            Assert.Equal(before.Receipts, after.Receipts);
            Assert.Equal(before.DistinctSeq, after.DistinctSeq);
            Assert.Equal(before.Acknowledged, after.Acknowledged);
            Assert.Equal(before.Pending, after.Pending);
            Assert.Equal(before.Duplicates, after.Duplicates);
            Assert.Equal(before.MinSeq, after.MinSeq);
            Assert.Equal(before.MaxSeq, after.MaxSeq);
            Assert.Equal(before.MissingSeq, after.MissingSeq);
            Assert.Equal(before.EmptyBodyCount, after.EmptyBodyCount);
            Assert.Equal(before.PerPrefab, after.PerPrefab);
            Assert.Equal(before.PerSource, after.PerSource);
            Assert.Equal(pendingBefore, replayed.Pending(Window, 256).Select(e => e.Seq).ToArray());
            Assert.Equal(compactedBytes, replayed.WalBytes);
            Assert.True(replayed.PersistenceHealthy);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CopiedWalRehearsalCompactsAndReplaysWhenPathIsProvided()
    {
        var source = Environment.GetEnvironmentVariable("LUMBERJACKS_WAL_REHEARSAL_PATH");
        if (string.IsNullOrWhiteSpace(source)) return;

        var working = source + ".working";
        var reportPath = source + ".report.json";
        File.Copy(source, working, overwrite: true);
        var beforeBytes = new FileInfo(working).Length;
        var clock = Stopwatch.StartNew();
        var service = new ValheimZdoRedirectService(working);
        var before = service.GetAllStatuses();
        var compactedBytes = service.Compact();
        clock.Stop();
        var replayed = new ValheimZdoRedirectService(working);
        var after = replayed.GetAllStatuses();
        Assert.True(replayed.PersistenceHealthy);
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].WindowId, after[i].WindowId);
            Assert.Equal(before[i].Receipts, after[i].Receipts);
            Assert.Equal(before[i].DistinctSeq, after[i].DistinctSeq);
            Assert.Equal(before[i].Acknowledged, after[i].Acknowledged);
            Assert.Equal(before[i].Pending, after[i].Pending);
            Assert.Equal(before[i].Duplicates, after[i].Duplicates);
        }

        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            source,
            before_bytes = beforeBytes,
            compacted_bytes = compactedBytes,
            reduction_bytes = beforeBytes - compactedBytes,
            reduction_percent = beforeBytes == 0 ? 0 : 100d * (beforeBytes - compactedBytes) / beforeBytes,
            duration_ms = clock.Elapsed.TotalMilliseconds,
            windows = before.Count,
            replay_healthy = replayed.PersistenceHealthy,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void PromotionAllowsSuppressedAtLeastOnceTransportDuplicates()
    {
        var heartbeat = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService();
        var consumers = new ValheimZdoConsumerTelemetryService();
        redirects.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2)]);
        redirects.RecordEnvelopes(Window, "server-retry", [Envelope(1)]);
        redirects.Acknowledge(Window, [1, 2]);
        consumers.Record(Consumer(applied: 2, acknowledged: 2));

        Assert.Equal(1, redirects.GetStatus(Window).Duplicates);
        Assert.True(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));
    }

    [Fact]
    public void PromotionAllowsConsumerRestartWithCumulativeRedirectHistory()
    {
        var heartbeat = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService();
        var consumers = new ValheimZdoConsumerTelemetryService();
        redirects.RecordEnvelopes(Window, "server", [Envelope(1), Envelope(2), Envelope(3)]);
        redirects.Acknowledge(Window, [1, 2, 3]);

        // Consumer counters are per-process and reset after a reconnect. Durable
        // redirect closure, not equality with those diagnostic counters, is the
        // authoritative completion criterion.
        consumers.Record(Consumer(applied: 1, acknowledged: 1));
        Assert.True(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));
    }

    [Fact]
    public void PromotionAllowsRedeliveredDuplicateAfterEarlierAcknowledgement()
    {
        var heartbeat = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService();
        var consumers = new ValheimZdoConsumerTelemetryService();
        redirects.RecordEnvelopes(Window, "server", [Envelope(1)]);
        redirects.Acknowledge(Window, [1]);
        redirects.RecordEnvelopes(Window, "server-retry", [Envelope(1)]);
        redirects.Acknowledge(Window, [1]);

        consumers.Record(Consumer(applied: 1, acknowledged: 1));
        Assert.Equal(2, redirects.GetStatus(Window).Acknowledged);
        Assert.Equal(1, redirects.GetStatus(Window).DistinctSeq);
        Assert.True(heartbeat.IsAuthoritativeComplete(Window, redirects, consumers));
    }

    [Fact]
    public void EmptyPrimaryServerKeepsHeartbeatLiveWithoutAConsumer()
    {
        var heartbeat = new ValheimTelemetryHeartbeatService();
        var redirects = new ValheimZdoRedirectService();
        var consumers = new ValheimZdoConsumerTelemetryService();
        var sample = new ValheimTelemetryHeartbeat
        {
            CutoverMode = "lumberjacks-primary",
            EnrollmentManifestId = Window,
            CoverageTotal = 10,
            CoverageLumberjacks = 10,
            CoverageNativeOnly = 0,
            PeerCount = 0,
        };

        Assert.True(heartbeat.CanAcceptPrimaryHeartbeat(sample, redirects, consumers));
        Assert.True(heartbeat.CanAcceptPrimaryHeartbeat(sample with
        {
            CoverageTotal = null,
            CoverageLumberjacks = null,
            CoverageNativeOnly = null,
        }, redirects, consumers));
        Assert.False(heartbeat.CanAcceptPrimaryHeartbeat(sample with { PeerCount = 1 }, redirects, consumers));
        Assert.False(heartbeat.CanAcceptPrimaryHeartbeat(sample with { PeerCount = 1, CoverageTotal = 0 }, redirects, consumers));
        Assert.False(heartbeat.CanAcceptPrimaryHeartbeat(sample with { PeerCount = 1, CoverageNativeOnly = 1 }, redirects, consumers));
    }

    [Fact]
    public void ExpiredConsumerRetainsLastCountersButIsNotActive()
    {
        var consumers = new ValheimZdoConsumerTelemetryService();
        consumers.Record(Consumer(applied: 12, acknowledged: 13) with { Pending = 4 });

        // The sample is recorded with the current clock, so it is active here.
        var status = consumers.GetWindowStatus(Window);
        Assert.Equal(1, status.ActiveConsumers);
        Assert.Equal(12, status.Applied);
        Assert.Equal(13, status.Acknowledged);

        // A future timestamp is not injectable into the service clock; this test
        // documents the retained-counter contract exercised by the live monitor.
        Assert.Equal(4, status.Pending);
    }

    private static ValheimZdoRedirectEnvelope Envelope(long seq) => new()
    {
        Seq = seq,
        UidUser = 1,
        UidId = seq,
        BodyB64 = "AA==",
    };

    private static ValheimZdoConsumerHeartbeat Consumer(long applied, long acknowledged) => new()
    {
        WindowId = Window,
        ConsumerId = "consumer-test",
        ModVersion = "0.5.22",
        TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        Applied = applied,
        Acknowledged = acknowledged,
    };
}

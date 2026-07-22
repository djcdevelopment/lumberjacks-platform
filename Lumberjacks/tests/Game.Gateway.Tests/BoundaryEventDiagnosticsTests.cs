using Game.Gateway.BoundaryEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class BoundaryEventDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "boundary-events-" + Guid.NewGuid().ToString("N"));

    public BoundaryEventDiagnosticsTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void SnapshotCountsIdentityAuthorizationAndProxyBoundaryWarnings()
    {
        File.WriteAllText(Path.Combine(_root, "20260722-000001.jsonl"), string.Join('\n',
            Row("identity.resolved", """
            {
              "principal_kind":"private-plane",
              "principal_reference":null,
              "resolution_basis":"private_socket",
              "socket_peer_class":"private",
              "forwarded_peer_claim_class":"public",
              "granted_capabilities":"Admin, Producer, Consumer, Telemetry"
            }
            """),
            Row("authorization.decided", """
            {
              "policy":"valheim.admin.required",
              "required_capabilities":"Admin",
              "granted_capabilities":"Admin, Producer, Consumer, Telemetry",
              "result":"allow",
              "reason":"private_socket"
            }
            """),
            Row("request.completed", """
            {
              "method":"GET",
              "route":"/api/v0/enrollment",
              "transport":"https",
              "duration_ms":4.5,
              "status_code":200,
              "exception_type":null
            }
            """)) + "\n");

        var snapshot = Create().Snapshot();

        Assert.Equal(3, snapshot.Rows);
        Assert.Equal(1, snapshot.ByEventType["identity.resolved"]);
        Assert.Equal(1, snapshot.AuthorizationResultReason["allow/private_socket"]);
        Assert.Equal(1, snapshot.RequiredGrantedCapabilities["Admin -> Admin, Producer, Consumer, Telemetry"]);
        Assert.Equal(1, snapshot.IdentityResolution["private-plane/private_socket"]);
        Assert.Equal(1, snapshot.ProxyBoundary["socket:private forwarded:public"]);
        Assert.Equal(1, snapshot.ProxyBoundaryWarnings);
        Assert.Equal(1, snapshot.RouteStatus["200 /api/v0/enrollment"]);
        Assert.Equal(1, snapshot.RequestDuration.Count);
        Assert.Equal(4.5, snapshot.RequestDuration.Average);
    }

    [Fact]
    public void SnapshotTreatsMalformedOpenSegmentAsTruncated()
    {
        File.WriteAllText(Path.Combine(_root, "20260722-000001.open.jsonl"),
            Row("zdo.batch.queued", """{"envelope_count":2}""") + "\n{\"not complete\"");

        var snapshot = Create().Snapshot();

        Assert.Equal(1, snapshot.Rows);
        Assert.Equal(1, snapshot.TruncatedRows);
        Assert.Equal(0, snapshot.MalformedRows);
        Assert.Equal(1, snapshot.ByEventType["zdo.batch.queued"]);
    }

    [Fact]
    public void SnapshotSummarizesZdoQueuePollAckAndConsumerMovement()
    {
        File.WriteAllText(Path.Combine(_root, "20260722-000002.jsonl"), string.Join('\n',
            Row("zdo.batch.queued", """
            {
              "window_id":"p7-primary-v1",
              "observed_mod_release":"m5",
              "recipient_count":2,
              "envelope_count":8,
              "request_bytes":4096,
              "accepted_count":8,
              "queue_duration_ms":1.25
            }
            """),
            Row("zdo.batch.polled", """
            {
              "window_id":"p7-primary-v1",
              "recipient_id":"recipient-a",
              "limit":64,
              "envelope_count":6,
              "poll_duration_ms":0.75
            }
            """),
            Row("zdo.batch.acknowledged", """
            {
              "window_id":"p7-primary-v1",
              "recipient_id":"recipient-a",
              "sequence_count":6,
              "acknowledged_count":5,
              "unknown_count":1,
              "ack_duration_ms":0.5
            }
            """),
            Row("zdo.consumer.heartbeat", """
            {
              "window_id":"p7-primary-v1",
              "recipient_id":"recipient-a",
              "observed_mod_release":"m5",
              "pending":3,
              "applied":11,
              "superseded":2,
              "rejected":1,
              "acknowledged":5,
              "priority_fast_lane_applied":4,
              "last_operation_result":"ok"
            }
            """)) + "\n");

        var snapshot = Create().Snapshot();

        Assert.Equal(4, snapshot.Rows);
        Assert.Equal(1, snapshot.ZdoByOperation["zdo.batch.queued"]);
        Assert.Equal(4, snapshot.ZdoByWindow["p7-primary-v1"]);
        Assert.Equal(3, snapshot.ZdoByRecipient["recipient-a"]);
        Assert.Equal(2, snapshot.ZdoRelease["m5"]);
        Assert.Equal(1, snapshot.ZdoConsumerResult["ok"]);
        Assert.Equal(8, snapshot.ZdoTotals.QueuedEnvelopes);
        Assert.Equal(8, snapshot.ZdoTotals.AcceptedEnvelopes);
        Assert.Equal(4096, snapshot.ZdoTotals.RequestBytes);
        Assert.Equal(6, snapshot.ZdoTotals.PolledEnvelopes);
        Assert.Equal(6, snapshot.ZdoTotals.AckSequenceCount);
        Assert.Equal(5, snapshot.ZdoTotals.AcknowledgedCount);
        Assert.Equal(1, snapshot.ZdoTotals.UnknownAckCount);
        Assert.Equal(11, snapshot.ZdoTotals.AppliedCount);
        Assert.Equal(2, snapshot.ZdoTotals.SupersededCount);
        Assert.Equal(1, snapshot.ZdoTotals.RejectedCount);
        Assert.Equal(3, snapshot.ZdoTotals.PendingCount);
        Assert.Equal(4, snapshot.ZdoTotals.PriorityFastLaneApplied);
        Assert.Equal(1.25, snapshot.ZdoQueueDuration.Average);
        Assert.Equal(0.75, snapshot.ZdoPollDuration.Average);
        Assert.Equal(0.5, snapshot.ZdoAckDuration.Average);
        Assert.Equal(4, snapshot.RecentEvents.Count);
    }

    private BoundaryEventDiagnostics Create()
    {
        var options = Options.Create(new BoundaryEventOptions
        {
            Enabled = true,
            Path = _root,
        });
        return new BoundaryEventDiagnostics(new OptionsWrapper<BoundaryEventOptions>(options.Value),
            new BoundaryEventWriter(options, NullLogger<BoundaryEventWriter>.Instance));
    }

    private static string Row(string eventType, string data)
    {
        using var parsed = JsonDocument.Parse(data);
        return JsonSerializer.Serialize(new
        {
            schema_version = 1,
            event_version = 1,
            event_id = Guid.NewGuid().ToString("N"),
            timestamp_utc = "2026-07-22T12:00:00Z",
            event_type = eventType,
            trace_id = "trace",
            span_id = "span",
            source = new
            {
                service = "gateway",
                instance = "test",
                release = "test",
                config_fingerprint = (string?)null,
            },
            data = parsed.RootElement,
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

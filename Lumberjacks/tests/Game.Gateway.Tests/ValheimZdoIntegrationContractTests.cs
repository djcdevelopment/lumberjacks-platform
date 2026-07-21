using Game.Gateway.Valheim;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class ValheimZdoIntegrationContractTests
{
    private const string Release = "m4-integration-20260719-r1";

    [Fact]
    public void SchemaTwoAdmitsOnlyTheReleaseBakedIntoTheGateway()
    {
        var admitted = ValheimZdoRedirectAdmissionPolicy.Evaluate(2, Release, Release);
        var wrong = ValheimZdoRedirectAdmissionPolicy.Evaluate(2, "m4-integration-20260719-r0", Release);
        var missing = ValheimZdoRedirectAdmissionPolicy.Evaluate(2, null, Release);
        var unconfigured = ValheimZdoRedirectAdmissionPolicy.Evaluate(2, Release, null);

        Assert.True(admitted.Allowed);
        Assert.False(wrong.Allowed);
        Assert.Equal("mod_release_incompatible", wrong.Error);
        Assert.Equal(409, wrong.StatusCode);
        Assert.Equal("mod_release_required", missing.Error);
        Assert.Equal("release_admission_unconfigured", unconfigured.Error);
        Assert.Equal(503, unconfigured.StatusCode);
    }

    [Fact]
    public void FrozenSchemaOneIsExplicitlyLegacyAndUnadmitted()
    {
        var result = ValheimZdoRedirectAdmissionPolicy.Evaluate(null, null, Release);

        Assert.True(result.Allowed);
        Assert.True(result.LegacyUnadmitted);
    }

    [Fact]
    public void CorrelatedImportanceApprovedPayloadRoutesToTheNamedRecipient()
    {
        var service = new ValheimZdoRedirectService();
        var envelope = new ValheimZdoRedirectEnvelope
        {
            CorrelationId = "corr-123",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Recipient = ValheimRecipient.Legacy,
            ImportanceClass = "player_critical",
            IdempotencyKey = "corr-123",
            Seq = 7,
            PriorityRank = 0,
            BodyB64 = "AA==",
        };

        service.RecordEnvelopes("integration", "server-instance", [envelope]);

        var delivered = service.Pending("integration", ValheimRecipient.Legacy, 10).Single();
        Assert.Equal("corr-123", delivered.CorrelationId);
        Assert.Equal("player_critical", delivered.ImportanceClass);
        Assert.Equal("corr-123", delivered.IdempotencyKey);
        Assert.Equal(0, service.GetStatus("integration", "not-the-recipient").Pending);
    }

    [Fact]
    public void ConsumerResultSurfaceRetainsTheProcessedCorrelation()
    {
        var telemetry = new ValheimZdoConsumerTelemetryService();
        telemetry.Record(new ValheimZdoConsumerHeartbeat
        {
            WindowId = "integration",
            ConsumerId = ValheimRecipient.Legacy,
            ModVersion = "0.5.31",
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            Applied = 1,
            Acknowledged = 1,
            PriorityTagged = 1,
            PriorityFastLaneApplied = 1,
            LastCorrelationId = "corr-123",
            LastOperationResult = "applied",
            FirstCorrelationId = "corr-123",
            FirstOperationResult = "applied",
        });

        var status = telemetry.GetRecipientStatus("integration", ValheimRecipient.Legacy);
        Assert.Equal("corr-123", status.LastCorrelationId);
        Assert.Equal("applied", status.LastOperationResult);
        Assert.Equal("corr-123", status.FirstCorrelationId);
        Assert.Equal(1, status.PriorityFastLaneApplied);
    }

    [Fact]
    public void PrivatePlaneConsumerUsesTheSameServerDerivedLegacyScopeAsPollAndAck()
    {
        var scope = ValheimRecipientScopePolicy.Resolve(
            "private-plane", recipientId: null, requestedRecipient: null, producerEmitsRecipients: false);
        var heartbeat = ValheimConsumerHeartbeatPolicy.Resolve(new ValheimZdoConsumerHeartbeat
        {
            WindowId = "integration",
            ModVersion = "0.5.31",
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        }, scope.Resolved);

        Assert.Null(scope.Error);
        Assert.Equal(ValheimRecipient.Legacy, heartbeat.Recorded?.ConsumerId);
    }
}

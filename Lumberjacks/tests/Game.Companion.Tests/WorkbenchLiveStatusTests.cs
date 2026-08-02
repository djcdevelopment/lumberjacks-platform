using System.Text.Json;
using Lumberjacks.Companion;
using Xunit;

namespace Game.Companion.Tests;

public sealed class WorkbenchLiveStatusTests
{
    [Fact]
    public void ReadyServerWithNoPeersProducesAnActionableIdleRead()
    {
        var snapshot = WorkbenchLiveStatus.Build(
            Json("""{"environment":"local","lumberjacks_version":"test"}"""),
            Json("""{"stale":false,"last_seen":"2026-08-02T08:31:04Z","heartbeat":{"server_state":"ready","peer_count":0,"players":[],"mod_version":"0.5.45"}}"""),
            Json("""{"stale":false,"mode":"lumberjacks","authoritative_window":{"pending":0,"active_consumers":0,"applied":20,"complete":true}}"""),
            Json("""{"received":0,"relayed_udp":0,"relayed_websocket":0}"""),
            Json("""{"persistence_healthy":true,"durable_objects":9155,"active_world_epoch":"world-0000000000001234-session-0000000000001001","epoch_invalidations":0}"""),
            null,
            "http://host.docker.internal:4000",
            DateTimeOffset.Parse("2026-08-02T08:32:00Z"));

        Assert.Equal("ready", snapshot.Level);
        Assert.Equal("AM4 is up and ready; nobody is connected.", snapshot.Headline);
        Assert.Contains("No action is required", snapshot.NextAction);
        Assert.True(snapshot.Server.Online);
        Assert.Equal(0, snapshot.Server.PeerCount);
        Assert.Equal("idle", snapshot.Activity.State);
    }

    [Fact]
    public void WaitingHumanIsReportedAsFinishedMachineWorkNotRunningWork()
    {
        var job = new WorkbenchJob
        {
            JobId = "job-fixture",
            CapabilityId = "build.rendered.c6-role-reversal",
            Title = "Run rendered C6 role reversal",
            State = "waiting_human",
            Target = "AM4",
            UpdatedUtc = DateTimeOffset.Parse("2026-08-02T08:30:00Z"),
        };

        var snapshot = WorkbenchLiveStatus.Build(
            Json("{}"),
            Json("""{"stale":false,"heartbeat":{"server_state":"ready","peer_count":0,"players":[]}}"""),
            Json("""{"stale":false,"mode":"lumberjacks","authoritative_window":{}}"""),
            Json("""{"received":0}"""),
            Json("""{"persistence_healthy":true,"durable_objects":10}"""),
            job,
            "http://gateway:4000");

        Assert.Equal("ready", snapshot.Level);
        Assert.Equal("AM4 is up and ready; nobody is connected.", snapshot.Headline);
        Assert.False(snapshot.Activity.Executing);
        Assert.True(snapshot.Activity.NeedsHuman);
        Assert.Contains("finished its machine work", snapshot.Activity.Summary);
        Assert.Contains("no machine action is still running", snapshot.NextAction);
    }

    [Fact]
    public void ActivePlayersAndMotionExposeNamesAndRuntimeCounters()
    {
        var snapshot = WorkbenchLiveStatus.Build(
            Json("{}"),
            Json("""
                {
                  "stale":false,
                  "heartbeat":{
                    "server_state":"ready",
                    "peer_count":2,
                    "players":[{"name":"wary.fool"},{"character_name":"durracktu"},{"id":"steam_76561198000000000"}],
                    "motion_state":"streaming",
                    "motion_websocket_connected":true,
                    "motion_udp_ready":true,
                    "motion_apply_enabled":true,
                    "motion_applied":88
                  }
                }
                """),
            Json("""{"stale":false,"mode":"lumberjacks","consumer_active":true,"authoritative_window":{"pending":3,"active_consumers":2,"consumer_acknowledged":55,"applied":50}}"""),
            Json("""{"received":120,"relayed_udp":70,"relayed_websocket":30}"""),
            Json("""{"persistence_healthy":true,"durable_objects":120,"active_world_epoch":"world-0000000000001234-session-0000000000001001","epoch_invalidations":1}"""),
            null,
            "http://gateway:4000");

        Assert.Equal("ready", snapshot.Level);
        Assert.Equal(["wary.fool", "durracktu"], snapshot.Server.Players);
        Assert.Contains("2 peers: wary.fool, durracktu", snapshot.Headline);
        Assert.Equal(120, snapshot.Motion.Received);
        Assert.Equal(100, snapshot.Motion.Relayed);
        Assert.Equal(3, snapshot.Cutover.Pending);
    }

    [Fact]
    public void MissingGatewayFailsClosedInsteadOfShowingDecorativeReadyNodes()
    {
        var snapshot = WorkbenchLiveStatus.Build(
            null,
            null,
            null,
            null,
            null,
            null,
            "https://example.invalid");

        Assert.Equal("bad", snapshot.Level);
        Assert.False(snapshot.Gateway.Reachable);
        Assert.Contains("Gateway telemetry is unavailable", snapshot.Headline);
        Assert.Contains("example.invalid", snapshot.NextAction);
    }

    [Fact]
    public void UnhealthyZoneBankPersistenceOverridesDecorativeServerReadiness()
    {
        var snapshot = WorkbenchLiveStatus.Build(
            Json("{}"),
            Json("""{"stale":false,"heartbeat":{"server_state":"ready","peer_count":0}}"""),
            Json("""{"stale":false,"mode":"lumberjacks","authoritative_window":{}}"""),
            Json("""{"received":0}"""),
            Json("""{"persistence_healthy":false,"durable_objects":9155}"""),
            null,
            "http://gateway:4000");

        Assert.Equal("bad", snapshot.Level);
        Assert.Contains("zone bank reports unhealthy persistence", snapshot.Headline);
        Assert.Contains("journal status", snapshot.NextAction);
    }

    static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

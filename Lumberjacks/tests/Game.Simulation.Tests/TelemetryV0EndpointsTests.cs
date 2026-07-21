using System.Text.Json;
using Game.Contracts.Entities;
using Game.Contracts.Events;
using Game.ServiceDefaults;
using Game.Simulation.Endpoints;
using Game.Simulation.Handlers;
using Game.Simulation.World;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Game.Simulation.Tests;

/// <summary>
/// Public Telemetry API v0 (community-telemetry-strategy.md Phase 3, G1) — response-shape
/// coverage for the four endpoints hosted in Game.Simulation.Endpoints, plus the hard privacy
/// rule from the strategy doc: no player ids, names, or positions may EVER appear in a v0
/// response. See tests/Game.Gateway.Tests/TelemetryV0SessionsEndpointsTests.cs for the
/// equivalent coverage of the sessions endpoint (which needs Gateway-only SessionManager state,
/// hence the separate test project).
///
/// Each Build*Info method is a plain static function of its dependencies (no HttpContext), so
/// it's tested directly here the same way the endpoints call it — no host required.
/// </summary>
public class TelemetryV0EndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string ToJson(object response) => JsonSerializer.Serialize(response, JsonOptions);

    // Deliberately large/oddly-precise coordinates — collision-proof against legitimate small
    // integer telemetry values (tick_rate_hz=20, near_radius=100, budget_ms=50, etc.) so a
    // substring match below can only mean the Position genuinely leaked, not a false positive.
    private static readonly Vec3 SentinelPosition = new(918273.645, 827364.591, 736451.827);

    private static WorldState WorldWithPlayers(params (string Id, string Name)[] players)
    {
        var world = new WorldState();
        foreach (var (id, name) in players)
        {
            world.Players[id] = new Player
            {
                Id = id,
                Name = name,
                Position = SentinelPosition,
                RegionId = "region-spawn",
                Connected = true,
                ConnectedAt = DateTimeOffset.UtcNow,
            };
        }
        return world;
    }

    // ── Server info ──

    [Fact]
    public void ServerInfoCarriesEnvelopeAndReplicationBlock()
    {
        var world = new WorldState();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:Policy"] = "radius",
                ["Replication:NearRadius"] = "150",
                ["Replication:MidRadius"] = "400",
                ["Replication:MidTickInterval"] = "3",
                ["Replication:SendWorkers"] = "2",
                ["Replication:BroadcastDeadlineMs"] = "40",
                ["Replication:AdaptiveDegrade"] = "true",
            })
            .Build();

        var json = ToJson(TelemetryV0Endpoints.BuildServerInfo(world, config));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("v0", root.GetProperty("api_version").GetString());
        Assert.Equal("unstable", root.GetProperty("stability").GetString());
        Assert.True(root.TryGetProperty("current_tick", out _));
        Assert.Equal(20, root.GetProperty("tick_rate_hz").GetInt32());
        Assert.True(root.TryGetProperty("uptime_seconds", out _));
        Assert.True(root.TryGetProperty("started_at", out _));

        var repl = root.GetProperty("replication");
        Assert.Equal("radius", repl.GetProperty("policy").GetString());
        Assert.Equal(150, repl.GetProperty("near_radius").GetDouble());
        Assert.Equal(400, repl.GetProperty("mid_radius").GetDouble());
        Assert.Equal(3, repl.GetProperty("mid_tick_interval").GetInt32());
        Assert.Equal(2, repl.GetProperty("send_workers").GetInt32()); // explicit config passes through as-is
        Assert.Equal(40, repl.GetProperty("deadline_ms").GetInt32());
        Assert.True(repl.GetProperty("adaptive").GetBoolean());
    }

    [Fact]
    public void ServerInfoAutoResolvesSendWorkersWithoutWaitingOnTickMetricsWindow()
    {
        // SendWorkers=0 means "auto" — must resolve immediately (no TickMetrics dependency,
        // unlike /tick's tick_timing which is null until the first ~5s window closes).
        var world = new WorldState();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:SendWorkers"] = "0" })
            .Build();

        var json = ToJson(TelemetryV0Endpoints.BuildServerInfo(world, config));
        using var doc = JsonDocument.Parse(json);
        var sendWorkers = doc.RootElement.GetProperty("replication").GetProperty("send_workers").GetInt32();

        Assert.True(sendWorkers >= 1);
    }

    // ── Tick info ──

    [Fact]
    public void TickInfoGracefullyNullBeforeFirstWindow()
    {
        var json = ToJson(TelemetryV0Endpoints.BuildTickInfo(metrics: null));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("v0", root.GetProperty("api_version").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("tick_timing").ValueKind);
    }

    // ── Delivery info ──

    [Fact]
    public void DeliveryInfoWrapsDeliveryTransitionAndUdpSnapshots()
    {
        var json = ToJson(TelemetryV0Endpoints.BuildDeliveryInfo());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("v0", root.GetProperty("api_version").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("delivery").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("transitions").ValueKind);

        var udp = root.GetProperty("udp_packets");
        Assert.Equal(JsonValueKind.Object, udp.ValueKind);
        // The block always carries the full outcome set + total + derived reject_rate.
        Assert.True(udp.TryGetProperty("received", out _));
        Assert.True(udp.TryGetProperty("invalid", out _));
        Assert.True(udp.TryGetProperty("unknown_session", out _));
        Assert.True(udp.TryGetProperty("send_error", out _));
        Assert.True(udp.TryGetProperty("total", out _));
        Assert.True(udp.TryGetProperty("reject_rate", out _));
    }

    // ── UDP packet-outcome block (D-06): reject/error rate, NOT network loss ──

    [Fact]
    public void UdpPacketsInfoComputesRejectRateOverAllReceivedPackets()
    {
        // received=90, rejected = invalid(4) + unknown_session(5) + send_error(1) = 10; total=100.
        var snapshot = new Dictionary<string, long>
        {
            ["received"] = 90,
            ["invalid"] = 4,
            ["unknown_session"] = 5,
            ["send_error"] = 1,
        };

        var json = ToJson(TelemetryV0Endpoints.BuildUdpPacketsInfo(snapshot));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(90, root.GetProperty("received").GetInt64());
        Assert.Equal(4, root.GetProperty("invalid").GetInt64());
        Assert.Equal(5, root.GetProperty("unknown_session").GetInt64());
        Assert.Equal(1, root.GetProperty("send_error").GetInt64());
        Assert.Equal(100, root.GetProperty("total").GetInt64());
        Assert.Equal(0.10, root.GetProperty("reject_rate").GetDouble(), 10);
    }

    [Fact]
    public void UdpPacketsInfoMissingOutcomesTreatedAsZero()
    {
        // Only 'received' has ever been recorded — every reject outcome absent from the dict.
        var snapshot = new Dictionary<string, long> { ["received"] = 7 };

        var json = ToJson(TelemetryV0Endpoints.BuildUdpPacketsInfo(snapshot));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(7, root.GetProperty("received").GetInt64());
        Assert.Equal(0, root.GetProperty("invalid").GetInt64());
        Assert.Equal(0, root.GetProperty("unknown_session").GetInt64());
        Assert.Equal(0, root.GetProperty("send_error").GetInt64());
        Assert.Equal(7, root.GetProperty("total").GetInt64());
        Assert.Equal(0.0, root.GetProperty("reject_rate").GetDouble(), 10);
    }

    [Fact]
    public void UdpPacketsInfoZeroPacketsYieldsZeroRejectRateNotNaN()
    {
        // Empty snapshot (no UDP traffic since startup) must guard divide-by-zero → 0.0, not NaN.
        var json = ToJson(TelemetryV0Endpoints.BuildUdpPacketsInfo(new Dictionary<string, long>()));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(0, root.GetProperty("total").GetInt64());
        var rejectRate = root.GetProperty("reject_rate").GetDouble();
        Assert.Equal(0.0, rejectRate);
        Assert.False(double.IsNaN(rejectRate));
    }

    [Fact]
    public void RecordUdpPacketFeedsSnapshotTally()
    {
        // Static, process-cumulative tally: assert on the DELTA so the test is robust to any
        // ordering/parallelism (no other unit test records UDP outcomes).
        long Before(string k) => LumberjacksTelemetry.SnapshotUdpPackets().TryGetValue(k, out var v) ? v : 0;

        var beforeReceived = Before("received");
        var beforeInvalid = Before("invalid");
        var beforeUnknown = Before("unknown_session");
        var beforeSendErr = Before("send_error");

        LumberjacksTelemetry.RecordUdpPacket("received");
        LumberjacksTelemetry.RecordUdpPacket("received");
        LumberjacksTelemetry.RecordUdpPacket("invalid");
        LumberjacksTelemetry.RecordUdpPacket("unknown_session");
        LumberjacksTelemetry.RecordUdpPacket("send_error");

        var after = LumberjacksTelemetry.SnapshotUdpPackets();
        Assert.Equal(beforeReceived + 2, after["received"]);
        Assert.Equal(beforeInvalid + 1, after["invalid"]);
        Assert.Equal(beforeUnknown + 1, after["unknown_session"]);
        Assert.Equal(beforeSendErr + 1, after["send_error"]);
    }

    // ── Regions info ──

    [Fact]
    public void RegionsInfoExposesOnlyStaticWorldFacts()
    {
        var world = new WorldState();
        var json = ToJson(TelemetryV0Endpoints.BuildRegionsInfo(world));

        using var doc = JsonDocument.Parse(json);
        var region = doc.RootElement.GetProperty("regions")[0];
        Assert.Equal("region-spawn", region.GetProperty("id").GetString());
        Assert.Equal("Spawn Island", region.GetProperty("name").GetString());
        Assert.True(region.TryGetProperty("player_count", out _));
        Assert.True(region.TryGetProperty("tick_rate", out _));

        var bounds = region.GetProperty("bounds");
        Assert.True(bounds.GetProperty("min").TryGetProperty("x", out _));
        Assert.True(bounds.GetProperty("max").TryGetProperty("x", out _));
    }

    // ── Privacy (hard rule): no player id, name, or position may EVER appear ──

    [Fact]
    public void NoV0ResponseLeaksConnectedPlayerIdentifiersOrPositions()
    {
        const string playerAId = "player-alpha-3f9c1e";
        const string playerAName = "SneakyLumberjackAlpha";
        const string playerBId = "player-bravo-7d2a08";
        const string playerBName = "SneakyLumberjackBravo";

        var world = WorldWithPlayers((playerAId, playerAName), (playerBId, playerBName));
        var config = new ConfigurationBuilder().Build();

        var responses = new object[]
        {
            TelemetryV0Endpoints.BuildServerInfo(world, config),
            TelemetryV0Endpoints.BuildTickInfo(metrics: null),
            TelemetryV0Endpoints.BuildDeliveryInfo(),
            TelemetryV0Endpoints.BuildRegionsInfo(world),
        };

        // Sentinel position coordinates, formatted the same way System.Text.Json would render
        // them, so a substring hit can only mean Position genuinely leaked into the response.
        var sentinelCoords = new[]
        {
            SentinelPosition.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SentinelPosition.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SentinelPosition.Z.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var response in responses)
        {
            var json = ToJson(response);

            Assert.DoesNotContain(playerAId, json);
            Assert.DoesNotContain(playerAName, json);
            Assert.DoesNotContain(playerBId, json);
            Assert.DoesNotContain(playerBName, json);
            foreach (var coord in sentinelCoords)
                Assert.DoesNotContain(coord, json);
        }
    }

    // ── Events feed (G4) ──

    private static readonly DateTimeOffset FixedNow =
        new(2026, 07, 13, 12, 00, 00, TimeSpan.Zero);

    private static CapturedEvent Ev(string id, string type, DateTimeOffset at, string? region = "region-spawn",
        string? detail = null, string provenance = "observed") =>
        new(id, type, at, region, detail, provenance);

    [Fact]
    public void EventsInfoCarriesEnvelopeAndFeedMetadata()
    {
        var snapshot = new FeedSnapshot(
            new[] { Ev("evt-000002", EventType.ItemPickedUp, FixedNow.AddMinutes(-5), detail: "wood") },
            Capacity: 200,
            DroppedSinceStart: 7);

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(snapshot, TimeSpan.Zero, FixedNow));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("v0", root.GetProperty("api_version").GetString());
        Assert.Equal("unstable", root.GetProperty("stability").GetString());
        Assert.Equal(200, root.GetProperty("capacity").GetInt32());
        Assert.Equal(7, root.GetProperty("dropped_since_start").GetInt32());
        Assert.Equal(0, root.GetProperty("delay_seconds").GetInt32());
        Assert.Equal(1, root.GetProperty("count").GetInt32());

        var evt = root.GetProperty("events")[0];
        Assert.Equal("evt-000002", evt.GetProperty("event_id").GetString());
        Assert.Equal(EventType.ItemPickedUp, evt.GetProperty("event_type").GetString());
        Assert.True(evt.TryGetProperty("occurred_at", out _));
        Assert.Equal("region-spawn", evt.GetProperty("region_id").GetString());
        Assert.Equal("wood", evt.GetProperty("detail").GetString());
        Assert.Equal("observed", evt.GetProperty("provenance").GetString());
    }

    [Fact]
    public void EventsInfoEmptyFeedReturnsEmptyArrayNotError()
    {
        var snapshot = new FeedSnapshot(Array.Empty<CapturedEvent>(), 200, 0);

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(snapshot, TimeSpan.Zero, FixedNow));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.GetProperty("events").ValueKind);
        Assert.Equal(0, root.GetProperty("events").GetArrayLength());
        Assert.Equal(0, root.GetProperty("count").GetInt32());
    }

    [Fact]
    public void EventsFeedSnapshotIsNewestFirstAndEndpointPreservesOrder()
    {
        GameplayEventFeed.Reset();
        GameplayEventFeed.Capture(EventType.StructurePlaced, "region-spawn", "wall", "observed", FixedNow.AddMinutes(-3));
        GameplayEventFeed.Capture(EventType.ItemPickedUp, "region-spawn", "stone", "observed", FixedNow.AddMinutes(-2));
        GameplayEventFeed.Capture(EventType.ItemStored, "region-spawn", "stone", "observed", FixedNow.AddMinutes(-1));

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(GameplayEventFeed.Snapshot(), TimeSpan.Zero, FixedNow));

        using var doc = JsonDocument.Parse(json);
        var events = doc.RootElement.GetProperty("events");
        Assert.Equal(3, events.GetArrayLength());
        // Newest capture first.
        Assert.Equal(EventType.ItemStored, events[0].GetProperty("event_type").GetString());
        Assert.Equal(EventType.ItemPickedUp, events[1].GetProperty("event_type").GetString());
        Assert.Equal(EventType.StructurePlaced, events[2].GetProperty("event_type").GetString());
        // Opaque monotonic event_id format.
        Assert.Matches(@"^evt-\d{6}$", events[0].GetProperty("event_id").GetString()!);
    }

    [Fact]
    public void EventsInfoExposureDelayHidesEventsNewerThanTheDelay()
    {
        // One event just 5s old (younger than the 30s delay → hidden), one 60s old (older → shown).
        var snapshot = new FeedSnapshot(
            new[]
            {
                Ev("evt-000002", EventType.ItemStored, FixedNow.AddSeconds(-5), detail: "iron"),   // newest first
                Ev("evt-000001", EventType.StructurePlaced, FixedNow.AddSeconds(-60), detail: "wall"),
            },
            200, 0);

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(snapshot, TimeSpan.FromSeconds(30), FixedNow));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(30, root.GetProperty("delay_seconds").GetInt32());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var events = root.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("evt-000001", events[0].GetProperty("event_id").GetString());
        // The too-recent event must not appear.
        Assert.DoesNotContain("evt-000002", json);
    }

    [Fact]
    public void CaptureRefusesExcludedIdentityEventTypes()
    {
        GameplayEventFeed.Reset();
        Assert.False(GameplayEventFeed.IsPublicEventType(EventType.PlayerConnected));

        // Excluded identity/social types are silently dropped by the capture layer.
        GameplayEventFeed.Capture(EventType.PlayerConnected, "region-spawn", "should-not-appear");
        GameplayEventFeed.Capture(EventType.PlayerDisconnected, "region-spawn", "should-not-appear");
        GameplayEventFeed.Capture(EventType.PlayerJoinedGuild, "region-spawn", "should-not-appear");
        // One allowed type does land. Backdated so the FixedNow cutoff in BuildEventsInfo sees it.
        GameplayEventFeed.Capture(EventType.StructurePlaced, "region-spawn", "wall", "observed", FixedNow.AddMinutes(-1));

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(GameplayEventFeed.Snapshot(), TimeSpan.Zero, FixedNow));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal(EventType.StructurePlaced, root.GetProperty("events")[0].GetProperty("event_type").GetString());
        Assert.DoesNotContain(EventType.PlayerConnected, json);
        Assert.DoesNotContain(EventType.PlayerDisconnected, json);
        Assert.DoesNotContain("should-not-appear", json);
    }

    [Fact]
    public void FeedRingDropsOldestPastCapacityAndCountsDrops()
    {
        GameplayEventFeed.Reset();
        const int overfill = GameplayEventFeed.Capacity + 5;
        for (var i = 0; i < overfill; i++)
            GameplayEventFeed.Capture(EventType.ItemPickedUp, "region-spawn", "wood", "observed", FixedNow.AddSeconds(-i));

        var snapshot = GameplayEventFeed.Snapshot();
        Assert.Equal(GameplayEventFeed.Capacity, snapshot.Events.Count);
        Assert.Equal(5, snapshot.DroppedSinceStart);

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(snapshot, TimeSpan.Zero, FixedNow));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(GameplayEventFeed.Capacity, root.GetProperty("count").GetInt32());
        Assert.Equal(GameplayEventFeed.Capacity, root.GetProperty("capacity").GetInt32());
        Assert.Equal(5, root.GetProperty("dropped_since_start").GetInt32());
    }

    [Fact]
    public void ResolveEventsDelayDefaultsTo30AndZeroDisables()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Equal(TimeSpan.FromSeconds(30), TelemetryV0Endpoints.ResolveEventsDelay(empty));

        var disabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:PublicEventsDelaySeconds"] = "0",
            }).Build();
        Assert.Equal(TimeSpan.Zero, TelemetryV0Endpoints.ResolveEventsDelay(disabled));

        var custom = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:PublicEventsDelaySeconds"] = "90",
            }).Build();
        Assert.Equal(TimeSpan.FromSeconds(90), TelemetryV0Endpoints.ResolveEventsDelay(custom));
    }

    // ── Privacy (hard rule) extended to the /events feed ──

    [Fact]
    public void EventsFeedResponseNeverLeaksPlayerIdentifiersOrPositions()
    {
        const string playerAId = "player-alpha-3f9c1e";
        const string playerAName = "SneakyLumberjackAlpha";
        const string playerBId = "player-bravo-7d2a08";
        const string playerBName = "SneakyLumberjackBravo";

        // A live world with sentinel-named players at a sentinel position exists alongside the feed.
        _ = WorldWithPlayers((playerAId, playerAName), (playerBId, playerBName));

        // Capture several events as the real producers would — non-identifying category detail only.
        GameplayEventFeed.Reset();
        GameplayEventFeed.Capture(EventType.StructurePlaced, "region-spawn", "wall", "observed", FixedNow.AddMinutes(-4));
        GameplayEventFeed.Capture(EventType.ItemPickedUp, "region-spawn", "wood", "observed", FixedNow.AddMinutes(-3));
        GameplayEventFeed.Capture(EventType.ItemStored, "region-spawn", "wood", "observed", FixedNow.AddMinutes(-2));
        GameplayEventFeed.Capture(EventType.InterestSubscriptionChanged, "region-spawn", "+2/-1", "observed", FixedNow.AddMinutes(-1));

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(GameplayEventFeed.Snapshot(), TimeSpan.Zero, FixedNow));

        var sentinelCoords = new[]
        {
            SentinelPosition.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SentinelPosition.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SentinelPosition.Z.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.DoesNotContain(playerAId, json);
        Assert.DoesNotContain(playerAName, json);
        Assert.DoesNotContain(playerBId, json);
        Assert.DoesNotContain(playerBName, json);
        foreach (var coord in sentinelCoords)
            Assert.DoesNotContain(coord, json);
    }

    // ── G4 producers added in v0.20.1: region_activated, region_deactivated, player_entered_region ──

    /// <summary>Stub IHttpClientFactory — the Join path's fire-and-forget EventLog POST fails silently.</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public void PlayerJoinCapturesPlayerEnteredRegionWithRegionIdOnly()
    {
        // Exercise the REAL producer seam: PlayerHandler.Join captures at the in-process emission point.
        GameplayEventFeed.Reset();
        var world = new WorldState(); // seeds region-spawn
        var handler = new PlayerHandler(
            world,
            new StubHttpClientFactory(),
            new ConfigurationBuilder().Build(),
            NullLogger<PlayerHandler>.Instance);

        const string sentinelPlayerId = "player-zeta-9c3f1a";
        var result = handler.Join(new JoinRequest { PlayerId = sentinelPlayerId, RegionId = "region-spawn" });
        Assert.True(result.Success);

        var snapshot = GameplayEventFeed.Snapshot();
        var entered = Assert.Single(snapshot.Events, e => e.EventType == EventType.PlayerEnteredRegion);
        Assert.Equal("region-spawn", entered.RegionId);
        Assert.Null(entered.Detail);              // region id ONLY — never the player or spawn position
        Assert.Equal("observed", entered.Provenance);

        // Privacy: the joining player's id/name/position must not leak into the feed response.
        // Real UtcNow here (not FixedNow): Join stamps the capture with the live clock, and the
        // event must actually serialize for these DoesNotContain checks to be non-vacuous.
        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(snapshot, TimeSpan.Zero, DateTimeOffset.UtcNow));
        Assert.Contains(EventType.PlayerEnteredRegion, json); // event really serialized — leak checks below are non-vacuous
        Assert.DoesNotContain(sentinelPlayerId, json);
        Assert.DoesNotContain(world.Players[sentinelPlayerId].Name, json);
    }

    [Fact]
    public void RegionActivatedProducerCapturesRegionIdAndNullDetail()
    {
        // Mirrors the RegionEndpoints POST /regions seam: region id only, no actor, no detail.
        GameplayEventFeed.Reset();
        GameplayEventFeed.Capture(EventType.RegionActivated, regionId: "region-north", detail: null, provenance: "observed", occurredAt: FixedNow.AddMinutes(-1));

        var snapshot = GameplayEventFeed.Snapshot();
        var evt = Assert.Single(snapshot.Events, e => e.EventType == EventType.RegionActivated);
        Assert.Equal("region-north", evt.RegionId);
        Assert.Null(evt.Detail);
        Assert.Equal("observed", evt.Provenance);

        var json = ToJson(TelemetryV0Endpoints.BuildEventsInfo(snapshot, TimeSpan.Zero, FixedNow));
        Assert.Equal(EventType.RegionActivated, JsonDocument.Parse(json).RootElement
            .GetProperty("events")[0].GetProperty("event_type").GetString());
    }

    [Fact]
    public void RegionDeactivatedProducerCapturesRegionIdAndNullDetail()
    {
        // Mirrors the RegionEndpoints DELETE /regions/{id} seam: region id only, no actor, no detail.
        GameplayEventFeed.Reset();
        GameplayEventFeed.Capture(EventType.RegionDeactivated, regionId: "region-north", detail: null, provenance: "observed");

        var snapshot = GameplayEventFeed.Snapshot();
        var evt = Assert.Single(snapshot.Events, e => e.EventType == EventType.RegionDeactivated);
        Assert.Equal("region-north", evt.RegionId);
        Assert.Null(evt.Detail);
        Assert.Equal("observed", evt.Provenance);
    }
}

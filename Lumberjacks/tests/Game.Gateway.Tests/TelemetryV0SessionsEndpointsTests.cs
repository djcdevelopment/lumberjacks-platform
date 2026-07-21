using System.Net.WebSockets;
using System.Text.Json;
using Game.Gateway.Endpoints;
using Game.Gateway.WebSocket;
using Xunit;

namespace Game.Gateway.Tests;

/// <summary>
/// Public Telemetry API v0 (community-telemetry-strategy.md Phase 3, G1) — the sessions
/// aggregate endpoint. Lives in Game.Gateway.Tests (not Game.Simulation.Tests) because it needs
/// the Gateway-only <see cref="SessionManager"/>; see
/// tests/Game.Simulation.Tests/TelemetryV0EndpointsTests.cs for the other four v0 endpoints.
///
/// Hard privacy rule: AGGREGATES ONLY. <see cref="GameSession"/> carries SessionId/PlayerId —
/// this suite asserts neither the real session/player ids minted by <see cref="SessionManager"/>
/// nor the resume token ever appear in the response.
/// </summary>
public sealed class TelemetryV0SessionsEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Minimal concrete WebSocket — never actually used for I/O, just needs to exist
    /// so SessionManager.Create() has something to hang a GameSession off of.</summary>
    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static string ToJson(object response) => JsonSerializer.Serialize(response, JsonOptions);

    [Fact]
    public void AggregatesTotalsByProtocolAndByRegion()
    {
        var sessions = new SessionManager();

        var a = sessions.Create(new FakeWebSocket());
        a.RegionId = "region-spawn";
        a.Protocol = ProtocolMode.Json;

        var b = sessions.Create(new FakeWebSocket());
        b.RegionId = "region-spawn";
        b.Protocol = ProtocolMode.Binary;

        var c = sessions.Create(new FakeWebSocket());
        c.Protocol = ProtocolMode.Json; // RegionId left unset (null) → "unassigned" bucket

        var json = ToJson(TelemetryV0SessionsEndpoints.BuildSessionsInfo(sessions));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("v0", root.GetProperty("api_version").GetString());
        Assert.Equal("unstable", root.GetProperty("stability").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());

        var byProtocol = root.GetProperty("by_protocol");
        Assert.Equal(2, byProtocol.GetProperty("json").GetInt64());
        Assert.Equal(1, byProtocol.GetProperty("binary").GetInt64());

        var byRegion = root.GetProperty("by_region");
        Assert.Equal(2, byRegion.GetProperty("region-spawn").GetInt64());
        Assert.Equal(1, byRegion.GetProperty("unassigned").GetInt64());
    }

    [Fact]
    public void NeverLeaksSessionOrPlayerIdentifiers()
    {
        var sessions = new SessionManager();

        var a = sessions.Create(new FakeWebSocket());
        a.RegionId = "region-spawn";
        var b = sessions.Create(new FakeWebSocket());
        b.RegionId = "region-forest";

        var json = ToJson(TelemetryV0SessionsEndpoints.BuildSessionsInfo(sessions));

        foreach (var session in new[] { a, b })
        {
            Assert.DoesNotContain(session.SessionId, json);
            Assert.DoesNotContain(session.PlayerId, json);
            Assert.DoesNotContain(session.ResumeToken, json);
        }
    }

    [Fact]
    public void EmptySessionManagerProducesZeroedAggregatesNotAnError()
    {
        var sessions = new SessionManager();

        var json = ToJson(TelemetryV0SessionsEndpoints.BuildSessionsInfo(sessions));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
    }
}

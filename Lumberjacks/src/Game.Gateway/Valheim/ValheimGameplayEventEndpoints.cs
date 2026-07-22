using System.Text.Json;
using Game.Contracts.Events;
using Game.ServiceDefaults;

namespace Game.Gateway.Valheim;

/// <summary>
/// Mod → gateway ingress for first-class gameplay events (first_hit, killing_blow, weapon_used,
/// quest_completed), produced by the ComfyNetworkSense <c>GameplayEventProducer</c>. Mirrors the
/// in-process emission seam <see cref="TickBroadcaster"/> already uses, but sourced from the mod
/// instead of the simulation: capture the public-safe projection into <see cref="GameplayEventFeed"/>
/// (drives <c>GET /api/v0/telemetry/events</c>), then fire-and-forget the FULL identifying event
/// to the out-of-process EventLog. Gated by <c>ValheimClientAccessMiddleware</c> as
/// <see cref="ValheimCapability.Producer"/> — only the server-side mod on the private Docker
/// plane may post (a public enrolled client cannot spoof gameplay events). Capture runs CLIENT-side
/// (the client owns the creature it fights — ADR 0012) and the event is relayed to the server over a
/// routed RPC; the server-side handler is the sole peer on the private plane, so it does the POST.
/// </summary>
public static class ValheimGameplayEventEndpoints
{
    // Only these mod-produced gameplay types are accepted here. Keeps the ingress from being a
    // general-purpose event injector — the mod cannot post arbitrary EventTypes into the feed.
    private static readonly HashSet<string> AcceptedTypes = new(StringComparer.Ordinal)
    {
        EventType.FirstHit,
        EventType.KillingBlow,
        EventType.WeaponUsed,
        EventType.QuestCompleted,
        EventType.TransportControlChanged,
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/valheim/events", (
            ValheimGameplayEventRequest body,
            IHttpClientFactory httpFactory,
            IConfiguration config) =>
        {
            // No endpoint-level key check: the route is Producer-gated in ValheimAccessPolicy, which
            // only the server-side mod on the private plane can satisfy. The shared telemetry key the
            // heartbeat uses is redundant here (and the producer posts none), so it is not required.
            if (string.IsNullOrWhiteSpace(body.EventType) || !AcceptedTypes.Contains(body.EventType))
            {
                return Results.BadRequest(new
                {
                    error = "event_type is required and must be one of: first_hit, killing_blow, weapon_used, quest_completed, transport_control_changed",
                });
            }

            var occurredAt = ParseTimestamp(body.OccurredAtUtc);
            var regionId = string.IsNullOrWhiteSpace(body.RegionId) ? null : body.RegionId;
            var detail = string.IsNullOrWhiteSpace(body.Detail) ? null : body.Detail;

            // In-process public-safe projection FIRST (identical ordering to TickBroadcaster):
            // carries only the non-identifying detail — never the acting player's id/name/position.
            GameplayEventFeed.Capture(body.EventType, regionId, detail, GameplayEventFeed.DefaultProvenance, occurredAt);

            // Then fire-and-forget the full identifying event to the durable EventLog. Best-effort:
            // the public feed (and the dashboard) do not depend on this succeeding.
            var eventLogUrl = (config["ServiceUrls:EventLog"] ?? "http://localhost:4002").TrimEnd('/');
            var actorId = string.IsNullOrWhiteSpace(body.ActorId) ? null : body.ActorId;
            var worldId = string.IsNullOrWhiteSpace(body.WorldId) ? "world-default" : body.WorldId;
            var eventType = body.EventType;
            var payload = body.Payload;

            _ = Task.Run(async () =>
            {
                try
                {
                    var client = httpFactory.CreateClient();
                    await client.PostAsJsonAsync($"{eventLogUrl}/events", new
                    {
                        event_id = Guid.NewGuid().ToString(),
                        event_type = eventType,
                        occurred_at = occurredAt,
                        world_id = worldId,
                        region_id = regionId,
                        actor_id = actorId,
                        source_service = "valheim-mod",
                        schema_version = 1,
                        payload,
                    });
                }
                catch
                {
                    // Durable-store write is best-effort; the live feed already has the projection.
                }
            });

            return Results.Ok(new { ok = true, received_at = DateTimeOffset.UtcNow });
        }).RequireRateLimiting("telemetry");
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}

/// <summary>
/// Mod-posted gameplay event. Bound from snake_case JSON (global SnakeCaseLower policy).
/// <c>ActorId</c> rides through to the durable EventLog only — the public feed projection never
/// carries it. <c>Detail</c> must be a non-identifying category (creature category, weapon skill).
/// </summary>
public sealed record ValheimGameplayEventRequest
{
    public string? EventType { get; init; }
    public string? OccurredAtUtc { get; init; }
    public string? WorldId { get; init; }
    public string? RegionId { get; init; }
    public string? ActorId { get; init; }
    public string? Detail { get; init; }
    public JsonElement Payload { get; init; }
}

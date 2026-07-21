using System.Text.Json;
using Game.Contracts.Events;
using Game.Persistence;
using Game.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Game.EventLog.Endpoints;

public static class EventEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/events", async (GameEvent gameEvent, GameDbContext db) =>
        {
            if (string.IsNullOrEmpty(gameEvent.EventId) || string.IsNullOrEmpty(gameEvent.EventType))
                return Results.BadRequest(new { error = "Validation failed", details = "event_id and event_type are required" });

            var entity = new EventEntity
            {
                EventId = gameEvent.EventId,
                EventType = gameEvent.EventType,
                OccurredAt = gameEvent.OccurredAt,
                WorldId = gameEvent.WorldId,
                RegionId = gameEvent.RegionId,
                ActorId = gameEvent.ActorId,
                GuildId = gameEvent.GuildId,
                SourceService = gameEvent.SourceService,
                SchemaVersion = gameEvent.SchemaVersion,
                Payload = gameEvent.Payload.ValueKind != JsonValueKind.Undefined
                    ? gameEvent.Payload.GetRawText()
                    : "{}",
            };

            db.Events.Add(entity);
            await db.SaveChangesAsync();

            return Results.Created($"/events/{entity.EventId}", new { event_id = entity.EventId });
        });

        app.MapGet("/events", async (
            string? type,
            string? actor_id,
            string? region_id,
            int? limit,
            int? offset,
            GameDbContext db) =>
        {
            var query = db.Events.AsQueryable();

            if (!string.IsNullOrEmpty(type))
                query = query.Where(e => e.EventType == type);
            if (!string.IsNullOrEmpty(actor_id))
                query = query.Where(e => e.ActorId == actor_id);
            if (!string.IsNullOrEmpty(region_id))
                query = query.Where(e => e.RegionId == region_id);

            var total = await query.CountAsync();
            var events = await query
                .OrderByDescending(e => e.OccurredAt)
                .Skip(offset ?? 0)
                .Take(limit ?? 50)
                .Select(e => new
                {
                    event_id = e.EventId,
                    event_type = e.EventType,
                    occurred_at = e.OccurredAt,
                    world_id = e.WorldId,
                    region_id = e.RegionId,
                    actor_id = e.ActorId,
                    guild_id = e.GuildId,
                    source_service = e.SourceService,
                    schema_version = e.SchemaVersion,
                    payload = e.Payload,
                })
                .ToListAsync();

            return Results.Ok(new { events, count = total });
        });
    }
}

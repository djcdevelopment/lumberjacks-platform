using Game.Contracts.Achievements;
using Game.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Game.EventLog.Endpoints;

/// <summary>
/// Exposes the community achievement projection. Achievements are a projection
/// over the authoritative durable event log — never manually-asserted badges —
/// so this endpoint owns the read because the event data lives here.
/// </summary>
public static class AchievementEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/achievements", async (GameDbContext db) =>
        {
            // Pull the minimal, payload-free projection of the event stream.
            var events = await db.Events
                .OrderBy(e => e.OccurredAt)
                .Select(e => new AchievementEvent(
                    e.EventId,
                    e.EventType,
                    e.OccurredAt,
                    e.ActorId,
                    e.GuildId))
                .ToListAsync();

            var now = DateTimeOffset.UtcNow;
            var achievements = AchievementCatalog.Evaluate(events, now);

            return Results.Ok(new
            {
                achievements = achievements.Select(a => new
                {
                    id = a.Id,
                    title = a.Title,
                    description = a.Description,
                    provenance = a.Provenance.ToWireString(),
                    scope = a.Scope.ToWireString(),
                    unlocked = a.Unlocked,
                    unlocked_at = a.UnlockedAt,
                    evidence = a.Evidence.Select(ev => new
                    {
                        event_id = ev.EventId,
                        event_type = ev.EventType,
                        occurred_at = ev.OccurredAt,
                    }),
                }),
                unlocked_count = achievements.Count(a => a.Unlocked),
                count = achievements.Count,
                generated_at = now,
            });
        });
    }
}

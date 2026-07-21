using System.Text.Json;
using Game.Persistence;
using Game.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Game.Progression.Endpoints;

public static class ChallengeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/challenges", async (CreateChallengeRequest request, GameDbContext db) =>
        {
            var challenge = new ChallengeEntity
            {
                Id = Guid.NewGuid().ToString(),
                Kind = request.Kind,
                Name = request.Name,
                TriggerEvent = request.TriggerEvent,
                TriggerFilters = request.TriggerFilters ?? "{}",
                ProgressMode = request.ProgressMode ?? "sum",
                Target = request.Target,
                WindowStart = request.WindowStart,
                WindowEnd = request.WindowEnd,
                Rewards = request.Rewards ?? "[]",
            };

            db.Challenges.Add(challenge);
            await db.SaveChangesAsync();

            return Results.Created($"/challenges/{challenge.Id}", new
            {
                challenge_id = challenge.Id,
                kind = challenge.Kind,
                name = challenge.Name,
                trigger_event = challenge.TriggerEvent,
                trigger_filters = challenge.TriggerFilters,
                progress_mode = challenge.ProgressMode,
                target = challenge.Target,
                window_start = challenge.WindowStart,
                window_end = challenge.WindowEnd,
                rewards = challenge.Rewards,
                active = challenge.Active,
            });
        });

        app.MapGet("/challenges", async (bool? active, GameDbContext db) =>
        {
            var query = db.Challenges.AsQueryable();
            if (active.HasValue)
                query = query.Where(c => c.Active == active.Value);

            var challenges = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
            return Results.Ok(challenges.Select(c => new
            {
                challenge_id = c.Id,
                kind = c.Kind,
                name = c.Name,
                trigger_event = c.TriggerEvent,
                progress_mode = c.ProgressMode,
                target = c.Target,
                window_start = c.WindowStart,
                window_end = c.WindowEnd,
                rewards = c.Rewards,
                active = c.Active,
                created_at = c.CreatedAt,
            }));
        });

        app.MapGet("/challenges/{id}", async (string id, GameDbContext db) =>
        {
            var challenge = await db.Challenges.FindAsync(id);
            if (challenge is null)
                return Results.NotFound(new { error = "Challenge not found" });

            return Results.Ok(new
            {
                challenge_id = challenge.Id,
                kind = challenge.Kind,
                name = challenge.Name,
                trigger_event = challenge.TriggerEvent,
                trigger_filters = challenge.TriggerFilters,
                progress_mode = challenge.ProgressMode,
                target = challenge.Target,
                window_start = challenge.WindowStart,
                window_end = challenge.WindowEnd,
                rewards = challenge.Rewards,
                active = challenge.Active,
                created_at = challenge.CreatedAt,
            });
        });

        app.MapGet("/challenges/{id}/progress", async (string id, GameDbContext db) =>
        {
            var challenge = await db.Challenges.FindAsync(id);
            if (challenge is null)
                return Results.NotFound(new { error = "Challenge not found" });

            var progress = await db.ChallengeProgress
                .Where(p => p.ChallengeId == id)
                .OrderByDescending(p => p.CurrentValue)
                .ToListAsync();

            return Results.Ok(new
            {
                challenge_id = id,
                challenge_name = challenge.Name,
                target = challenge.Target,
                guilds = progress.Select(p => new
                {
                    guild_id = p.GuildId,
                    current_value = p.CurrentValue,
                    completed = p.Completed,
                    completed_at = p.CompletedAt,
                    updated_at = p.UpdatedAt,
                }),
            });
        });

        app.MapPatch("/challenges/{id}", async (string id, UpdateChallengeRequest request, GameDbContext db) =>
        {
            var challenge = await db.Challenges.FindAsync(id);
            if (challenge is null)
                return Results.NotFound(new { error = "Challenge not found" });

            if (request.Active.HasValue)
                challenge.Active = request.Active.Value;

            if (request.WindowEnd.HasValue)
                challenge.WindowEnd = request.WindowEnd.Value;

            await db.SaveChangesAsync();
            return Results.Ok(new { updated = true, challenge_id = id });
        });
    }
}

public record CreateChallengeRequest
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string TriggerEvent { get; init; }
    public string? TriggerFilters { get; init; }
    public string? ProgressMode { get; init; }
    public required int Target { get; init; }
    public DateTimeOffset? WindowStart { get; init; }
    public DateTimeOffset? WindowEnd { get; init; }
    public string? Rewards { get; init; }
}

public record UpdateChallengeRequest
{
    public bool? Active { get; init; }
    public DateTimeOffset? WindowEnd { get; init; }
}

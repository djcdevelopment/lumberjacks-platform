using System.Text.Json;
using Game.Persistence;
using Game.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Game.Progression;

public class ChallengeEngine
{
    private readonly GameDbContext _db;
    private readonly ILogger<ChallengeEngine> _logger;

    public ChallengeEngine(GameDbContext db, ILogger<ChallengeEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<ChallengeCompletionResult>> EvaluateAsync(
        string eventType, string? guildId, JsonElement payload, int incrementValue = 1)
    {
        var results = new List<ChallengeCompletionResult>();
        if (string.IsNullOrEmpty(guildId)) return results;

        var now = DateTimeOffset.UtcNow;

        // Find active challenges that trigger on this event type
        var challenges = await _db.Challenges
            .Where(c => c.Active && c.TriggerEvent == eventType)
            .Where(c => c.WindowStart == null || c.WindowStart <= now)
            .Where(c => c.WindowEnd == null || c.WindowEnd >= now)
            .ToListAsync();

        foreach (var challenge in challenges)
        {
            if (!MatchesFilters(challenge.TriggerFilters, payload))
                continue;

            // Atomic upsert + increment via raw SQL to avoid lost updates
            var newValue = await AtomicIncrementAsync(
                challenge.Id, guildId, challenge.ProgressMode, incrementValue);

            if (newValue is null)
                continue; // already completed, no-op

            // Check completion
            if (newValue.Value >= challenge.Target)
            {
                // Mark completed atomically (only first to reach target wins)
                var completed = await TryMarkCompletedAsync(challenge.Id, guildId);
                if (!completed)
                    continue; // another request already completed it

                // Award guild points from rewards
                var rewardPoints = ParseRewardPoints(challenge.Rewards);
                if (rewardPoints > 0)
                {
                    await AwardGuildPointsAsync(guildId, rewardPoints);
                }

                results.Add(new ChallengeCompletionResult(
                    challenge.Id, challenge.Name, guildId, rewardPoints));

                _logger.LogInformation(
                    "Challenge '{Name}' completed by guild {GuildId} — awarded {Points} points",
                    challenge.Name, guildId, rewardPoints);
            }
        }

        return results;
    }

    /// <summary>
    /// Atomically inserts or increments the progress row.
    /// Returns the new current_value, or null if already completed.
    /// </summary>
    private async Task<int?> AtomicIncrementAsync(
        string challengeId, string guildId, string progressMode, int incrementValue)
    {
        // Atomic upsert via Postgres ON CONFLICT
        if (progressMode == "max")
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO challenge_progress (challenge_id, guild_id, current_value, completed, updated_at)
                VALUES ({challengeId}, {guildId}, {incrementValue}, false, now())
                ON CONFLICT (challenge_id, guild_id)
                DO UPDATE SET
                    current_value = GREATEST(challenge_progress.current_value, EXCLUDED.current_value),
                    updated_at = now()
                WHERE NOT challenge_progress.completed");
        }
        else
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO challenge_progress (challenge_id, guild_id, current_value, completed, updated_at)
                VALUES ({challengeId}, {guildId}, {incrementValue}, false, now())
                ON CONFLICT (challenge_id, guild_id)
                DO UPDATE SET
                    current_value = challenge_progress.current_value + EXCLUDED.current_value,
                    updated_at = now()
                WHERE NOT challenge_progress.completed");
        }

        // Read back the current value (same connection, sees our write)
        var row = await _db.ChallengeProgress
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ChallengeId == challengeId && p.GuildId == guildId);

        if (row is null || row.Completed) return null;
        return row.CurrentValue;
    }

    /// <summary>
    /// Atomically marks the challenge as completed. Returns true only for the
    /// first caller that transitions completed from false to true.
    /// </summary>
    private async Task<bool> TryMarkCompletedAsync(string challengeId, string guildId)
    {
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE challenge_progress
            SET completed = true, completed_at = now()
            WHERE challenge_id = {challengeId}
              AND guild_id = {guildId}
              AND NOT completed");
        return rows > 0;
    }

    /// <summary>
    /// Atomically upserts guild progress and adds points.
    /// </summary>
    private async Task AwardGuildPointsAsync(string guildId, int points)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO guild_progress (guild_id, points, challenges_completed, updated_at)
            VALUES ({guildId}, {points}, 1, now())
            ON CONFLICT (guild_id)
            DO UPDATE SET
                points = guild_progress.points + {points},
                challenges_completed = guild_progress.challenges_completed + 1,
                updated_at = now()");
    }

    private static bool MatchesFilters(string filtersJson, JsonElement payload)
    {
        if (filtersJson is "{}" or "") return true;

        try
        {
            using var doc = JsonDocument.Parse(filtersJson);
            foreach (var filter in doc.RootElement.EnumerateObject())
            {
                if (!payload.TryGetProperty(filter.Name, out var val))
                    return false;

                if (val.ToString() != filter.Value.ToString())
                    return false;
            }
            return true;
        }
        catch
        {
            return true; // If filters are malformed, don't block progress
        }
    }

    private static int ParseRewardPoints(string rewardsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rewardsJson);
            foreach (var reward in doc.RootElement.EnumerateArray())
            {
                if (reward.TryGetProperty("type", out var typeEl) &&
                    typeEl.GetString() == "guild_points" &&
                    reward.TryGetProperty("amount", out var amountEl))
                {
                    return amountEl.GetInt32();
                }
            }
        }
        catch { }
        return 0;
    }
}

public record ChallengeCompletionResult(
    string ChallengeId, string ChallengeName, string GuildId, int PointsAwarded);

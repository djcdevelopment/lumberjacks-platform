using Game.Contracts.Achievements;
using Game.Contracts.Events;
using Xunit;

namespace Game.Contracts.Tests;

public class AchievementCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 11, 12, 0, 0, TimeSpan.Zero);

    private static AchievementEvent Ev(
        string type, string id, DateTimeOffset at, string? actor = null, string? guild = null) =>
        new(id, type, at, actor, guild);

    private static Achievement Get(IReadOnlyList<Achievement> all, string id) =>
        all.Single(a => a.Id == id);

    [Fact]
    public void Empty_event_stream_leaves_everything_locked()
    {
        var result = AchievementCatalog.Evaluate([], Now);

        Assert.NotEmpty(result);
        Assert.All(result, a =>
        {
            Assert.False(a.Unlocked);
            Assert.Null(a.UnlockedAt);
            Assert.Empty(a.Evidence);
        });
    }

    [Fact]
    public void Catalog_only_auto_computes_observed_and_reconstructed_tiers()
    {
        var result = AchievementCatalog.Evaluate([], Now);

        // Verified and Community-awarded exist as valid tiers but must never be
        // produced automatically (dashboard §04).
        Assert.All(result, a =>
            Assert.True(
                a.Provenance is ProvenanceTier.Observed or ProvenanceTier.Reconstructed,
                $"{a.Id} used non-auto tier {a.Provenance}"));
        Assert.Contains(result, a => a.Provenance == ProvenanceTier.Reconstructed);
    }

    [Fact]
    public void Single_structure_placed_unlocks_first_foundation_observed_with_evidence()
    {
        var placed = Ev(EventType.StructurePlaced, "evt-struct-1", Now.AddHours(-2), actor: "player-a");

        var result = AchievementCatalog.Evaluate([placed], Now);
        var achievement = Get(result, "first_foundation");

        Assert.True(achievement.Unlocked);
        Assert.Equal(ProvenanceTier.Observed, achievement.Provenance);
        Assert.Equal(AchievementScope.Community, achievement.Scope);
        Assert.Equal(placed.OccurredAt, achievement.UnlockedAt);

        var evidence = Assert.Single(achievement.Evidence);
        Assert.Equal("evt-struct-1", evidence.EventId);
        Assert.Equal(EventType.StructurePlaced, evidence.EventType);
        Assert.Equal(placed.OccurredAt, evidence.OccurredAt);

        // A single placement is not enough for the count-based milestone.
        Assert.False(Get(result, "community_builder").Unlocked);
    }

    [Fact]
    public void First_foundation_evidence_is_the_earliest_matching_event()
    {
        var later = Ev(EventType.StructurePlaced, "evt-late", Now.AddHours(-1));
        var earlier = Ev(EventType.StructurePlaced, "evt-early", Now.AddHours(-5));

        // Deliberately out of order to prove the rule picks the earliest.
        var result = AchievementCatalog.Evaluate([later, earlier], Now);
        var achievement = Get(result, "first_foundation");

        Assert.Equal("evt-early", Assert.Single(achievement.Evidence).EventId);
        Assert.Equal(earlier.OccurredAt, achievement.UnlockedAt);
    }

    [Fact]
    public void Community_builder_unlocks_at_the_threshold_event()
    {
        var events = Enumerable.Range(0, AchievementCatalog.CommunityBuilderThreshold)
            .Select(i => Ev(EventType.StructurePlaced, $"evt-{i}", Now.AddHours(-100 + i)))
            .ToList();

        var result = AchievementCatalog.Evaluate(events, Now);
        var achievement = Get(result, "community_builder");

        Assert.True(achievement.Unlocked);
        Assert.Equal(ProvenanceTier.Observed, achievement.Provenance);
        var last = events[^1];
        Assert.Equal(last.OccurredAt, achievement.UnlockedAt);
        // Evidence spans first + threshold-crossing event.
        Assert.Equal(2, achievement.Evidence.Count);
        Assert.Equal("evt-0", achievement.Evidence[0].EventId);
        Assert.Equal(last.EventId, achievement.Evidence[1].EventId);
    }

    [Fact]
    public void Founding_members_counts_distinct_actors_only()
    {
        // 4 events but only 3 distinct actors — below the threshold of 5.
        var events = new[]
        {
            Ev(EventType.PlayerConnected, "c1", Now.AddHours(-9), actor: "a"),
            Ev(EventType.PlayerConnected, "c2", Now.AddHours(-8), actor: "a"),
            Ev(EventType.PlayerConnected, "c3", Now.AddHours(-7), actor: "b"),
            Ev(EventType.PlayerConnected, "c4", Now.AddHours(-6), actor: "c"),
        };

        Assert.False(Get(AchievementCatalog.Evaluate(events, Now), "founding_members").Unlocked);

        var withFive = events.Concat(new[]
        {
            Ev(EventType.PlayerConnected, "c5", Now.AddHours(-5), actor: "d"),
            Ev(EventType.PlayerConnected, "c6", Now.AddHours(-4), actor: "e"),
        }).ToList();

        var achievement = Get(AchievementCatalog.Evaluate(withFive, Now), "founding_members");
        Assert.True(achievement.Unlocked);
        Assert.Equal(AchievementCatalog.FoundingMembersThreshold, achievement.Evidence.Count);
    }

    [Fact]
    public void World_age_is_reconstructed_and_keys_off_earliest_event()
    {
        var old = Ev(EventType.PlayerConnected, "old", Now.AddDays(-10), actor: "a");
        var recent = Ev(EventType.StructurePlaced, "recent", Now.AddHours(-1));

        var achievement = Get(AchievementCatalog.Evaluate([recent, old], Now), "world_age_7_days");

        Assert.True(achievement.Unlocked);
        Assert.Equal(ProvenanceTier.Reconstructed, achievement.Provenance);
        Assert.Equal("old", Assert.Single(achievement.Evidence).EventId);
        Assert.Equal(old.OccurredAt + AchievementCatalog.WorldAgeThreshold, achievement.UnlockedAt);
    }

    [Fact]
    public void World_age_stays_locked_for_a_young_world()
    {
        var recent = Ev(EventType.StructurePlaced, "recent", Now.AddDays(-2));

        var achievement = Get(AchievementCatalog.Evaluate([recent], Now), "world_age_7_days");
        Assert.False(achievement.Unlocked);
    }

    [Fact]
    public void Guild_rank_and_challenge_first_events_unlock_their_observed_claims()
    {
        var events = new[]
        {
            Ev(EventType.GuildObjectiveCompleted, "g1", Now.AddHours(-3), guild: "guild-x"),
            Ev(EventType.PlayerRankChanged, "r1", Now.AddHours(-2), actor: "p1"),
            Ev(EventType.ChallengeCompleted, "ch1", Now.AddHours(-1), guild: "guild-x"),
        };

        var result = AchievementCatalog.Evaluate(events, Now);

        var guild = Get(result, "guild_milestone");
        Assert.True(guild.Unlocked);
        Assert.Equal(AchievementScope.Guild, guild.Scope);

        var ranks = Get(result, "rising_ranks");
        Assert.True(ranks.Unlocked);
        Assert.Equal(AchievementScope.Player, ranks.Scope);

        Assert.True(Get(result, "challenge_champions").Unlocked);
    }
}

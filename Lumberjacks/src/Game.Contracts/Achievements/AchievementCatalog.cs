using Game.Contracts.Events;

namespace Game.Contracts.Achievements;

/// <summary>
/// The static catalog of achievement definitions and the pure projection that
/// evaluates them against an event stream.
///
/// Each definition is a rule over authoritative events (dashboard §02, §04):
/// the projection maps a list of <see cref="AchievementEvent"/> to a list of
/// <see cref="Achievement"/>, tagging every unlocked claim with the triggering
/// evidence and its provenance tier. Rules are pure and side-effect free, so
/// the same input always yields the same output.
///
/// Only Observed and Reconstructed tiers unlock automatically. Verified and
/// Community-awarded are valid tiers in the type system but are never produced
/// here — they are left as extension points (see <see cref="ProvenanceTier"/>).
/// </summary>
public static class AchievementCatalog
{
    /// <summary>Count threshold for the "community builder" milestone.</summary>
    public const int CommunityBuilderThreshold = 25;

    /// <summary>Distinct connected actors needed for "founding members".</summary>
    public const int FoundingMembersThreshold = 5;

    /// <summary>Age of the oldest event that unlocks the reconstructed world-age claim.</summary>
    public static readonly TimeSpan WorldAgeThreshold = TimeSpan.FromDays(7);

    private sealed record Definition(
        string Id,
        string Title,
        string Description,
        ProvenanceTier Provenance,
        AchievementScope Scope,
        Func<IReadOnlyList<AchievementEvent>, DateTimeOffset, Unlock?> Evaluate);

    /// <summary>Result of a satisfied rule: when it unlocked and the backing evidence.</summary>
    private sealed record Unlock(DateTimeOffset UnlockedAt, IReadOnlyList<AchievementEvidence> Evidence);

    private static readonly IReadOnlyList<Definition> Definitions =
    [
        new Definition(
            "first_foundation",
            "First Foundation",
            "The first structure was placed in the world.",
            ProvenanceTier.Observed,
            AchievementScope.Community,
            (events, _) => FirstOf(events, EventType.StructurePlaced)),

        new Definition(
            "community_builder",
            "Community Builder",
            $"The community has placed at least {CommunityBuilderThreshold} structures together.",
            ProvenanceTier.Observed,
            AchievementScope.Community,
            (events, _) => NthOf(events, EventType.StructurePlaced, CommunityBuilderThreshold)),

        new Definition(
            "challenge_champions",
            "Challenge Champions",
            "The community completed its first challenge.",
            ProvenanceTier.Observed,
            AchievementScope.Community,
            (events, _) => FirstOf(events, EventType.ChallengeCompleted)),

        new Definition(
            "guild_milestone",
            "Guild Milestone",
            "A guild completed its first shared objective.",
            ProvenanceTier.Observed,
            AchievementScope.Guild,
            (events, _) => FirstOf(events, EventType.GuildObjectiveCompleted)),

        new Definition(
            "rising_ranks",
            "Rising Ranks",
            "A player advanced to a new rank.",
            ProvenanceTier.Observed,
            AchievementScope.Player,
            (events, _) => FirstOf(events, EventType.PlayerRankChanged)),

        new Definition(
            "founding_members",
            "Founding Members",
            $"At least {FoundingMembersThreshold} distinct players have connected to the world.",
            ProvenanceTier.Observed,
            AchievementScope.Community,
            (events, _) => DistinctActors(events, EventType.PlayerConnected, FoundingMembersThreshold)),

        new Definition(
            "landmark_explorers",
            "Landmark Explorers",
            "A player discovered the world's first landmark.",
            ProvenanceTier.Observed,
            AchievementScope.Community,
            (events, _) => FirstOf(events, EventType.PlayerDiscoveredLandmark)),

        // Reconstructed: inferred from an aggregate (the earliest recorded event
        // timestamp) rather than a single triggering event.
        new Definition(
            "world_age_7_days",
            "World Age: 7 Days",
            "The world's oldest recorded observation is at least 7 days old.",
            ProvenanceTier.Reconstructed,
            AchievementScope.Community,
            WorldAgeSevenDays),
    ];

    /// <summary>
    /// Projects the catalog over the given events, returning every achievement
    /// (locked and unlocked) with provenance, evidence and unlock time filled in.
    /// </summary>
    public static IReadOnlyList<Achievement> Evaluate(
        IReadOnlyList<AchievementEvent> events, DateTimeOffset now)
    {
        var result = new List<Achievement>(Definitions.Count);
        foreach (var def in Definitions)
        {
            var unlock = def.Evaluate(events, now);
            result.Add(new Achievement
            {
                Id = def.Id,
                Title = def.Title,
                Description = def.Description,
                Provenance = def.Provenance,
                Scope = def.Scope,
                Unlocked = unlock is not null,
                UnlockedAt = unlock?.UnlockedAt,
                Evidence = unlock?.Evidence ?? [],
            });
        }

        return result;
    }

    // --- Rule primitives (pure) ---

    /// <summary>Unlocks on the earliest event of the given type.</summary>
    private static Unlock? FirstOf(IReadOnlyList<AchievementEvent> events, string eventType)
    {
        AchievementEvent? first = null;
        foreach (var e in events)
        {
            if (e.EventType != eventType) continue;
            if (first is null || e.OccurredAt < first.OccurredAt)
                first = e;
        }

        return first is null ? null : new Unlock(first.OccurredAt, [Evidence(first)]);
    }

    /// <summary>Unlocks once the Nth event of the given type is observed.</summary>
    private static Unlock? NthOf(IReadOnlyList<AchievementEvent> events, string eventType, int n)
    {
        var matches = events
            .Where(e => e.EventType == eventType)
            .OrderBy(e => e.OccurredAt)
            .ToList();

        if (matches.Count < n) return null;

        var trigger = matches[n - 1];
        // Evidence: the first and the threshold-crossing events, so the claim
        // shows the span it was earned over without dumping every event.
        var evidence = matches.Count == 1
            ? new[] { Evidence(trigger) }
            : new[] { Evidence(matches[0]), Evidence(trigger) };

        return new Unlock(trigger.OccurredAt, evidence);
    }

    /// <summary>Unlocks once N distinct actors have produced the given event type.</summary>
    private static Unlock? DistinctActors(IReadOnlyList<AchievementEvent> events, string eventType, int n)
    {
        var firstByActor = new Dictionary<string, AchievementEvent>();
        foreach (var e in events)
        {
            if (e.EventType != eventType || string.IsNullOrEmpty(e.ActorId)) continue;
            if (!firstByActor.TryGetValue(e.ActorId, out var existing) || e.OccurredAt < existing.OccurredAt)
                firstByActor[e.ActorId] = e;
        }

        if (firstByActor.Count < n) return null;

        // Order the distinct actors' first-connect events by time; the Nth is the
        // moment the threshold was crossed.
        var ordered = firstByActor.Values.OrderBy(e => e.OccurredAt).ToList();
        var trigger = ordered[n - 1];
        var evidence = ordered.Take(n).Select(Evidence).ToArray();
        return new Unlock(trigger.OccurredAt, evidence);
    }

    /// <summary>
    /// Reconstructed: uses the earliest event timestamp as a proxy for world age.
    /// </summary>
    private static Unlock? WorldAgeSevenDays(IReadOnlyList<AchievementEvent> events, DateTimeOffset now)
    {
        AchievementEvent? earliest = null;
        foreach (var e in events)
        {
            if (earliest is null || e.OccurredAt < earliest.OccurredAt)
                earliest = e;
        }

        if (earliest is null || now - earliest.OccurredAt < WorldAgeThreshold)
            return null;

        // Unlocked once the age threshold was reached, not "now".
        return new Unlock(earliest.OccurredAt + WorldAgeThreshold, [Evidence(earliest)]);
    }

    private static AchievementEvidence Evidence(AchievementEvent e) =>
        new(e.EventId, e.EventType, e.OccurredAt);
}

namespace Game.Contracts.Achievements;

/// <summary>
/// Provenance tier for an achievement claim (dashboard §04).
/// Never present the tiers as equivalent: only <see cref="Observed"/> and
/// <see cref="Reconstructed"/> unlock automatically in v1. <see cref="Verified"/>
/// and <see cref="CommunityAwarded"/> are modelled as valid tiers but require
/// multi-signal confirmation or human authority, so they are extension points
/// only — nothing auto-computes them yet.
/// </summary>
public enum ProvenanceTier
{
    /// <summary>Directly derived from authoritative server events (the event log).</summary>
    Observed,

    /// <summary>Confirmed by an independent second signal. Not auto-computed in v1.</summary>
    Verified,

    /// <summary>Explicitly granted by members/stewards; must be attributable. Not auto-computed in v1.</summary>
    CommunityAwarded,

    /// <summary>Inferred from aggregate/derived data (e.g. world age from earliest event).</summary>
    Reconstructed,
}

/// <summary>Who an achievement is credited to.</summary>
public enum AchievementScope
{
    Community,
    Guild,
    Player,
}

/// <summary>
/// A single authoritative observation that backs an achievement. Evidence is
/// always the triggering event(s), so any unlocked claim can be traced back to
/// the durable event log.
/// </summary>
public record AchievementEvidence(string EventId, string EventType, DateTimeOffset OccurredAt);

/// <summary>
/// An achievement is a projection over recorded authoritative observations,
/// never a manually-asserted badge. Locked achievements are still returned so
/// the community dashboard can show what is left to accomplish.
/// </summary>
public record Achievement
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required ProvenanceTier Provenance { get; init; }
    public required AchievementScope Scope { get; init; }
    public bool Unlocked { get; init; }
    public DateTimeOffset? UnlockedAt { get; init; }
    public IReadOnlyList<AchievementEvidence> Evidence { get; init; } = [];
}

/// <summary>
/// Minimal, payload-free view of an authoritative event that the achievement
/// catalog evaluates. Keeps the projection pure (event list in, achievements
/// out) and decoupled from storage entities.
/// </summary>
public record AchievementEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    string? ActorId = null,
    string? GuildId = null);

/// <summary>Snake_case wire strings for the provenance/scope enums (dashboard §04).</summary>
public static class AchievementWire
{
    public static string ToWireString(this ProvenanceTier tier) => tier switch
    {
        ProvenanceTier.Observed => "observed",
        ProvenanceTier.Verified => "verified",
        ProvenanceTier.CommunityAwarded => "community_awarded",
        ProvenanceTier.Reconstructed => "reconstructed",
        _ => "observed",
    };

    public static string ToWireString(this AchievementScope scope) => scope switch
    {
        AchievementScope.Community => "community",
        AchievementScope.Guild => "guild",
        AchievementScope.Player => "player",
        _ => "community",
    };
}

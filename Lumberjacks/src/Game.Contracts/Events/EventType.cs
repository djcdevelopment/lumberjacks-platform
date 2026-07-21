namespace Game.Contracts.Events;

/// <summary>
/// Canonical event types from docs/events.md.
/// String constants rather than enum to match wire format.
/// </summary>
public static class EventType
{
    // Player events
    public const string PlayerConnected = "player_connected";
    public const string PlayerDisconnected = "player_disconnected";
    public const string PlayerEnteredRegion = "player_entered_region";
    public const string PlayerFollowedRoadSegment = "player_followed_road_segment";
    public const string PlayerDiscoveredLandmark = "player_discovered_landmark";
    public const string PlayerJoinedGuild = "player_joined_guild";
    public const string PlayerLeftGuild = "player_left_guild";
    public const string PlayerRankChanged = "player_rank_changed";

    // Build and world events
    public const string StructurePlacementRequested = "structure_placement_requested";
    public const string StructurePlaced = "structure_placed";
    public const string StructureRemoved = "structure_removed";
    public const string ContainerOpened = "container_opened";
    public const string ItemPickedUp = "item_picked_up";
    public const string ItemStored = "item_stored";
    public const string RoadSegmentMaintained = "road_segment_maintained";
    public const string SettlementSignatureUpdated = "settlement_signature_updated";

    // Progression and community events
    public const string ChallengeStarted = "challenge_started";
    public const string ChallengeCompleted = "challenge_completed";
    public const string GuildObjectiveProgressed = "guild_objective_progressed";
    public const string GuildObjectiveCompleted = "guild_objective_completed";
    public const string RewardGranted = "reward_granted";
    public const string DiscordIdentityLinked = "discord_identity_linked";
    public const string DiscordRoleSyncRequested = "discord_role_sync_requested";
    public const string DiscordRoleSynced = "discord_role_synced";

    // Operational events
    public const string RegionActivated = "region_activated";
    public const string RegionDeactivated = "region_deactivated";
    public const string InterestSubscriptionChanged = "interest_subscription_changed";
    public const string EdgeNodeRegistered = "edge_node_registered";
    public const string EdgeNodeUnhealthy = "edge_node_unhealthy";
    public const string EdgeNodeDetached = "edge_node_detached";

    public static readonly IReadOnlyList<string> All =
    [
        PlayerConnected, PlayerDisconnected, PlayerEnteredRegion, PlayerFollowedRoadSegment,
        PlayerDiscoveredLandmark, PlayerJoinedGuild, PlayerLeftGuild, PlayerRankChanged,
        StructurePlacementRequested, StructurePlaced, StructureRemoved, ContainerOpened,
        ItemPickedUp, ItemStored, RoadSegmentMaintained, SettlementSignatureUpdated,
        ChallengeStarted, ChallengeCompleted, GuildObjectiveProgressed, GuildObjectiveCompleted,
        RewardGranted, DiscordIdentityLinked, DiscordRoleSyncRequested, DiscordRoleSynced,
        RegionActivated, RegionDeactivated, InterestSubscriptionChanged,
        EdgeNodeRegistered, EdgeNodeUnhealthy, EdgeNodeDetached,
    ];
}

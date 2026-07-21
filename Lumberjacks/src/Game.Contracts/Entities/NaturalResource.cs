namespace Game.Contracts.Entities;

/// <summary>
/// A persistent natural resource (Tree, Rock, etc.) that evolves over time.
/// Supports Biomimetic "Nature 2.0" features like growth history and axe geometry.
/// </summary>
public record NaturalResource
{
    public required string Id { get; init; }
    public required string Type { get; init; } // e.g., "pine_tree", "oak_tree", "basalt_rock"
    public required Vec3 Position { get; init; }
    public required string RegionId { get; init; }
    
    // Health & Regrowth (ADR 0019)
    public double Health { get; init; } = 100.0;
    public double StumpHealth { get; init; } = 50.0;
    public double RegrowthProgress { get; init; } = 0.0; // 0.0 to 1.0 (sapling to full tree)
    
    // Axe Geometry Accumulators (ADR 0020)
    // Tracks the cumulative "lean" based on strike angles.
    public double LeanX { get; init; } = 0.0;
    public double LeanZ { get; init; } = 0.0;
    
    // Growth History (JSON)
    // Stores traits: "twist", "fire_scars", "age_years", "habitat_seed"
    public Dictionary<string, string> GrowthHistory { get; init; } = [];
    
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

namespace Game.Persistence.Entities;

public class NaturalResourceEntity
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double PositionZ { get; set; }
    public required string RegionId { get; set; }
    
    // Health & Regrowth
    public double Health { get; set; } = 100.0;
    public double StumpHealth { get; set; } = 50.0;
    public double RegrowthProgress { get; set; } = 0.0;
    
    // Axe Geometry Accumulators
    public double LeanX { get; set; }
    public double LeanZ { get; set; }
    
    // Growth History (JSONB)
    public string GrowthHistory { get; set; } = "{}";
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

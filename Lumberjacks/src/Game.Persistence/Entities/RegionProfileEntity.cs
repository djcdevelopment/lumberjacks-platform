namespace Game.Persistence.Entities;

public class RegionProfileEntity
{
    public required string Id { get; set; }
    public required string RegionId { get; set; } // FK to RegionEntity
    
    // 1D Flattened Grids for Terrain and Climate (JSONB)
    public string AltitudeGrid { get; set; } = "[]";
    public string HumidityGrid { get; set; } = "[]";
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }
    
    // Regional Trade Winds
    public double TradeWindX { get; set; }
    public double TradeWindZ { get; set; }
    
    // Geologic History (JSONB)
    public string GeologicHistory { get; set; } = "{}";
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

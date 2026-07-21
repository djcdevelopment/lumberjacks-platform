namespace Game.Contracts.Entities;

/// <summary>
/// Defines the Geologic and Meteorologic profile for a region.
/// Shapes the growth, history, and active weather of all nature in the area.
/// </summary>
public record RegionProfile
{
    public required string Id { get; init; }
    public required string RegionId { get; init; } // FK to RegionEntity
    
    // 1D Flattened Grids for Terrain and Climate (Width x Height)
    public List<double> AltitudeGrid { get; init; } = [];
    public List<double> HumidityGrid { get; init; } = [];
    public int GridWidth { get; init; }
    public int GridHeight { get; init; }
    
    // Regional Trade Winds (Active Meteorology baseline)
    public double TradeWindX { get; init; }
    public double TradeWindZ { get; init; }
    
    // Geologic History (JSON)
    // Stores traits: "volcanic_activity", "uplift_rate", "age_millions"
    public Dictionary<string, string> GeologicHistory { get; init; } = [];
    
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

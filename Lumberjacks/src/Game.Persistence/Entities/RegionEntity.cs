namespace Game.Persistence.Entities;

public class RegionEntity
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public double BoundsMinX { get; set; }
    public double BoundsMinY { get; set; }
    public double BoundsMinZ { get; set; }
    public double BoundsMaxX { get; set; }
    public double BoundsMaxY { get; set; }
    public double BoundsMaxZ { get; set; }
    public bool Active { get; set; } = true;
    public double TickRate { get; set; } = 20;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace Game.Persistence.Entities;

public class WorldItemEntity
{
    public required string Id { get; set; }
    public required string ItemType { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double PositionZ { get; set; }
    public required string RegionId { get; set; }
    public int Quantity { get; set; } = 1;
    public DateTimeOffset SpawnedAt { get; set; } = DateTimeOffset.UtcNow;
}

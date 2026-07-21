namespace Game.Persistence.Entities;

public class StructureEntity
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double PositionZ { get; set; }
    public double Rotation { get; set; }
    public required string OwnerId { get; set; }
    public required string RegionId { get; set; }
    public DateTimeOffset PlacedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Tags { get; set; } = "[]";
}

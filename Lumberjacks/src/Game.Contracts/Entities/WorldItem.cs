namespace Game.Contracts.Entities;

public record WorldItem
{
    public required string Id { get; init; }
    public required string ItemType { get; init; }
    public required Vec3 Position { get; init; }
    public required string RegionId { get; init; }
    public int Quantity { get; init; } = 1;
    public DateTimeOffset SpawnedAt { get; init; }
}

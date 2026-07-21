namespace Game.Contracts.Entities;

public record Structure
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required Vec3 Position { get; init; }
    public double Rotation { get; init; }
    public required string OwnerId { get; init; }
    public required string RegionId { get; init; }
    public required DateTimeOffset PlacedAt { get; init; }
    public List<string> Tags { get; init; } = [];
}

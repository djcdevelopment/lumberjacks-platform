namespace Game.Contracts.Entities;

public record Region
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Vec3 BoundsMin { get; init; }
    public required Vec3 BoundsMax { get; init; }
    public bool Active { get; init; }
    public int PlayerCount { get; init; }
    public double TickRate { get; init; } = 20;
}

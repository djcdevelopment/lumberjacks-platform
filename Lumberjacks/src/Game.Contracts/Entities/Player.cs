namespace Game.Contracts.Entities;

public record Player
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? GuildId { get; init; }
    public int Rank { get; init; }
    public required Vec3 Position { get; init; }
    public Vec3 Velocity { get; init; }
    /// <summary>Heading in degrees (0-360). Updated from input Direction.</summary>
    public double Heading { get; init; }
    /// <summary>Last processed input sequence number (echoed back to client for prediction).</summary>
    public ushort LastInputSeq { get; init; }
    public string? EquippedItemType { get; init; } // e.g., "axe", "pickaxe"
    public required string RegionId { get; init; }
    public bool Connected { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; } = DateTimeOffset.UtcNow;
}


namespace Game.Persistence.Entities;

public class PlayerProgressEntity
{
    public required string PlayerId { get; set; }
    public int Rank { get; set; }
    public int Points { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

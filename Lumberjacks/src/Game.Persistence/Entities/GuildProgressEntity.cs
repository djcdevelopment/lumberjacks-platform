namespace Game.Persistence.Entities;

public class GuildProgressEntity
{
    public required string GuildId { get; set; }
    public int Points { get; set; }
    public int ChallengesCompleted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

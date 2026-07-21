namespace Game.Persistence.Entities;

public class ChallengeProgressEntity
{
    public int Id { get; set; }
    public required string ChallengeId { get; set; }
    public required string GuildId { get; set; }
    public int CurrentValue { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

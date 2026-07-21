namespace Game.Persistence.Entities;

public class ChallengeEntity
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; } = 1;
    public required string TriggerEvent { get; set; }
    public string TriggerFilters { get; set; } = "{}";
    public string ProgressMode { get; set; } = "sum";
    public int Target { get; set; }
    public DateTimeOffset? WindowStart { get; set; }
    public DateTimeOffset? WindowEnd { get; set; }
    public string Rewards { get; set; } = "[]";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

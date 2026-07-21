namespace Game.Persistence.Entities;

public class EventEntity
{
    public int Id { get; set; }
    public required string EventId { get; set; }
    public required string EventType { get; set; }
    public required DateTimeOffset OccurredAt { get; set; }
    public required string WorldId { get; set; }
    public string? RegionId { get; set; }
    public string? ActorId { get; set; }
    public string? GuildId { get; set; }
    public required string SourceService { get; set; }
    public int SchemaVersion { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

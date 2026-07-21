using System.Text.Json;

namespace Game.Contracts.Events;

public record GameEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string WorldId { get; init; }
    public string? RegionId { get; init; }
    public string? ActorId { get; init; }
    public string? GuildId { get; init; }
    public required string SourceService { get; init; }
    public required int SchemaVersion { get; init; }
    public JsonElement Payload { get; init; }
}

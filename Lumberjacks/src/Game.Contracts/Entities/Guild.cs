namespace Game.Contracts.Entities;

public record Guild
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string LeaderId { get; init; }
    public required List<string> MemberIds { get; init; }
    public int Points { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

namespace Game.Persistence.Entities;

public class ContainerItemEntity
{
    public int Id { get; set; }
    public required string ContainerId { get; set; }
    public required string ItemType { get; set; }
    public int Quantity { get; set; } = 1;
    public DateTimeOffset StoredAt { get; set; } = DateTimeOffset.UtcNow;
}

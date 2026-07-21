namespace Game.Persistence.Entities;

public class PlayerInventoryEntity
{
    public int Id { get; set; }
    public required string PlayerId { get; set; }
    public required string ItemType { get; set; }
    public int Quantity { get; set; } = 1;
    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;
}

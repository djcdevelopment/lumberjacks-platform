namespace Game.Contracts.Entities;

public record InventorySlot
{
    public required string ItemType { get; init; }
    public int Quantity { get; init; } = 1;
}

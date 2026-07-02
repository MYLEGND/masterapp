namespace Domain.Entities;

public sealed class CommerceProductInventoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceProductId { get; set; }
    public CommerceProduct? CommerceProduct { get; set; }

    public string ExternalInventoryKey { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int StockQuantity { get; set; }
    public int LowStockThreshold { get; set; } = 20;
    public int DisplayOrder { get; set; }
}

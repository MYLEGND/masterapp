namespace Domain.Entities;

public sealed class CommerceProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public string ExternalProductKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PriceLabel { get; set; } = "Coming Soon";
    public string Badge { get; set; } = "Parfait";

    public int PriceCents { get; set; }
    public int CompareAtPriceCents { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public List<CommerceProductImage> Images { get; set; } = [];
    public List<CommerceProductInventoryItem> InventoryItems { get; set; } = [];
    public List<CommerceProductDiscount> Discounts { get; set; } = [];
}

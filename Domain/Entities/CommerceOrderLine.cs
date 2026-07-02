namespace Domain.Entities;

public sealed class CommerceOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceOrderId { get; set; }
    public CommerceOrder? CommerceOrder { get; set; }

    public string ProductExternalKey { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string ProductSlug { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int UnitPriceCents { get; set; }
    public int CompareAtPriceCents { get; set; }
    public string? ImageUrl { get; set; }
}

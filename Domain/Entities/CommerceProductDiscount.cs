namespace Domain.Entities;

public sealed class CommerceProductDiscount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceProductId { get; set; }
    public CommerceProduct? CommerceProduct { get; set; }

    public string ExternalDiscountKey { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string DiscountType { get; set; } = "Percent";
    public decimal Amount { get; set; }
    // Maximum distinct training members, including pending reservations and prior subscribers.
    public int? UsageLimit { get; set; }
    public bool IsActive { get; set; } = true;
}

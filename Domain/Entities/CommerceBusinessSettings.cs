namespace Domain.Entities;

public sealed class CommerceBusinessSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public CommerceBusiness? CommerceBusiness { get; set; }

    public int ShippingFeeCents { get; set; }
    public decimal TaxPercent { get; set; }

    public string GlobalDiscountCode { get; set; } = string.Empty;
    public string GlobalDiscountType { get; set; } = "Percent";
    public decimal GlobalDiscountAmount { get; set; }
    public bool GlobalDiscountIsActive { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

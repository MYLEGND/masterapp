using Domain.Billing;

namespace Domain.Entities;

public sealed class ClientSubscriptionOffer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }

    public string OwnerAgentUserId { get; set; } = string.Empty;
    public ClientSubscriptionOfferPriceType PriceType { get; set; } = ClientSubscriptionOfferPriceType.Fixed100;
    public int MonthlyAmountCents { get; set; }
    public string Currency { get; set; } = "USD";
    public BillingAnchorSelectionMode BillingAnchorSelectionMode { get; set; } = BillingAnchorSelectionMode.FirstOfMonth;
    public int? SelectedBillingAnchorDay { get; set; }
    public int FreeTrialDays { get; set; }
    public ClientSubscriptionOfferStatus Status { get; set; } = ClientSubscriptionOfferStatus.Draft;
    public DateTime? EffectiveUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

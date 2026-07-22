using Domain.Billing;

namespace Domain.Entities;

public sealed class SubscriptionPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ClientSubscriptionId { get; set; }
    public ClientSubscription? ClientSubscription { get; set; }

    public Guid? CommerceOrderId { get; set; }
    public CommerceOrder? CommerceOrder { get; set; }

    public BillingProvider Provider { get; set; } = BillingProvider.Square;
    public BillingProviderEnvironment ProviderEnvironment { get; set; } = BillingProviderEnvironment.Sandbox;
    public string? ProviderPaymentId { get; set; }
    public string? ProviderInvoiceId { get; set; }
    public string? ProviderRefundId { get; set; }
    public int AmountCents { get; set; }
    public string Currency { get; set; } = "USD";
    public SubscriptionPaymentStatus Status { get; set; } = SubscriptionPaymentStatus.Pending;
    public string? SafeFailureCode { get; set; }
    public DateTime? BillingPeriodStartUtc { get; set; }
    public DateTime? BillingPeriodEndUtc { get; set; }
    public DateTime? ProviderOccurredUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

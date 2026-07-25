using Domain.Billing;

namespace Domain.Entities;

public sealed class ClientSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }

    public Guid AcceptedOfferId { get; set; }
    public ClientSubscriptionOffer? AcceptedOffer { get; set; }

    public string OwnerAgentUserId { get; set; } = string.Empty;
    public BillingProvider Provider { get; set; } = BillingProvider.Square;
    public BillingProviderEnvironment ProviderEnvironment { get; set; } = BillingProviderEnvironment.Sandbox;
    public string? ProviderCustomerId { get; set; }
    public Guid? DefaultPaymentMethodId { get; set; }
    public ClientPaymentMethod? DefaultPaymentMethod { get; set; }

    public string? ProviderSubscriptionId { get; set; }
    public string? ProviderPlanVariationId { get; set; }
    public int MonthlyAmountCents { get; set; }
    public string Currency { get; set; } = "USD";
    public string BillingTimeZoneId { get; set; } = "UTC";
    public int? BillingAnchorDay { get; set; }
    public ClientSubscriptionStatus Status { get; set; } = ClientSubscriptionStatus.Draft;
    public ClientSubscriptionPaymentStanding PaymentStanding { get; set; } = ClientSubscriptionPaymentStanding.Unknown;
    public DateTime? FirstChargeUtc { get; set; }
    public DateTime? FirstRecurringRenewalUtc { get; set; }
    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public DateTime? NextBillingDateUtc { get; set; }
    public DateTime? NextChargeAttemptUtc { get; set; }
    public DateTime? LastChargeAttemptUtc { get; set; }
    public DateTime? LastSuccessfulChargeUtc { get; set; }
    public bool IsPlatformManaged { get; set; }
    public DateTime? PlatformManagedSinceUtc { get; set; }
    public DateTime? ActivatedUtc { get; set; }
    public DateTime? CancelledUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? GracePeriodEndsUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

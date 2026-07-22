using Domain.Billing;

namespace Domain.Entities;

public sealed class ClientEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }

    public string EntitlementKey { get; set; } = BillingEntitlementKeys.ClientAppFullAccess;
    public ClientEntitlementStatus Status { get; set; } = ClientEntitlementStatus.NotGranted;
    public ClientEntitlementSourceType SourceType { get; set; } = ClientEntitlementSourceType.Subscription;
    public string SourceId { get; set; } = string.Empty;
    public DateTime? EffectiveUtc { get; set; }
    public DateTime? ExpirationUtc { get; set; }
    public DateTime? GraceOrSuspensionUtc { get; set; }
    public string? ReasonCode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

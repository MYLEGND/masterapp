using Domain.Billing;

namespace Domain.Entities;

public sealed class ClientIdentityContinuation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }

    public Guid? SubscriptionActivationInvitationId { get; set; }
    public SubscriptionActivationInvitation? SubscriptionActivationInvitation { get; set; }

    public Guid? ClientSubscriptionId { get; set; }
    public ClientSubscription? ClientSubscription { get; set; }

    public ClientIdentityContinuationPurpose Purpose { get; set; } = ClientIdentityContinuationPurpose.Activation;
    public string TokenHash { get; set; } = string.Empty;
    public string IntendedNormalizedEmail { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/";
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ConsumedUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

using Domain.Billing;

namespace Domain.Entities;

public sealed class SubscriptionActivationInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }

    public Guid ClientSubscriptionOfferId { get; set; }
    public ClientSubscriptionOffer? ClientSubscriptionOffer { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public string IntendedNormalizedEmail { get; set; } = string.Empty;
    public SubscriptionActivationInvitationStatus Status { get; set; } = SubscriptionActivationInvitationStatus.Pending;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ViewedUtc { get; set; }
    public DateTime? PaymentStartedUtc { get; set; }
    public DateTime? RedeemedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public DateTime? SupersededUtc { get; set; }
    public string CreatedByAgentUserId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSentUtc { get; set; }
    public int SendCount { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

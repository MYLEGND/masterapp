using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// An auditable, single-use invitation for a partner to receive a distinct
/// MASTERAPP profile and join an existing household. It is unrelated to
/// subscription-payment activation invitations.
/// </summary>
public sealed class HouseholdMemberInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid HouseholdAccountId { get; set; }

    public Guid HouseholdMembershipId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public string IntendedNormalizedEmail { get; set; } = string.Empty;

    public string InvitedFirstName { get; set; } = string.Empty;

    public string InvitedLastName { get; set; } = string.Empty;

    public HouseholdInvitationStatus Status { get; set; } = HouseholdInvitationStatus.Pending;

    public DateTime ExpiresUtc { get; set; }

    public DateTime? SentUtc { get; set; }

    public DateTime? AcceptedUtc { get; set; }

    public DateTime? DeclinedUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    public string? DeclineReasonCode { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

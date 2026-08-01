using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// One person in a household. Membership grants access to shared household
/// financial data only; each member retains an independent client profile and
/// therefore an independent social, messaging, Journey, and settings identity.
/// </summary>
public sealed class HouseholdMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid HouseholdAccountId { get; set; }

    /// <summary>
    /// Null while an invited partner has not yet accepted and received their
    /// distinct MASTERAPP profile.
    /// </summary>
    public Guid? ClientProfileId { get; set; }

    public HouseholdMemberRole Role { get; set; }

    public HouseholdMembershipStatus Status { get; set; } = HouseholdMembershipStatus.PendingInvitation;

    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>
    /// Canonical Entra object ID. This is a projection identifier, never the
    /// membership authority and never used as a display/profile identifier.
    /// </summary>
    public string? ExternalIdentityObjectId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ActivatedUtc { get; set; }

    public DateTime? SuspendedUtc { get; set; }

    public DateTime? RemovedUtc { get; set; }

    public string? StatusReasonCode { get; set; }

    public string? CreatedByUserId { get; set; }

    public string? UpdatedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// The authoritative paid membership boundary for a household. A household has
/// one subscription owner and can contain that owner plus one invited partner.
/// It intentionally contains no personal-profile, social, or Journey data.
/// </summary>
public sealed class HouseholdAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SubscriptionOwnerClientProfileId { get; set; }

    public HouseholdAccountStatus Status { get; set; } = HouseholdAccountStatus.PendingActivation;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ActivatedUtc { get; set; }

    public DateTime? SuspendedUtc { get; set; }

    public DateTime? ClosedUtc { get; set; }

    public string? StatusReasonCode { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

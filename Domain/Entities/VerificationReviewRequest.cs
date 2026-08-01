namespace Domain.Entities;

/// <summary>
/// One member's auditable request for the private Legend verification review.
/// The request is separate from the staff conversation, so the review queue can
/// stay private while each member only sees their own verification state.
/// </summary>
public sealed class VerificationReviewRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReviewConversationId { get; set; }

    public string RequesterUserId { get; set; } = string.Empty;

    public string RequesterParticipantType { get; set; } = string.Empty;

    public string Status { get; set; } = VerificationReviewStatuses.Pending;

    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedUtc { get; set; }

    public string? ResolvedByUserId { get; set; }

    public string? ResolutionNote { get; set; }
}

public static class VerificationReviewStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Declined = "Declined";
}

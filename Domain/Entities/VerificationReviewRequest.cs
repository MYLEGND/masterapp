namespace Domain.Entities;

/// <summary>
/// One member's auditable request for a founder-controlled Legend resource.
/// This table began with verification and remains the single request queue for
/// every controlled resource. The requester is never added to the staff review
/// conversation, so the queue stays private.
/// </summary>
public sealed class VerificationReviewRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReviewConversationId { get; set; }

    public string RequesterUserId { get; set; } = string.Empty;

    public string RequesterParticipantType { get; set; } = string.Empty;

    /// <summary>
    /// The resource requested through the shared Founder + Legend review queue.
    /// Existing rows are verification requests by default.
    /// </summary>
    public string ResourceType { get; set; } = ControlledResourceTypes.VerificationBadge;

    public string Status { get; set; } = VerificationReviewStatuses.Pending;

    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedUtc { get; set; }

    public string? ResolvedByUserId { get; set; }

    public string? ResolutionNote { get; set; }
}

/// <summary>
/// Resource identifiers shared by the request queue, server authorization, and
/// grant persistence. These values are never inferred by the mobile client.
/// </summary>
public static class ControlledResourceTypes
{
    public const string VerificationBadge = "VerificationBadge";
    public const string LanguageTranslation = "LanguageTranslation";
    public const string ScriptureManagement = "ScriptureManagement";

    public static bool IsSupported(string? value) =>
        string.Equals(value, VerificationBadge, StringComparison.Ordinal) ||
        string.Equals(value, LanguageTranslation, StringComparison.Ordinal) ||
        string.Equals(value, ScriptureManagement, StringComparison.Ordinal);

    /// <summary>
    /// Scripture management is issued only through an explicit Founder grant;
    /// it never enters the member-request review queue.
    /// </summary>
    public static bool SupportsMemberRequest(string? value) =>
        string.Equals(value, VerificationBadge, StringComparison.Ordinal) ||
        string.Equals(value, LanguageTranslation, StringComparison.Ordinal);
}

public static class ControlledResourceAccessStates
{
    public const string NotGranted = "NotGranted";
    public const string Pending = "Pending";
    public const string Granted = "Granted";
}

public static class VerificationReviewStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Declined = "Declined";
}

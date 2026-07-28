namespace Domain.Entities;

/// <summary>
/// An authenticated profile visit, optionally attributed to a specific post.
/// A source post ID of Guid.Empty represents a direct profile visit.
/// </summary>
public sealed class SocialProfileVisit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TargetUserId { get; set; } = string.Empty;
    public string TargetParticipantType { get; set; } = string.Empty;
    public string VisitorUserId { get; set; } = string.Empty;
    public string VisitorParticipantType { get; set; } = string.Empty;
    public Guid SourceSocialPostId { get; set; }
    public DateTime FirstVisitedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastVisitedUtc { get; set; } = DateTime.UtcNow;
}

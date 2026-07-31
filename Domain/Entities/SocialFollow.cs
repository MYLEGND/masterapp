namespace Domain.Entities;

/// <summary>
/// The authoritative relationship between two mobile social profiles. Pending
/// rows represent a private-account request; only Accepted rows grant access.
/// </summary>
public sealed class SocialFollow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FollowerUserId { get; set; } = string.Empty;
    public string FollowerParticipantType { get; set; } = string.Empty;
    public string FollowedUserId { get; set; } = string.Empty;
    public string FollowedParticipantType { get; set; } = string.Empty;
    public Guid? SourceSocialPostId { get; set; }
    public string Status { get; set; } = "Accepted";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedUtc { get; set; }
}

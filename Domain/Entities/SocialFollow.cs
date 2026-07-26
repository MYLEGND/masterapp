namespace Domain.Entities;

/// <summary>
/// A feed preference. Following never widens visibility; server-side audience
/// authorization remains the sole access authority.
/// </summary>
public sealed class SocialFollow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FollowerUserId { get; set; } = string.Empty;
    public string FollowerParticipantType { get; set; } = string.Empty;
    public string FollowedUserId { get; set; } = string.Empty;
    public string FollowedParticipantType { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

namespace Domain.Entities;

/// <summary>One repost state per post and logical participant identity.</summary>
public sealed class SocialPostRepost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorParticipantType { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SocialPost SocialPost { get; set; } = null!;
}

namespace Domain.Entities;

public sealed class SocialPostReaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorParticipantType { get; set; } = string.Empty;
    public string ReactionType { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SocialPost SocialPost { get; set; } = null!;
}

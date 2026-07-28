namespace Domain.Entities;

/// <summary>
/// An authenticated share intent. Legend records one share per logical
/// identity and post so client retries cannot inflate creator analytics.
/// </summary>
public sealed class SocialPostShare
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorParticipantType { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SocialPost SocialPost { get; set; } = null!;
}

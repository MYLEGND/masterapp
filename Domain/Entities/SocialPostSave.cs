namespace Domain.Entities;

/// <summary>Persisted per-identity save state for a social post.</summary>
public sealed class SocialPostSave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ActorParticipantType { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SocialPost SocialPost { get; set; } = null!;
}

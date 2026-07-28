namespace Domain.Entities;

/// <summary>
/// Provider-neutral licensed music metadata selected for one social post.
/// Audio is never copied into the social media store; delivery remains the
/// responsibility of the configured music provider.
/// </summary>
public sealed class SocialPostMusicAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderTrackId { get; set; } = string.Empty;
    public string TrackTitle { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public decimal TrackDurationSeconds { get; set; }
    public string? PreviewUrl { get; set; }
    public decimal TrimStartSeconds { get; set; }
    public decimal TrimEndSeconds { get; set; }
    public decimal MusicVolume { get; set; }
    public decimal OriginalAudioVolume { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public SocialPost SocialPost { get; set; } = null!;
}

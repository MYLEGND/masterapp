namespace Domain.Entities;

/// <summary>
/// One authenticated viewer's authoritative engagement with a social post.
/// The composite identity prevents a person with client and agent profiles
/// from collapsing into one social viewer.
/// </summary>
public sealed class SocialPostViewer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }
    public string ViewerUserId { get; set; } = string.Empty;
    public string ViewerParticipantType { get; set; } = string.Empty;
    public DateTime FirstViewedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastViewedUtc { get; set; } = DateTime.UtcNow;
    public decimal? MaximumWatchDurationSeconds { get; set; }
    public decimal? MaximumWatchCompletionPercentage { get; set; }
    public int StoryExitCount { get; set; }
    public int StoryTapForwardCount { get; set; }
    public int StoryTapBackwardCount { get; set; }
    public SocialPost SocialPost { get; set; } = null!;
}

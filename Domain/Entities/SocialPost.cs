namespace Domain.Entities;

/// <summary>
/// A Legend community item. The author is a logical participant identity, not
/// only a user ID, because one person can hold both client and agent profiles.
/// </summary>
public sealed class SocialPost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AuthorUserId { get; set; } = string.Empty;
    public string AuthorParticipantType { get; set; } = string.Empty;
    public Guid AuthorProfileId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public Guid? RepostOfSocialPostId { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>Author-supplied place label. Never a resolved coordinate.</summary>
    public string? Location { get; set; }

    /// <summary>When false, the server rejects new comments on this post.</summary>
    public bool CommentsEnabled { get; set; } = true;

    public DateTime PostedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }

    public ICollection<SocialPostMediaAsset> MediaAssets { get; set; } =
        new List<SocialPostMediaAsset>();

    public SocialPostMusicAttachment? MusicAttachment { get; set; }
}

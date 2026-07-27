namespace Domain.Entities;

/// <summary>
/// An ordered image or video owned by a Legend social post.
///
/// StorageKey and ThumbnailStorageKey are provider-independent object keys.
/// Public or signed delivery URLs are resolved outside the domain model.
/// </summary>
public sealed class SocialPostMediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SocialPostId { get; set; }

    /// <summary>
    /// Zero-based position within the post. A single-item post uses zero.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Logical media classification, such as Image or Video.
    /// </summary>
    public string MediaKind { get; set; } = string.Empty;

    /// <summary>
    /// Provider-independent key for the original or processed media object.
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Provider-independent key for an optional generated thumbnail.
    /// </summary>
    public string? ThumbnailStorageKey { get; set; }

    public string MimeType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public decimal? AspectRatio { get; set; }
    public decimal? DurationSeconds { get; set; }

    /// <summary>
    /// Media lifecycle state, such as Pending, Processing, Ready, or Failed.
    /// </summary>
    public string ProcessingState { get; set; } = string.Empty;

    public string? AccessibilityText { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public SocialPost SocialPost { get; set; } = null!;
}

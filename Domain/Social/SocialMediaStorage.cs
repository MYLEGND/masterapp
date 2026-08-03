namespace Domain.Social;

/// <summary>
/// Provider-independent storage authority for original Legend social media.
///
/// Implementations return stable storage keys only. Public or signed delivery
/// URLs are resolved outside this contract.
/// </summary>
public interface ISocialMediaStorage
{
    Task<SocialMediaStorageResult> StoreAsync(
        Guid mediaAssetId,
        string originalFileName,
        long declaredSizeBytes,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<SocialMediaReadResult> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The local-media implementation is the sole authority allowed to finalize a
/// stored video. Upload requests only persist complete source bytes; this
/// contract is consumed by the single hosted processing queue after the
/// request has been released.
/// </summary>
public interface ISocialMediaVideoProcessor
{
    Task<SocialMediaVideoProcessingResult> ProcessAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory signal for the durable media lifecycle. The database state remains
/// the recovery authority, so dropping a signal can never lose a video job.
/// </summary>
public interface ISocialMediaProcessingQueue
{
    void Enqueue(Guid mediaAssetId);
}

/// <summary>
/// The result of resolving a protected social-media object. A missing object
/// and an unavailable storage provider are deliberately distinct: the former
/// remains hidden from unauthorized callers, while the latter lets an already
/// authorized caller retry a transient platform failure safely.
/// </summary>
public sealed record SocialMediaReadResult(
    SocialMediaReadStatus Status,
    Stream? Content)
{
    public static SocialMediaReadResult Available(Stream content) =>
        new(SocialMediaReadStatus.Available, content);

    public static SocialMediaReadResult Missing() =>
        new(SocialMediaReadStatus.Missing, null);

    public static SocialMediaReadResult Unavailable() =>
        new(SocialMediaReadStatus.Unavailable, null);
}

public enum SocialMediaReadStatus
{
    Available,
    Missing,
    Unavailable
}

public sealed record SocialMediaStorageResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    SocialStoredMedia? Media)
{
    public static SocialMediaStorageResult Success(SocialStoredMedia media) =>
        new(true, null, null, media);

    public static SocialMediaStorageResult Failure(
        string errorCode,
        string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record SocialStoredMedia(
    string OriginalFileName,
    string StoredFileName,
    string MediaKind,
    string MimeType,
    long FileSizeBytes,
    string StorageKey,
    bool RequiresBackgroundProcessing = false);

public sealed record SocialMediaVideoProcessingResult(
    bool Succeeded,
    long? FileSizeBytes,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static SocialMediaVideoProcessingResult Success(long fileSizeBytes) =>
        new(true, fileSizeBytes, null, null);

    public static SocialMediaVideoProcessingResult Failure(
        string errorCode,
        string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}

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

    Task<Stream?> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
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
    string StorageKey);

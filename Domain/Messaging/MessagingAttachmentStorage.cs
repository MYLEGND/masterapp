namespace Domain.Messaging;

public interface IMessageAttachmentStorage
{
    Task<MessagingStoredAttachmentResult> StoreAsync(
        Guid attachmentId,
        string originalFileName,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default);
}

public sealed record MessagingStoredAttachmentResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    MessagingStoredAttachment? Attachment)
{
    public static MessagingStoredAttachmentResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, null);
}

public sealed record MessagingStoredAttachment(
    string OriginalFileName,
    string StoredFileName,
    string ContentType,
    long SizeBytes,
    string StoragePath);

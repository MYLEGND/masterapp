using Domain.Messaging;
using Infrastructure.Security.UploadValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Messaging;

internal sealed class MessagingAttachmentStorage : IMessageAttachmentStorage
{
    private const long DefaultMaximumAttachmentBytes = 10 * 1024 * 1024;
    private const int MaximumOriginalFileNameLength = 255;
    private readonly string _rootPath;
    private readonly long _maximumAttachmentBytes;
    private readonly ILogger<MessagingAttachmentStorage> _logger;

    public MessagingAttachmentStorage(IConfiguration configuration, ILogger<MessagingAttachmentStorage> logger)
    {
        var configuredRoot = configuration["Messaging:Attachments:RootPath"];
        _rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "messaging-attachments")
            : configuredRoot.Trim());
        _maximumAttachmentBytes = ParseMaximumAttachmentBytes(configuration["Messaging:Attachments:MaximumBytes"]);
        _logger = logger;
    }

    public async Task<MessagingStoredAttachmentResult> StoreAsync(
        Guid attachmentId,
        string originalFileName,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty || content is null || sizeBytes <= 0 || sizeBytes > _maximumAttachmentBytes)
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_SIZE_INVALID", "The attachment size is not permitted.");

        var safeOriginalName = Path.GetFileName(originalFileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeOriginalName) || safeOriginalName.Length > MaximumOriginalFileNameLength)
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_NAME_INVALID", "The attachment name is invalid.");

        byte[] bytes;
        try
        {
            await using var buffer = new MemoryStream(capacity: checked((int)sizeBytes));
            await content.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Messaging attachment input could not be read. AttachmentId={AttachmentId}", attachmentId);
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_CONTENT_INVALID", "The attachment could not be read.");
        }

        if (bytes.LongLength != sizeBytes)
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_SIZE_MISMATCH", "The attachment size did not match the request.");

        var detectedContentType = UploadValidator.CanonicalContentType(
            UploadValidator.DetectContentType(bytes));
        var contentType = detectedContentType;
        var extension = detectedContentType is null
            ? Path.GetExtension(safeOriginalName).ToLowerInvariant()
            : UploadValidator.CanonicalExtensionForContentType(detectedContentType);

        if (contentType is null)
        {
            if (!UploadValidator.TryResolveAttachmentContentType(safeOriginalName, out contentType))
                return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_TYPE_INVALID", "This attachment type is not permitted.");
            extension = Path.GetExtension(safeOriginalName).ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(extension))
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_TYPE_INVALID", "This attachment type is not permitted.");

        var storedFileName = $"{attachmentId:N}{extension}";
        var storagePath = $"attachments/{storedFileName}";
        var physicalPath = ResolvePhysicalPath(storagePath);
        if (physicalPath is null)
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_PATH_INVALID", "The attachment path is invalid.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
            await File.WriteAllBytesAsync(physicalPath, bytes, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Messaging attachment storage failed. AttachmentId={AttachmentId}", attachmentId);
            return MessagingStoredAttachmentResult.Failure("MESSAGING_ATTACHMENT_STORAGE_FAILED", "The attachment could not be stored.");
        }

        return new MessagingStoredAttachmentResult(
            true,
            null,
            null,
            new MessagingStoredAttachment(
                safeOriginalName,
                storedFileName,
                contentType,
                sizeBytes,
                storagePath));
    }

    public Task<Stream?> OpenReadAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var physicalPath = ResolvePhysicalPath(storagePath);
        if (physicalPath is null || !File.Exists(physicalPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 80 * 1024,
            useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var physicalPath = ResolvePhysicalPath(storagePath);
        if (physicalPath is null || !File.Exists(physicalPath))
            return Task.CompletedTask;

        try
        {
            File.Delete(physicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Messaging attachment deletion failed. StoragePath={StoragePath}", storagePath);
        }

        return Task.CompletedTask;
    }

    private string? ResolvePhysicalPath(string? storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            return null;

        var normalized = storagePath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(_rootPath, normalized));
        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : $"{_rootPath}{Path.DirectorySeparatorChar}";
        return candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? candidate : null;
    }

    private static long ParseMaximumAttachmentBytes(string? configuredValue)
    {
        return long.TryParse(configuredValue, out var parsed) && parsed > 0
            ? parsed
            : DefaultMaximumAttachmentBytes;
    }
}

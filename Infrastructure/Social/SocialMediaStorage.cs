using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Domain.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Social;

internal sealed class SocialMediaStorage : ISocialMediaStorage
{
    private const long DefaultMaximumMediaBytes =
        SocialMediaUploadLimits.MaximumMediaBytes;
    private const int MaximumOriginalFileNameLength = 255;
    private const int CopyBufferSize = 80 * 1024;

    private static readonly IReadOnlyDictionary<string, SupportedSocialMediaType>
        SupportedMediaTypes =
            new Dictionary<string, SupportedSocialMediaType>(
                StringComparer.OrdinalIgnoreCase)
            {
                [".jpg"] = new("Image", "image/jpeg"),
                [".jpeg"] = new("Image", "image/jpeg"),
                [".png"] = new("Image", "image/png"),
                [".webp"] = new("Image", "image/webp"),
                [".heic"] = new("Image", "image/heic"),
                [".heif"] = new("Image", "image/heif"),
                [".mp4"] = new("Video", "video/mp4"),
                [".mov"] = new("Video", "video/quicktime"),
                [".webm"] = new("Video", "video/webm")
            };

    private readonly string _rootPath;
    private readonly long _maximumMediaBytes;
    private readonly BlobContainerClient? _blobContainer;
    private readonly ILogger<SocialMediaStorage> _logger;

    public SocialMediaStorage(
        IConfiguration configuration,
        ILogger<SocialMediaStorage> logger,
        BlobClientOptions? blobClientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var configuredRoot = configuration["Social:Media:RootPath"];

        _rootPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "App_Data",
                    "social-media")
                : configuredRoot.Trim());

        _maximumMediaBytes = ParseMaximumMediaBytes(
            configuration["Social:Media:MaximumBytes"]);

        _blobContainer = BuildBlobContainerClient(
            configuration,
            blobClientOptions);
        _logger = logger;
    }

    public async Task<SocialMediaStorageResult> StoreAsync(
        Guid mediaAssetId,
        string originalFileName,
        long declaredSizeBytes,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (mediaAssetId == Guid.Empty)
        {
            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_ID_INVALID",
                "The social media identifier is invalid.");
        }

        if (content is null || !content.CanRead)
        {
            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_CONTENT_INVALID",
                "The social media content is unavailable.");
        }

        if (declaredSizeBytes <= 0 ||
            declaredSizeBytes > _maximumMediaBytes)
        {
            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_SIZE_INVALID",
                "The social media file size is not permitted.");
        }

        var safeOriginalName = Path.GetFileName(
            originalFileName?.Trim() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(safeOriginalName) ||
            safeOriginalName.Length > MaximumOriginalFileNameLength)
        {
            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_NAME_INVALID",
                "The social media filename is invalid.");
        }

        var extension = Path.GetExtension(safeOriginalName);

        if (!SupportedMediaTypes.TryGetValue(
                extension,
                out var supportedType))
        {
            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_TYPE_INVALID",
                "This social media file type is not permitted.");
        }

        var normalizedExtension = extension.ToLowerInvariant();
        var storedFileName = $"{mediaAssetId:N}{normalizedExtension}";

        // Date partitioning prevents an indefinitely flat storage directory.
        var utcNow = DateTime.UtcNow;
        var storageKey =
            $"originals/{utcNow:yyyy}/{utcNow:MM}/{mediaAssetId:N}/{storedFileName}";

        return _blobContainer is not null
            ? await StoreInBlobAsync(
                storageKey,
                safeOriginalName,
                storedFileName,
                supportedType,
                declaredSizeBytes,
                content,
                cancellationToken)
            : await StoreOnFileSystemAsync(
                storageKey,
                safeOriginalName,
                storedFileName,
                supportedType,
                declaredSizeBytes,
                content,
                cancellationToken);
    }

    private async Task<SocialMediaStorageResult> StoreInBlobAsync(
        string storageKey,
        string originalFileName,
        string storedFileName,
        SupportedSocialMediaType supportedType,
        long declaredSizeBytes,
        Stream content,
        CancellationToken cancellationToken)
    {
        var blobClient = _blobContainer!.GetBlobClient(storageKey);

        try
        {
            long actualSizeBytes;
            await using (var destination = await _blobContainer
                .GetBlockBlobClient(storageKey)
                .OpenWriteAsync(
                    // Azure's streaming BlockBlob writer requires overwrite.
                    // Each object key contains the newly generated media asset ID.
                    overwrite: true,
                    options: new BlockBlobOpenWriteOptions
                    {
                        HttpHeaders = new BlobHttpHeaders
                        {
                            ContentType = supportedType.MimeType
                        }
                    },
                    cancellationToken: cancellationToken))
            {
                actualSizeBytes = await CopyWithLimitAsync(
                    content,
                    destination,
                    _maximumMediaBytes,
                    cancellationToken);
            }

            if (actualSizeBytes != declaredSizeBytes)
            {
                await DeleteBlobIfExistsAsync(blobClient);

                return SocialMediaStorageResult.Failure(
                    "SOCIAL_MEDIA_SIZE_MISMATCH",
                    "The uploaded social media size did not match the request.");
            }

            return CreateStoredMediaResult(
                originalFileName,
                storedFileName,
                supportedType,
                actualSizeBytes,
                storageKey);
        }
        catch (SocialMediaMaximumSizeExceededException)
        {
            await DeleteBlobIfExistsAsync(blobClient);

            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_SIZE_INVALID",
                "The social media file size is not permitted.");
        }
        catch (OperationCanceledException)
        {
            await DeleteBlobIfExistsAsync(blobClient);
            throw;
        }
        catch (Exception ex)
            when (ex is RequestFailedException or IOException or UnauthorizedAccessException)
        {
            await DeleteBlobIfExistsAsync(blobClient);

            _logger.LogError(
                ex,
                "Social media blob storage failed. StorageKey={StorageKey}",
                storageKey);

            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_STORAGE_FAILED",
                "The social media file could not be stored.");
        }
    }

    private async Task<SocialMediaStorageResult> StoreOnFileSystemAsync(
        string storageKey,
        string originalFileName,
        string storedFileName,
        SupportedSocialMediaType supportedType,
        long declaredSizeBytes,
        Stream content,
        CancellationToken cancellationToken)
    {
        var physicalPath = ResolvePhysicalPath(storageKey);

        if (physicalPath is null)
        {
            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_PATH_INVALID",
                "The social media storage path is invalid.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);

            await using var destination = new FileStream(
                physicalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true);

            var actualSizeBytes = await CopyWithLimitAsync(
                content,
                destination,
                _maximumMediaBytes,
                cancellationToken);

            if (actualSizeBytes != declaredSizeBytes)
            {
                await destination.DisposeAsync();
                TryDeletePhysicalFile(physicalPath);

                return SocialMediaStorageResult.Failure(
                    "SOCIAL_MEDIA_SIZE_MISMATCH",
                    "The uploaded social media size did not match the request.");
            }

            return CreateStoredMediaResult(
                originalFileName,
                storedFileName,
                supportedType,
                actualSizeBytes,
                storageKey);
        }
        catch (SocialMediaMaximumSizeExceededException)
        {
            TryDeletePhysicalFile(physicalPath);

            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_SIZE_INVALID",
                "The social media file size is not permitted.");
        }
        catch (OperationCanceledException)
        {
            TryDeletePhysicalFile(physicalPath);
            throw;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeletePhysicalFile(physicalPath);

            _logger.LogError(
                ex,
                "Social media file storage failed. StorageKey={StorageKey}",
                storageKey);

            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_STORAGE_FAILED",
                "The social media file could not be stored.");
        }
    }

    public async Task<SocialMediaReadResult> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_blobContainer is null)
            return await OpenReadFromFileSystem(storageKey);

        var blobClient = _blobContainer.GetBlobClient(storageKey);

        try
        {
            var download = await blobClient.DownloadStreamingAsync(
                cancellationToken: cancellationToken);
            return SocialMediaReadResult.Available(download.Value.Content);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return await MigrateLegacyFileAsync(
                storageKey,
                blobClient,
                cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(
                ex,
                "Social media retrieval failed. StorageKey={StorageKey}",
                storageKey);
            return SocialMediaReadResult.Unavailable();
        }
    }

    public async Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_blobContainer is not null)
        {
            await DeleteBlobIfExistsAsync(
                _blobContainer.GetBlobClient(storageKey));
        }

        DeleteFromFileSystem(storageKey);
    }

    private async Task<SocialMediaReadResult> MigrateLegacyFileAsync(
        string storageKey,
        BlobClient blobClient,
        CancellationToken cancellationToken)
    {
        var physicalPath = ResolvePhysicalPath(storageKey);

        if (physicalPath is null || !File.Exists(physicalPath))
            return SocialMediaReadResult.Missing();

        try
        {
            await using var source = new FileStream(
                physicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                useAsync: true);

            await blobClient.UploadAsync(
                source,
                overwrite: false,
                cancellationToken: cancellationToken);

            var download = await blobClient.DownloadStreamingAsync(
                cancellationToken: cancellationToken);
            return SocialMediaReadResult.Available(download.Value.Content);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var download = await blobClient.DownloadStreamingAsync(
                cancellationToken: cancellationToken);
            return SocialMediaReadResult.Available(download.Value.Content);
        }
        catch (Exception ex)
            when (ex is RequestFailedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Legacy social media could not be migrated to blob storage. StorageKey={StorageKey}",
                storageKey);
            return await OpenReadFromFileSystem(storageKey);
        }
    }

    private Task<SocialMediaReadResult> OpenReadFromFileSystem(string storageKey)
    {
        var physicalPath = ResolvePhysicalPath(storageKey);

        if (physicalPath is null || !File.Exists(physicalPath))
            return Task.FromResult(SocialMediaReadResult.Missing());

        try
        {
            Stream stream = new FileStream(
                physicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                useAsync: true);

            return Task.FromResult(SocialMediaReadResult.Available(stream));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Social media file retrieval failed. StorageKey={StorageKey}",
                storageKey);
            return Task.FromResult(SocialMediaReadResult.Unavailable());
        }
    }

    private void DeleteFromFileSystem(string storageKey)
    {
        var physicalPath = ResolvePhysicalPath(storageKey);

        if (physicalPath is null || !File.Exists(physicalPath))
            return;

        try
        {
            File.Delete(physicalPath);
            DeleteEmptyParentDirectories(physicalPath);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Social media file deletion failed. StorageKey={StorageKey}",
                storageKey);
        }
    }

    private static SocialMediaStorageResult CreateStoredMediaResult(
        string originalFileName,
        string storedFileName,
        SupportedSocialMediaType supportedType,
        long actualSizeBytes,
        string storageKey) =>
        SocialMediaStorageResult.Success(
            new SocialStoredMedia(
                originalFileName,
                storedFileName,
                supportedType.MediaKind,
                supportedType.MimeType,
                actualSizeBytes,
                storageKey));

    private static BlobContainerClient? BuildBlobContainerClient(
        IConfiguration configuration,
        BlobClientOptions? blobClientOptions)
    {
        var connectionString = configuration[
            "Social:Media:StorageConnectionString"];
        var containerName = configuration["Social:Media:ContainerName"];

        if (!string.IsNullOrWhiteSpace(connectionString) &&
            !string.IsNullOrWhiteSpace(containerName))
        {
            return new BlobContainerClient(
                connectionString,
                containerName,
                blobClientOptions);
        }

        var containerUrl = configuration["Social:Media:BlobContainerUrl"];
        return Uri.TryCreate(containerUrl, UriKind.Absolute, out var uri)
            ? new BlobContainerClient(
                uri,
                new DefaultAzureCredential(),
                blobClientOptions)
            : null;
    }

    private async Task DeleteBlobIfExistsAsync(BlobClient blobClient)
    {
        try
        {
            await blobClient.DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: CancellationToken.None);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(
                ex,
                "Social media blob deletion failed. StorageKey={StorageKey}",
                blobClient.Name);
        }
    }

    private string? ResolvePhysicalPath(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;

        var normalized = storageKey
            .Trim()
            .Replace(
                '/',
                Path.DirectorySeparatorChar)
            .Replace(
                '\\',
                Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized))
            return null;

        var candidate = Path.GetFullPath(
            Path.Combine(_rootPath, normalized));

        var rootWithSeparator = _rootPath.EndsWith(
            Path.DirectorySeparatorChar)
                ? _rootPath
                : $"{_rootPath}{Path.DirectorySeparatorChar}";

        return candidate.StartsWith(
            rootWithSeparator,
            StringComparison.Ordinal)
                ? candidate
                : null;
    }

    private static async Task<long> CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long totalBytes = 0;

        while (true)
        {
            var bytesRead = await source.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);

            if (bytesRead == 0)
                return totalBytes;

            totalBytes += bytesRead;

            if (totalBytes > maximumBytes)
                throw new SocialMediaMaximumSizeExceededException();

            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }
    }

    private void DeleteEmptyParentDirectories(string physicalPath)
    {
        var current = Directory.GetParent(physicalPath);

        while (current is not null)
        {
            var currentDirectory = current;
            var currentPath = currentDirectory.FullName;

            if (string.Equals(
                    currentPath,
                    _rootPath,
                    StringComparison.Ordinal) ||
                !currentPath.StartsWith(
                    _rootPath,
                    StringComparison.Ordinal))
            {
                break;
            }

            try
            {
                if (currentDirectory.EnumerateFileSystemInfos().Any())
                    break;

                current = currentDirectory.Parent;
                currentDirectory.Delete();
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(
                    ex,
                    "An empty social media storage directory could not be removed. Path={Path}",
                    currentPath);
                break;
            }
        }
    }

    private static void TryDeletePhysicalFile(string physicalPath)
    {
        try
        {
            if (File.Exists(physicalPath))
                File.Delete(physicalPath);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException)
        {
            // The primary storage failure is returned to the caller. Cleanup
            // failure must not replace that authoritative result.
        }
    }

    private static long ParseMaximumMediaBytes(string? configuredValue)
    {
        return long.TryParse(configuredValue, out var parsed) && parsed > 0
            ? parsed
            : DefaultMaximumMediaBytes;
    }

    private sealed record SupportedSocialMediaType(
        string MediaKind,
        string MimeType);

    private sealed class SocialMediaMaximumSizeExceededException : Exception
    {
    }
}

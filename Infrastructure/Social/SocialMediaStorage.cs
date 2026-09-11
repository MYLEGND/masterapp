using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Domain.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Social;

internal sealed class SocialMediaStorage : ISocialMediaStorage, ISocialMediaVideoProcessor
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
    private readonly BlobContainerClient? _legacyBlobContainer;
    private readonly ILogger<SocialMediaStorage> _logger;
    private readonly LocalFfmpegSocialVideoProcessor _videoProcessor;

    public SocialMediaStorage(
        IConfiguration configuration,
        ILogger<SocialMediaStorage> logger,
        BlobClientOptions? blobClientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _rootPath = ResolveRootPath(configuration["Social:Media:RootPath"]);

        _maximumMediaBytes = ParseMaximumMediaBytes(
            configuration["Social:Media:MaximumBytes"]);

        _blobContainer = BuildBlobContainerClient(
            configuration,
            blobClientOptions);
        _legacyBlobContainer = BuildLegacyBlobContainerClient(
            configuration,
            blobClientOptions);
        _logger = logger;
        _videoProcessor = new LocalFfmpegSocialVideoProcessor(
            configuration,
            logger);
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
                    durationProbe: supportedType.MediaKind == "Video" ? new Mp4HeaderDurationProbe() : null,
                    cancellationToken: cancellationToken);
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
        catch (SocialVideoDurationExceededException)
        {
            await DeleteBlobIfExistsAsync(blobClient);
            return SocialMediaStorageResult.Failure("SOCIAL_VIDEO_DURATION_EXCEEDED", "Videos must be 10 minutes or less.");
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
            when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            await DeleteBlobIfExistsAsync(blobClient);

            _logger.LogError(
                ex,
                "Social media blob credentials could not be authenticated. StorageKey={StorageKey}",
                storageKey);

            return SocialMediaStorageResult.Failure(
                "SOCIAL_MEDIA_STORAGE_UNAVAILABLE",
                "Legend media storage is temporarily unavailable. Please try again shortly.");
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

            long actualSizeBytes;
            await using (var destination = new FileStream(
                physicalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true))
            {
                var durationProbe = string.Equals(
                    supportedType.MediaKind,
                    "Video",
                    StringComparison.Ordinal)
                    ? new Mp4HeaderDurationProbe()
                    : null;
                actualSizeBytes = await CopyWithLimitAsync(
                    content,
                    destination,
                    _maximumMediaBytes,
                    durationProbe,
                    cancellationToken);
            }

            if (actualSizeBytes != declaredSizeBytes)
            {
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
        catch (SocialVideoDurationExceededException)
        {
            TryDeletePhysicalFile(physicalPath);

            return SocialMediaStorageResult.Failure(
                "SOCIAL_VIDEO_DURATION_EXCEEDED",
                "Videos must be 10 minutes or less.");
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
        {
            var local = await OpenReadFromFileSystem(storageKey);
            return local.Status == SocialMediaReadStatus.Missing &&
                   _legacyBlobContainer is not null
                ? await MigrateLegacyBlobToFileSystemAsync(
                    storageKey,
                    cancellationToken)
                : local;
        }

        var blobClient = _blobContainer.GetBlobClient(storageKey);

        try
        {
            return SocialMediaReadResult.Available(
                await OpenBlobDeliveryReadAsync(blobClient, cancellationToken));
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

    /// <summary>
    /// Finalizes one already-persisted video through the existing local processor. This is intentionally separate
    /// from StoreAsync so a request releases its socket as soon as storage is
    /// durable, while exactly one hosted worker owns FFmpeg execution.
    /// </summary>
    public async Task<SocialMediaVideoProcessingResult> ProcessAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_blobContainer is not null)
            return await ProcessBlobVideoAsync(storageKey, cancellationToken);

        var physicalPath = ResolvePhysicalPath(storageKey);
        if (physicalPath is null)
        {
            return SocialMediaVideoProcessingResult.Failure(
                "SOCIAL_VIDEO_PATH_INVALID",
                "Legend could not prepare the uploaded video for delivery.");
        }

        var result = await _videoProcessor.OptimizeAsync(
            physicalPath,
            cancellationToken);
        return result.Succeeded && result.FileSizeBytes is { } size
            ? SocialMediaVideoProcessingResult.Success(size)
            : SocialMediaVideoProcessingResult.Failure(
                result.ErrorCode ?? "SOCIAL_VIDEO_PROCESSING_FAILED",
                result.ErrorMessage ?? "Legend could not optimize this video for playback.");
    }

    private async Task<SocialMediaVideoProcessingResult> ProcessBlobVideoAsync(
        string storageKey, CancellationToken cancellationToken)
    {
        // Validate against the same key boundary as local storage; a blob name
        // never becomes a caller-selected temporary filesystem path.
        if (ResolvePhysicalPath(storageKey) == null)
            return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_PATH_INVALID", "The video storage path is invalid.");
        var workDirectory = Path.Combine(_rootPath, ".processing", Guid.NewGuid().ToString("N"));
        var workFile = Path.Combine(workDirectory, "source.mp4");
        var blob = _blobContainer!.GetBlobClient(storageKey);
        try
        {
            BlobDownloadStreamingResult download;
            try
            {
                download = (await blob.DownloadStreamingAsync(cancellationToken: cancellationToken)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Reuse the established disk-to-blob migration. A local fallback
                // stream alone is not sufficient for a conditional blob commit.
                var legacyPath = ResolvePhysicalPath(storageKey)!;
                if (File.Exists(legacyPath) && new FileInfo(legacyPath).Length is var legacySize &&
                    (legacySize <= 0 || legacySize > _maximumMediaBytes))
                    return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_SIZE_INVALID", "The stored video size is not permitted.");
                var migrated = await MigrateLegacyFileAsync(storageKey, blob, cancellationToken);
                if (migrated.Content != null) await migrated.Content.DisposeAsync();
                download = (await blob.DownloadStreamingAsync(cancellationToken: cancellationToken)).Value;
            }
            await using (var source = download.Content)
            {
                if (download.Details.ContentLength <= 0 || download.Details.ContentLength > _maximumMediaBytes ||
                    string.IsNullOrEmpty(download.Details.ETag.ToString()))
                    return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_SIZE_INVALID", "The stored video size is not permitted.");
                Directory.CreateDirectory(workDirectory);
                await using var destination = new FileStream(workFile, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
                var actualSize = await CopyWithLimitAsync(source, destination, _maximumMediaBytes,
                    new Mp4HeaderDurationProbe(), cancellationToken);
                if (actualSize != download.Details.ContentLength)
                    return SocialMediaVideoProcessingResult.Failure("SOCIAL_MEDIA_SIZE_MISMATCH", "The stored video download was incomplete.");
            }
            var processed = await _videoProcessor.OptimizeAsync(workFile, cancellationToken);
            if (!processed.Succeeded || processed.FileSizeBytes is not long outputSize)
                return SocialMediaVideoProcessingResult.Failure(processed.ErrorCode ?? "SOCIAL_VIDEO_PROCESSING_FAILED",
                    processed.ErrorMessage ?? "Legend could not optimize this video for playback.");
            if (outputSize <= 0 || outputSize > _maximumMediaBytes)
                return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_SIZE_INVALID", "The optimized video size is not permitted.");
            await using var optimized = new FileStream(workFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, CopyBufferSize, useAsync: true);
            await blob.UploadAsync(optimized, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = download.Details.ETag },
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = download.Details.ContentType,
                    CacheControl = download.Details.CacheControl,
                    ContentDisposition = download.Details.ContentDisposition,
                    ContentEncoding = download.Details.ContentEncoding,
                    ContentLanguage = download.Details.ContentLanguage
                },
                Metadata = download.Details.Metadata
            }, cancellationToken);
            return SocialMediaVideoProcessingResult.Success(outputSize);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_SOURCE_CHANGED", "The stored video changed during processing. Please retry.");
        }
        catch (SocialMediaMaximumSizeExceededException)
        {
            return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_SIZE_INVALID", "The stored video size is not permitted.");
        }
        catch (SocialVideoDurationExceededException)
        {
            return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_DURATION_EXCEEDED", "Videos must be 10 minutes or less.");
        }
        catch (Exception ex) when (ex is RequestFailedException or IOException or UnauthorizedAccessException or
            AuthenticationFailedException or CredentialUnavailableException)
        {
            _logger.LogWarning("Blob video processing could not complete. FailureType={FailureType}", ex.GetType().Name);
            return SocialMediaVideoProcessingResult.Failure("SOCIAL_VIDEO_PROCESSING_FAILED", "Legend could not finalize this video for playback. Please retry.");
        }
        finally
        {
            // Delete only this operation's unique workspace, never the blob or
            // the legacy disk original. The processor cleans its own output.
            try { if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("A temporary video processing directory could not be removed.");
            }
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

        // A legacy Blob object is retained only until it has been read through
        // to the local single-server store. Deletion must remove it too, so a
        // deleted post can never be revived by the compatibility bridge.
        if (_legacyBlobContainer is not null)
        {
            await DeleteBlobIfExistsAsync(
                _legacyBlobContainer.GetBlobClient(storageKey));
        }

        DeleteFromFileSystem(storageKey);
    }

    private async Task<SocialMediaReadResult> MigrateLegacyBlobToFileSystemAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        var physicalPath = ResolvePhysicalPath(storageKey);
        if (physicalPath is null)
            return SocialMediaReadResult.Missing();

        var stagedPath = $"{physicalPath}.legacy-{Guid.NewGuid():N}";
        var blobClient = _legacyBlobContainer!.GetBlobClient(storageKey);

        try
        {
            var download = await blobClient.DownloadStreamingAsync(
                cancellationToken: cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);

            await using (var source = download.Value.Content)
            await using (var destination = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true))
            {
                await CopyWithLimitAsync(
                    source,
                    destination,
                    _maximumMediaBytes,
                    durationProbe: null,
                    cancellationToken: cancellationToken);
            }

            try
            {
                // The staging file and target are under the same persistent
                // root, so the promotion is atomic. A concurrent request that
                // won the race simply serves the already-promoted local copy.
                File.Move(stagedPath, physicalPath);
            }
            catch (IOException) when (File.Exists(physicalPath))
            {
                TryDeletePhysicalFile(stagedPath);
            }

            return await OpenReadFromFileSystem(storageKey);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return SocialMediaReadResult.Missing();
        }
        catch (SocialMediaMaximumSizeExceededException)
        {
            TryDeletePhysicalFile(stagedPath);
            _logger.LogWarning(
                "Legacy social media exceeds the configured limit. StorageKey={StorageKey}",
                storageKey);
            return SocialMediaReadResult.Unavailable();
        }
        catch (Exception ex)
            when (ex is RequestFailedException or IOException or UnauthorizedAccessException)
        {
            TryDeletePhysicalFile(stagedPath);
            _logger.LogError(
                ex,
                "Legacy social media could not be migrated to local storage. StorageKey={StorageKey}",
                storageKey);
            return SocialMediaReadResult.Unavailable();
        }
    }

    private static Task<Stream> OpenBlobDeliveryReadAsync(
        BlobClient blobClient, CancellationToken cancellationToken) =>
        // MVC needs a seekable stream to honor HTTP byte ranges. OpenRead uses
        // blob properties for Length and fetches only bounded ranges on demand;
        // the captured ETag prevents mixing versions across subsequent seeks.
        // Reuse the existing 80 KiB copy bound rather than buffering a whole
        // media object. Sequential reads trade more blob requests for that
        // fixed per-reader memory/overfetch bound.
        blobClient.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false)
        {
            BufferSize = CopyBufferSize
        }, cancellationToken);

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

            return SocialMediaReadResult.Available(
                await OpenBlobDeliveryReadAsync(blobClient, cancellationToken));
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return SocialMediaReadResult.Available(
                await OpenBlobDeliveryReadAsync(blobClient, cancellationToken));
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
                storageKey,
                RequiresBackgroundProcessing: string.Equals(
                    supportedType.MediaKind,
                    "Video",
                    StringComparison.Ordinal)));

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

    private static BlobContainerClient? BuildLegacyBlobContainerClient(
        IConfiguration configuration,
        BlobClientOptions? blobClientOptions)
    {
        var containerUrl = configuration["Social:Media:LegacyBlobContainerUrl"];
        return Uri.TryCreate(containerUrl, UriKind.Absolute, out var uri)
            ? new BlobContainerClient(
                uri,
                new DefaultAzureCredential(),
                blobClientOptions)
            : null;
    }

    private static string ResolveRootPath(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.GetFullPath(configuredRoot.Trim());

        var azureHome = Environment.GetEnvironmentVariable("HOME");
        var azureSiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (!string.IsNullOrWhiteSpace(azureHome) &&
            !string.IsNullOrWhiteSpace(azureSiteName))
        {
            // %HOME%/data survives ZipDeploy. Do not store user media below
            // wwwroot: deployment cleanup would otherwise remove it.
            return Path.GetFullPath(Path.Combine(
                azureHome,
                "data",
                "legend-social-media"));
        }

        return Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "App_Data",
            "social-media"));
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
        Mp4HeaderDurationProbe? durationProbe,
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

            durationProbe?.Inspect(buffer.AsSpan(0, bytesRead));
            if (durationProbe?.DurationSeconds > SocialMediaUploadLimits.MaximumVideoDurationSeconds)
                throw new SocialVideoDurationExceededException();

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

    private sealed class SocialVideoDurationExceededException : Exception
    {
    }
}

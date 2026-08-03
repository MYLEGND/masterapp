using System.Threading.Channels;
using Domain.Entities;
using Domain.Social;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Social;

/// <summary>
/// The only executor for post-ingest local video normalization. Multipart
/// requests persist complete bytes and return; this single sequential worker
/// changes the existing asset lifecycle from PendingProcessing to Ready or
/// Failed. The durable database state is swept at startup and periodically, so
/// an App Service recycle cannot lose a queued video.
/// </summary>
internal sealed class SocialMediaProcessingWorker : BackgroundService, ISocialMediaProcessingQueue
{
    private static readonly TimeSpan RecoverySweepInterval = TimeSpan.FromSeconds(20);

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISocialMediaVideoProcessor _videoProcessor;
    private readonly ILogger<SocialMediaProcessingWorker> _logger;

    public SocialMediaProcessingWorker(
        IServiceScopeFactory scopeFactory,
        ISocialMediaVideoProcessor videoProcessor,
        ILogger<SocialMediaProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _videoProcessor = videoProcessor;
        _logger = logger;
    }

    public void Enqueue(Guid mediaAssetId)
    {
        if (mediaAssetId != Guid.Empty)
            _queue.Writer.TryWrite(mediaAssetId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueueDurableWorkAsync(stoppingToken);

        using var timer = new PeriodicTimer(RecoverySweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            while (_queue.Reader.TryRead(out var mediaAssetId))
                await ProcessAsync(mediaAssetId, stoppingToken);

            var hasQueuedWork = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var recoveryDue = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            var completed = await Task.WhenAny(hasQueuedWork, recoveryDue);

            if (completed == recoveryDue && await recoveryDue)
                await EnqueueDurableWorkAsync(stoppingToken);
            else
                await hasQueuedWork;
        }
    }

    private async Task EnqueueDurableWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var pendingAssetIds = await db.SocialPostMediaAssets
            .AsNoTracking()
            .Where(asset =>
                asset.MediaKind == "Video" &&
                (asset.ProcessingState == SocialMediaProcessingStates.PendingProcessing ||
                 asset.ProcessingState == SocialMediaProcessingStates.Processing) &&
                asset.SocialPost.DeletedUtc == null)
            .Select(asset => asset.Id)
            .ToArrayAsync(cancellationToken);

        foreach (var mediaAssetId in pendingAssetIds)
            _queue.Writer.TryWrite(mediaAssetId);
    }

    private async Task ProcessAsync(Guid mediaAssetId, CancellationToken cancellationToken)
    {
        string? storageKey;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var asset = await db.SocialPostMediaAssets
                .Include(item => item.SocialPost)
                .SingleOrDefaultAsync(item => item.Id == mediaAssetId, cancellationToken);

            if (asset is null ||
                asset.SocialPost.DeletedUtc is not null ||
                asset.MediaKind != "Video" ||
                (asset.ProcessingState != SocialMediaProcessingStates.PendingProcessing &&
                 asset.ProcessingState != SocialMediaProcessingStates.Processing))
            {
                return;
            }

            asset.ProcessingState = SocialMediaProcessingStates.Processing;
            asset.UpdatedUtc = DateTime.UtcNow;
            storageKey = asset.StorageKey;
            await db.SaveChangesAsync(cancellationToken);
        }

        var result = await _videoProcessor.ProcessAsync(storageKey!, cancellationToken);

        await using var completionScope = _scopeFactory.CreateAsyncScope();
        var completionDb = completionScope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var completedAsset = await completionDb.SocialPostMediaAssets
            .Include(item => item.SocialPost)
            .SingleOrDefaultAsync(item => item.Id == mediaAssetId, cancellationToken);
        if (completedAsset is null || completedAsset.SocialPost.DeletedUtc is not null)
            return;

        completedAsset.ProcessingState = result.Succeeded
            ? SocialMediaProcessingStates.Ready
            : SocialMediaProcessingStates.Failed;
        completedAsset.UpdatedUtc = DateTime.UtcNow;
        if (result.FileSizeBytes is { } fileSizeBytes)
            completedAsset.FileSizeBytes = fileSizeBytes;

        await completionDb.SaveChangesAsync(cancellationToken);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Legend social video processing failed. AssetId={MediaAssetId} Code={ErrorCode}",
                mediaAssetId,
                result.ErrorCode);
        }
    }
}

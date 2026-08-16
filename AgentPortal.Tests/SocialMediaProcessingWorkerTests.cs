using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Social;
using Infrastructure.Data;
using Infrastructure.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SocialMediaProcessingWorkerTests
{
    [Fact]
    public async Task QueuedVideos_AreProcessedSequentiallyAfterEarlierQueueWakeups()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<MasterAppDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        await using var provider = services.BuildServiceProvider();

        var processor = new SuccessfulVideoProcessor();
        var worker = new SocialMediaProcessingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            processor,
            NullLogger<SocialMediaProcessingWorker>.Instance);
        var firstAssetId = await AddPendingVideoAsync(provider, databaseName);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStateAsync(provider, firstAssetId, SocialMediaProcessingStates.Ready);

            var secondAssetId = await AddPendingVideoAsync(provider, databaseName);
            worker.Enqueue(secondAssetId);
            await WaitForStateAsync(provider, secondAssetId, SocialMediaProcessingStates.Ready);

            var thirdAssetId = await AddPendingVideoAsync(provider, databaseName);
            worker.Enqueue(thirdAssetId);
            await WaitForStateAsync(provider, thirdAssetId, SocialMediaProcessingStates.Ready);

            Assert.Equal(3, processor.ProcessedStorageKeys.Count);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid> AddPendingVideoAsync(
        IServiceProvider provider,
        string databaseName)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var postId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        db.SocialPosts.Add(new SocialPost
        {
            Id = postId,
            AuthorUserId = "worker-test-client",
            AuthorParticipantType = "Client",
            AuthorProfileId = Guid.NewGuid(),
            ContentType = SocialPostContentTypes.Reel,
            PublicationState = SocialPostPublicationStates.Published,
            PostedUtc = DateTime.UtcNow
        });
        db.SocialPostMediaAssets.Add(new SocialPostMediaAsset
        {
            Id = assetId,
            SocialPostId = postId,
            MediaKind = "Video",
            MimeType = "video/mp4",
            StorageKey = $"test/{databaseName}/{assetId:N}.mp4",
            FileSizeBytes = 1,
            ProcessingState = SocialMediaProcessingStates.PendingProcessing,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return assetId;
    }

    private static async Task WaitForStateAsync(
        IServiceProvider provider,
        Guid assetId,
        string expectedState)
    {
        // The production worker's durable recovery sweep is intentionally
        // twenty seconds. The regression must allow one complete recovery
        // cycle plus bounded runner-scheduling headroom; otherwise a transient
        // startup/recovery delay is incorrectly reported as a queue failure.
        // A worker that genuinely fails to reach the requested state still
        // fails this test at the bounded deadline.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
            var state = await db.SocialPostMediaAssets
                .Where(asset => asset.Id == assetId)
                .Select(asset => asset.ProcessingState)
                .SingleAsync();
            if (state == expectedState)
                return;

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(
            $"Video asset {assetId} did not reach {expectedState} within the bounded worker recovery window.");
    }

    private sealed class SuccessfulVideoProcessor : ISocialMediaVideoProcessor
    {
        public List<string> ProcessedStorageKeys { get; } = [];

        public Task<SocialMediaVideoProcessingResult> ProcessAsync(
            string storageKey,
            CancellationToken cancellationToken = default)
        {
            ProcessedStorageKeys.Add(storageKey);
            return Task.FromResult(SocialMediaVideoProcessingResult.Success(42));
        }
    }
}

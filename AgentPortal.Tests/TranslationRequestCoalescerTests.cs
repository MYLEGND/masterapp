using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class TranslationRequestCoalescerTests
{
    [Fact]
    public async Task ConcurrentReorderedBatches_PreserveEachCallersResultOrder()
    {
        var language = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        language.Setup(item => item.NormalizeEnabledTranslationLanguageAsync(
            It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? value, CancellationToken _) => value);
        var provider = new Mock<ITranslationProvider>(MockBehavior.Strict);
        provider.SetupGet(item => item.ProviderName).Returns("AzureTranslator");
        provider.SetupGet(item => item.ProviderVersion).Returns("test-v1");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intelligence = new Mock<ILegendConnectTranslationIntelligence>(MockBehavior.Strict);
        intelligence.Setup(item => item.TryGetTrustedScopedMemoriesAsync(
            It.IsAny<IReadOnlyList<LegendTrustedTranslationLookup>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<LegendTrustedTranslationLookup> lookups, CancellationToken _) =>
            {
                await release.Task;
                return (IReadOnlyDictionary<string, LegendTranslationMemoryMatch>)lookups.ToDictionary(
                    item => item.Key,
                    item => new LegendTranslationMemoryMatch("[ht] " + item.SourceText, 1m, "HumanValidated", "Approved"));
            });
        intelligence.Setup(item => item.TryGetRetainedTranslationsAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, LegendRetainedTranslationMemoryMatch>());
        var router = new LegendConnectTranslationRouter(provider.Object, language.Object,
            new Mock<ITranslationCapacityAuthority>(MockBehavior.Strict).Object,
            NullLogger<LegendConnectTranslationRouter>.Instance, intelligence: intelligence.Object);
        var a = new RetainedTranslationRequest("a", "First", "en", "ht", "1", "interface", "", TranslationReuseScopes.Global);
        var b = a with { StableSourceContentId = "b", SourceText = "Second" };
        var first = router.TranslateRetainedBatchAsync([a, b]);
        var reversed = router.TranslateRetainedBatchAsync([b, a]);
        release.SetResult();
        Assert.Equal(new[] { "[ht] First", "[ht] Second" }, (await first).Select(item => item.Text));
        Assert.Equal(new[] { "[ht] Second", "[ht] First" }, (await reversed).Select(item => item.Text));
        provider.Verify(item => item.TranslateBatchAsync(It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotReleaseFenceOrCancelOwner()
    {
        var coalescer = new TranslationRequestCoalescer();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<string> Translate()
        {
            Interlocked.Increment(ref calls);
            return completion.Task;
        }
        var owner = coalescer.ExecuteAsync("user-a:source:en:ht", Translate);
        using var waiterCancellation = new CancellationTokenSource();
        var waiter = coalescer.ExecuteAsync("user-a:source:en:ht", Translate, waiterCancellation.Token);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        var nextWaiter = coalescer.ExecuteAsync("user-a:source:en:ht", Translate);
        Assert.Equal(1, calls);
        completion.SetResult("translation");
        Assert.False((await owner).JoinedExistingRequest);
        Assert.True((await nextWaiter).JoinedExistingRequest);
        Assert.Equal(1, calls);
        await coalescer.ExecuteAsync("user-a:source:en:ht", Translate);
        Assert.Equal(2, calls); // This authority caches only in-flight work.
    }

    [Fact]
    public async Task DifferentScopes_DoNotShareResults_AndFailureReleasesFence()
    {
        var coalescer = new TranslationRequestCoalescer();
        var firstCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coalescer.ExecuteAsync("user-a", () => firstCompletion.Task);
        var second = await coalescer.ExecuteAsync("user-b", () => Task.FromResult("private-b"));
        Assert.Equal("private-b", second.Result);
        Assert.False(second.JoinedExistingRequest);
        firstCompletion.SetException(new InvalidOperationException("provider unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first);
        var retry = await coalescer.ExecuteAsync("user-a", () => Task.FromResult("private-a"));
        Assert.Equal("private-a", retry.Result);
        Assert.False(retry.JoinedExistingRequest);
    }
}

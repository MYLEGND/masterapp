using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class UnifiedMarketingPerformanceIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderTransportFailuresPreserveCanonicalOpenAiOutcomesWithoutMetaRows(bool timeout)
    {
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var scope = ScopeContext.ForAgent(owner.AgentTrackingProfileId!.Value);
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-1), ToUtc = DateTime.UtcNow };
        var connections = new Mock<IOpenAiAdsAccountConnectionAuthority>();
        connections.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "account", "Account", null, "approved", "api_key", null, null,
            Array.Empty<string>(), "pixel", "source", true, true, DateTime.UtcNow, null, DateTime.UtcNow));
        var openai = new Mock<IOpenAiAdsExecutionService>();
        Exception failure = timeout ? new TaskCanceledException("provider timeout") : new HttpRequestException("provider unavailable");
        openai.Setup(x => x.GetAccountInsightsAsync(owner, "campaign", It.IsAny<OpenAiAdsInsightsQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var meta = new Mock<IMetaAdsService>();
        meta.Setup(x => x.GetCampaignsAsync(range, scope, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var paid = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", EventUtc = DateTime.UtcNow, AgentTrackingProfileId = owner.AgentTrackingProfileId,
            IsServerAuthority = true, Oppref = "openai-current-click", Metadata = new { valueCents = 12500 }
        });
        paid.Id = 1;
        var direct = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", EventUtc = DateTime.UtcNow, AgentTrackingProfileId = owner.AgentTrackingProfileId,
            IsServerAuthority = true, Metadata = new { valueCents = 99900 }
        });
        direct.Id = 2;
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.LoadAttributedEventsAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyticsEvent> { paid, direct, paid });
        var result = await new UnifiedMarketingPerformanceService(openai.Object, connections.Object, analytics.Object, meta.Object)
            .GetAsync(owner, scope, range);
        Assert.Equal(1, result.ChatGptAdsOutcomes.Customers);
        Assert.Equal(125m, result.ChatGptAdsOutcomes.Revenue);
        Assert.Equal(2, result.DataQualityNotes.Count);
        meta.Verify(x => x.GetCampaignsAsync(range, scope, It.IsAny<CancellationToken>()), Times.Once);
    }
}

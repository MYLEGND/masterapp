using System;
using System.Collections.Generic;
using System.Linq;
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
    [Fact]
    public async Task ReportingUsesCanonicalFieldsAndDisclosesCompletedHourWindow()
    {
        var owner = MarketingOwnerScope.Founder;
        var scope = ScopeContext.ForAgent(Guid.NewGuid());
        var from = new DateTime(2026, 9, 20, 7, 12, 0, DateTimeKind.Utc);
        var to = from.AddHours(3);
        var range = new TimeRangeRequest { FromUtc = from, ToUtc = to };
        var connections = new Mock<IOpenAiAdsAccountConnectionAuthority>();
        connections.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "account", "Account", null, "approved", "api_key", null, null,
            Array.Empty<string>(), "pixel", "source", true, true, DateTime.UtcNow, null, DateTime.UtcNow));
        var openai = new Mock<IOpenAiAdsExecutionService>();
        using var payload = System.Text.Json.JsonDocument.Parse("""{"data":[{"campaign_id":"cmpn_1","spend":12.5,"impressions":100,"clicks":4}]}""");
        openai.Setup(x => x.GetAccountInsightsAsync(owner, "campaign", It.IsAny<OpenAiAdsInsightsQuery>(), It.IsAny<CancellationToken>()))
            .Callback<MarketingOwnerScope, string, OpenAiAdsInsightsQuery, CancellationToken>((_, _, query, _) => {
                Assert.Equal(new[] { "campaign.spend", "campaign.impressions", "campaign.clicks", "campaign.id", "campaign.name", "campaign.status" }, query.Fields);
                Assert.Equal(from, query.FromUtc);
                Assert.Equal(to, query.ToUtc);
            })
            .ReturnsAsync(new OpenAiAdsInsightsResult(payload.RootElement.Clone(), from.Date.AddHours(8), from.Date.AddHours(10)));
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.LoadAttributedEventsAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyticsEvent>());
        var meta = new Mock<IMetaAdsService>();
        meta.Setup(x => x.GetCampaignsAsync(range, scope, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Meta unavailable"));
        var external = new Mock<IMarketingExternalAdsReportingService>();
        external.Setup(x => x.GetCampaignsAsync(owner, It.IsAny<string>(), range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalAdsCampaignReport(
                Array.Empty<ProviderDeliveryMetricRow>(), "UTC", range.FromUtc.Date, range.ToUtc.Date));
        var result = await new UnifiedMarketingPerformanceService(openai.Object, connections.Object, analytics.Object, meta.Object, external.Object)
            .GetAsync(owner, scope, range);
        Assert.Contains(result.DataQualityNotes, x => x.Contains("completed account-local hours", StringComparison.Ordinal));
        analytics.Verify(x => x.LoadAttributedEventsAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderTransportFailuresPreserveCanonicalOutcomesAndReportUnknownEconomics(bool timeout)
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
            IsServerAuthority = true, Oppref = "openai-current-click", Metadata = new { valueCents = 12500, clientUserId = "customer-one" }
        });
        paid.Id = 1;
        var direct = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", EventUtc = DateTime.UtcNow, AgentTrackingProfileId = owner.AgentTrackingProfileId,
            IsServerAuthority = true, Metadata = new { valueCents = 99900, clientUserId = "customer-one" }
        });
        direct.Id = 2;
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.LoadAttributedEventsAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AnalyticsEvent> { paid, direct, paid });
        var external = new Mock<IMarketingExternalAdsReportingService>();
        external.Setup(x => x.GetCampaignsAsync(owner, It.IsAny<string>(), range, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalAdsCampaignReport(
                Array.Empty<ProviderDeliveryMetricRow>(), "UTC", range.FromUtc.Date, range.ToUtc.Date));
        var result = await new UnifiedMarketingPerformanceService(openai.Object, connections.Object, analytics.Object, meta.Object, external.Object)
            .GetAsync(owner, scope, range);
        Assert.Equal(1, result.ChatGptAdsOutcomes.Customers);
        Assert.Equal(125m, result.ChatGptAdsOutcomes.Revenue);
        foreach (var row in result.Channels.Where(x => x.Channel is MarketingChannels.ChatGptAds or MarketingChannels.MetaAds))
        {
            Assert.Null(row.Spend); Assert.Null(row.Roas);
        }
        var performance = new Mock<IUnifiedMarketingPerformanceService>();
        performance.Setup(x => x.GetAsync(owner, scope, range, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var economics = await new BlendedGrowthEconomicsService(performance.Object).GetAsync(owner, scope, range);
        Assert.Same(result.Economics, economics);
        analytics.Verify(x => x.LoadAttributedEventsAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, economics.CustomersAcquired);
        Assert.Null(economics.TotalMarketingSpend); Assert.Null(economics.BlendedRoas); Assert.Null(economics.CostPerCustomer);
        Assert.Equal(4, result.DataQualityNotes.Count);
        Assert.Contains(result.DataQualityNotes, note =>
            note.Contains("ChatGPT Ads delivery metrics are temporarily unavailable", StringComparison.Ordinal));
        Assert.Contains(result.DataQualityNotes, note =>
            note.Contains("Meta Ads comparison is unavailable", StringComparison.Ordinal));
        Assert.Contains(result.DataQualityNotes, note =>
            note.Contains("Google Ads reporting uses account-local dates", StringComparison.Ordinal));
        Assert.Contains(result.DataQualityNotes, note =>
            note.Contains("TikTok Ads reporting uses account-local dates", StringComparison.Ordinal));
        meta.Verify(x => x.GetCampaignsAsync(range, scope, It.IsAny<CancellationToken>()), Times.Once);
    }
}

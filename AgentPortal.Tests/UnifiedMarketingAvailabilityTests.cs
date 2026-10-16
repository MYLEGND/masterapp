using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Analytics;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class UnifiedMarketingAvailabilityTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[],\"has_more\":true}")]
    [InlineData("{\"data\":[{\"spend\":0,\"impressions\":0,\"clicks\":0}]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"impressions\":0,\"clicks\":0}]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"spend\":0,\"clicks\":0}]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"spend\":0,\"impressions\":0}]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"spend\":\"invalid\",\"impressions\":0,\"clicks\":0}]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"spend\":0,\"impressions\":1.5,\"clicks\":0}]}")]
    public async Task InvalidOrIncompleteReportIsUnavailable(string json)
    {
        var result = await Read(json);
        var row = result.Channels.Single(x => x.Channel == MarketingChannels.ChatGptAds);
        Assert.Null(row.Spend); Assert.Null(row.Clicks); Assert.Null(row.Impressions);
        Assert.Empty(result.ProviderDelivery);
        Assert.Contains(result.DataQualityNotes, x => x.StartsWith("ChatGPT Ads delivery metrics are temporarily unavailable:"));
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"spend\":0,\"impressions\":0,\"clicks\":0}]}")]
    [InlineData("{\"data\":[{\"campaign_id\":\"c\",\"spend\":\"0\",\"impressions\":\"0\",\"clicks\":\"0\"}],\"has_more\":false}")]
    public async Task VerifiedZeroRemainsZeroAndUnrequestedConversionsRemainUnknown(string json)
    {
        var result = await Read(json);
        var row = result.Channels.Single(x => x.Channel == MarketingChannels.ChatGptAds);
        Assert.Equal(0m, row.Spend); Assert.Equal(0L, row.Clicks); Assert.Equal(0L, row.Impressions);
        Assert.All(result.ProviderDelivery, x => Assert.Null(x.Conversions));
        foreach (var unavailable in result.Channels.Where(x => x.Channel is MarketingChannels.MetaAds or MarketingChannels.GoogleAds or MarketingChannels.TikTokAds))
        { Assert.Null(unavailable.Spend); Assert.Null(unavailable.Impressions); Assert.Null(unavailable.Clicks); }
    }

    [Theory]
    [InlineData("spend_amount")]
    [InlineData("cost")]
    public async Task SupportedSpendAliasRemainsAvailable(string field)
    {
        var result = await Read("{\"data\":[{\"campaign_id\":\"c\",\"" + field + "\":\"12.5\",\"impressions\":100,\"clicks\":4}]}");
        Assert.Equal(12.5m, result.Channels.Single(x => x.Channel == MarketingChannels.ChatGptAds).Spend);
    }

    [Fact]
    public async Task ExplicitConversionsZeroSurvivesAndAiRedactionPreservesUnknown()
    {
        var result = await Read("""{"data":[{"campaign_id":"c","spend":"12.5","impressions":"100","clicks":"4","conversions":0}]}""");
        Assert.Equal(0L, Assert.Single(result.ProviderDelivery).Conversions);
        var safe = WebsiteAnalyticsAiRedactor.Redact(new AiSafeAnalyticsPayload {
            Channels = [new(MarketingChannels.ChatGptAds, null, null, null, 0, 0, 0, 0, 0, null, "unavailable")],
            PaidCampaigns = [new() { Channel = MarketingChannels.ChatGptAds, ProviderConversions = null }, new() { Channel = MarketingChannels.ChatGptAds, ProviderConversions = 0 }]
        });
        Assert.Null(Assert.Single(safe.Channels).Clicks); Assert.Null(safe.Channels[0].Impressions);
        Assert.Null(safe.PaidCampaigns[0].ProviderConversions); Assert.Equal(0L, safe.PaidCampaigns[1].ProviderConversions);
    }

    private static async Task<UnifiedChannelPerformanceSnapshot> Read(string json)
    {
        var owner = MarketingOwnerScope.Founder;
        var scope = ScopeContext.ForAgent(Guid.NewGuid());
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-1), ToUtc = DateTime.UtcNow };
        var connection = new Mock<IOpenAiAdsAccountConnectionAuthority>();
        connection.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "account", "Account", null, "approved", "api_key", null, null,
            [], "pixel", "source", true, true, DateTime.UtcNow, null, DateTime.UtcNow));
        var openai = new Mock<IOpenAiAdsExecutionService>();
        using var payload = JsonDocument.Parse(json);
        openai.Setup(x => x.GetAccountInsightsAsync(owner, "campaign", It.IsAny<OpenAiAdsInsightsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiAdsInsightsResult(payload.RootElement.Clone()));
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.LoadAttributedEventsAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnalyticsEvent>());
        var meta = new Mock<IMetaAdsService>();
        meta.Setup(x => x.GetCampaignsAsync(range, scope, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Unavailable"));
        var external = new Mock<IMarketingExternalAdsReportingService>();
        external.Setup(x => x.GetCampaignsAsync(owner, It.IsAny<string>(), range, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Unavailable"));
        return await new UnifiedMarketingPerformanceService(openai.Object, connection.Object, analytics.Object, meta.Object, external.Object).GetAsync(owner, scope, range);
    }
}

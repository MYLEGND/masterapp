using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalMarketingContextTests
{
    [Fact]
    public void PrivacyProjectionRemovesNamesIdentifiersTokensAndPreservesCrossChannelMetrics()
    {
        var raw = new AiSafeAnalyticsPayload {
            ScopeLabel = "Agent Jane Customer", TopPage = "/client/Jane-Customer?token=private-secret",
            TopCampaign = "Jane Customer", TopSource = "jane@example.invalid",
            Warnings = ["Devices failed for Jane Customer jane@example.invalid private-secret"],
            PagePerformance = [new() { PageKey = "/client/Jane-Customer", Views = 9 }],
            SourcePerformance = [new() { Source = "facebook", Campaign = "Jane Customer", Sessions = 3 }],
            PaidCampaigns = [new() { Channel = "chatgpt_ads", CampaignName = "Jane Customer", Spend = 25, Clicks = 12 }],
            Channels = [new("meta_ads", 10, 100, 5, 2, 1, 0, 1, 50, 5, "reference_observed"),
                new("chatgpt_ads", 25, 120, 12, 1, 0, 0, 0, 0, 0, "not_observed"),
                new("google_ads", 18, 90, 9, 1, 1, 0, 0, 0, 0, "canonical_lineage"),
                new("tiktok_ads", 12, 70, 7, 1, 0, 0, 0, 0, 0, "unavailable")],
            Devices = [new("Jane Customer", 5, 8, 1, 2, 1, 1)],
            MarketingHealth = new() { ClientTrackingErrors = 4, MetaHealthStatus = "Watch", Warnings = ["Jane Customer"] }
        };
        var safe = WebsiteAnalyticsAiRedactor.Redact(raw);
        var json = JsonSerializer.Serialize(safe);
        foreach (var secret in new[] { "Jane", "jane@example.invalid", "private-secret" })
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Equal(4, safe.MarketingHealth!.ClientTrackingErrors);
        Assert.Equal(25m, safe.PaidCampaigns.Single().Spend);
        Assert.Equal(4, safe.Channels.Count);
        Assert.Contains(safe.Channels, x => x.Channel == "google_ads" && x.AttributionConfidence == "canonical_lineage");
        Assert.Contains(safe.Channels, x => x.Channel == "tiktok_ads" && x.AttributionConfidence == "unavailable");
        Assert.Equal(json, JsonSerializer.Serialize(WebsiteAnalyticsAiRedactor.Redact(safe)));
        Assert.Equal(9, raw.PagePerformance.Single().Views);
        Assert.Equal("Jane Customer", raw.TopCampaign);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("founder")]
    [InlineData("business")]
    public async Task ContextUsesOnePermanentOwnerAndSameWindowAcrossModules(string kind)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "owner", AgentUpn = kind == "founder" ? "founder@example.invalid" : "agent@example.invalid", Slug = "owner", Status = "Active" };
        var business = new CommerceBusiness { Id = Guid.NewGuid(), IsActive = true, Status = "Active" };
        db.AddRange(profile, business); await db.SaveChangesAsync();
        var scope = kind == "business" ? ScopeContext.ForBusiness(business.Id) : kind == "founder" ? ScopeContext.ForFounder(profile.Id) : ScopeContext.ForAgent(profile.Id);
        var owner = kind == "business" ? MarketingOwnerScope.Business(business.Id) : kind == "founder" ? MarketingOwnerScope.Founder : MarketingOwnerScope.Agent(profile.Id);
        var range = TimeRangeRequest.FromPreset("today");
        var analytics = new AnalyticsQueryService(db, Config());
        var meta = new Mock<IMetaAdsService>();
        meta.Setup(x => x.GetCampaignsAsync(range, scope, It.IsAny<CancellationToken>())).ReturnsAsync(new MetaCampaignsDto());
        var signals = new Mock<IMetaSignalAnalyticsService>();
        signals.Setup(x => x.GetAiSummaryAsync(range, scope, TrafficType.All, It.IsAny<CancellationToken>())).ReturnsAsync(new MetaSignalAiSummaryDto());
        signals.Setup(x => x.GetHealthDashboardAsync(range, scope, It.IsAny<CancellationToken>())).ReturnsAsync(new MetaSignalHealthDashboardDto());
        var performance = new Mock<IUnifiedMarketingPerformanceService>(MockBehavior.Strict);
        performance.Setup(x => x.GetAsync(owner, scope, range, It.IsAny<CancellationToken>())).ReturnsAsync(new UnifiedChannelPerformanceSnapshot(owner,
            range.FromUtc, range.ToUtc, DateTime.UtcNow, [], new(0,0,0,0,0), [new("chatgpt_ads", 23, 100, 12, 2, 1, 0, 0, 0, 0, "reference_observed", "canonical")], []));
        var promotion = new Mock<IPromotionOrchestrationService>(MockBehavior.Strict);
        promotion.Setup(x => x.SourcesAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new PromotionSourceOption("website_page", "/", "Fitness coaching", "/", "Book a consultation", kind == "business" ? "business" : "legend") });
        var builder = new WebsiteAnalyticsAiDataBuilder(analytics, meta.Object, signals.Object, NullLogger<WebsiteAnalyticsAiDataBuilder>.Instance,
            db, Config(), performance.Object, promotion.Object);
        var result = await builder.BuildAsync(range, scope, "ignored", "private name", "All", expectedOwner: owner);
        Assert.Equal(owner.OwnerType, result.ScopeLabel);
        Assert.Equal(range.FromUtc, result.FromUtc);
        Assert.Equal(range.ToUtc, result.ToUtc);
        Assert.Equal(23m, result.Channels.Single().Spend);
        Assert.Equal("Fitness coaching", result.PublishedSources.Single().Label);
        Assert.DoesNotContain("private name", WebsiteAnalyticsAiDataBuilder.FormatSnapshot(result));
        performance.VerifyAll(); promotion.VerifyAll();
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(range, scope, "", "", "", expectedOwner: MarketingOwnerScope.Business(Guid.NewGuid())));
    }

    [Fact]
    public async Task GlobalAndMixedScopesAreRejectedBeforeAnyAnalyticsRead()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var analytics = new Mock<IAnalyticsQueryService>(MockBehavior.Strict);
        var builder = new WebsiteAnalyticsAiDataBuilder(analytics.Object, Mock.Of<IMetaAdsService>(), Mock.Of<IMetaSignalAnalyticsService>(),
            NullLogger<WebsiteAnalyticsAiDataBuilder>.Instance, db, Config(), Mock.Of<IUnifiedMarketingPerformanceService>(), Mock.Of<IPromotionOrchestrationService>());
        foreach (var scope in new[] { ScopeContext.Global, new ScopeContext { ScopeType = ScopeType.Business, CommerceBusinessId = Guid.NewGuid(), AgentTrackingProfileId = Guid.NewGuid() } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(TimeRangeRequest.FromPreset("today"), scope, "", "", ""));
        analytics.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("facebook", null)]
    [InlineData("chatgpt", "openai-reference")]
    [InlineData("direct", null)]
    public async Task BothConversionProjectionsUseSameConfirmedFactRegardlessOfAcquisitionChannel(string source, string? oppref)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var row = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Lead", EventUtc = DateTime.UtcNow, AgentTrackingProfileId = Guid.NewGuid(),
            Host = "protect.mylegnd.com", Url = "https://protect.mylegnd.com/RiskAssessment", PageKey = "/RiskAssessment",
            IsServerAuthority = true, IsBrowserSignal = false, MetaServerAuthorityEligible = true,
            UtmSource = source, Oppref = oppref
        });
        UnifiedAnalyticsWriter.Write(db, row); await db.SaveChangesAsync();
        Assert.True(CanonicalAdvertisingEventProjection.CanProjectServer(row));
        Assert.True(OpenAiMeasurementEventMapper.TryMap(row, out var openAi));
        Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, row));
        var meta = Assert.Single(db.MetaSignalEvents);
        Assert.Equal(CanonicalAdvertisingEventProjection.ResolveEventId(row), openAi.Id);
        Assert.Equal(openAi.Id, meta.EventId);
        Assert.Equal("lead_created", openAi.Type);
        Assert.Equal("Lead", meta.EventName);
        Assert.Equal(oppref, openAi.Oppref);
        Assert.False(await MetaSignalAnalyticsBridge.PersistAsync(db, row));
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Founder:Upn"] = "founder@example.invalid", ["Analytics:IncludeLocalhost"] = "true"
    }).Build();
}

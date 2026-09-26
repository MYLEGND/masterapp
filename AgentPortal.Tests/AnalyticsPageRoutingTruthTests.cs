using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AnalyticsPageRoutingTruthTests
{
    [Fact]
    public void SharedAnalyticsPageRoutesEveryVisibleModuleThroughTheConfiguredAnalyticsBase()
    {
        var root = RepoRoot();
        var ui = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics.js"));
        var kpi = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics-kpi-modal.js"));
        var controller = File.ReadAllText(Path.Combine(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs"));
        var businessController = File.ReadAllText(Path.Combine(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs"));
        var businessService = File.ReadAllText(Path.Combine(root, "Infrastructure", "Businesses", "BusinessWorkspaceService.cs"));

        var sharedReadRoutes = new[]
        {
            "/summary",
            "/traffic",
            "/page-performance",
            "/cta-performance",
            "/quote-funnel",
            "/marketing-health",
            "/conversions",
            "/leads",
            "/meta-signal",
            "/meta-signal-health",
            "/behavior/summary",
            "/behavior/time-on-page",
            "/behavior/exit-analysis",
            "/behavior/journey",
            "/behavior/source-performance",
            "/quote-funnel/abandonment"
        };

        foreach (var route in sharedReadRoutes)
        {
            Assert.Contains($"analyticsEndpoint('{route}')", ui, StringComparison.Ordinal);
            Assert.Contains($"\"{route.TrimStart('/')}\"", businessService, StringComparison.Ordinal);
        }

        Assert.Contains("analyticsEndpoint(\"/DeviceIntelligence\")", ui, StringComparison.Ordinal);
        Assert.Contains("\"DeviceIntelligence\"", businessService, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"DeviceIntelligence\")]", controller, StringComparison.Ordinal);

        Assert.Contains("dataset.analyticsBase", kpi, StringComparison.Ordinal);
        Assert.Contains("/kpi-detail?", kpi, StringComparison.Ordinal);
        Assert.Contains("/visitor-timeline?", kpi, StringComparison.Ordinal);
        Assert.Contains("section is \"kpi-detail\" or \"visitor-timeline\"", businessService, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"kpi-detail\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"visitor-timeline\")]", controller, StringComparison.Ordinal);

        foreach (var route in new[] { "meta-campaigns", "meta-connection-status", "meta-connect", "meta-disconnect" })
            Assert.Contains($"analytics/{route}", businessController, StringComparison.Ordinal);

        Assert.Contains("ViewData[\"AnalyticsCanAiReview\"] = false", businessController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"AnalyticsCanAgentPerformance\"] = false", businessController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"AnalyticsCanIncidentMonitor\"] = false", businessController, StringComparison.Ordinal);

        Assert.Contains("quoteType, campaign, pageMode, scoreTier", businessController, StringComparison.Ordinal);
        Assert.Contains("quoteType, campaign, pageMode, scoreTier, cancellationToken", businessController, StringComparison.Ordinal);
        Assert.Contains("quoteType, campaign, pageMode, scoreTier, ct", businessService, StringComparison.Ordinal);

        Assert.Contains("AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes)", businessController, StringComparison.Ordinal);
        Assert.Contains("AnalyticsViewerTimeZoneResolver.Resolve(timezoneId, timezoneOffsetMinutes)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyticsPageNeverRepresentsUnavailableSummaryOrFailedTrafficAsZero()
    {
        var root = RepoRoot();
        var ui = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics.js"));
        var controller = File.ReadAllText(Path.Combine(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs"));
        var dto = File.ReadAllText(Path.Combine(root, "SHARED", "Analytics", "SummaryDtos.cs"));

        Assert.Contains("public bool IsAvailable { get; set; } = true;", dto, StringComparison.Ordinal);
        Assert.Contains("IsAvailable = false", controller, StringComparison.Ordinal);
        Assert.Contains("renderSummaryUnavailable", ui, StringComparison.Ordinal);
        Assert.Contains("data?.isAvailable === false", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing the last successfully loaded summary", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("setText('traffic-exited-before-start-count', '0')", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("setText('traffic-exited-before-start-modal-count', '0')", ui, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusinessMetaSignalFiltersAreActuallyApplied_NotSilentlyIgnored()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.MetaSignalEvents.AddRange(
            Signal(businessId, now.AddMinutes(-2), "life", "campaign-life", "landing", "high", "visitor-life"),
            Signal(businessId, now.AddMinutes(-1), "auto", "campaign-auto", "quote", "medium", "visitor-auto"));
        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var service = new BusinessWorkspaceService(db, analytics, new(db, new ConfigurationBuilder().Build()));
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.AllTraffic,
            ViewerTimeZone = TimeZoneInfo.Utc,
            Label = "test",
            Preset = "custom"
        };

        var life = Assert.IsType<MetaSignalDashboardDto>(await service.AnalyticsDataAsync(
            businessId, "meta-signal", range, TrafficType.All, quoteType: "life"));

        Assert.Contains("life", life.AvailableQuoteTypes);
        Assert.Contains("auto", life.AvailableQuoteTypes);
        Assert.DoesNotContain(life.EventsByQuoteType, x => x.Label.Equals("auto", StringComparison.OrdinalIgnoreCase));

        var campaign = Assert.IsType<MetaSignalDashboardDto>(await service.AnalyticsDataAsync(
            businessId, "meta-signal", range, TrafficType.All, campaign: "campaign-auto"));

        Assert.DoesNotContain(campaign.EventsByCampaign, x => x.Label.Equals("campaign-life", StringComparison.OrdinalIgnoreCase));
    }

    private static MetaSignalEvent Signal(
        Guid businessId,
        DateTime createdUtc,
        string quoteType,
        string campaign,
        string pageMode,
        string scoreTier,
        string visitorId) => new()
    {
        CreatedUtc = createdUtc,
        EventId = Guid.NewGuid().ToString("N"),
        EventName = "ViewContent",
        CommerceBusinessId = businessId,
        AgentTrackingProfileId = null,
        QuoteType = quoteType,
        UtmCampaign = campaign,
        PageMode = pageMode,
        ScoreTier = scoreTier,
        VisitorId = visitorId,
        SessionId = visitorId + "-session",
        TrafficType = "PaidAds",
        UtmSource = "facebook",
        UtmMedium = "paid_social",
        FbclidPresent = true,
        TotalSignalScore = scoreTier == "high" ? 80 : 50,
        Environment = "production",
        Host = "business.example.org"
    };

    private static string RepoRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(Path.Combine(workspace, "Infrastructure")))
            return workspace;

        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "masterapp.sln")) ||
                Directory.Exists(Path.Combine(current, "Infrastructure")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}

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

        Assert.Contains("window.websiteAnalyticsBridge?.endpoint", ui, StringComparison.Ordinal);
        Assert.Contains("endpoint(\"/DeviceIntelligence\")", ui, StringComparison.Ordinal);
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
    public void AnalyticsPageContinuouslyRefreshesSummaryAndOpenDetailWithoutManualReload()
    {
        var root = RepoRoot();
        var ui = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics.js"));

        Assert.Contains("pollMs: 15000", ui, StringComparison.Ordinal);
        Assert.Contains("async function refreshLiveAnalytics()", ui, StringComparison.Ordinal);
        Assert.Contains("summaryRefreshInFlight", ui, StringComparison.Ordinal);
        Assert.Contains("await loadSummary();", ui, StringComparison.Ordinal);
        Assert.Contains("if (state.openModal) refreshOpenModal();", ui, StringComparison.Ordinal);
        Assert.Contains("setInterval(refreshLiveAnalytics, state.pollMs)", ui, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", ui, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('focus', refreshLiveAnalytics)", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("pollMs: 1500,", ui, StringComparison.Ordinal);
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
    public void AnalyticsIncidentAndAiReviewFailureStatesNeverClaimFalseSuccess()
    {
        var root = RepoRoot();
        var ai = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics-ai.js"));
        var incidents = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics-incidents.js"));
        var incidentDtos = File.ReadAllText(Path.Combine(root, "AgentPortal", "Models", "Analytics", "AnalyticsIncidentDtos.cs"));
        var incidentService = File.ReadAllText(Path.Combine(root, "AgentPortal", "Services", "Analytics", "AnalyticsIncidentQueryService.cs"));

        Assert.DoesNotContain("BACKDROP_ID", ai, StringComparison.Ordinal);
        Assert.Contains("e.target === d", ai, StringComparison.Ordinal);
        Assert.Contains("public bool IsAvailable { get; set; } = true;", incidentDtos, StringComparison.Ordinal);
        Assert.Contains("IsAvailable = false", incidentService, StringComparison.Ordinal);
        Assert.Contains("incident_monitor_metrics_unavailable", incidentService, StringComparison.Ordinal);
        Assert.Contains("payload?.isAvailable === false", incidents, StringComparison.Ordinal);
        Assert.Contains("buttonCountEl.textContent = '—'", incidents, StringComparison.Ordinal);
        Assert.Contains("No zero-incident result has been verified", incidents, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketingDeliveryEvidenceAndAiLearningTruthHaveSingleReadAuthorities()
    {
        var root = RepoRoot();
        var evidence = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MarketingMeasurementEvidenceService.cs"));
        var eventMap = File.ReadAllText(Path.Combine(root, "Infrastructure", "WebsiteEditing", "WebsiteEventMapQuery.cs"));
        var studio = File.ReadAllText(Path.Combine(root, "Infrastructure", "WebsiteEditing", "WebsitePlatformController.cs"));
        var metaSignals = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MetaSignalAnalyticsService.cs"));
        var analyticsView = File.ReadAllText(Path.Combine(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml"));

        Assert.Contains("public static class MarketingDeliveryEvidencePolicy", evidence, StringComparison.Ordinal);
        Assert.Contains("MetaProviderAccepted", evidence, StringComparison.Ordinal);
        Assert.Contains("HttpTransportAccepted", evidence, StringComparison.Ordinal);
        Assert.Contains("MarketingDeliveryEvidencePolicy.MetaProviderAccepted", eventMap, StringComparison.Ordinal);
        Assert.Contains("MarketingDeliveryEvidencePolicy.HttpTransportAccepted", eventMap, StringComparison.Ordinal);
        Assert.Contains("MarketingDeliveryEvidencePolicy.MetaProviderAccepted", studio, StringComparison.Ordinal);
        Assert.Contains("MarketingDeliveryEvidencePolicy.HttpTransportAccepted", studio, StringComparison.Ordinal);
        Assert.DoesNotContain("metaRows.All(m => m.MetaServerSent &&", eventMap, StringComparison.Ordinal);
        Assert.DoesNotContain("httpAccepted = value.Status == \"sent\"", studio, StringComparison.Ordinal);

        Assert.Contains("Submitted Leads value is a funnel-signal count, not the canonical CRM lead total", metaSignals, StringComparison.Ordinal);
        Assert.Contains("Use Analytics Verified Leads and canonical channel outcomes for business truth", metaSignals, StringComparison.Ordinal);
        Assert.DoesNotContain("Meta Paid Signal Intelligence only evaluates paid Meta-attributed traffic", analyticsView, StringComparison.Ordinal);
        Assert.Contains("Loading canonical Meta learning scope", analyticsView, StringComparison.Ordinal);
        Assert.Contains("Meta Funnel Lead Signals", analyticsView, StringComparison.Ordinal);
        Assert.DoesNotContain(">Submitted Leads<", analyticsView, StringComparison.Ordinal);
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
        var service = new BusinessWorkspaceService(db, analytics);
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

    [Fact]
    public void MetaAdsAndPixelRoutesFailClosedAndUseCanonicalQualityBuckets()
    {
        var root = RepoRoot();
        var controller = File.ReadAllText(Path.Combine(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs"));
        var ads = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MetaAdsService.cs"));
        var resolver = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MetaPixelResolutionService.cs"));
        var metaRuntime = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "meta-signal-intelligence.js"));
        var protectBootstrap = File.ReadAllText(Path.Combine(root, "Protect-Website", "Views", "Shared", "_QuoteMetaSignalBootstrap.cshtml"));

        Assert.DoesNotContain("Configured fallback account", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Using the configured fallback Meta Ads account", controller, StringComparison.Ordinal);
        Assert.Contains("ApplyLeadBucketMembershipInMemory", ads, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildLeadPredicate(range.QualityMode)", ads, StringComparison.Ordinal);
        Assert.Contains("Scoped agent traffic is tenant-owned", resolver, StringComparison.Ordinal);
        Assert.Contains("trackSingle", metaRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("trackSingleCustom", metaRuntime, StringComparison.Ordinal);
        Assert.Contains("config.pixelId", metaRuntime, StringComparison.Ordinal);
        Assert.Contains("ResolvedMetaPixelId", protectBootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopedWebsiteAnalyticsUsesCanonicalOwnerAndEndpointAuthorities()
    {
        var root = RepoRoot();
        var scope = File.ReadAllText(Path.Combine(root, "SHARED", "Analytics", "ScopeContext.cs"));
        var resolver = File.ReadAllText(Path.Combine(root, "AgentPortal", "Services", "Analytics", "WebsiteAnalyticsScopeResolver.cs"));
        var queryScope = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "AnalyticsScopeQueryExtensions.cs"));
        var proxy = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "WebsiteTrackingProxyAuthority.cs"));
        var analyticsJs = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics.js"));
        var layout = File.ReadAllText(Path.Combine(root, "Protect-Website", "Views", "Shared", "_Layout.cshtml"));

        Assert.Contains("Founder,", scope, StringComparison.Ordinal);
        Assert.Contains("ScopeContext.ForFounder(founderProfile.Id)", resolver, StringComparison.Ordinal);
        Assert.Contains("ScopeType.Founder", queryScope, StringComparison.Ordinal);
        Assert.Contains("PersistProtectEventAsync(req, isFounderOwner, protectScope, ct)", proxy, StringComparison.Ordinal);
        Assert.Contains("WebsiteContentVersionId = publishedScope?.PublishedVersion?.Id", proxy, StringComparison.Ordinal);
        Assert.Contains("WebsiteBindingId = Clean(req.WebsiteBindingId)", proxy, StringComparison.Ordinal);
        Assert.Contains("CanonicalizePublishedBinding(req, protectScope)", proxy, StringComparison.Ordinal);
        Assert.Contains("ProtectWebsiteOwnerResolver.ResolveAsync", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveByUpnAsync", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain("ForwardAsync(\"/api/analytics/ingest\"", proxy, StringComparison.Ordinal);
        Assert.Contains("siteKey = Infrastructure.WebsiteEditing.WebsiteEditorSiteKeys.Protect", layout, StringComparison.Ordinal);
        Assert.Contains("endpoint(path)", analyticsJs, StringComparison.Ordinal);
        Assert.Contains("window.websiteAnalyticsBridge?.endpoint", analyticsJs, StringComparison.Ordinal);
    }

    [Fact]
    public void FounderPersonalHydrationAndVisitorDrillInUseTheCanonicalFounderResolver()
    {
        var root = RepoRoot();
        var controller = File.ReadAllText(Path.Combine(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs"));
        var resolver = File.ReadAllText(Path.Combine(root, "AgentPortal", "Services", "Analytics", "WebsiteAnalyticsScopeResolver.cs"));
        var visitor = File.ReadAllText(Path.Combine(root, "AgentPortal", "Controllers", "API", "VisitorConcentrationController.cs"));
        var ui = File.ReadAllText(Path.Combine(root, "AgentPortal", "wwwroot", "js", "website-analytics.js"));

        Assert.Contains("scope.ScopeType is ScopeType.Founder or ScopeType.Agent", controller, StringComparison.Ordinal);
        Assert.Contains("requestedAgentId.Value == founderProfile.Id", resolver, StringComparison.Ordinal);
        Assert.Contains("ScopeContext.ForFounder(founderProfile.Id)", resolver, StringComparison.Ordinal);
        Assert.Contains("new WebsiteAnalyticsScopeResolver(_effectiveContext, _tracking, _db, _logger)", visitor, StringComparison.Ordinal);
        Assert.DoesNotContain("FounderGuard.IsFounder(User)", visitor, StringComparison.Ordinal);
        Assert.Contains("Founder Personal", ui, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicWebsiteRuntimeLoadsCanonicalAnalyticsBeforeOptionalAdvertisingProviders()
    {
        var root = RepoRoot();
        var runtime = File.ReadAllText(Path.Combine(root, "SHARED", "WebsitePlatform", "legend-public-cms.js"));
        var controller = File.ReadAllText(Path.Combine(root, "Infrastructure", "WebsiteEditing", "WebsitePlatformController.cs"));

        var trackingLoad = runtime.IndexOf("await loadRuntimeScript(trackingAsset)", StringComparison.Ordinal);
        var metaLoad = runtime.IndexOf("await loadRuntimeScript(context.metaSignalAsset", StringComparison.Ordinal);
        var openAiLoad = runtime.IndexOf("await loadRuntimeScript(context.openAiMeasurementAsset", StringComparison.Ordinal);

        Assert.True(trackingLoad >= 0, "Canonical tracking runtime must load.");
        Assert.True(metaLoad > trackingLoad, "Meta runtime must remain downstream of canonical tracking.");
        Assert.True(openAiLoad > trackingLoad, "OpenAI measurement must remain downstream of canonical tracking.");
        Assert.Contains("schedulePublicRuntimeRetry()", runtime, StringComparison.Ordinal);
        Assert.Contains("publicRuntimeStarted = true", runtime, StringComparison.Ordinal);
        Assert.Contains("using Microsoft.Extensions.Logging;", controller, StringComparison.Ordinal);
        Assert.Contains("MarketingBrowserConfigurationService", controller, StringComparison.Ordinal);
        var browserConfiguration = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "MarketingBrowserConfigurationService.cs"));
        Assert.Contains("first-party tracking continues", browserConfiguration, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedAnalyticsTrackingHasNoUserSpecificFounderFallback()
    {
        var root = RepoRoot();
        var proxy = File.ReadAllText(Path.Combine(root, "Infrastructure", "Analytics", "WebsiteTrackingProxyAuthority.cs"));
        var routing = File.ReadAllText(Path.Combine(root, "Protect-Website", "Services", "Tracking", "SlugRoutingMiddleware.cs"));

        Assert.Contains("Founder:Upn configuration is required", proxy, StringComparison.Ordinal);
        Assert.Contains("Founder:Upn configuration is required", routing, StringComparison.Ordinal);
        Assert.DoesNotContain("zac.owen@mylegnd.com", proxy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zac.owen@mylegnd.com", routing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicLeadSubmissionsAndNotificationRecoveryUseSharedAuthorities()
    {
        var root = RepoRoot();
        var controllers = new[]
        {
            "LifeQuoteController.cs",
            "HomeQuoteController.cs",
            "AutoQuoteController.cs",
            "CommercialQuoteController.cs",
            "DisabilityQuoteController.cs",
            "DentalVisionHearingQuoteController.cs",
            "RiskAssessmentController.cs"
        };

        foreach (var controllerFile in controllers)
        {
            var source = File.ReadAllText(Path.Combine(root, "Protect-Website", "Controllers", controllerFile));
            Assert.Contains("PlatformRateLimiting.PublicFormPolicy", source, StringComparison.Ordinal);
        }

        var notificationAuthority = File.ReadAllText(Path.Combine(
            root, "Infrastructure", "Leads", "WebsiteLeadNotificationAuthority.cs"));
        var submission = File.ReadAllText(Path.Combine(
            root, "Infrastructure", "Leads", "WebsiteLeadSubmission.cs"));
        var program = File.ReadAllText(Path.Combine(root, "Protect-Website", "Program.cs"));

        Assert.Contains("WebsiteLeadNotificationRecoveryWorker", notificationAuthority, StringComparison.Ordinal);
        Assert.Contains("WebsiteLeadNotificationAuthority.DeliverAsync(", notificationAuthority, StringComparison.Ordinal);
        Assert.Contains("WebsiteIntakeRecipientResolver", notificationAuthority, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business(scopedBusinessId)", notificationAuthority, StringComparison.Ordinal);
        Assert.Contains("CommerceWebsiteInquiry", notificationAuthority, StringComparison.Ordinal);
        Assert.Contains("AnyAsync(x => x.WebsiteLeadId == lead.LeadId", notificationAuthority, StringComparison.Ordinal);
        Assert.Contains("lead.NotificationAttemptUtc = accepted ? lead.NotificationAttemptUtc : DateTime.UtcNow", submission, StringComparison.Ordinal);
        Assert.Contains("WebsiteLeadSubmission.NotificationRetryCutoff", notificationAuthority, StringComparison.Ordinal);
        var portal = File.ReadAllText(Path.Combine(root, "AgentPortal", "Program.cs"));
        var leadRegistration = File.ReadAllText(Path.Combine(root, "Infrastructure", "Leads", "WebsiteLeadServiceRegistration.cs"));
        Assert.Contains("AddWebsiteLeadBackgroundWorkers", portal, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<WebsiteLeadNotificationRecoveryWorker>()", leadRegistration, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<WebsiteLeadNotificationRecoveryWorker>()", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisitorConcentrationUsesTheSameSelectedTrafficSliceAsUniqueVisitors()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                ClientEventId = Guid.NewGuid(),
                CommerceBusinessId = businessId,
                EventType = "page_engaged_15s",
                EventUtc = now.AddMinutes(-2),
                ReceivedUtc = now.AddMinutes(-2),
                SessionId = "paid-session",
                VisitorId = "paid-visitor",
                PageKey = "/paid",
                UtmSource = "facebook",
                UtmMedium = "paid_social",
                MetaCampaignId = "campaign-paid",
                Environment = "production",
                Host = "business.example.org",
                UserAgent = "Mozilla/5.0",
                EngagedMilliseconds = 15000,
                DwellMilliseconds = 20000,
                ScrollPercent = 80,
                HumanInteractionCount = 5,
                MouseMoveCount = 20
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(),
                ClientEventId = Guid.NewGuid(),
                CommerceBusinessId = businessId,
                EventType = "page_engaged_15s",
                EventUtc = now.AddMinutes(-1),
                ReceivedUtc = now.AddMinutes(-1),
                SessionId = "direct-session",
                VisitorId = "direct-visitor",
                PageKey = "/direct",
                Environment = "production",
                Host = "business.example.org",
                UserAgent = "Mozilla/5.0",
                EngagedMilliseconds = 15000,
                DwellMilliseconds = 20000,
                ScrollPercent = 80,
                HumanInteractionCount = 5,
                MouseMoveCount = 20
            });
        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var concentration = new AgentPortal.Services.Analytics.VisitorConcentrationService(
            db,
            new AgentPortal.Services.Analytics.VisitorTrustScoringService(),
            analytics);
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.RealHumanTraffic,
            ViewerTimeZone = TimeZoneInfo.Utc,
            Label = "test",
            Preset = "custom"
        };

        var paidRows = await concentration.GetVisitorConcentrationAsync(
            range, ScopeContext.ForBusiness(businessId), TrafficType.PaidAds);

        var directRows = await concentration.GetVisitorConcentrationAsync(
            range, ScopeContext.ForBusiness(businessId), TrafficType.NonPaid);

        Assert.Single(paidRows);
        Assert.Equal("paid-visitor", paidRows[0].VisitorId);
        Assert.Single(directRows);
        Assert.Equal("direct-visitor", directRows[0].VisitorId);
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

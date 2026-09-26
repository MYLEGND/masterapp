using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AnalyticsCanonicalReconciliationTests
{
    [Fact]
    public async Task BusinessRealHumanSessionCarriesOneQualityStoryAcrossCoreReporting()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var foreignBusinessId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string sessionId = "canonical-session";
        const string visitorId = "canonical-visitor";

        db.AnalyticsEvents.AddRange(
            Event(businessId, now.AddMinutes(-5), "page_view", sessionId, visitorId, "/", bounce: true),
            Event(businessId, now.AddMinutes(-4), "page_engaged_15s", sessionId, visitorId, "/", strongHuman: true),
            Event(businessId, now.AddMinutes(-3), "cta_click", sessionId, visitorId, "/", elementKey: "contact_primary"),
            Event(foreignBusinessId, now.AddMinutes(-2), "page_engaged_15s", "foreign-session", "foreign-visitor", "/", strongHuman: true));

        db.WebsiteLeads.AddRange(
            new WebsiteLead
            {
                LeadId = Guid.NewGuid(),
                CommerceBusinessId = businessId,
                AgentTrackingProfileId = null,
                SessionId = sessionId,
                VisitorId = visitorId,
                SourcePageKey = "/",
                FirstName = "Canonical",
                Email = "canonical@example.org",
                TermsAccepted = false,
                MarketingEmailConsent = false,
                CallTextConsent = false,
                Environment = "production",
                Host = "business.example.org",
                CreatedUtc = now.AddMinutes(-2)
            },
            new WebsiteLead
            {
                LeadId = Guid.NewGuid(),
                CommerceBusinessId = foreignBusinessId,
                AgentTrackingProfileId = null,
                SessionId = "foreign-session",
                VisitorId = "foreign-visitor",
                SourcePageKey = "/",
                FirstName = "Foreign",
                Email = "foreign@example.org",
                TermsAccepted = true,
                Environment = "production",
                Host = "foreign.example.org",
                CreatedUtc = now.AddMinutes(-1)
            });

        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.RealHumanTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };
        var scope = ScopeContext.ForBusiness(businessId);

        var summary = await analytics.GetSummaryAsync(range, scope);
        var traffic = await analytics.GetTrafficAsync(range, scope);
        var pages = await analytics.GetPagePerformanceAsync(range, scope);
        var ctas = await analytics.GetCtaPerformanceAsync(range, scope);
        var conversions = await analytics.GetConversionsAsync(range, scope);
        var leads = await analytics.GetLeadsAsync(range, scope);
        var health = await analytics.GetMarketingHealthAsync(range, scope);

        Assert.Equal(1, summary.PageViews);
        Assert.Equal(1, summary.Sessions);
        Assert.Equal(1, summary.UniqueVisitors);
        Assert.Equal(1, summary.VerifiedLeads);

        Assert.Equal(1, traffic.PageViewTrend.Sum(x => x.Value));
        Assert.Contains(traffic.TopPages, x => x.Key == "/" && x.Count == 1);

        var page = Assert.Single(pages.Rows);
        Assert.Equal("/", page.PageKey);
        Assert.Equal(1, page.Views);
        Assert.Equal(1, page.CtaClicks);
        Assert.Equal(1, page.Leads);

        var cta = Assert.Single(ctas.Rows);
        Assert.Equal("contact_primary", cta.ElementKey);
        Assert.Equal(1, cta.Clicks);

        Assert.Equal(1, conversions.TotalConversions);
        Assert.Single(conversions.Recent);
        Assert.Equal(1, leads.Total);
        Assert.Single(leads.Leads);

        // Marketing health reads the same classified population and must not
        // manufacture a missing lead simply because the lead row itself lacked
        // consent/UTM hints that the browser session did not need for human proof.
        Assert.DoesNotContain(health.Warnings, x =>
            x.Contains("Unknown lead attribution remains on 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FounderPersonalAggregatesProtectAndLegendButExcludesOtherOwners()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founderId = Guid.NewGuid();
        var founderAliasId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var businessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.AgentTrackingProfiles.AddRange(
            new AgentTrackingProfile { Id = founderId, AgentUpn = "founder@example.org", AgentUserId = "founder-1" },
            new AgentTrackingProfile { Id = founderAliasId, AgentUpn = "founder@example.org", AgentUserId = "founder-2" },
            new AgentTrackingProfile { Id = otherAgentId, AgentUpn = "other@example.org", AgentUserId = "other" });

        db.AnalyticsEvents.AddRange(
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-5), ReceivedUtc = now.AddMinutes(-5),
                SessionId = "founder-protect", VisitorId = "founder-protect-v", PageKey = "protect_home",
                AgentTrackingProfileId = founderAliasId, CommerceBusinessId = null,
                Environment = "production", Host = "protect.mylegnd.com", MetadataJson = "{\"siteKey\":\"protect\",\"reportingOwner\":\"founder\"}"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-4), ReceivedUtc = now.AddMinutes(-4),
                SessionId = "founder-legend", VisitorId = "founder-legend-v", PageKey = "legend_home",
                AgentTrackingProfileId = null, CommerceBusinessId = null,
                Environment = "production", Host = "mylegnd.com", MetadataJson = "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-3), ReceivedUtc = now.AddMinutes(-3),
                SessionId = "other-agent", VisitorId = "other-agent-v", PageKey = "protect_home",
                AgentTrackingProfileId = otherAgentId, CommerceBusinessId = null,
                Environment = "production", Host = "protect.mylegnd.com", MetadataJson = "{\"siteKey\":\"protect\",\"reportingOwner\":\"agent\"}"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-2), ReceivedUtc = now.AddMinutes(-2),
                SessionId = "business", VisitorId = "business-v", PageKey = "/",
                AgentTrackingProfileId = null, CommerceBusinessId = businessId,
                Environment = "production", Host = "business.example.org", MetadataJson = "{\"siteKey\":\"business\"}"
            });

        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.AllTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };

        var summary = await analytics.GetSummaryAsync(range, ScopeContext.ForFounder(founderId));

        Assert.Equal(2, summary.PageViews);
        Assert.Equal(2, summary.Sessions);
        Assert.Equal(2, summary.UniqueVisitors);
    }

    [Fact]
    public async Task StandaloneServerLeadUsesLeadContextOnlyWhenNoBrowserIdentityExists()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            CommerceBusinessId = businessId,
            AgentTrackingProfileId = null,
            SessionId = "server-only-session",
            VisitorId = "server-only-visitor",
            SourcePageKey = "/contact",
            FirstName = "Server",
            Email = "server@example.org",
            TermsAccepted = true,
            Environment = "production",
            Host = "business.example.org",
            CreatedUtc = now
        });
        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddMinutes(-5),
            ToUtc = now.AddMinutes(5),
            QualityMode = TrafficQualityMode.RealHumanTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };

        var summary = await analytics.GetSummaryAsync(range, ScopeContext.ForBusiness(businessId));
        var conversions = await analytics.GetConversionsAsync(range, ScopeContext.ForBusiness(businessId));

        Assert.Equal(1, summary.VerifiedLeads);
        Assert.Equal(1, conversions.TotalConversions);
    }

    private static AnalyticsEvent Event(
        Guid businessId,
        DateTime utc,
        string eventType,
        string sessionId,
        string visitorId,
        string pageKey,
        bool bounce = false,
        bool strongHuman = false,
        string? elementKey = null) => new()
    {
        EventId = Guid.NewGuid(),
        ClientEventId = Guid.NewGuid(),
        CommerceBusinessId = businessId,
        AgentTrackingProfileId = null,
        EventType = eventType,
        EventUtc = utc,
        ReceivedUtc = utc,
        SessionId = sessionId,
        VisitorId = visitorId,
        PageKey = pageKey,
        ElementKey = elementKey,
        Environment = "production",
        Host = "business.example.org",
        UserAgent = "Mozilla/5.0",
        WebDriver = false,
        IsHeadless = false,
        IsInternal = false,
        IsBounceCandidate = bounce,
        IsExitPage = false,
        EngagedMilliseconds = strongHuman ? 15000 : 0,
        DwellMilliseconds = strongHuman ? 20000 : 100,
        ScrollPercent = strongHuman ? 80 : 0,
        HumanInteractionCount = strongHuman ? 5 : 0,
        MouseMoveCount = strongHuman ? 20 : 0
    };
}

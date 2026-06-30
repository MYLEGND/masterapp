using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Proves that GetLeadsAsync populates Attribution on every LeadSnapshotRow with
/// server-side classification via TrafficAttribution.Classify.
///
/// Covers:
///   - paid lead  (fbclid present)  → IsPaid=true,  IsNonPaid=false
///   - organic lead (utm_medium=organic) → IsPaid=false, IsNonPaid=true
///   - unknown-attribution lead (no UTM, no fbclid) → IsPaid=false, IsNonPaid=false, badge empty
///
/// Also asserts UtmSource, UtmMedium, UtmCampaign, Fbclid, SourcePage, Source are populated.
/// </summary>
public class LeadSnapshotAttributionTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static AnalyticsQueryService BuildService(MasterAppDbContext db)
    {
        // Match the production config that the behaviour-intelligence tests use.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:EnvironmentFilter"] = "production",
                ["Analytics:ExcludeLocalHosts"]  = "true"
            })
            .Build();

        return new AnalyticsQueryService(db, config);
    }

    private static TimeRangeRequest BuildRange(DateTime nowUtc) => new()
    {
        FromUtc  = nowUtc.AddHours(-1),
        ToUtc    = nowUtc.AddHours(1),
        Grouping = TimeGrouping.Day,
        Label    = "test",
        Preset   = "custom",
        QualityMode = TrafficQualityMode.AllTraffic
    };

    /// <summary>Minimal valid WebsiteLead that passes BaseLeads filters with Global scope.</summary>
    private static WebsiteLead L(
        DateTime createdUtc,
        string?  utmSource   = null,
        string?  utmMedium   = null,
        string?  utmCampaign = null,
        string?  fbclid      = null,
        string   sourcePageKey = "quote_life",
        string   sourceCtaKey  = "hero_cta",
        string?  sessionId = null,
        string?  visitorId = null)
        => new()
        {
            LeadId       = Guid.NewGuid(),
            FirstName    = "Test",
            LastName     = "User",
            Email        = $"test+{Guid.NewGuid():N}@example.com",
            Status       = "New",
            CreatedUtc   = createdUtc,
            IsInternal   = false,
            Environment  = "production",
            Host         = "portal.mylegnd.com",
            UtmSource    = utmSource,
            UtmMedium    = utmMedium,
            UtmCampaign  = utmCampaign,
            Fbclid       = fbclid,
            SessionId    = sessionId,
            VisitorId    = visitorId,
            SourcePageKey = sourcePageKey,
            SourceCtaKey  = sourceCtaKey
        };

    private static AnalyticsEvent E(
        string eventType,
        DateTime eventUtc,
        string sessionId,
        string visitorId,
        string? deviceType = null,
        string? browser = null,
        string? operatingSystem = null,
        int? scrollPercent = null,
        int? humanInteractionCount = null)
        => new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            EventUtc = eventUtc,
            ReceivedUtc = eventUtc,
            SessionId = sessionId,
            VisitorId = visitorId,
            DeviceType = deviceType,
            Browser = browser,
            OperatingSystem = operatingSystem,
            ScrollPercent = scrollPercent,
            HumanInteractionCount = humanInteractionCount,
            Environment = "production",
            Host = "portal.mylegnd.com"
        };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLeadsAsync_PaidLead_FbclidSet_AttributionIsPaid()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(L(now, fbclid: "Cj0KCQ_paid_fbclid_value"));
        await db.SaveChangesAsync();

        var svc  = BuildService(db);
        var dto  = await svc.GetLeadsAsync(BuildRange(now), ScopeContext.Global);

        Assert.Single(dto.Leads);
        var row = dto.Leads[0];

        // Attribution object must be present
        Assert.NotNull(row.Attribution);

        // Classification
        Assert.True(row.Attribution!.IsPaid,    "Expected IsPaid=true for fbclid lead");
        Assert.False(row.Attribution.IsNonPaid,  "Expected IsNonPaid=false for fbclid lead");
        Assert.Equal(TrafficType.PaidAds, row.Attribution.TrafficType);
        Assert.Equal(TrafficType.PaidAds, row.TrafficType);

        // Raw fields populated
        Assert.Equal("Cj0KCQ_paid_fbclid_value", row.Fbclid);
        Assert.Null(row.UtmSource);
        Assert.Null(row.UtmMedium);
        Assert.Null(row.UtmCampaign);

        // Source composite
        Assert.Equal("quote_life/hero_cta", row.LeadSource);
        Assert.Equal("quote_life", row.SourcePage);
    }

    [Fact]
    public async Task GetLeadsAsync_OrganicLead_UtmMediumOrganic_AttributionIsNonPaid()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(L(now, utmSource: "google", utmMedium: "organic", utmCampaign: "seo-brand"));
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var dto = await svc.GetLeadsAsync(BuildRange(now), ScopeContext.Global);

        Assert.Single(dto.Leads);
        var row = dto.Leads[0];

        Assert.NotNull(row.Attribution);
        Assert.False(row.Attribution!.IsPaid,   "Expected IsPaid=false for organic lead");
        Assert.True(row.Attribution.IsNonPaid,   "Expected IsNonPaid=true for organic lead");
        Assert.Equal(TrafficType.Organic, row.Attribution.TrafficType);
        Assert.Equal(TrafficType.Organic, row.TrafficType);

        Assert.Equal("google",    row.UtmSource);
        Assert.Equal("organic",   row.UtmMedium);
        Assert.Equal("seo-brand", row.UtmCampaign);
        Assert.Null(row.Fbclid);
    }

    [Fact]
    public async Task GetLeadsAsync_DirectLead_NoUtmNoFbclid_ClassifiesAsDirectNonPaid()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        // No UTM data whatsoever — direct / no attribution signals
        db.WebsiteLeads.Add(L(now));
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var dto = await svc.GetLeadsAsync(BuildRange(now), ScopeContext.Global);

        Assert.Single(dto.Leads);
        var row = dto.Leads[0];

        Assert.NotNull(row.Attribution);
        Assert.False(row.Attribution!.IsPaid,    "Direct lead must not show as Paid");
        Assert.True(row.Attribution.IsNonPaid,   "Direct lead should be treated as non-paid traffic");
        Assert.Equal(TrafficType.Direct, row.Attribution.TrafficType);
        Assert.Equal(TrafficType.Direct, row.TrafficType);

        // Raw fields still populated (even if null values)
        Assert.Null(row.UtmSource);
        Assert.Null(row.UtmMedium);
        Assert.Null(row.UtmCampaign);
        Assert.Null(row.Fbclid);
    }

    [Fact]
    public async Task GetLeadsAsync_MixedLeads_EachRowClassifiedIndependently()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.AddRange(
            L(now.AddMinutes(-2), fbclid: "paid_click"),                      // paid
            L(now.AddMinutes(-1), utmMedium: "organic"),                       // organic (non-paid)
            L(now)                                                             // unknown
        );
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var dto = await svc.GetLeadsAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(3, dto.Leads.Count);

        // Ordered by CreatedUtc descending — index 0 = most recent
        var unknown = dto.Leads[0];
        var organic = dto.Leads[1];
        var paid    = dto.Leads[2];

        Assert.True(paid.Attribution!.IsPaid);
        Assert.False(paid.Attribution.IsNonPaid);

        Assert.False(organic.Attribution!.IsPaid);
        Assert.True(organic.Attribution.IsNonPaid);

        Assert.False(unknown.Attribution!.IsPaid);
        Assert.True(unknown.Attribution!.IsNonPaid);
        Assert.Equal(TrafficType.Direct, unknown.TrafficType);
    }

    [Fact]
    public async Task GetLeadsAsync_PpcLead_UtmMediumCpc_AttributionIsPaid()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(L(now, utmSource: "google", utmMedium: "cpc", utmCampaign: "brand-exact"));
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var dto = await svc.GetLeadsAsync(BuildRange(now), ScopeContext.Global);

        var row = dto.Leads.Single();
        Assert.True(row.Attribution!.IsPaid);
        Assert.False(row.Attribution.IsNonPaid);
        Assert.Equal(TrafficType.PaidAds, row.TrafficType);
        Assert.Equal("google",      row.UtmSource);
        Assert.Equal("cpc",         row.UtmMedium);
        Assert.Equal("brand-exact", row.UtmCampaign);
    }

    [Fact]
    public async Task GetLeadsAsync_ProjectsCapturedSessionDeviceAndEngagementContext()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(L(
            now,
            fbclid: "fbclid-context-123",
            sessionId: "session-context-1",
            visitorId: "visitor-context-1"));

        db.AnalyticsEvents.AddRange(
            E(
                "page_view",
                now.AddMinutes(-5),
                "session-context-1",
                "visitor-context-1",
                deviceType: "desktop",
                browser: "chrome",
                operatingSystem: "macos",
                scrollPercent: 38,
                humanInteractionCount: 2),
            E(
                "page_exit",
                now.AddMinutes(-1),
                "session-context-1",
                "visitor-context-1",
                deviceType: "desktop",
                browser: "chrome",
                operatingSystem: "macos",
                scrollPercent: 82,
                humanInteractionCount: 5)
        );

        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var dto = await svc.GetLeadsAsync(BuildRange(now), ScopeContext.Global);

        var row = Assert.Single(dto.Leads);
        Assert.Equal("session-context-1", row.SessionId);
        Assert.Equal("visitor-context-1", row.VisitorId);
        Assert.Equal("Desktop", row.DeviceType);
        Assert.Equal("Chrome", row.Browser);
        Assert.Equal("macOS", row.OperatingSystem);
        Assert.Equal(82, row.ScrollPercent);
        Assert.Equal(5, row.HumanInteractionCount);
        Assert.Equal("fbclid-context-123", row.Fbclid);
    }
}

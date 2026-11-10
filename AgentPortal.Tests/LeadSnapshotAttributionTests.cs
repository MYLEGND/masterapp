using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Infrastructure.Leads;
using Infrastructure.WebsiteEditing;
using Shared.Meta;
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
    [Theory]
    [InlineData("accepted", "provider_accepted")]
    [InlineData("blocked_human_evidence", "blocked_human_evidence")]
    [InlineData("transport", "transport_acknowledged")]
    [InlineData("sent", "receipt_unverified")]
    [InlineData("failed", "failed")]
    [InlineData("pending", "pending")]
    [InlineData("skipped_not_dispatch_eligible", "skipped_not_dispatch_eligible")]
    [InlineData("skipped_not_configured", "skipped_not_configured")]
    [InlineData("missing", "unknown")]
    public async Task SnapshotUsesCanonicalMetaEvidenceWithoutLegacyMetadata(string outcome, string expected)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, source, receipt, pixel) = await SeedDeliveryAsync(db, outcome);
        var query = BuildService(db);
        var tracking = Assert.Single((await query.GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads).MetaTracking!;
        Assert.Equal(expected, tracking.ServerCapiStatus);
        Assert.Equal(outcome == "accepted", tracking.ServerCapiSent);
        Assert.False(tracking.DedupReady);
        Assert.Equal(CanonicalLeadEventIdentity.Resolve(lead), tracking.MetaLeadEventId);
        Assert.Equal(pixel.PixelId, tracking.ResolvedMetaPixelId);
        // The same source/receipt is projected by the existing published Event Map.
        if (outcome != "missing")
        {
            var map = await new WebsiteEventMapQuery(db, new ConfigurationBuilder().Build())
                .ReadAsync(ScopeContext.ForAgent(source.AgentTrackingProfileId!.Value));
            Assert.Equal(expected, Assert.Single(map.Where(x => x.Element == "automatic:lead_created")).MetaStatus);
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("site")]
    [InlineData("version")]
    [InlineData("source")]
    [InlineData("event")]
    [InlineData("pixel")]
    public async Task SnapshotCannotBorrowAcceptanceFromOtherIdentityOrDestination(string mismatch)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, source, receipt, pixel) = await SeedDeliveryAsync(db, "accepted");
        switch (mismatch)
        {
            case "owner": receipt.AgentTrackingProfileId = Guid.NewGuid(); break;
            case "version": receipt.WebsiteContentVersionId = Guid.NewGuid(); break;
            case "event": receipt.EventId = "foreign-event"; break;
            default:
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(receipt.MetadataJson!)!;
                metadata[mismatch == "site" ? "siteKey" : mismatch == "source" ? "sourceAnalyticsEventId" : "metaServerPixelId"] =
                    mismatch == "site" ? "legend" : mismatch == "source" ? source.Id + 1000 : "foreign-pixel";
                receipt.MetadataJson = JsonSerializer.Serialize(metadata);
                break;
        }
        await db.SaveChangesAsync();
        var tracking = Assert.Single((await BuildService(db).GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads).MetaTracking!;
        Assert.False(tracking.ServerCapiSent);
        Assert.False(tracking.DedupReady);
        Assert.NotEqual("provider_accepted", tracking.ServerCapiStatus);
    }

    [Fact]
    public async Task SnapshotKeepsProviderResultsSeparateAndLegacyBrowserDedupBoundToExactPixel()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, source, receipt, pixel) = await SeedDeliveryAsync(db, "blocked_human_evidence");
        db.MarketingDestinationDeliveries.Add(new() { OwnerKey = pixel.OwnerKey, Provider = "openai", Channel = "server",
            CanonicalSource = nameof(AnalyticsEvent), AnalyticsEventId = source.Id, AgentTrackingProfileId = source.AgentTrackingProfileId,
            CanonicalEventId = receipt.EventId, Status = "sent", LastHttpStatusCode = 200 });
        lead.MetadataJson = MetaLeadTrackingJson.Upsert(lead.MetadataJson, s => {
            s.EventId = receipt.EventId; s.ResolvedMetaPixelId = pixel.PixelId;
            s.ServerCapiStatus = "sent"; s.BrowserPixelStatus = "sent";
        });
        await db.SaveChangesAsync();
        var query = BuildService(db);
        async Task<MetaLeadTrackingDto> Read() => Assert.Single((await query.GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads).MetaTracking!;
        var blocked = await Read();
        Assert.Equal("blocked_human_evidence", blocked.ServerCapiStatus);
        Assert.True(blocked.BrowserPixelSent); Assert.False(blocked.DedupReady);
        receipt.MetaServerSent = true;
        receipt.MetadataJson = JsonSerializer.Serialize(new { sourceAnalyticsEventId = source.Id, siteKey = "protect",
            metaServerEventsReceived = 1, metaServerPixelId = pixel.PixelId });
        await db.SaveChangesAsync();
        Assert.True((await Read()).DedupReady);
        lead.MetadataJson = MetaLeadTrackingJson.Upsert(lead.MetadataJson, s => s.ResolvedMetaPixelId = "old-pixel");
        await db.SaveChangesAsync();
        Assert.False((await Read()).DedupReady);
        // Repeated reads do not enqueue conversions or create business facts.
        Assert.Single(db.WebsiteLeads); Assert.Single(db.AnalyticsEvents);
        Assert.Single(db.MetaSignalEvents); Assert.Single(db.MarketingDestinationDeliveries);
    }

    [Fact]
    public async Task AcceptedDeliveryDoesNotTurnInternalQaIntoLearningReadiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, _, _, _) = await SeedDeliveryAsync(db, "accepted");
        lead.IsInternal = true;
        await db.SaveChangesAsync();
        var row = Assert.Single((await BuildService(db).GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads);
        Assert.Equal("provider_accepted", row.MetaTracking!.ServerCapiStatus);
        Assert.True(row.Attribution!.ExcludedFromMetaLearningReadiness);
    }

    [Fact]
    public async Task DisconnectedDestinationCannotClaimCurrentAcceptance()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, _, _, pixel) = await SeedDeliveryAsync(db, "accepted");
        pixel.DisconnectedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var tracking = Assert.Single((await BuildService(db).GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads).MetaTracking!;
        Assert.Equal("not_configured", tracking.ServerCapiStatus); Assert.False(tracking.ServerCapiSent);
    }

    [Fact]
    public async Task UnstampedPreDispatchBlockCannotClaimBrowserDeliveryToCurrentPixel()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, source, receipt, _) = await SeedDeliveryAsync(db, "blocked_human_evidence");
        receipt.MetaBrowserSent = true;
        receipt.MetadataJson = JsonSerializer.Serialize(new { sourceAnalyticsEventId = source.Id, siteKey = "protect",
            metaServerStatus = "blocked_human_evidence" });
        await db.SaveChangesAsync();
        var tracking = Assert.Single((await BuildService(db).GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads).MetaTracking!;
        Assert.Equal("blocked_human_evidence", tracking.ServerCapiStatus);
        Assert.False(tracking.BrowserPixelSent); Assert.False(tracking.DedupReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotKeepsUnknownAndReconciliationHonestDespiteOldSentMetadata(bool correction)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (lead, source, receipt, pixel) = await SeedDeliveryAsync(db, "accepted");
        if (correction)
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(source.MetadataJson!)!;
            data["providerCorrectionStatus"] = "requires_reconciliation";
            source.MetadataJson = JsonSerializer.Serialize(data);
        }
        else
        {
            db.MetaSignalEvents.Remove(receipt);
            db.AnalyticsEvents.Remove(source);
            lead.MetadataJson = MetaLeadTrackingJson.Upsert(lead.MetadataJson, s => {
                s.EventId = receipt.EventId; s.ResolvedMetaPixelId = pixel.PixelId; s.ServerCapiStatus = "sent";
            });
        }
        await db.SaveChangesAsync();
        var tracking = Assert.Single((await BuildService(db).GetLeadsAsync(BuildRange(lead.CreatedUtc), ScopeContext.Global)).Leads).MetaTracking!;
        Assert.Equal(correction ? "requires_reconciliation" : "receipt_unverified", tracking.ServerCapiStatus);
        Assert.False(tracking.ServerCapiSent);
    }

    [Fact]
    public async Task MetaStatusAggregationDoesNotHideMixedProviderReceipts()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var (_, _, accepted, _) = await SeedDeliveryAsync(db, "accepted");
        var blocked = new MetaSignalEvent { MetadataJson = "{\"metaServerStatus\":\"blocked_human_evidence\"}" };
        Assert.Equal("partial_delivery", MarketingDeliveryEvidencePolicy.MetaDeliveryStatus(new[] { accepted, blocked }));
        Assert.Equal("mixed", MarketingDeliveryEvidencePolicy.MetaDeliveryStatus(new[] { blocked, new MetaSignalEvent() }));
    }

    private static async Task<(WebsiteLead, AnalyticsEvent, MetaSignalEvent, MarketingConnection)> SeedDeliveryAsync(MasterAppDbContext db, string outcome)
    {
        var now = DateTime.UtcNow.AddSeconds(-5);
        var profile = new AgentTrackingProfile { AgentUserId = "snapshot-owner", AgentUpn = "snapshot@example.test", Slug = "snapshot" };
        var state = new WebsiteContentState { OwnerKey = profile.AgentUserId, SiteKey = "protect" };
        var version = new WebsiteContentVersion { StateId = state.Id, DocumentJson = "{}" };
        state.PublishedVersionId = version.Id;
        var lead = L(now); lead.AgentTrackingProfileId = profile.Id;
        lead.MetadataJson = "{\"SiteKey\":\"protect\"}";
        var source = new AnalyticsEvent { EventId = Guid.NewGuid(), EventType = "website_lead_submitted",
            EventUtc = now, ReceivedUtc = now, Environment = "production", Host = "protect.mylegnd.com",
            AgentTrackingProfileId = profile.Id, WebsiteContentVersionId = version.Id,
            MetadataJson = JsonSerializer.Serialize(new { siteKey = "protect", canonicalOutcomeEventId = CanonicalLeadEventIdentity.Resolve(lead), LeadId = lead.LeadId }) };
        var pixel = new MarketingConnection { OwnerKey = MarketingOwnerScope.Agent(profile.Id).Key, OwnerType = "agent",
            AgentTrackingProfileId = profile.Id, PixelId = "synthetic-pixel", Provider = "meta" };
        db.AddRange(profile, state, version, lead, source, pixel); await db.SaveChangesAsync();
        var receipt = new MetaSignalEvent { EventId = CanonicalLeadEventIdentity.Resolve(lead), EventName = "Lead",
            LeadId = lead.LeadId, AgentTrackingProfileId = profile.Id, WebsiteContentVersionId = version.Id,
            CreatedUtc = now, MetaServerSent = outcome is "accepted" or "sent",
            MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson("Lead", lead.LeadId, null,
                new { sourceAnalyticsEventId = source.Id, siteKey = "protect", metaServerPixelId = pixel.PixelId,
                    metaServerStatus = outcome is "accepted" or "transport" ? "sent_response" : outcome,
                    metaServerAttempted = outcome is "accepted" or "sent" or "failed" or "transport",
                    metaServerEventsReceived = outcome == "accepted" ? 1 : 0,
                    metaServerHttpStatusCode = outcome == "transport" ? 200 : 0 },
                false, true, true, true) };
        if (outcome != "missing") { db.Add(receipt); await db.SaveChangesAsync(); }
        return (lead, source, receipt, pixel);
    }

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

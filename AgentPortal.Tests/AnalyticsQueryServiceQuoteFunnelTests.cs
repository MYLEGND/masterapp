using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsQueryServiceQuoteFunnelTests
{
    private static AnalyticsQueryService BuildService(MasterAppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:EnvironmentFilter"] = "production",
                ["Analytics:ExcludeLocalHosts"] = "true"
            })
            .Build();

        var resolver = new AgentTrackingResolver(db, NullLogger<AgentTrackingResolver>.Instance);
        return new AnalyticsQueryService(db, config, resolver);
    }

    private static TimeRangeRequest BuildRange(DateTime nowUtc) => new()
    {
        FromUtc = nowUtc.AddHours(-2),
        ToUtc = nowUtc.AddHours(2),
        Grouping = TimeGrouping.Day,
        Label = "test",
        Preset = "custom"
    };

    private static AnalyticsEvent E(
        string eventType,
        DateTime eventUtc,
        string sessionId,
        string? formKey = null,
        string? quoteType = null,
        string? submitOutcome = null,
        string? utmMedium = null,
        string? utmSource = null,
        string? utmCampaign = null,
        string? fbclid = null,
        string? metadataJson = null)
        => new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            EventUtc = eventUtc,
            ReceivedUtc = eventUtc,
            SessionId = sessionId,
            FormKey = formKey,
            QuoteType = quoteType,
            SubmitOutcome = submitOutcome,
            UtmSource = utmSource,
            UtmMedium = utmMedium,
            UtmCampaign = utmCampaign,
            Fbclid = fbclid,
            MetadataJson = metadataJson,
            Environment = "production",
            Host = "portal.mylegnd.com"
        };

    [Fact]
    public async Task GetQuoteFunnelAsync_UsesUniqueSessions_AndIncludesDirectFormStartsInStarts()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            // s1: duplicate clicks should count once at stage level
            E("quote_click", now.AddMinutes(-40), "s1", quoteType: "life"),
            E("quote_click", now.AddMinutes(-39), "s1", quoteType: "life"),
            E("form_start", now.AddMinutes(-38), "s1", formKey: "quote_life_form", quoteType: "life"),
            E("form_submit", now.AddMinutes(-37), "s1", formKey: "quote_life_form", quoteType: "life", submitOutcome: "success"),

            // s2: direct form start (no quote_click) should still count as a start
            E("form_start", now.AddMinutes(-36), "s2", formKey: "quote_home_form", quoteType: "home"),
            E("form_submit", now.AddMinutes(-35), "s2", formKey: "quote_home_form", quoteType: "home", submitOutcome: "success"),

            // s3: click-only start
            E("quote_click", now.AddMinutes(-34), "s3", quoteType: "auto"),

            // s4: duplicate form starts should count once at stage level
            E("form_start", now.AddMinutes(-33), "s4", formKey: "quote_auto_form", quoteType: "auto"),
            E("form_start", now.AddMinutes(-32), "s4", formKey: "quote_auto_form", quoteType: "auto")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(4, dto.QuoteStarts);
        Assert.Equal(3, dto.QuoteFormStarts);
        Assert.Equal(2, dto.QuoteFormSubmits);
        Assert.Equal(2, dto.CtaStartCount);
        Assert.Equal(2, dto.DirectFormStartCount);
        Assert.Equal(25.00m, dto.DropOffStartsToFormStarts);
        Assert.Equal(33.33m, dto.DropOffFormStartsToSubmits);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_LifeContactFirstEvents_CountAsFunnelStart_And_ContactStep()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("quote_landing_view", now.AddMinutes(-20), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("primary_cta_seen", now.AddMinutes(-19.5), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("life_contact_first_view", now.AddMinutes(-19), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("life_contact_first_start", now.AddMinutes(-18), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("first_question_view", now.AddMinutes(-17.5), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("first_question_answered", now.AddMinutes(-17.25), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("estimate_results_viewed", now.AddMinutes(-17), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("estimate_contact_continue", now.AddMinutes(-16), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing"),
            E("life_step2_submit_attempt", now.AddMinutes(-15), "life-cf-1", quoteType: "term", formKey: "quote_term_life_landing")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(1, dto.QuoteStarts);
        Assert.Equal(1, dto.QuoteFormStarts);
        Assert.Equal(1, dto.QuoteSubmitAttempts);
        Assert.Equal(0, dto.CtaStartCount);
        Assert.Equal(1, dto.DirectFormStartCount);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "primary_cta_seen" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "first_question_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "first_question_answered" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "recommendation_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "contact_step_viewed" && metric.Count == 1);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_TrafficFilters_ExposeUnknownSeparately_AndRemainExhaustive()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        // Paid session
        db.AnalyticsEvents.AddRange(
            E("quote_click", now.AddMinutes(-30), "p1", quoteType: "life", utmMedium: "cpc"),
            E("form_start", now.AddMinutes(-29), "p1", formKey: "quote_life_form", quoteType: "life", utmMedium: "cpc"),
            E("form_submit", now.AddMinutes(-28), "p1", formKey: "quote_life_form", quoteType: "life", submitOutcome: "success", utmMedium: "cpc")
        );

        // Organic non-paid session
        db.AnalyticsEvents.AddRange(
            E("quote_click", now.AddMinutes(-27), "o1", quoteType: "home", utmMedium: "organic"),
            E("form_start", now.AddMinutes(-26), "o1", formKey: "quote_home_form", quoteType: "home", utmMedium: "organic")
        );

        // Unknown session (no attribution)
        db.AnalyticsEvents.AddRange(
            E("quote_click", now.AddMinutes(-25), "u1", quoteType: "auto"),
            E("form_start", now.AddMinutes(-24), "u1", formKey: "quote_auto_form", quoteType: "auto"),
            E("form_submit", now.AddMinutes(-23), "u1", formKey: "quote_auto_form", quoteType: "auto", submitOutcome: "success")
        );

        await db.SaveChangesAsync();

        var service = BuildService(db);
        var range = BuildRange(now);

        var all = await service.GetQuoteFunnelAsync(range, ScopeContext.Global, TrafficType.All);
        var paid = await service.GetQuoteFunnelAsync(range, ScopeContext.Global, TrafficType.PaidAds);
        var nonPaid = await service.GetQuoteFunnelAsync(range, ScopeContext.Global, TrafficType.NonPaid);

        Assert.Equal((3, 3, 2), (all.QuoteStarts, all.QuoteFormStarts, all.QuoteFormSubmits));
        Assert.Equal((1, 1, 1), (paid.QuoteStarts, paid.QuoteFormStarts, paid.QuoteFormSubmits));
        Assert.Equal((1, 1, 0), (nonPaid.QuoteStarts, nonPaid.QuoteFormStarts, nonPaid.QuoteFormSubmits));
        Assert.Equal(1, all.PaidStartCount);
        Assert.Equal(1, all.NonPaidStartCount);
        Assert.Equal(1, all.UnknownStartCount);
        Assert.Equal(all.QuoteStarts, all.PaidStartCount + all.NonPaidStartCount + all.UnknownStartCount);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_FirstQuestionViewAlone_DoesNotCreateStart()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("first_question_view", now.AddMinutes(-29), "question-only", formKey: "quote_life_form", quoteType: "life"));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(0, dto.QuoteStarts);
        Assert.Equal(0, dto.QuoteFormStarts);
        Assert.Equal(0, dto.CtaStartCount);
        Assert.Equal(0, dto.DirectFormStartCount);
        Assert.DoesNotContain(dto.StageMetrics, metric => metric.StageKey == "funnel_started");
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "first_question_viewed" && metric.Count == 1);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_LateStageEvents_DoNotCreateStart()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("estimate_results_viewed", now.AddMinutes(-28), "late-stage-1", formKey: "quote_life_form", quoteType: "life"),
            E("contact_step_view", now.AddMinutes(-27), "late-stage-2", formKey: "quote_life_form", quoteType: "life"),
            E("lead_form_submit_success", now.AddMinutes(-26), "late-stage-3", formKey: "quote_life_form", quoteType: "life"));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(0, dto.QuoteStarts);
        Assert.Equal(0, dto.QuoteFormStarts);
        Assert.Equal(0, dto.CtaStartCount);
        Assert.Equal(0, dto.DirectFormStartCount);
        Assert.DoesNotContain(dto.StageMetrics, metric => metric.StageKey == "funnel_started");
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "recommendation_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "contact_step_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "server_confirmed_leads" && metric.Count == 1);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_CtaClick_CreatesStart_WithoutFormStart()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("cta_clicked", now.AddMinutes(-15), "cta-start", formKey: "quote_life_form", quoteType: "life"));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(1, dto.QuoteStarts);
        Assert.Equal(0, dto.QuoteFormStarts);
        Assert.Equal(1, dto.CtaStartCount);
        Assert.Equal(0, dto.DirectFormStartCount);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "cta_clicked" && metric.Count == 1);
        Assert.DoesNotContain(dto.StageMetrics, metric => metric.StageKey == "funnel_started");
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_DirectFormEntry_CountsAsDirectFormStart_NotCtaStart()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("form_start", now.AddMinutes(-15), "direct-form", formKey: "quote_life_form", quoteType: "life"));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(1, dto.QuoteStarts);
        Assert.Equal(1, dto.QuoteFormStarts);
        Assert.Equal(0, dto.CtaStartCount);
        Assert.Equal(1, dto.DirectFormStartCount);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "funnel_started" && metric.Count == 1);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_AliasEvents_MapIntoCanonicalStages()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("cta_clicked", now.AddMinutes(-20), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("funnel_started", now.AddMinutes(-19), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("first_question_view", now.AddMinutes(-18), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("first_question_answered", now.AddMinutes(-17), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("protecting_who_completed", now.AddMinutes(-16), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("goal_completed", now.AddMinutes(-15), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("age_completed", now.AddMinutes(-14), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("processing_bridge_viewed", now.AddMinutes(-13), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("recommendation_viewed", now.AddMinutes(-12), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("contact_step_viewed", now.AddMinutes(-11), "alias-1", formKey: "quote_life_form", quoteType: "life"),
            E("form_submit_success", now.AddMinutes(-10), "alias-1", formKey: "quote_life_form", quoteType: "life"));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetQuoteFunnelAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal((1, 1, 1), (dto.QuoteStarts, dto.QuoteFormStarts, dto.QuoteFormSubmits));
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "cta_clicked" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "funnel_started" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "protecting_who_completed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "goal_completed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "age_completed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "processing_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "recommendation_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "contact_step_viewed" && metric.Count == 1);
        Assert.Contains(dto.StageMetrics, metric => metric.StageKey == "server_confirmed_leads" && metric.Count == 1);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_PaidFilter_InheritsPaidFromSessionLandingAttribution()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            // Paid session where only landing has paid signals (Meta click id + campaign)
            E("page_view", now.AddMinutes(-30), "paid-1", formKey: null, quoteType: "life",
                utmCampaign: "120246895965190404", fbclid: "fbclid_paid_1"),
            E("form_start", now.AddMinutes(-29), "paid-1", formKey: "quote_life_form", quoteType: "life"),
            E("form_submit", now.AddMinutes(-28), "paid-1", formKey: "quote_life_form", quoteType: "life", submitOutcome: "success"),

            // Organic session
            E("page_view", now.AddMinutes(-27), "organic-1", quoteType: "life", utmMedium: "organic", utmSource: "google"),
            E("form_start", now.AddMinutes(-26), "organic-1", formKey: "quote_life_form", quoteType: "life")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var range = BuildRange(now);

        var paid = await service.GetQuoteFunnelAsync(range, ScopeContext.Global, TrafficType.PaidAds);
        var nonPaid = await service.GetQuoteFunnelAsync(range, ScopeContext.Global, TrafficType.NonPaid);

        Assert.Equal((1, 1, 1), (paid.QuoteStarts, paid.QuoteFormStarts, paid.QuoteFormSubmits));
        Assert.Equal((1, 1, 0), (nonPaid.QuoteStarts, nonPaid.QuoteFormStarts, nonPaid.QuoteFormSubmits));
    }

    [Fact]
    public async Task GetTrafficAsync_TopCampaigns_UsesSessionLandingAttribution_NotEventVolume()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        const string activeCampaignId = "120246895965190404";
        const string staleCampaignId = "120246773387880404";

        db.AnalyticsEvents.AddRange(
            // Active campaign sessions (2 sessions)
            E("page_view", now.AddMinutes(-50), "active-1", utmCampaign: activeCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("form_start", now.AddMinutes(-49), "active-1", formKey: "quote_life_form", quoteType: "life"),
            E("page_view", now.AddMinutes(-48), "active-2", utmCampaign: activeCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("form_start", now.AddMinutes(-47), "active-2", formKey: "quote_life_form", quoteType: "life"),

            // Stale campaign session (1 session) but many stale-volume events
            E("page_view", now.AddMinutes(-46), "stale-1", utmCampaign: staleCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("cta_click", now.AddMinutes(-45), "stale-1", utmCampaign: staleCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("cta_click", now.AddMinutes(-44), "stale-1", utmCampaign: staleCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("cta_click", now.AddMinutes(-43), "stale-1", utmCampaign: staleCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("cta_click", now.AddMinutes(-42), "stale-1", utmCampaign: staleCampaignId, utmSource: "facebook", utmMedium: "cpc"),
            E("cta_click", now.AddMinutes(-41), "stale-1", utmCampaign: staleCampaignId, utmSource: "facebook", utmMedium: "cpc")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var traffic = await service.GetTrafficAsync(BuildRange(now), ScopeContext.Global, TrafficType.PaidAds);

        var topCampaign = Assert.Single(traffic.TopCampaigns.Take(1));
        Assert.Equal(activeCampaignId, topCampaign.Key);
        Assert.Equal(2, topCampaign.Count);
    }

    [Fact]
    public async Task GetTrafficAsync_ReportsUnknownSessionsSeparately()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("page_view", now.AddMinutes(-10), "paid-1", quoteType: "life", utmMedium: "cpc", utmSource: "facebook"),
            E("page_view", now.AddMinutes(-9), "organic-1", quoteType: "life", utmMedium: "organic", utmSource: "google"),
            E("page_view", now.AddMinutes(-8), "unknown-1", quoteType: "life"),
            E("page_view", now.AddMinutes(-7), "test-1", quoteType: "life", utmCampaign: "internal-preview"));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var traffic = await service.GetTrafficAsync(BuildRange(now), ScopeContext.Global, TrafficType.All);

        Assert.Equal(1, traffic.PaidSessionCount);
        Assert.Equal(1, traffic.NonPaidSessionCount);
        Assert.Equal(2, traffic.UnknownSessionCount);
    }

    [Fact]
    public async Task GetFormAbandonmentAsync_TrafficFilter_NonPaidIncludesOnlyKnownNonPaid()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        const string meta = "{\"quoteType\":\"life\"}";

        db.AnalyticsEvents.AddRange(
            // paid
            E("form_start", now.AddMinutes(-20), "p1", formKey: "quote_life_form", quoteType: "life", utmMedium: "cpc"),
            E("form_abandon", now.AddMinutes(-19), "p1", formKey: "quote_life_form", quoteType: "life", utmMedium: "cpc", metadataJson: meta),

            // organic (known non-paid)
            E("form_start", now.AddMinutes(-18), "o1", formKey: "quote_life_form", quoteType: "life", utmMedium: "organic"),
            E("form_abandon", now.AddMinutes(-17), "o1", formKey: "quote_life_form", quoteType: "life", utmMedium: "organic", metadataJson: meta),

            // unknown
            E("form_start", now.AddMinutes(-16), "u1", formKey: "quote_life_form", quoteType: "life"),
            E("form_abandon", now.AddMinutes(-15), "u1", formKey: "quote_life_form", quoteType: "life", metadataJson: meta)
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var range = BuildRange(now);

        var all = await service.GetFormAbandonmentAsync(range, ScopeContext.Global, TrafficType.All);
        var nonPaid = await service.GetFormAbandonmentAsync(range, ScopeContext.Global, TrafficType.NonPaid);

        var allLife = all.Summary.Single(r => r.QuoteType == "life");
        var nonPaidLife = nonPaid.Summary.Single(r => r.QuoteType == "life");

        Assert.Equal((3, 3), (allLife.Abandons, allLife.Starts));
        Assert.Equal((1, 1), (nonPaidLife.Abandons, nonPaidLife.Starts));
    }
}

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
            UtmMedium = utmMedium,
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
        Assert.Equal(25.00m, dto.DropOffStartsToFormStarts);
        Assert.Equal(33.33m, dto.DropOffFormStartsToSubmits);
    }

    [Fact]
    public async Task GetQuoteFunnelAsync_TrafficFilters_NonPaidExcludesUnknown()
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

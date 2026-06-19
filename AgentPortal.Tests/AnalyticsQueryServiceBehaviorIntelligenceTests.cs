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

public class AnalyticsQueryServiceBehaviorIntelligenceTests
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
        FromUtc = nowUtc.AddHours(-1),
        ToUtc = nowUtc.AddHours(1),
        Grouping = TimeGrouping.Day,
        Label = "test",
        Preset = "custom",
        QualityMode = TrafficQualityMode.AllTraffic
    };

    private static AnalyticsEvent E(
        string eventType,
        DateTime eventUtc,
        string sessionId,
        string pageKey = "quote_life",
        string? formKey = null,
        string? quoteType = null,
        string? submitOutcome = null,
        long? dwellMs = null,
        long? engagedMs = null,
        bool? isBounceCandidate = null,
        string? utmSource = null,
        string? metadataJson = null)
        => new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            EventUtc = eventUtc,
            ReceivedUtc = eventUtc,
            SessionId = sessionId,
            PageKey = pageKey,
            FormKey = formKey,
            QuoteType = quoteType,
            SubmitOutcome = submitOutcome,
            DwellMilliseconds = dwellMs,
            EngagedMilliseconds = engagedMs,
            IsBounceCandidate = isBounceCandidate,
            UtmSource = utmSource,
            MetadataJson = metadataJson,
            Environment = "production",
            Host = "portal.mylegnd.com"
        };

    [Fact]
    public async Task GetEngagementSummary_UsesFinalPageExitPerSession_ForQuickExitRate()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("page_view", now.AddMinutes(-10), "s1", pageKey: "page-a"),
            E("page_exit", now.AddMinutes(-9), "s1", pageKey: "page-a", dwellMs: 5_000, isBounceCandidate: true),
            E("page_exit", now.AddMinutes(-8), "s1", pageKey: "page-a", dwellMs: 20_000, isBounceCandidate: false),

            E("page_view", now.AddMinutes(-7), "s2", pageKey: "page-b"),
            E("page_exit", now.AddMinutes(-6), "s2", pageKey: "page-b", dwellMs: 4_000, isBounceCandidate: true)
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var range = BuildRange(now);

        var summary = await service.GetEngagementSummaryAsync(range, ScopeContext.Global);
        var exit = await service.GetExitAnalysisAsync(range, ScopeContext.Global);

        Assert.Equal(50.00m, summary.QuickExitRate);
        Assert.Equal(2, summary.TotalSessions);

        var quickRows = exit.QuickExitPages;
        Assert.Single(quickRows);
        Assert.Equal("page-b", quickRows[0].Key);
        Assert.Equal(1, quickRows[0].Count);
    }

    [Fact]
    public async Task GetEngagementSummary_EngagedSessionRate_UsesThirtySecondSignalsOrThreshold()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("page_view", now.AddMinutes(-10), "s1"),
            E("page_engaged_10s", now.AddMinutes(-9), "s1"),

            E("page_view", now.AddMinutes(-8), "s2"),
            E("page_engaged_30s", now.AddMinutes(-7), "s2"),

            E("page_view", now.AddMinutes(-6), "s3"),
            E("page_exit", now.AddMinutes(-5), "s3", dwellMs: 40_000, engagedMs: 35_000)
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var summary = await service.GetEngagementSummaryAsync(BuildRange(now), ScopeContext.Global);

        Assert.Equal(3, summary.TotalSessions);
        Assert.Equal(66.67m, summary.EngagedSessionRate);
    }

    [Fact]
    public async Task GetSourcePerformance_UsesEngagementSignalsPopulation_ForEngagedSessions()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("page_view", now.AddMinutes(-10), "s1", utmSource: "facebook"),
            E("page_view", now.AddMinutes(-9), "s2", utmSource: "facebook"),
            E("page_engaged_30s", now.AddMinutes(-8), "s1")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetSourcePerformanceAsync(BuildRange(now), ScopeContext.Global);
        var row = Assert.Single(dto.Rows.Where(r => r.Source == "Facebook / Meta"));

        Assert.Equal(2, row.Sessions);
        Assert.Equal(1, row.EngagedSessions);
    }

    [Fact]
    public async Task GetFormAbandonment_ExcludesSessionsWithSuccessfulSubmit()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("form_start", now.AddMinutes(-10), "s1", formKey: "quote_life", quoteType: "life"),
            E("form_abandon", now.AddMinutes(-9), "s1", formKey: "quote_life", quoteType: "life", metadataJson: "{\"quoteType\":\"life\"}"),
            E("form_submit", now.AddMinutes(-8), "s1", formKey: "quote_life", quoteType: "life", submitOutcome: "success"),

            E("form_start", now.AddMinutes(-7), "s2", formKey: "quote_life", quoteType: "life"),
            E("form_abandon", now.AddMinutes(-6), "s2", formKey: "quote_life", quoteType: "life", metadataJson: "{\"quoteType\":\"life\"}")
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var dto = await service.GetFormAbandonmentAsync(BuildRange(now), ScopeContext.Global);
        var row = Assert.Single(dto.Summary.Where(r => r.QuoteType == "life"));

        Assert.Equal(2, row.Starts);
        Assert.Equal(1, row.Abandons);
        Assert.Equal(50.00m, row.AbandonRate);
    }

    [Fact]
    public async Task QuickExitSummaryAndExitTable_UseSameFinalPagePopulation()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;

        db.AnalyticsEvents.AddRange(
            E("page_view", now.AddMinutes(-10), "s1", pageKey: "page-a"),
            E("page_exit", now.AddMinutes(-9), "s1", pageKey: "page-a", dwellMs: 3_000, isBounceCandidate: true),

            E("page_view", now.AddMinutes(-8), "s2", pageKey: "page-a"),
            E("page_exit", now.AddMinutes(-7), "s2", pageKey: "page-a", dwellMs: 18_000, isBounceCandidate: false),

            E("page_view", now.AddMinutes(-6), "s3", pageKey: "page-b"),
            E("page_exit", now.AddMinutes(-5), "s3", pageKey: "page-b", dwellMs: 2_000, isBounceCandidate: true)
        );
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var range = BuildRange(now);

        var summary = await service.GetEngagementSummaryAsync(range, ScopeContext.Global);
        var exit = await service.GetExitAnalysisAsync(range, ScopeContext.Global);

        var quickExitCount = exit.QuickExitPages.Sum(x => x.Count);

        Assert.Equal(66.67m, summary.QuickExitRate);
        Assert.Equal(2, quickExitCount);
    }
}

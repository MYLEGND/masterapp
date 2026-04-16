using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

/// <summary>
/// Assembles an <see cref="AiSafeAnalyticsPayload"/> from the existing analytics query
/// service. Only aggregate metrics are included — never individual lead rows.
/// </summary>
public sealed class WebsiteAnalyticsAiDataBuilder
{
    private readonly IAnalyticsQueryService _analytics;

    public WebsiteAnalyticsAiDataBuilder(IAnalyticsQueryService analytics)
    {
        _analytics = analytics;
    }

    public async Task<AiSafeAnalyticsPayload> BuildAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        string rangeLabel,
        string scopeLabel,
        string trafficFilter,
        TrafficType trafficType = TrafficType.All,
        CancellationToken ct = default)
    {
        // Run all queries concurrently — same pattern as existing AiReviewSnapshot endpoint.
        var summaryTask = SafeLoadAsync(() => _analytics.GetSummaryAsync(range, scope, trafficType),
            () => new SummaryKpiDto { RangeLabel = range.Label });
        var trafficTask = SafeLoadAsync(() => _analytics.GetTrafficAsync(range, scope, trafficType),
            () => new TrafficOverviewDto { RangeLabel = range.Label });
        var pagePerfTask = SafeLoadAsync(() => _analytics.GetPagePerformanceAsync(range, scope, trafficType),
            () => new PagePerformanceDto { RangeLabel = range.Label });
        var ctaPerfTask = SafeLoadAsync(() => _analytics.GetCtaPerformanceAsync(range, scope, trafficType),
            () => new CtaPerformanceDto { RangeLabel = range.Label });
        var quoteFunnelTask = SafeLoadAsync(() => _analytics.GetQuoteFunnelAsync(range, scope, trafficType),
            () => new QuoteFunnelDto { RangeLabel = range.Label });
        var conversionsTask = SafeLoadAsync(() => _analytics.GetConversionsAsync(range, scope, trafficType),
            () => new ConversionCenterDto { RangeLabel = range.Label });
        var engagementTask = SafeLoadAsync(() => _analytics.GetEngagementSummaryAsync(range, scope, trafficType),
            () => new EngagementSummaryDto { RangeLabel = range.Label });
        var timeOnPageTask = SafeLoadAsync(() => _analytics.GetTimeOnPageAsync(range, scope, trafficType),
            () => new TimeOnPageDto { RangeLabel = range.Label });
        var exitTask = SafeLoadAsync(() => _analytics.GetExitAnalysisAsync(range, scope, trafficType),
            () => new ExitAnalysisDto { RangeLabel = range.Label });
        var sourceTask = SafeLoadAsync(() => _analytics.GetSourcePerformanceAsync(range, scope, trafficType),
            () => new SourcePerformanceDto { RangeLabel = range.Label });
        var abandonTask = SafeLoadAsync(() => _analytics.GetFormAbandonmentAsync(range, scope, trafficType),
            () => new FormAbandonmentDto { RangeLabel = range.Label });

        await Task.WhenAll(
            summaryTask, trafficTask, pagePerfTask, ctaPerfTask,
            quoteFunnelTask, conversionsTask, engagementTask,
            timeOnPageTask, exitTask, sourceTask, abandonTask);

        var summary = await summaryTask;
        var traffic = await trafficTask;
        var pagePerf = await pagePerfTask;
        var ctaPerf = await ctaPerfTask;
        var quote = await quoteFunnelTask;
        var conversions = await conversionsTask;
        var engagement = await engagementTask;
        var timeOnPage = await timeOnPageTask;
        var exit = await exitTask;
        var source = await sourceTask;
        var abandon = await abandonTask;

        return new AiSafeAnalyticsPayload
        {
            RangeLabel = rangeLabel,
            ScopeLabel = scopeLabel,
            TrafficFilter = trafficFilter,

            // Summary KPIs
            PageViews = summary.PageViews,
            UniqueVisitors = summary.UniqueVisitors,
            Sessions = summary.Sessions,
            VerifiedLeads = summary.VerifiedLeads,
            SessionConversionRate = summary.SessionConversionRate,
            IntentConversionRate = summary.IntentConversionRate,
            IntentAvailable = summary.IntentAvailable,
            TopPage = summary.TopPage,
            TopCta = summary.TopCta,
            TopSource = summary.TopSource,
            TopCampaign = summary.TopCampaign,

            // Traffic breakdowns — labels and counts only, no PII
            TopPages = (traffic.TopPages ?? new List<KeyCountDto>()).Take(10)
                .Select(x => new LabelCount { Label = x.Key, Count = x.Count }).ToList(),
            TopSources = (traffic.TopSources ?? new List<KeyCountDto>()).Take(10)
                .Select(x => new LabelCount { Label = x.Key, Count = x.Count }).ToList(),
            TopCampaigns = (traffic.TopCampaigns ?? new List<KeyCountDto>()).Take(10)
                .Select(x => new LabelCount { Label = x.Key, Count = x.Count }).ToList(),
            EntryPages = (traffic.EntryPages ?? new List<KeyCountDto>()).Take(10)
                .Select(x => new LabelCount { Label = x.Key, Count = x.Count }).ToList(),

            // Page performance — aggregate, no names/emails
            PagePerformance = (pagePerf.Rows ?? new List<PagePerformanceRow>()).Take(15)
                .Select(x => new PagePerfRow
                {
                    PageKey = x.PageKey,
                    Views = x.Views,
                    CtaClicks = x.CtaClicks,
                    Leads = x.Leads,
                    ConversionRate = x.ConversionRate
                }).ToList(),

            // CTA performance
            CtaPerformance = (ctaPerf.Rows ?? new List<CtaPerformanceRow>()).Take(15)
                .Select(x => new CtaPerfRow
                {
                    PageKey = x.PageKey,
                    ElementKey = x.ElementKey,
                    Clicks = x.Clicks
                }).ToList(),

            // Quote funnel
            QuoteStarts = quote.QuoteStarts,
            QuoteFormStarts = quote.QuoteFormStarts,
            QuoteFormSubmits = quote.QuoteFormSubmits,
            DropOffStartsToFormStarts = quote.DropOffStartsToFormStarts,
            DropOffFormStartsToSubmits = quote.DropOffFormStartsToSubmits,

            // Conversions — aggregate count only
            TotalConversions = conversions.TotalConversions,

            // Behavior
            AvgSessionDurationMs = engagement.AvgSessionDurationMs,
            QuickExitRate = engagement.QuickExitRate,
            EngagedSessionRate = engagement.EngagedSessionRate,
            TopDwellPages = (timeOnPage.LongestAvgDwell ?? new List<DwellPageRow>()).Take(5)
                .Select(x => new DwellRow
                {
                    PageKey = x.PageKey,
                    AvgDwellMs = x.AvgDwellMs,
                    Samples = x.TimingSamples
                }).ToList(),
            TopExitPages = (exit.TopExitPages ?? new List<ExitPageRow>()).Take(5)
                .Select(x => new ExitRow
                {
                    PageKey = x.PageKey,
                    Exits = x.Exits,
                    ExitRate = x.ExitRate
                }).ToList(),

            // Source performance — source/medium/campaign labels + aggregate counts
            SourcePerformance = (source.Rows ?? new List<SourcePerformanceRow>()).Take(10)
                .Select(x => new SourceRow
                {
                    Source = x.Source,
                    Medium = x.Medium,
                    Campaign = x.Campaign,
                    Sessions = x.Sessions,
                    VerifiedLeads = x.VerifiedLeads,
                    SessionConversionRate = x.SessionConversionRate
                }).ToList(),

            // Form abandonment — aggregate only
            FormAbandonment = (abandon.Summary ?? new List<FormAbandonSummaryRow>()).Take(5)
                .Select(x => new AbandonRow
                {
                    QuoteType = x.QuoteType,
                    Abandons = x.Abandons,
                    Starts = x.Starts,
                    AbandonRate = x.AbandonRate
                }).ToList(),
            TopAbandonedFields = (abandon.TopAbandonedFields ?? new List<TopAbandonedFieldRow>()).Take(5)
                .Select(x => new LabelCount { Label = x.FieldName, Count = x.AbandonCount }).ToList()
        };
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<T> SafeLoadAsync<T>(Func<Task<T>> loader, Func<T> fallback)
    {
        try
        {
            return await loader();
        }
        catch
        {
            return fallback();
        }
    }
}

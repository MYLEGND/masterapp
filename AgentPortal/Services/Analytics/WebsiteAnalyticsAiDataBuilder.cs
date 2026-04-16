using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;

namespace AgentPortal.Services.Analytics;

/// <summary>
/// Assembles a focused <see cref="AiSafeAnalyticsPayload"/> for AI review.
/// Only includes: active campaign performance, page performance, quote funnel,
/// behavior intelligence, unique visitors, and form/lead data.
/// Never includes PII, historical comparisons, or non-conversion breakdowns.
/// </summary>
public sealed class WebsiteAnalyticsAiDataBuilder
{
    private readonly IAnalyticsQueryService _analytics;
    private readonly IMetaAdsService _metaAds;

    public WebsiteAnalyticsAiDataBuilder(IAnalyticsQueryService analytics, IMetaAdsService metaAds)
    {
        _analytics = analytics;
        _metaAds  = metaAds;
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
        // Run only the queries needed for conversion-focused analysis.
        // Removed: traffic breakdowns, CTA performance, total conversions, dwell time.
        var summaryTask     = SafeLoadAsync(() => _analytics.GetSummaryAsync(range, scope, trafficType),
                                 () => new SummaryKpiDto { RangeLabel = range.Label });
        var pagePerfTask    = SafeLoadAsync(() => _analytics.GetPagePerformanceAsync(range, scope, trafficType),
                                 () => new PagePerformanceDto { RangeLabel = range.Label });
        var quoteFunnelTask = SafeLoadAsync(() => _analytics.GetQuoteFunnelAsync(range, scope, trafficType),
                                 () => new QuoteFunnelDto { RangeLabel = range.Label });
        var engagementTask  = SafeLoadAsync(() => _analytics.GetEngagementSummaryAsync(range, scope, trafficType),
                                 () => new EngagementSummaryDto { RangeLabel = range.Label });
        var exitTask        = SafeLoadAsync(() => _analytics.GetExitAnalysisAsync(range, scope, trafficType),
                                 () => new ExitAnalysisDto { RangeLabel = range.Label });
        var sourceTask      = SafeLoadAsync(() => _analytics.GetSourcePerformanceAsync(range, scope, trafficType),
                                 () => new SourcePerformanceDto { RangeLabel = range.Label });
        var abandonTask     = SafeLoadAsync(() => _analytics.GetFormAbandonmentAsync(range, scope, trafficType),
                                 () => new FormAbandonmentDto { RangeLabel = range.Label });

        // Meta Ads — active campaigns. SafeLoadAsync catches "not connected" / API errors gracefully.
        var metaAdsTask = SafeLoadAsync(() => _metaAds.GetCampaignsAsync(range, scope, ct),
                              () => new MetaCampaignsDto { RangeLabel = range.Label });

        await Task.WhenAll(
            summaryTask, pagePerfTask, quoteFunnelTask,
            engagementTask, exitTask, sourceTask, abandonTask, metaAdsTask);

        var summary    = await summaryTask;
        var pagePerf   = await pagePerfTask;
        var quote      = await quoteFunnelTask;
        var engagement = await engagementTask;
        var exit       = await exitTask;
        var source     = await sourceTask;
        var abandon    = await abandonTask;
        var metaCampaigns = await metaAdsTask;

        return new AiSafeAnalyticsPayload
        {
            RangeLabel   = rangeLabel,
            ScopeLabel   = scopeLabel,
            TrafficFilter = trafficFilter,

            // (e) Unique Visitors + Lead data
            UniqueVisitors        = summary.UniqueVisitors,
            Sessions              = summary.Sessions,
            VerifiedLeads         = summary.VerifiedLeads,
            SessionConversionRate = summary.SessionConversionRate,

            // (b) Page Performance — top 5 by view volume
            PagePerformance = (pagePerf.Rows ?? new List<PagePerformanceRow>())
                .Take(5)
                .Select(x => new PagePerfRow
                {
                    PageKey        = x.PageKey,
                    Views          = x.Views,
                    CtaClicks      = x.CtaClicks,
                    Leads          = x.Leads,
                    ConversionRate = x.ConversionRate
                }).ToList(),

            // (c) Quote Funnel metrics
            QuoteStarts                  = quote.QuoteStarts,
            QuoteFormStarts              = quote.QuoteFormStarts,
            QuoteFormSubmits             = quote.QuoteFormSubmits,
            DropOffStartsToFormStarts    = quote.DropOffStartsToFormStarts,
            DropOffFormStartsToSubmits   = quote.DropOffFormStartsToSubmits,

            // (d) Behavior Intelligence
            AvgSessionDurationMs = engagement.AvgSessionDurationMs,
            QuickExitRate        = engagement.QuickExitRate,
            EngagedSessionRate   = engagement.EngagedSessionRate,
            TopExitPages = (exit.TopExitPages ?? new List<ExitPageRow>())
                .Take(3)
                .Select(x => new ExitRow
                {
                    PageKey  = x.PageKey,
                    Exits    = x.Exits,
                    ExitRate = x.ExitRate
                }).ToList(),

            // (a) Active Campaign Performance — only rows with actual campaign attribution
            SourcePerformance = (source.Rows ?? new List<SourcePerformanceRow>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Campaign))
                .Take(5)
                .Select(x => new SourceRow
                {
                    Source                = x.Source,
                    Medium                = x.Medium,
                    Campaign              = x.Campaign,
                    Sessions              = x.Sessions,
                    VerifiedLeads         = x.VerifiedLeads,
                    SessionConversionRate = x.SessionConversionRate
                }).ToList(),

            // (e) Form/Lead data
            FormAbandonment = (abandon.Summary ?? new List<FormAbandonSummaryRow>())
                .Take(3)
                .Select(x => new AbandonRow
                {
                    QuoteType   = x.QuoteType,
                    Abandons    = x.Abandons,
                    Starts      = x.Starts,
                    AbandonRate = x.AbandonRate
                }).ToList(),
            TopAbandonedFields = (abandon.TopAbandonedFields ?? new List<TopAbandonedFieldRow>())
                .Take(3)
                .Select(x => new LabelCount { Label = x.FieldName, Count = x.AbandonCount })
                .ToList(),

            // (a) Active Meta Ads campaigns — Status == ACTIVE only, top 5 by spend
            ActiveCampaigns = (metaCampaigns.Rows ?? new List<MetaCampaignRow>())
                .Where(x => string.Equals(x.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Spend)
                .Take(5)
                .Select(x => new AiCampaignRow
                {
                    CampaignName = x.CampaignName,
                    Spend        = x.Spend,
                    Impressions  = x.Impressions,
                    Clicks       = x.Clicks,
                    Ctr          = x.Ctr,
                    Cpc          = x.Cpc,
                    Leads        = x.Leads
                }).ToList()

            // Intentionally omitted (non-conversion breakdowns):
            // TopPages, TopSources, TopCampaigns, EntryPages, CtaPerformance,
            // TotalConversions, TopDwellPages, IntentConversionRate, PageViews
        };
    }

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

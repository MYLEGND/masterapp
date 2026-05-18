using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Microsoft.Extensions.Logging;

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
    private readonly IMetaSignalAnalyticsService _metaSignalAnalytics;
    private readonly ILogger<WebsiteAnalyticsAiDataBuilder> _logger;

    public WebsiteAnalyticsAiDataBuilder(
        IAnalyticsQueryService analytics,
        IMetaAdsService metaAds,
        IMetaSignalAnalyticsService metaSignalAnalytics,
        ILogger<WebsiteAnalyticsAiDataBuilder> logger)
    {
        _analytics = analytics;
        _metaAds  = metaAds;
        _metaSignalAnalytics = metaSignalAnalytics;
        _logger   = logger;
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
        _logger.LogInformation(
            "AiDataBuilder starting. Scope={ScopeType} AgentId={AgentId} Range={Range} Traffic={Traffic}",
            scope.ScopeType,
            scope.AgentTrackingProfileId,
            range.Label,
            trafficType);

        var warnings = new List<string>();

        // Run only the queries needed for conversion-focused analysis.
        // Removed: traffic breakdowns, CTA performance, total conversions, dwell time.
        var summaryTask     = SafeLoadAsync("Summary",     () => _analytics.GetSummaryAsync(range, scope, trafficType),
                                 () => new SummaryKpiDto { RangeLabel = range.Label }, warnings);
        var pagePerfTask    = SafeLoadAsync("PagePerf",    () => _analytics.GetPagePerformanceAsync(range, scope, trafficType),
                                 () => new PagePerformanceDto { RangeLabel = range.Label }, warnings);
        var quoteFunnelTask = SafeLoadAsync("QuoteFunnel", () => _analytics.GetQuoteFunnelAsync(range, scope, trafficType),
                                 () => new QuoteFunnelDto { RangeLabel = range.Label }, warnings);
        var engagementTask  = SafeLoadAsync("Engagement",  () => _analytics.GetEngagementSummaryAsync(range, scope, trafficType),
                                 () => new EngagementSummaryDto { RangeLabel = range.Label }, warnings);
        var exitTask        = SafeLoadAsync("Exit",        () => _analytics.GetExitAnalysisAsync(range, scope, trafficType),
                                 () => new ExitAnalysisDto { RangeLabel = range.Label }, warnings);
        var sourceTask      = SafeLoadAsync("Source",      () => _analytics.GetSourcePerformanceAsync(range, scope, trafficType),
                                 () => new SourcePerformanceDto { RangeLabel = range.Label }, warnings);
        var abandonTask     = SafeLoadAsync("Abandon",     () => _analytics.GetFormAbandonmentAsync(range, scope, trafficType),
                                 () => new FormAbandonmentDto { RangeLabel = range.Label }, warnings);

        // Meta Ads — active campaigns. SafeLoadAsync catches "not connected" / API errors gracefully.
        var metaAdsTask = SafeLoadAsync("MetaAds", () => _metaAds.GetCampaignsAsync(range, scope, ct),
                              () => new MetaCampaignsDto { RangeLabel = range.Label }, warnings);
        var metaSignalTask = SafeLoadAsync("MetaSignal", () => _metaSignalAnalytics.GetAiSummaryAsync(range, scope, trafficType, ct),
                              () => new MetaSignalAiSummaryDto(), warnings);

        await Task.WhenAll(
            summaryTask, pagePerfTask, quoteFunnelTask,
            engagementTask, exitTask, sourceTask, abandonTask, metaAdsTask, metaSignalTask);

        var summary    = await summaryTask;
        var pagePerf   = await pagePerfTask;
        var quote      = await quoteFunnelTask;
        var engagement = await engagementTask;
        var exit       = await exitTask;
        var source     = await sourceTask;
        var abandon    = await abandonTask;
        var metaCampaigns = await metaAdsTask;
        var metaSignal = await metaSignalTask;

        if (!string.IsNullOrWhiteSpace(metaSignal.LearningScopeNote))
        {
            warnings.Add(metaSignal.LearningScopeNote);
        }

        _logger.LogInformation(
            "AiDataBuilder results. Sessions={Sessions} UniqueVisitors={UniqueVisitors} VerifiedLeads={VerifiedLeads} " +
            "QuoteStarts={QuoteStarts} QuoteFormStarts={QuoteFormStarts} QuoteFormSubmits={QuoteFormSubmits}",
            summary.Sessions, summary.UniqueVisitors, summary.VerifiedLeads,
            quote.QuoteStarts, quote.QuoteFormStarts, quote.QuoteFormSubmits);

        return new AiSafeAnalyticsPayload
        {
            RangeLabel   = rangeLabel,
            ScopeLabel   = scopeLabel,
            TrafficFilter = trafficFilter,
            Warnings = warnings,

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
                }).ToList(),

            MetaSignal = new MetaSignalAiPayload
            {
                LearningScopeNote = metaSignal.LearningScopeNote,
                TotalSignalEvents = metaSignal.TotalSignalEvents,
                TotalVisitors = metaSignal.TotalVisitors,
                HighIntentVisitors = metaSignal.HighIntentVisitors,
                LeadReadyVisitors = metaSignal.LeadReadyVisitors,
                SubmittedLeads = metaSignal.SubmittedLeads,
                SubmitAttemptsWithoutLead = metaSignal.SubmitAttemptsWithoutLead,
                HighIntentAbandons = metaSignal.HighIntentAbandons,
                ContactStepAbandons = metaSignal.ContactStepAbandons,
                SignalToLeadConversionRate = metaSignal.SignalToLeadConversionRate,
                RecommendedOptimizationEvent = metaSignal.RecommendedOptimizationEvent,
                BestPerformingLandingPageVersion = metaSignal.BestPerformingLandingPageVersion,
                WorstFrictionStep = metaSignal.WorstFrictionStep,
                VisitorsByScoreTier = (metaSignal.VisitorsByScoreTier ?? new List<MetaSignalTierRowDto>())
                    .Select(x => new MetaSignalTierAiRow
                    {
                        ScoreTier = x.ScoreTier,
                        Visitors = x.Visitors
                    }).ToList(),
                AverageScoreByCampaign = (metaSignal.AverageScoreByCampaign ?? new List<MetaSignalAverageRowDto>())
                    .Take(5)
                    .Select(x => new MetaSignalAverageAiRow
                    {
                        Label = x.Label,
                        AverageScore = x.AverageScore
                    }).ToList(),
                AverageScoreByPageVariant = (metaSignal.AverageScoreByPageVariant ?? new List<MetaSignalAverageRowDto>())
                    .Take(5)
                    .Select(x => new MetaSignalAverageAiRow
                    {
                        Label = x.Label,
                        AverageScore = x.AverageScore
                    }).ToList(),
                EventLadder = (metaSignal.EventLadder ?? new List<MetaSignalLadderRowDto>())
                    .Select(x => new MetaSignalLadderAiRow
                    {
                        StepLabel = x.StepLabel,
                        Visitors = x.Visitors,
                        ProgressionRate = x.ProgressionRate
                    }).ToList()
            }

            // Intentionally omitted (non-conversion breakdowns):
            // TopPages, TopSources, TopCampaigns, EntryPages, CtaPerformance,
            // TotalConversions, TopDwellPages, IntentConversionRate, PageViews
        };
    }

    private async Task<T> SafeLoadAsync<T>(string taskName, Func<Task<T>> loader, Func<T> fallback, ICollection<string> warnings)
    {
        try
        {
            return await loader();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AiDataBuilder: {Task} query failed — returning zero fallback. {Message}",
                taskName, ex.Message);
            lock (warnings)
            {
                warnings.Add($"{taskName} used fallback data: {ex.Message}");
            }
            return fallback();
        }
    }
}

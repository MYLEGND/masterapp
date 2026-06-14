using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        var marketingHealthTask = SafeLoadAsync("MarketingHealth", () => _analytics.GetMarketingHealthAsync(range, scope, trafficType),
                                 () => new MarketingHealthDto { RangeLabel = range.Label, TrafficType = trafficType }, warnings);

        // Meta Ads — active campaigns. SafeLoadAsync catches "not connected" / API errors gracefully.
        var metaAdsTask = SafeLoadAsync("MetaAds", () => _metaAds.GetCampaignsAsync(range, scope, ct),
                              () => new MetaCampaignsDto { RangeLabel = range.Label }, warnings);
        var metaSignalTask = SafeLoadAsync("MetaSignal", () => _metaSignalAnalytics.GetAiSummaryAsync(range, scope, trafficType, ct),
                              () => new MetaSignalAiSummaryDto(), warnings);

        await Task.WhenAll(
            summaryTask, pagePerfTask, quoteFunnelTask,
            engagementTask, exitTask, sourceTask, abandonTask, marketingHealthTask, metaAdsTask, metaSignalTask);

        var summary    = await summaryTask;
        var pagePerf   = await pagePerfTask;
        var quote      = await quoteFunnelTask;
        var engagement = await engagementTask;
        var exit       = await exitTask;
        var source     = await sourceTask;
        var abandon    = await abandonTask;
        var marketingHealth = await marketingHealthTask;
        var metaCampaigns = await metaAdsTask;
        var metaSignal = await metaSignalTask;

        if (!string.IsNullOrWhiteSpace(metaSignal.LearningScopeNote))
        {
            warnings.Add(metaSignal.LearningScopeNote);
        }

        foreach (var healthWarning in marketingHealth.Warnings ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(healthWarning))
                warnings.Add(healthWarning);
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
            },

            MarketingHealth = new MarketingHealthAiPayload
            {
                ClientTrackingErrors = marketingHealth.ClientTrackingErrors,
                ClientTrackingErrorSessions = marketingHealth.ClientTrackingErrorSessions,
                InferredFormStarts = marketingHealth.InferredFormStarts,
                MissingStartEventSessions = marketingHealth.MissingStartEventSessions,
                LeadPersistedEvents = marketingHealth.LeadPersistedEvents,
                WorkstationCaptureAttempts = marketingHealth.WorkstationCaptureAttempts,
                WorkstationCaptureSuccesses = marketingHealth.WorkstationCaptureSuccesses,
                WorkstationCaptureFailures = marketingHealth.WorkstationCaptureFailures,
                WorkstationNoOwnerFailures = marketingHealth.WorkstationNoOwnerFailures,
                UnknownAttributedLeads = marketingHealth.UnknownAttributedLeads,
                InternalTrafficSessions = marketingHealth.InternalTrafficSessions,
                TestTrafficSessions = marketingHealth.TestTrafficSessions,
                BotSuspiciousSessions = marketingHealth.BotSuspiciousSessions,
                Warnings = (marketingHealth.Warnings ?? new List<string>()).ToList()
            }

            // Intentionally omitted (non-conversion breakdowns):
            // TopPages, TopSources, TopCampaigns, EntryPages, CtaPerformance,
            // TotalConversions, TopDwellPages, IntentConversionRate, PageViews
        };
    }

    public string BuildAiReviewSnapshotText(
        MetaCampaignsDto? metaCampaigns,
        MetaSignalDashboardDto? metaSignal,
        SummaryKpiDto summary,
        TrafficOverviewDto traffic,
        QuoteFunnelDto quote,
        ConversionCenterDto conversions,
        LeadSnapshotDto leads,
        PagePerformanceDto pagePerf,
        CtaPerformanceDto ctaPerf,
        TimeOnPageDto timeOnPage,
        ExitAnalysisDto exit,
        SourcePerformanceDto source,
        FormAbandonmentDto abandonment,
        string generatedAtLocal,
        string scopeLabel,
        string rangeLabel,
        string trafficScopeLabel,
        IReadOnlyCollection<string> warnings)
    {
        var sb = new StringBuilder();

        void Line(string value = "") => sb.AppendLine(value);

        static string Safe(string? value, string fallback = "—") =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        static string Pct(decimal? value) =>
            value.HasValue ? $"{value.Value:0.##}%" : "—";

        static string Money(decimal value) => $"${value.ToString("0.00", CultureInfo.InvariantCulture)}";

        static string Whole(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        static string Duration(double ms)
        {
            if (ms <= 0) return "—";
            var span = TimeSpan.FromMilliseconds(ms);
            if (span.TotalMinutes < 1) return $"{Math.Round(span.TotalSeconds)}s";
            return $"{(int)span.TotalMinutes}m {span.Seconds:00}s";
        }

        static List<KeyCountDto> TopKeyCounts(IEnumerable<KeyCountDto>? items, int take = 5)
        {
            return (items ?? Enumerable.Empty<KeyCountDto>())
                .OrderByDescending(x => x.Count)
                .Take(take)
                .ToList();
        }

        void AddKeyCountBlock(string title, IEnumerable<KeyCountDto>? rows, int take = 5)
        {
            Line(title);
            var top = TopKeyCounts(rows, take);
            if (!top.Any())
            {
                Line("No data in range.");
                return;
            }

            foreach (var row in top)
                Line($"- {Safe(row.Key)} ({row.Count})");
        }

        Line("SECTION A — HEADER");
        Line("WEBSITE ANALYTICS AI REVIEW SNAPSHOT");
        Line($"Generated: {generatedAtLocal}");
        Line($"Range: {rangeLabel}");
        Line($"Scope: {scopeLabel}");
        Line($"Traffic Filter: {trafficScopeLabel}");
        Line();

        var activeCampaigns = (metaCampaigns?.Rows ?? new List<MetaCampaignRow>())
            .Where(r => string.Equals(r.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Spend)
            .ThenByDescending(r => r.Impressions)
            .ThenBy(r => r.CampaignName)
            .ToList();

        Line("SECTION B — ACTIVE CAMPAIGN PERFORMANCE");
        Line($"Total active campaigns in range: {activeCampaigns.Count}");
        Line($"Total active campaign spend in range: {Money(activeCampaigns.Sum(x => x.Spend))}");
        if (activeCampaigns.Count == 0)
        {
            Line("- No active campaigns in range.");
        }
        else
        {
            foreach (var c in activeCampaigns)
            {
                Line($"- {Safe(c.CampaignName)} | {Safe(c.Status)} | {Safe(c.Objective)} | spend {Money(c.Spend)} | impr {Whole(c.Impressions)} | reach {Whole(c.Reach)} | clicks {Whole(c.Clicks)} | CTR {c.Ctr:0.##}% | CPC {Money(c.Cpc)} | CPM {Money(c.Cpm)} | meta leads {Whole(c.Leads)} | website leads {Whole(c.WebsiteLeads)} | qualified {Whole(c.QualifiedLeads)} | appointments {Whole(c.Appointments)} | applications {Whole(c.Applications)} | issued {Whole(c.PoliciesIssued)} | paid {Whole(c.PoliciesPaid)} | paid premium {Money(c.PaidPremium)} | premium ROAS {c.PremiumRoas:0.##}x");
            }

            var bestCtr = activeCampaigns
                .OrderByDescending(x => x.Ctr)
                .ThenByDescending(x => x.Impressions)
                .FirstOrDefault();
            if (bestCtr != null)
                Line($"Best CTR campaign: {Safe(bestCtr.CampaignName)} ({bestCtr.Ctr:0.##}%)");

            var lowestCpc = activeCampaigns
                .Where(x => x.Cpc > 0)
                .OrderBy(x => x.Cpc)
                .ThenByDescending(x => x.Clicks)
                .FirstOrDefault();
            if (lowestCpc != null)
                Line($"Lowest CPC campaign: {Safe(lowestCpc.CampaignName)} ({Money(lowestCpc.Cpc)})");
        }
        Line();

        Line("SECTION B2 — CAMPAIGN OUTCOME / REVENUE ATTRIBUTION");
        var campaignOutcomeRows = (metaCampaigns?.Rows ?? new List<MetaCampaignRow>())
            .OrderByDescending(x => x.PaidPremium)
            .ThenByDescending(x => x.PoliciesPaid)
            .ThenByDescending(x => x.WebsiteLeads)
            .ThenByDescending(x => x.Spend)
            .ThenBy(x => x.CampaignName)
            .Take(10)
            .ToList();

        if (campaignOutcomeRows.Count == 0)
        {
            Line("- No campaign rows available in range.");
        }
        else
        {
            foreach (var c in campaignOutcomeRows)
            {
                Line($"- {Safe(c.CampaignName)} | {Safe(c.Status)} | spend {Money(c.Spend)} | meta leads {Whole(c.Leads)} | website leads {Whole(c.WebsiteLeads)} | gap {Whole(c.WebsiteLeadGap)} | qualified {Whole(c.QualifiedLeads)} | appointments {Whole(c.Appointments)} | applications {Whole(c.Applications)} | issued {Whole(c.PoliciesIssued)} | paid {Whole(c.PoliciesPaid)} | paid premium {Money(c.PaidPremium)} | premium ROAS {c.PremiumRoas:0.##}x");
            }
        }

        Line("Interpretation rule: Campaign outcome/revenue fields come from server-side Meta signal attribution. Zeroes mean no attributed outcome in this selected range, not necessarily that the feature is broken.");

        Line();

        Line("SECTION C — META SIGNAL INTELLIGENCE");
        if (metaSignal == null)
        {
            Line("Meta Signal Intelligence unavailable in this snapshot.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(metaSignal.LearningScopeNote))
                Line(metaSignal.LearningScopeNote);
            Line($"Total signal events: {metaSignal.TotalSignalEvents}");
            Line($"Total visitors with signal: {metaSignal.TotalVisitors}");
            Line($"High-intent visitors: {metaSignal.HighIntentVisitors}");
            Line($"Lead-ready visitors: {metaSignal.LeadReadyVisitors}");
            Line($"Submitted leads: {metaSignal.SubmittedLeads}");
            Line($"Submit attempts without confirmed lead: {metaSignal.SubmitAttemptsWithoutLead}");
            Line($"High-intent abandons: {metaSignal.HighIntentAbandons}");
            Line($"Contact-step abandons: {metaSignal.ContactStepAbandons}");
            Line($"Excluded signal events: {metaSignal.ExcludedSignalEvents}");
            Line($"Excluded signal visitors: {metaSignal.ExcludedSignalVisitors}");
            Line($"Signal-to-lead conversion: {metaSignal.SignalToLeadConversionRate:0.##}%");
            Line($"Recommended optimization event right now: {Safe(metaSignal.RecommendedOptimizationEvent)}");
            Line($"Best-performing landing version: {Safe(metaSignal.BestPerformingLandingPageVersion)}");
            Line($"Worst friction step: {Safe(metaSignal.WorstFrictionStep)}");
            Line("Visitors by score tier:");
            var visitorsByTier = metaSignal.VisitorsByScoreTier ?? new List<MetaSignalTierRowDto>();
            if (visitorsByTier.Count == 0)
            {
                Line("No data in range.");
            }
            else
            {
                foreach (var tier in visitorsByTier)
                    Line($"- {Safe(tier.ScoreTier)}: {tier.Visitors}");
            }
            Line("Event ladder:");
            var signalLadder = metaSignal.EventLadder ?? new List<MetaSignalLadderRowDto>();
            if (signalLadder.Count == 0)
            {
                Line("No paid Meta-attributed funnel progression in range.");
            }
            else
            {
                foreach (var stage in signalLadder)
                {
                    var rateText = stage.ProgressionRate.HasValue ? $"{stage.ProgressionRate.Value:0.##}%" : "—";
                    Line($"- {Safe(stage.StepLabel)} | visitors {stage.Visitors} | progression {rateText}");
                }
            }
        }
        Line();

        Line("SECTION D — TRAFFIC HEALTH");
        Line($"Page Views: {summary.PageViews}");
        Line($"Unique Visitors: {summary.UniqueVisitors}");
        Line($"Sessions: {summary.Sessions}");
        AddKeyCountBlock("Top Pages (Top 5):", traffic.TopPages, 5);
        AddKeyCountBlock("Entry Pages (Top 5):", traffic.EntryPages, 5);
        AddKeyCountBlock("Top Sources (Top 5):", traffic.TopSources, 5);
        AddKeyCountBlock("Top Campaigns (Top 5):", traffic.TopCampaigns, 5);
        Line();

        Line("SECTION E — FUNNEL HEALTH");
        Line($"Quote Starts: {quote.QuoteStarts}");
        Line($"Quote Form Starts: {quote.QuoteFormStarts}");
        Line($"Successful Quote Submits: {quote.QuoteFormSubmits}");
        Line($"Leads: {leads.Total}");
        Line($"Intent Conversion: {(summary.IntentAvailable ? Pct(summary.IntentConversionRate) : "—")}");
        Line($"Session Conversion: {Pct(summary.SessionConversionRate)}");
        Line($"Total Conversions: {conversions.TotalConversions}");
        Line();

        var leadPages = (pagePerf.Rows ?? new List<PagePerformanceRow>())
            .Where(r => r.Leads > 0)
            .OrderByDescending(r => r.Leads)
            .Take(5)
            .ToList();

        Line("SECTION F — LEAD PICTURE");
        Line($"Total Leads in Range: {leads.Total}");
        Line("Lead volume by source page (Top 5):");
        if (leadPages.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in leadPages)
                Line($"- {Safe(row.PageKey)} ({row.Leads})");
        }
        Line(leads.Total > 0
            ? $"Recent lead activity summary: {leads.Total} leads captured in this range."
            : "Recent lead activity summary: No leads in range.");
        Line($"Top lead source page: {(leadPages.Count > 0 ? $"{Safe(leadPages[0].PageKey)} ({leadPages[0].Leads})" : "No data in range.")}");
        Line();

        Line("SECTION G — PAGE + CTA PERFORMANCE");
        Line($"Top Page: {Safe(summary.TopPage)}");
        Line($"Top CTA: {Safe(summary.TopCta)}");
        Line("Top 5 page performance rows:");
        var topPagesPerf = (pagePerf.Rows ?? new List<PagePerformanceRow>()).Take(5).ToList();
        if (topPagesPerf.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in topPagesPerf)
                Line($"- {Safe(row.PageKey)} | views {row.Views} | cta clicks {row.CtaClicks} | leads {row.Leads} | conv {row.ConversionRate:0.##}%");
        }
        Line("Top 5 CTA performance rows:");
        var topCtasPerf = (ctaPerf.Rows ?? new List<CtaPerformanceRow>()).Take(5).ToList();
        if (topCtasPerf.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in topCtasPerf)
                Line($"- {Safe(row.PageKey)} / {Safe(row.ElementKey)} | clicks {row.Clicks}");
        }
        Line();

        var topSources = TopKeyCounts(traffic.TopSources, 5);
        var topCampaigns = TopKeyCounts(traffic.TopCampaigns, 5);
        var topSourceTotal = topSources.Sum(x => x.Count);
        var topCampaignTotal = topCampaigns.Sum(x => x.Count);
        var topSourceLeadCount = topSources.Any() ? topSources[0].Count : 0;
        var topCampaignLeadCount = topCampaigns.Any() ? topCampaigns[0].Count : 0;
        var topSourceShare = topSourceTotal > 0 ? Math.Round((decimal)topSourceLeadCount / topSourceTotal * 100, 2) : 0;
        var topCampaignShare = topCampaignTotal > 0 ? Math.Round((decimal)topCampaignLeadCount / topCampaignTotal * 100, 2) : 0;

        Line("SECTION H — CAMPAIGN / SOURCE READ");
        if (topSources.Any())
        {
            Line($"Top source by events: {Safe(topSources[0].Key)} ({topSources[0].Count})");
            Line($"Top source concentration (within top source set): {topSourceShare:0.##}%");
        }
        else
        {
            Line("Top source by events: No data in range.");
        }
        if (topCampaigns.Any())
        {
            Line($"Top campaign by events: {Safe(topCampaigns[0].Key)} ({topCampaigns[0].Count})");
            Line($"Top campaign concentration (within top campaign set): {topCampaignShare:0.##}%");
        }
        else
        {
            Line("Top campaign by events: No data in range.");
        }

        var sourceRows = (source.Rows ?? new List<SourcePerformanceRow>()).Take(3).ToList();
        Line("Best performing source rows (Top 3 by sessions):");
        if (sourceRows.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in sourceRows)
                Line($"- {Safe(row.Source)} | sessions {row.Sessions} | leads {row.VerifiedLeads} | session conv {row.SessionConversionRate:0.##}%");
        }
        Line();

        Line("SECTION I — BEHAVIOR SIGNALS (DIRECTIONAL)");
        Line("Avg Time on Top Pages (Top 5):");
        var dwellRows = (timeOnPage.LongestAvgDwell ?? new List<DwellPageRow>()).Take(5).ToList();
        if (dwellRows.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in dwellRows)
                Line($"- {Safe(row.PageKey)} | avg dwell {Duration(row.AvgDwellMs)}");
        }
        Line("Exit Analysis (Top 3 exit pages):");
        var exitRows = (exit.TopExitPages ?? new List<ExitPageRow>()).Take(3).ToList();
        if (exitRows.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in exitRows)
                Line($"- {Safe(row.PageKey)} | exits {row.Exits} | exit rate {row.ExitRate:0.##}%");
        }
        Line("Form Abandonment Summary:");
        var abandonSummary = (abandonment.Summary ?? new List<FormAbandonSummaryRow>()).Take(3).ToList();
        if (abandonSummary.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in abandonSummary)
            {
                var abandonRate = row.AbandonRate.HasValue ? $"{row.AbandonRate.Value:0.##}%" : "—";
                Line($"- {Safe(row.QuoteType)} | abandons {row.Abandons} | abandon rate {abandonRate}");
            }
        }
        Line("Top Abandoned Fields:");
        var topFields = (abandonment.TopAbandonedFields ?? new List<TopAbandonedFieldRow>()).Take(5).ToList();
        if (topFields.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var field in topFields)
                Line($"- {Safe(field.FieldName)} ({field.AbandonCount})");
        }
        Line();

        Line("SECTION J — DATA QUALITY / CONTEXT NOTES");
        Line("- Metrics reflect the currently selected range and current scope.");
        Line("- Behavior signals are directional and should be interpreted with context.");
        Line("- Snapshot excludes sensitive lead details.");
        Line($"- Production/local filtering follows current analytics configuration: {summary.EnvironmentLabel}.");
        Line("- Use this summary together with campaign context and recent page changes.");
        Line("- Fallback warnings reduce confidence in affected modules only; they must not be treated as proof that observed non-zero sessions, events, form starts, submit attempts, or confirmed leads are zero.");
        Line("- If paid Meta Signal Intelligence is zero while traffic is internal or non-paid, describe it as no paid Meta-attributed signal in this slice, not as broken website tracking.");
        if (warnings.Any())
        {
            Line("- Current warnings:");
            foreach (var warning in warnings)
                Line($"  - {warning}");
        }
        Line();

        Line("SECTION K — CHATGPT COPY PROMPT FOOTER");
        Line("CHATGPT ANALYSIS REQUEST");
        Line("Analyze this website and ad performance snapshot.");
        Line("Identify:");
        Line("1. what is working");
        Line("2. what is underperforming");
        Line("3. likely causes");
        Line("4. the top 3 priorities");
        Line("5. what should be changed now");
        Line("6. what should be monitored longer before changing");
        Line("7. whether the data suggests a website issue, ad issue, funnel issue, traffic quality issue, or tracking issue");
        Line();
        Line("Provide a blunt, practical breakdown with priority order.");

        return sb.ToString().TrimEnd();
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

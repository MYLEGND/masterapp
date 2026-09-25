using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Shared.Analytics;

namespace Infrastructure.Analytics;

// The canonical KPI projection is used by agent and business analytics. Callers
// resolve authorization first and pass a server-owned scope; query strings never
// select a business identity here.
public sealed class AnalyticsDetailProjection(IAnalyticsQueryService _analytics, IKpiDetailBreakdownService _kpiDetailBreakdownService)
{
    public async Task<KpiDetailDto> KpiAsync(string metric, TimeRangeRequest range, ScopeContext scope,
        TrafficType trafficType, Func<TimeRangeRequest, ScopeContext, CancellationToken, Task<List<VisitorConcentrationDto>>> loadConcentration,
        CancellationToken ct = default)
    {
        metric = metric.Trim().ToLowerInvariant();
        if (metric is not ("pageviews" or "visitors" or "sessions" or "leads"))
            throw new ArgumentException("Choose a supported metric.");
        var span = range.ToUtc - range.FromUtc;
        var prevFrom = range.FromUtc - span;
        var prevTo = range.ToUtc - span;
        var prevRange = new TimeRangeRequest
        {
            FromUtc = prevFrom,
            ToUtc = prevTo,
            Grouping = range.Grouping,
            Label = range.Label,
            Preset = range.Preset,
            ViewerTimeZone = range.ViewerTimeZone,
            QualityMode = range.QualityMode
        };

        // Pull the data we need — reuse existing service methods, no duplication
        var traffic = await _analytics.GetTrafficAsync(range, scope, trafficType);
        var prevTraffic = await _analytics.GetTrafficAsync(prevRange, scope, trafficType);

        var summary = await _analytics.GetSummaryAsync(range, scope, trafficType);
        var previousSummary = await _analytics.GetSummaryAsync(prevRange, scope, trafficType);
        int total, prevTotal;
        List<TrendPointDto> series;

        switch (metric)
        {
            case "pageviews":
                total = traffic.PageViewTrend.Sum(p => p.Value);
                prevTotal = prevTraffic.PageViewTrend.Sum(p => p.Value);
                series = traffic.PageViewTrend;
                break;
            case "visitors":
                total = summary.UniqueVisitors;
                prevTotal = previousSummary.UniqueVisitors;
                series = traffic.VisitorTrend;
                break;
            case "sessions":
                total = summary.Sessions;
                prevTotal = previousSummary.Sessions;
                series = traffic.SessionTrend;
                break;
            case "leads":
                var leads = await _analytics.GetLeadsAsync(range, scope, trafficType, 5000);
                var prevLeads = await _analytics.GetLeadsAsync(prevRange, scope, trafficType, 5000);
                total = leads.Total;
                prevTotal = prevLeads.Total;
                series = BuildLeadDailySeries(leads, range);
                break;
            default:
                total = 0; prevTotal = 0; series = new List<TrendPointDto>();
                break;
        }

        var deltaCount = total - prevTotal;
        var deltaPct = prevTotal > 0 ? Math.Round((decimal)deltaCount / prevTotal * 100, 1) : 0;
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.FromUtc, DateTimeKind.Utc), range.ViewerTimeZone).Date;
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.ToUtc, DateTimeKind.Utc), range.ViewerTimeZone).Date;
        var days = Math.Max(1, (localEnd - localStart).TotalDays + 1);
        var avgPerDay = Math.Round((decimal)total / (decimal)days, 1);

        // Build breakdown
        var breakdown = new KpiDetailBreakdownDto();

        switch (metric)
        {
            case "pageviews":
                breakdown.TopPages = traffic.TopPages.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopSources = traffic.TopSources.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopCampaigns = traffic.TopCampaigns.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                break;

            case "visitors":
                breakdown.TopLandingPages = traffic.EntryPages.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();

                breakdown.TopSources = traffic.TopSources.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();

                breakdown.VisitorConcentration =
                    await loadConcentration(range, scope, ct);

                break;

            case "sessions":
                breakdown.TopLandingPages = traffic.EntryPages.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopSources = traffic.TopSources.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopCampaigns = traffic.TopCampaigns.Take(10)
                    .Select(x => new KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                break;

            case "leads":
                var leadsForBreakdown = await _analytics.GetLeadsAsync(range, scope, trafficType, 5000);
                breakdown = _kpiDetailBreakdownService.BuildLeadBreakdown(leadsForBreakdown);
                break;
        }

        var metricLabel = metric switch
        {
            "pageviews" => "Page Views",
            "visitors" => "Unique Visitors",
            "sessions" => "Sessions",
            "leads" => "Leads",
            _ => metric
        };

        var result = new KpiDetailDto
        {
            Metric = metric,
            Label = metricLabel,
            StartDateLocal = localStart.ToString("MMM d, yyyy"),
            EndDateLocal = localEnd.ToString("MMM d, yyyy"),
            Totals = new KpiDetailTotalsDto
            {
                Total = total,
                PreviousTotal = prevTotal,
                DeltaCount = deltaCount,
                DeltaPct = deltaPct,
                AvgPerDay = avgPerDay
            },
            Series = series,
            Breakdown = breakdown
        };

        return result;
    }

    private static List<TrendPointDto> BuildLeadDailySeries(LeadSnapshotDto leads, TimeRangeRequest range)
    {
        var tz = range.ViewerTimeZone;
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.FromUtc, DateTimeKind.Utc), tz).Date;
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.ToUtc, DateTimeKind.Utc), tz).Date;
        var grouped = leads.Leads
            .GroupBy(l => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(l.CreatedUtc, DateTimeKind.Utc), tz).Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var series = new List<TrendPointDto>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            series.Add(new TrendPointDto
            {
                Label = day.ToString("yyyy-MM-dd"),
                Value = grouped.TryGetValue(day, out var value) ? value : 0
            });
        }

        return series;
    }

    public async Task<object> VisitorTimelineAsync(string? visitorId, string? sessionId, TimeRangeRequest range,
        ScopeContext scope, TrafficType trafficType, IVisitorTrustScoringService trustScoring, CancellationToken ct = default)
    {
        var events = (await _analytics.LoadAttributedEventsAsync(range, scope, trafficType, ct))
            .Where(x => string.IsNullOrWhiteSpace(visitorId) || x.VisitorId == visitorId)
            .Where(x => string.IsNullOrWhiteSpace(sessionId) || x.SessionId == sessionId)
            .OrderBy(x => x.EventUtc).Take(500).ToList();
        var metaSignals = await _analytics.LoadScopedMetaEventsAsync(range, scope, events, ct);

        var trust = trustScoring.Calculate(events, metaSignals);

        return new
        {
            visitorId,
            sessionId,
            trustScore = trust.TrustScore,
            trustTier = trust.TrustTier,
            signals = trust.Signals,
            totalEvents = trust.TotalEvents,
            sessions = trust.Sessions,
            maxScroll = trust.MaxScroll,
            formStarts = trust.FormStarts,
            ctaClicks = trust.CtaClicks,
            averageSecondsBetweenEvents = trust.AverageSecondsBetweenEvents,
            burstEventCount = trust.BurstEventCount,
            humanConfidence = trust.HumanConfidence,
            behaviorScore = trust.BehaviorScore,
            intentScore = trust.IntentScore,
            engagementScore = trust.EngagementScore,
            frictionScore = trust.FrictionScore,
            leadReadinessScore = trust.LeadReadinessScore,
            events = events.Select(x => new { x.EventUtc, x.EventType, x.PageKey, x.SessionId,
                x.ScrollPercent, x.DwellMilliseconds, x.EngagedMilliseconds })
        };
    }

}

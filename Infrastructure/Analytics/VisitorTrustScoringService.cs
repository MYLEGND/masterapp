using Shared.Analytics;
using Infrastructure.Analytics;
using AgentPortal.Models.Analytics;
using Domain.Entities;

namespace AgentPortal.Services.Analytics;

public sealed class VisitorTrustScoringService : IVisitorTrustScoringService
{
    public VisitorTrustScoreDto Calculate(
        IReadOnlyCollection<AnalyticsEvent> events,
        IReadOnlyCollection<MetaSignalEvent> metaSignals)
    {
        var ordered = events.OrderBy(x => x.EventUtc).ToList();

        var totalEvents = ordered.Count;
        var sessions = ordered
            .Select(x => x.SessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var maxScroll = ordered
            .Where(x => x.ScrollPercent.HasValue)
            .Select(x => x.ScrollPercent!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var formStarts = CountEvents(ordered, "form_start", "lead_form_start");
        var ctaClicks = CountEvents(ordered, "cta_click", "quote_click");
        var pageViews = CountEvents(ordered, "page_view");
        var exits = CountEvents(ordered, "page_exit");
        var visibilityEvents = ordered.Count(x =>
            IsEvent(x, "page_visibility_hidden") ||
            IsEvent(x, "page_visibility_return"));

        var avgSecondsBetweenEvents = AverageSecondsBetweenEvents(ordered);
        var burstEventCount = MaxEventsInsideWindow(ordered, TimeSpan.FromSeconds(10));

        var bestMetaScore = metaSignals.Select(x => x.TotalSignalScore).DefaultIfEmpty(0).Max();
        var intentScore = metaSignals.Select(x => x.IntentScore).DefaultIfEmpty(0).Max();
        var engagementScore = metaSignals.Select(x => x.EngagementScore).DefaultIfEmpty(0).Max();
        var qualificationScore = metaSignals.Select(x => x.QualificationScore).DefaultIfEmpty(0).Max();
        var frictionScore = metaSignals.Select(x => x.FrictionScore).DefaultIfEmpty(0).Max();

        var bucket = TrafficQualityBucketFilters.Classify(ordered);
        var trustScore = ordered.Select(TrafficQualityBucketFilters.HumanScore).DefaultIfEmpty().Max();
        if (bucket is TrafficQualityMode.InternalQa or TrafficQualityMode.LikelyBotsAutomation) trustScore = 0;
        var trustTier = bucket switch {
            TrafficQualityMode.RealHumanTraffic => "Trusted",
            TrafficQualityMode.LikelyHuman => "Likely Human",
            TrafficQualityMode.InternalQa => "Internal QA",
            TrafficQualityMode.LikelyBotsAutomation => "Likely Bot",
            TrafficQualityMode.SuspiciousActivity => "Suspicious",
            _ => "Review"
        };
        var signals = new List<string> { "Canonical observed-behavior policy: " + TrafficQualityBucketFilters.ToClientValue(bucket) };

        return new VisitorTrustScoreDto
        {
            TrustScore = trustScore,
            TrustTier = trustTier,
            HumanConfidence = trustScore,
            Signals = signals.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TotalEvents = totalEvents,
            Sessions = sessions,
            MaxScroll = maxScroll,
            FormStarts = formStarts,
            CtaClicks = ctaClicks,
            AverageSecondsBetweenEvents = avgSecondsBetweenEvents,
            BurstEventCount = burstEventCount,
            BehaviorScore = bestMetaScore,
            IntentScore = intentScore,
            EngagementScore = engagementScore,
            FrictionScore = frictionScore,
            LeadReadinessScore = qualificationScore
        };
    }

    private static int CountEvents(IReadOnlyCollection<AnalyticsEvent> events, params string[] names) =>
        events.Count(x => names.Any(name => IsEvent(x, name)));

    private static bool IsEvent(AnalyticsEvent e, string eventType) =>
        string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static decimal AverageSecondsBetweenEvents(IReadOnlyList<AnalyticsEvent> events)
    {
        if (events.Count < 2) return 0;

        var gaps = new List<decimal>();

        for (var i = 1; i < events.Count; i++)
        {
            var gap = (decimal)(events[i].EventUtc - events[i - 1].EventUtc).TotalSeconds;
            if (gap >= 0 && gap <= 3600)
                gaps.Add(gap);
        }

        return gaps.Count == 0 ? 0 : Math.Round(gaps.Average(), 2);
    }

    private static int MaxEventsInsideWindow(IReadOnlyList<AnalyticsEvent> events, TimeSpan window)
    {
        if (events.Count == 0) return 0;

        var max = 1;
        var left = 0;

        for (var right = 0; right < events.Count; right++)
        {
            while (events[right].EventUtc - events[left].EventUtc > window)
                left++;

            max = Math.Max(max, right - left + 1);
        }

        return max;
    }
}

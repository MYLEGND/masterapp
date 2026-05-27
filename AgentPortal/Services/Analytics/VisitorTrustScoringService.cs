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

        var formStarts = ordered.Count(x =>
            string.Equals(x.EventType, "form_start", StringComparison.OrdinalIgnoreCase));

        var ctaClicks = ordered.Count(x =>
            string.Equals(x.EventType, "cta_click", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.EventType, "quote_click", StringComparison.OrdinalIgnoreCase));

        var avgSecondsBetweenEvents = AverageSecondsBetweenEvents(ordered);
        var burstEventCount = MaxEventsInsideWindow(ordered, TimeSpan.FromSeconds(10));

        var bestMetaScore = metaSignals.Select(x => x.TotalSignalScore).DefaultIfEmpty(0).Max();
        var intentScore = metaSignals.Select(x => x.IntentScore).DefaultIfEmpty(0).Max();
        var engagementScore = metaSignals.Select(x => x.EngagementScore).DefaultIfEmpty(0).Max();
        var qualificationScore = metaSignals.Select(x => x.QualificationScore).DefaultIfEmpty(0).Max();
        var frictionScore = metaSignals.Select(x => x.FrictionScore).DefaultIfEmpty(0).Max();

        var trustScore = 100;
        var signals = new List<string>();

        if (totalEvents >= 120)
        {
            trustScore -= 20;
            signals.Add("High event volume");
        }

        if (formStarts >= 4)
        {
            trustScore -= 15;
            signals.Add("Repeated form starts");
        }

        if (ctaClicks >= 12)
        {
            trustScore -= 15;
            signals.Add("Repeated CTA clicks");
        }

        if (maxScroll < 10 && totalEvents >= 10)
        {
            trustScore -= 10;
            signals.Add("Low/no scroll behavior");
        }

        if (ordered.Any(x => x.IsInternal))
        {
            trustScore -= 25;
            signals.Add("Internal/test traffic");
        }

        if (burstEventCount >= 25)
        {
            trustScore -= 20;
            signals.Add("High-speed event burst");
        }

        if (totalEvents >= 10 && avgSecondsBetweenEvents > 0 && avgSecondsBetweenEvents < 1.25m)
        {
            trustScore -= 15;
            signals.Add("Unnatural event cadence");
        }

        if (bestMetaScore >= 60)
        {
            trustScore += 8;
            signals.Add("Strong behavioral intent signal");
        }

        if (engagementScore >= 20 || maxScroll >= 50)
        {
            trustScore += 6;
            signals.Add("Meaningful engagement detected");
        }

        if (qualificationScore >= 20)
        {
            trustScore += 8;
            signals.Add("Lead-readiness behavior detected");
        }

        if (frictionScore >= 30)
        {
            trustScore -= 8;
            signals.Add("High friction detected");
        }

        trustScore = Math.Max(0, Math.Min(100, trustScore));

        var trustTier =
            trustScore >= 85 ? "Trusted" :
            trustScore >= 65 ? "Review" :
            trustScore >= 40 ? "Suspicious" :
            "Likely Bot";

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

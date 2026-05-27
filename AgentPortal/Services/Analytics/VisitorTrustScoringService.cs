using AgentPortal.Models.Analytics;
using Domain.Entities;

namespace AgentPortal.Services.Analytics;

public sealed class VisitorTrustScoringService : IVisitorTrustScoringService
{
    private static readonly HashSet<string> AutomationUserAgentTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "bot",
        "crawler",
        "spider",
        "headless",
        "phantom",
        "selenium",
        "puppeteer",
        "playwright",
        "curl",
        "wget",
        "python-requests",
        "httpclient"
    };

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

        var trustScore = 72;
        var signals = new List<string>();

        var hasResolvedBrowser = ordered.Any(x => !IsUnknown(x.Browser));
        var hasResolvedOs = ordered.Any(x => !IsUnknown(x.OperatingSystem));
        var hasResolvedDevice = ordered.Any(x => !IsUnknown(x.DeviceType));
        var hasUserAgent = ordered.Any(x => !string.IsNullOrWhiteSpace(x.UserAgent));
        var hasMouseMovement = ordered.Any(x => (x.MouseMoveCount ?? 0) > 0);
        var hasHumanInteraction = ordered.Any(x => (x.HumanInteractionCount ?? 0) > 0);
        var maxMouseMoves = ordered.Select(x => x.MouseMoveCount ?? 0).DefaultIfEmpty(0).Max();
        var webdriverDetected = ordered.Any(x => x.WebDriver == true);
        var headlessDetected = ordered.Any(x => x.IsHeadless == true);
        var automationUaDetected = ordered.Any(x => ContainsAutomationUserAgentToken(x.UserAgent));
        var directOnly = ordered
            .Where(x => !string.IsNullOrWhiteSpace(x.UtmSource))
            .Select(x => x.UtmSource!.Trim())
            .DefaultIfEmpty("Direct")
            .All(x => string.Equals(x, "Direct", StringComparison.OrdinalIgnoreCase));

        if (ordered.Any(x => x.IsInternal))
        {
            trustScore -= 30;
            signals.Add("Internal/test traffic");
        }

        if (webdriverDetected)
        {
            trustScore -= 45;
            signals.Add("Browser automation detected");
        }

        if (headlessDetected)
        {
            trustScore -= 45;
            signals.Add("Headless browser detected");
        }

        if (automationUaDetected)
        {
            trustScore -= 35;
            signals.Add("Automation user-agent pattern");
        }

        if (!hasUserAgent)
        {
            trustScore -= 18;
            signals.Add("Missing user agent");
        }

        if (!hasResolvedBrowser)
        {
            trustScore -= 14;
            signals.Add("Unknown browser");
        }

        if (!hasResolvedOs)
        {
            trustScore -= 14;
            signals.Add("Unknown operating system");
        }

        if (!hasResolvedDevice)
        {
            trustScore -= 8;
            signals.Add("Unknown device type");
        }

        if (!hasHumanInteraction && !hasMouseMovement && totalEvents >= 8 && formStarts == 0 && ctaClicks == 0)
        {
            trustScore -= 18;
            signals.Add("No human interaction captured");
        }

        if (hasHumanInteraction || maxMouseMoves >= 5)
        {
            trustScore += 8;
            signals.Add(hasHumanInteraction ? "Human interaction detected" : "Human pointer movement detected");
        }

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
            trustScore -= 12;
            signals.Add("Low/no scroll behavior");
        }

        if (maxScroll >= 25)
        {
            trustScore += 7;
            signals.Add("Scroll behavior detected");
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

        if (directOnly && totalEvents >= 10 && formStarts == 0 && ctaClicks == 0 && maxScroll < 50)
        {
            trustScore -= 10;
            signals.Add("Low-intent direct session");
        }

        if (pageViews <= 1 && exits == 0 && totalEvents >= 10 && formStarts == 0)
        {
            trustScore -= 8;
            signals.Add("No clean exit signal");
        }

        if (visibilityEvents >= 2)
        {
            trustScore += 4;
            signals.Add("Natural visibility changes");
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

        if (qualificationScore >= 20 || formStarts > 0)
        {
            trustScore += 10;
            signals.Add("Lead-readiness behavior detected");
        }

        if (frictionScore >= 30)
        {
            trustScore -= 8;
            signals.Add("High friction detected");
        }

        trustScore = Math.Max(0, Math.Min(100, trustScore));

        var trustTier =
            webdriverDetected || headlessDetected || automationUaDetected ? "Likely Bot" :
            trustScore >= 88 ? "Trusted" :
            trustScore >= 70 ? "Likely Human" :
            trustScore >= 50 ? "Review" :
            trustScore >= 30 ? "Suspicious" :
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

    private static int CountEvents(IReadOnlyCollection<AnalyticsEvent> events, params string[] names) =>
        events.Count(x => names.Any(name => IsEvent(x, name)));

    private static bool IsEvent(AnalyticsEvent e, string eventType) =>
        string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAutomationUserAgentToken(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return false;
        return AutomationUserAgentTokens.Any(token =>
            userAgent.Contains(token, StringComparison.OrdinalIgnoreCase));
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

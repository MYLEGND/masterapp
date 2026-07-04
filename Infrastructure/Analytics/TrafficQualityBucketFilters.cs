using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.Analytics;

public static class TrafficQualityBucketFilters
{
    public const string RealHumanTrafficClientValue = "real_human_traffic";
    public const string LikelyHumanClientValue = "likely_human";
    public const string ReviewedNeededClientValue = "reviewed_needed";
    public const string SuspiciousActivityClientValue = "suspicious_activity";
    public const string LikelyBotsAutomationClientValue = "likely_bots_automation";
    public const string InternalQaClientValue = "internal_qa";
    public const string AllTrafficClientValue = "all_traffic";

    private static readonly string[] StrongHumanEventTypes =
    [
        "page_engaged_15s",
        "page_engaged_30s",
        "page_engaged_60s",
        "scroll_depth_50",
        "scroll_depth_75",
        "scroll_depth_90",
        "scroll_depth_100",
        "lead_form_submit_success",
        "website_lead_submitted",
        "lead_persisted",
        "appointment_booked",
        "appointment_completed",
        "life_step2_submit_success",
        "results_contact_submit",
        "life_contact_first_submit_success",
        "life_contact_first_complete"
    ];

    private static readonly string[] ModerateHumanEventTypes =
    [
        "page_engaged_5s",
        "page_engaged_10s",
        "scroll_depth_25",
        "cta_click",
        "quote_cta_click",
        "cta_clicked",
        "quote_entry_engaged",
        "quote_step_complete",
        "form_start",
        "lead_form_start",
        "first_question_answered",
        "contact_step_view",
        "quote_contact_step_view",
        "life_contact_first_start"
    ];

    // Shared analytics buckets should treat only explicit dev/test-like environment
    // labels as non-production. App/site names such as "ParfaitApp" are runtime
    // identifiers, not traffic-quality signals, and should not be auto-classified
    // as internal QA.

    private sealed record InMemoryEventBucketMembership(
        HashSet<string> SessionIds,
        HashSet<string> VisitorIds,
        HashSet<Guid> EventIds);

    public static Expression<Func<AnalyticsEvent, bool>> BuildEventPredicate(TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.AllTraffic => e => true,

            TrafficQualityMode.InternalQa => e =>
                e.IsInternal ||
                (e.Environment != null &&
                 e.Environment != "" &&
                 (e.Environment.ToLower().StartsWith("dev") ||
                  e.Environment.ToLower().StartsWith("stag") ||
                  e.Environment.ToLower().StartsWith("preview") ||
                  e.Environment.ToLower().StartsWith("sandbox") ||
                  e.Environment.ToLower().StartsWith("qa") ||
                  e.Environment.ToLower().StartsWith("test") ||
                  e.Environment.ToLower().StartsWith("local"))) ||
                (e.Host != null &&
                 e.Host != "" &&
                 (e.Host.ToLower().Contains("localhost") ||
                  e.Host.StartsWith("127.0.0.1") ||
                  e.Host.StartsWith("::1") ||
                  e.Host.StartsWith("[::1]"))),

            TrafficQualityMode.LikelyBotsAutomation => e =>
                !(e.IsInternal ||
                  (e.Environment != null &&
                   e.Environment != "" &&
                   (e.Environment.ToLower().StartsWith("dev") ||
                    e.Environment.ToLower().StartsWith("stag") ||
                    e.Environment.ToLower().StartsWith("preview") ||
                    e.Environment.ToLower().StartsWith("sandbox") ||
                    e.Environment.ToLower().StartsWith("qa") ||
                    e.Environment.ToLower().StartsWith("test") ||
                    e.Environment.ToLower().StartsWith("local"))) ||
                  (e.Host != null &&
                   e.Host != "" &&
                   (e.Host.ToLower().Contains("localhost") ||
                    e.Host.StartsWith("127.0.0.1") ||
                    e.Host.StartsWith("::1") ||
                    e.Host.StartsWith("[::1]")))) &&
                (e.WebDriver == true ||
                 e.IsHeadless == true ||
                 (e.UserAgent ?? "").ToLower().Contains("bot") ||
                 (e.UserAgent ?? "").ToLower().Contains("crawler") ||
                 (e.UserAgent ?? "").ToLower().Contains("spider") ||
                 (e.UserAgent ?? "").ToLower().Contains("headless") ||
                 (e.UserAgent ?? "").ToLower().Contains("selenium") ||
                 (e.UserAgent ?? "").ToLower().Contains("puppeteer") ||
                 (e.UserAgent ?? "").ToLower().Contains("playwright") ||
                 (e.UserAgent ?? "").ToLower().Contains("curl") ||
                 (e.UserAgent ?? "").ToLower().Contains("wget") ||
                 (e.UserAgent ?? "").ToLower().Contains("python-requests") ||
                 (e.UserAgent ?? "").ToLower().Contains("httpclient")),

            TrafficQualityMode.SuspiciousActivity => e =>
                !(e.IsInternal ||
                  (e.Environment != null &&
                   e.Environment != "" &&
                   (e.Environment.ToLower().StartsWith("dev") ||
                    e.Environment.ToLower().StartsWith("stag") ||
                    e.Environment.ToLower().StartsWith("preview") ||
                    e.Environment.ToLower().StartsWith("sandbox") ||
                    e.Environment.ToLower().StartsWith("qa") ||
                    e.Environment.ToLower().StartsWith("test") ||
                    e.Environment.ToLower().StartsWith("local"))) ||
                  (e.Host != null &&
                   e.Host != "" &&
                   (e.Host.ToLower().Contains("localhost") ||
                    e.Host.StartsWith("127.0.0.1") ||
                    e.Host.StartsWith("::1") ||
                    e.Host.StartsWith("[::1]")))) &&
                !(e.WebDriver == true ||
                  e.IsHeadless == true ||
                  (e.UserAgent ?? "").ToLower().Contains("bot") ||
                  (e.UserAgent ?? "").ToLower().Contains("crawler") ||
                  (e.UserAgent ?? "").ToLower().Contains("spider") ||
                  (e.UserAgent ?? "").ToLower().Contains("headless") ||
                  (e.UserAgent ?? "").ToLower().Contains("selenium") ||
                  (e.UserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (e.UserAgent ?? "").ToLower().Contains("playwright") ||
                  (e.UserAgent ?? "").ToLower().Contains("curl") ||
                  (e.UserAgent ?? "").ToLower().Contains("wget") ||
                  (e.UserAgent ?? "").ToLower().Contains("python-requests") ||
                  (e.UserAgent ?? "").ToLower().Contains("httpclient")) &&
                (e.IsBounceCandidate == true || e.IsExitPage == true) &&
                (e.EngagedMilliseconds == null || e.EngagedMilliseconds < 1000) &&
                (e.DwellMilliseconds == null || e.DwellMilliseconds < 5000) &&
                (e.ScrollPercent == null || e.ScrollPercent < 15) &&
                (e.HumanInteractionCount == null || e.HumanInteractionCount <= 0) &&
                (e.MouseMoveCount == null || e.MouseMoveCount < 2),

            TrafficQualityMode.RealHumanTraffic => e =>
                !(e.IsInternal ||
                  (e.Environment != null &&
                   e.Environment != "" &&
                   (e.Environment.ToLower().StartsWith("dev") ||
                    e.Environment.ToLower().StartsWith("stag") ||
                    e.Environment.ToLower().StartsWith("preview") ||
                    e.Environment.ToLower().StartsWith("sandbox") ||
                    e.Environment.ToLower().StartsWith("qa") ||
                    e.Environment.ToLower().StartsWith("test") ||
                    e.Environment.ToLower().StartsWith("local"))) ||
                  (e.Host != null &&
                   e.Host != "" &&
                   (e.Host.ToLower().Contains("localhost") ||
                    e.Host.StartsWith("127.0.0.1") ||
                    e.Host.StartsWith("::1") ||
                    e.Host.StartsWith("[::1]")))) &&
                !(e.WebDriver == true ||
                  e.IsHeadless == true ||
                  (e.UserAgent ?? "").ToLower().Contains("bot") ||
                  (e.UserAgent ?? "").ToLower().Contains("crawler") ||
                  (e.UserAgent ?? "").ToLower().Contains("spider") ||
                  (e.UserAgent ?? "").ToLower().Contains("headless") ||
                  (e.UserAgent ?? "").ToLower().Contains("selenium") ||
                  (e.UserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (e.UserAgent ?? "").ToLower().Contains("playwright") ||
                  (e.UserAgent ?? "").ToLower().Contains("curl") ||
                  (e.UserAgent ?? "").ToLower().Contains("wget") ||
                  (e.UserAgent ?? "").ToLower().Contains("python-requests") ||
                  (e.UserAgent ?? "").ToLower().Contains("httpclient")) &&
                !((e.IsBounceCandidate == true || e.IsExitPage == true) &&
                  (e.EngagedMilliseconds == null || e.EngagedMilliseconds < 1000) &&
                  (e.DwellMilliseconds == null || e.DwellMilliseconds < 5000) &&
                  (e.ScrollPercent == null || e.ScrollPercent < 15) &&
                  (e.HumanInteractionCount == null || e.HumanInteractionCount <= 0) &&
                  (e.MouseMoveCount == null || e.MouseMoveCount < 2)) &&
                e.SessionId != null &&
                e.SessionId != "" &&
                e.VisitorId != null &&
                e.VisitorId != "" &&
                (StrongHumanEventTypes.Contains(e.EventType ?? "") ||
                 (e.EngagedMilliseconds != null && e.EngagedMilliseconds >= 5000) ||
                 (e.DwellMilliseconds != null && e.DwellMilliseconds >= 15000) ||
                 (e.ScrollPercent != null && e.ScrollPercent >= 50) ||
                 (e.HumanInteractionCount != null && e.HumanInteractionCount >= 3) ||
                 (e.MouseMoveCount != null && e.MouseMoveCount >= 10)),

            TrafficQualityMode.LikelyHuman => e =>
                !(e.IsInternal ||
                  (e.Environment != null &&
                   e.Environment != "" &&
                   (e.Environment.ToLower().StartsWith("dev") ||
                    e.Environment.ToLower().StartsWith("stag") ||
                    e.Environment.ToLower().StartsWith("preview") ||
                    e.Environment.ToLower().StartsWith("sandbox") ||
                    e.Environment.ToLower().StartsWith("qa") ||
                    e.Environment.ToLower().StartsWith("test") ||
                    e.Environment.ToLower().StartsWith("local"))) ||
                  (e.Host != null &&
                   e.Host != "" &&
                   (e.Host.ToLower().Contains("localhost") ||
                    e.Host.StartsWith("127.0.0.1") ||
                    e.Host.StartsWith("::1") ||
                    e.Host.StartsWith("[::1]")))) &&
                !(e.WebDriver == true ||
                  e.IsHeadless == true ||
                  (e.UserAgent ?? "").ToLower().Contains("bot") ||
                  (e.UserAgent ?? "").ToLower().Contains("crawler") ||
                  (e.UserAgent ?? "").ToLower().Contains("spider") ||
                  (e.UserAgent ?? "").ToLower().Contains("headless") ||
                  (e.UserAgent ?? "").ToLower().Contains("selenium") ||
                  (e.UserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (e.UserAgent ?? "").ToLower().Contains("playwright") ||
                  (e.UserAgent ?? "").ToLower().Contains("curl") ||
                  (e.UserAgent ?? "").ToLower().Contains("wget") ||
                  (e.UserAgent ?? "").ToLower().Contains("python-requests") ||
                  (e.UserAgent ?? "").ToLower().Contains("httpclient")) &&
                !((e.IsBounceCandidate == true || e.IsExitPage == true) &&
                  (e.EngagedMilliseconds == null || e.EngagedMilliseconds < 1000) &&
                  (e.DwellMilliseconds == null || e.DwellMilliseconds < 5000) &&
                  (e.ScrollPercent == null || e.ScrollPercent < 15) &&
                  (e.HumanInteractionCount == null || e.HumanInteractionCount <= 0) &&
                  (e.MouseMoveCount == null || e.MouseMoveCount < 2)) &&
                !(e.SessionId != null &&
                  e.SessionId != "" &&
                  e.VisitorId != null &&
                  e.VisitorId != "" &&
                  (StrongHumanEventTypes.Contains(e.EventType ?? "") ||
                   (e.EngagedMilliseconds != null && e.EngagedMilliseconds >= 5000) ||
                   (e.DwellMilliseconds != null && e.DwellMilliseconds >= 15000) ||
                   (e.ScrollPercent != null && e.ScrollPercent >= 50) ||
                   (e.HumanInteractionCount != null && e.HumanInteractionCount >= 3) ||
                   (e.MouseMoveCount != null && e.MouseMoveCount >= 10))) &&
                ((e.SessionId != null && e.SessionId != "") ||
                 (e.VisitorId != null && e.VisitorId != "")) &&
                (StrongHumanEventTypes.Contains(e.EventType ?? "") ||
                 ModerateHumanEventTypes.Contains(e.EventType ?? "") ||
                 (e.EngagedMilliseconds != null && e.EngagedMilliseconds >= 1000) ||
                 (e.DwellMilliseconds != null && e.DwellMilliseconds >= 5000) ||
                 (e.ScrollPercent != null && e.ScrollPercent >= 15) ||
                 (e.HumanInteractionCount != null && e.HumanInteractionCount >= 1) ||
                 (e.MouseMoveCount != null && e.MouseMoveCount >= 3) ||
                 (e.ReferrerHost != null && e.ReferrerHost != "") ||
                 (e.UtmSource != null && e.UtmSource != "") ||
                 (e.UtmMedium != null && e.UtmMedium != "") ||
                 (e.UtmCampaign != null && e.UtmCampaign != "") ||
                 (e.MetaCampaignId != null && e.MetaCampaignId != "") ||
                 (e.MetaAdSetId != null && e.MetaAdSetId != "") ||
                 (e.MetaAdId != null && e.MetaAdId != "") ||
                 (e.Fbclid != null && e.Fbclid != "")),

            TrafficQualityMode.ReviewedNeeded => e =>
                !(e.IsInternal ||
                  (e.Environment != null &&
                   e.Environment != "" &&
                   (e.Environment.ToLower().StartsWith("dev") ||
                    e.Environment.ToLower().StartsWith("stag") ||
                    e.Environment.ToLower().StartsWith("preview") ||
                    e.Environment.ToLower().StartsWith("sandbox") ||
                    e.Environment.ToLower().StartsWith("qa") ||
                    e.Environment.ToLower().StartsWith("test") ||
                    e.Environment.ToLower().StartsWith("local"))) ||
                  (e.Host != null &&
                   e.Host != "" &&
                   (e.Host.ToLower().Contains("localhost") ||
                    e.Host.StartsWith("127.0.0.1") ||
                    e.Host.StartsWith("::1") ||
                    e.Host.StartsWith("[::1]")))) &&
                !(e.WebDriver == true ||
                  e.IsHeadless == true ||
                  (e.UserAgent ?? "").ToLower().Contains("bot") ||
                  (e.UserAgent ?? "").ToLower().Contains("crawler") ||
                  (e.UserAgent ?? "").ToLower().Contains("spider") ||
                  (e.UserAgent ?? "").ToLower().Contains("headless") ||
                  (e.UserAgent ?? "").ToLower().Contains("selenium") ||
                  (e.UserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (e.UserAgent ?? "").ToLower().Contains("playwright") ||
                  (e.UserAgent ?? "").ToLower().Contains("curl") ||
                  (e.UserAgent ?? "").ToLower().Contains("wget") ||
                  (e.UserAgent ?? "").ToLower().Contains("python-requests") ||
                  (e.UserAgent ?? "").ToLower().Contains("httpclient")) &&
                !((e.IsBounceCandidate == true || e.IsExitPage == true) &&
                  (e.EngagedMilliseconds == null || e.EngagedMilliseconds < 1000) &&
                  (e.DwellMilliseconds == null || e.DwellMilliseconds < 5000) &&
                  (e.ScrollPercent == null || e.ScrollPercent < 15) &&
                  (e.HumanInteractionCount == null || e.HumanInteractionCount <= 0) &&
                  (e.MouseMoveCount == null || e.MouseMoveCount < 2)) &&
                !(e.SessionId != null &&
                  e.SessionId != "" &&
                  e.VisitorId != null &&
                  e.VisitorId != "" &&
                  (StrongHumanEventTypes.Contains(e.EventType ?? "") ||
                   (e.EngagedMilliseconds != null && e.EngagedMilliseconds >= 5000) ||
                   (e.DwellMilliseconds != null && e.DwellMilliseconds >= 15000) ||
                   (e.ScrollPercent != null && e.ScrollPercent >= 50) ||
                   (e.HumanInteractionCount != null && e.HumanInteractionCount >= 3) ||
                   (e.MouseMoveCount != null && e.MouseMoveCount >= 10))) &&
                !(((e.SessionId != null && e.SessionId != "") ||
                   (e.VisitorId != null && e.VisitorId != "")) &&
                  (StrongHumanEventTypes.Contains(e.EventType ?? "") ||
                   ModerateHumanEventTypes.Contains(e.EventType ?? "") ||
                   (e.EngagedMilliseconds != null && e.EngagedMilliseconds >= 1000) ||
                   (e.DwellMilliseconds != null && e.DwellMilliseconds >= 5000) ||
                   (e.ScrollPercent != null && e.ScrollPercent >= 15) ||
                   (e.HumanInteractionCount != null && e.HumanInteractionCount >= 1) ||
                   (e.MouseMoveCount != null && e.MouseMoveCount >= 3) ||
                   (e.ReferrerHost != null && e.ReferrerHost != "") ||
                   (e.UtmSource != null && e.UtmSource != "") ||
                   (e.UtmMedium != null && e.UtmMedium != "") ||
                   (e.UtmCampaign != null && e.UtmCampaign != "") ||
                   (e.MetaCampaignId != null && e.MetaCampaignId != "") ||
                   (e.MetaAdSetId != null && e.MetaAdSetId != "") ||
                   (e.MetaAdId != null && e.MetaAdId != "") ||
                   (e.Fbclid != null && e.Fbclid != ""))),

            _ => BuildEventPredicate(TrafficQualityMode.RealHumanTraffic)
        };
    }

    public static List<AnalyticsEvent> ApplyEventBucketMembershipInMemory(
        IEnumerable<AnalyticsEvent> events,
        TrafficQualityMode mode)
    {
        var eventList = events.ToList();
        if (mode == TrafficQualityMode.AllTraffic || eventList.Count == 0)
            return eventList;

        var internalQaBucket = BuildEventBucketMembership(
            eventList.Where(BuildEventPredicate(TrafficQualityMode.InternalQa).Compile()));
        if (mode == TrafficQualityMode.InternalQa)
            return ApplyEventBucketMembership(eventList, internalQaBucket);

        var botCandidates = ExcludeEventBucketMembership(
            eventList.Where(BuildEventPredicate(TrafficQualityMode.LikelyBotsAutomation).Compile()),
            internalQaBucket);
        var botBucket = BuildEventBucketMembership(botCandidates);
        if (mode == TrafficQualityMode.LikelyBotsAutomation)
            return ApplyEventBucketMembership(eventList, botBucket);

        var realHumanCandidates = ExcludeEventBucketMembership(
            eventList.Where(BuildEventPredicate(TrafficQualityMode.RealHumanTraffic).Compile()),
            internalQaBucket,
            botBucket);
        var realHumanBucket = BuildEventBucketMembership(realHumanCandidates);
        if (mode == TrafficQualityMode.RealHumanTraffic)
            return ApplyEventBucketMembership(eventList, realHumanBucket);

        var likelyHumanCandidates = ExcludeEventBucketMembership(
            eventList.Where(BuildEventPredicate(TrafficQualityMode.LikelyHuman).Compile()),
            internalQaBucket,
            botBucket,
            realHumanBucket);
        var likelyHumanBucket = BuildEventBucketMembership(likelyHumanCandidates);
        if (mode == TrafficQualityMode.LikelyHuman)
            return ApplyEventBucketMembership(eventList, likelyHumanBucket);

        // Bucket membership is session-oriented, so a later strong-human action
        // should outrank an early starter row that briefly looks bounce-like.
        var suspiciousCandidates = ExcludeEventBucketMembership(
            eventList.Where(BuildEventPredicate(TrafficQualityMode.SuspiciousActivity).Compile()),
            internalQaBucket,
            botBucket,
            realHumanBucket,
            likelyHumanBucket);
        var suspiciousBucket = BuildEventBucketMembership(suspiciousCandidates);
        if (mode == TrafficQualityMode.SuspiciousActivity)
            return ApplyEventBucketMembership(eventList, suspiciousBucket);

        var reviewedNeededCandidates = ExcludeEventBucketMembership(
            eventList,
            internalQaBucket,
            botBucket,
            realHumanBucket,
            likelyHumanBucket,
            suspiciousBucket);

        return ApplyEventBucketMembership(
            eventList,
            BuildEventBucketMembership(reviewedNeededCandidates));
    }

    private static InMemoryEventBucketMembership BuildEventBucketMembership(IEnumerable<AnalyticsEvent> events)
    {
        var sessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eventIds = new HashSet<Guid>();

        foreach (var analyticsEvent in events)
        {
            if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId))
                sessionIds.Add(analyticsEvent.SessionId!);
            else if (!string.IsNullOrWhiteSpace(analyticsEvent.VisitorId))
                visitorIds.Add(analyticsEvent.VisitorId!);
            else
                eventIds.Add(analyticsEvent.EventId);
        }

        return new InMemoryEventBucketMembership(sessionIds, visitorIds, eventIds);
    }

    private static IEnumerable<AnalyticsEvent> ExcludeEventBucketMembership(
        IEnumerable<AnalyticsEvent> events,
        params InMemoryEventBucketMembership[] excludedBuckets)
    {
        return events.Where(analyticsEvent => !excludedBuckets.Any(bucket => IsEventInBucket(analyticsEvent, bucket)));
    }

    private static List<AnalyticsEvent> ApplyEventBucketMembership(
        IEnumerable<AnalyticsEvent> events,
        InMemoryEventBucketMembership bucket)
    {
        return events.Where(analyticsEvent => IsEventInBucket(analyticsEvent, bucket)).ToList();
    }

    private static bool IsEventInBucket(AnalyticsEvent analyticsEvent, InMemoryEventBucketMembership bucket)
    {
        if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId))
            return bucket.SessionIds.Contains(analyticsEvent.SessionId!);

        if (!string.IsNullOrWhiteSpace(analyticsEvent.VisitorId))
            return bucket.VisitorIds.Contains(analyticsEvent.VisitorId!);

        return bucket.EventIds.Contains(analyticsEvent.EventId);
    }

    public static Expression<Func<WebsiteLead, bool>> BuildLeadPredicate(TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.AllTraffic => l => true,

            TrafficQualityMode.InternalQa => l =>
                l.IsInternal ||
                (l.Environment != null &&
                 l.Environment != "" &&
                 (l.Environment.ToLower().StartsWith("dev") ||
                  l.Environment.ToLower().StartsWith("stag") ||
                  l.Environment.ToLower().StartsWith("preview") ||
                  l.Environment.ToLower().StartsWith("sandbox") ||
                  l.Environment.ToLower().StartsWith("qa") ||
                  l.Environment.ToLower().StartsWith("test") ||
                  l.Environment.ToLower().StartsWith("local"))) ||
                (l.Host != null &&
                 l.Host != "" &&
                 (l.Host.ToLower().Contains("localhost") ||
                  l.Host.StartsWith("127.0.0.1") ||
                  l.Host.StartsWith("::1") ||
                  l.Host.StartsWith("[::1]"))),

            TrafficQualityMode.LikelyBotsAutomation => l =>
                !(l.IsInternal ||
                  (l.Environment != null &&
                   l.Environment != "" &&
                   (l.Environment.ToLower().StartsWith("dev") ||
                    l.Environment.ToLower().StartsWith("stag") ||
                    l.Environment.ToLower().StartsWith("preview") ||
                    l.Environment.ToLower().StartsWith("sandbox") ||
                    l.Environment.ToLower().StartsWith("qa") ||
                    l.Environment.ToLower().StartsWith("test") ||
                    l.Environment.ToLower().StartsWith("local"))) ||
                  (l.Host != null &&
                   l.Host != "" &&
                   (l.Host.ToLower().Contains("localhost") ||
                    l.Host.StartsWith("127.0.0.1") ||
                    l.Host.StartsWith("::1") ||
                    l.Host.StartsWith("[::1]")))) &&
                ((l.ClientUserAgent ?? "").ToLower().Contains("bot") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("crawler") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("spider") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("headless") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("selenium") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("puppeteer") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("playwright") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("curl") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("wget") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("python-requests") ||
                 (l.ClientUserAgent ?? "").ToLower().Contains("httpclient")),

            TrafficQualityMode.SuspiciousActivity => l =>
                !(l.IsInternal ||
                  (l.Environment != null &&
                   l.Environment != "" &&
                   (l.Environment.ToLower().StartsWith("dev") ||
                    l.Environment.ToLower().StartsWith("stag") ||
                    l.Environment.ToLower().StartsWith("preview") ||
                    l.Environment.ToLower().StartsWith("sandbox") ||
                    l.Environment.ToLower().StartsWith("qa") ||
                    l.Environment.ToLower().StartsWith("test") ||
                    l.Environment.ToLower().StartsWith("local"))) ||
                  (l.Host != null &&
                   l.Host != "" &&
                   (l.Host.ToLower().Contains("localhost") ||
                    l.Host.StartsWith("127.0.0.1") ||
                    l.Host.StartsWith("::1") ||
                    l.Host.StartsWith("[::1]")))) &&
                !((l.ClientUserAgent ?? "").ToLower().Contains("bot") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("crawler") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("spider") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("headless") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("selenium") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("playwright") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("curl") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("wget") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("python-requests") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("httpclient")) &&
                (l.SessionId == null || l.SessionId == "") &&
                (l.VisitorId == null || l.VisitorId == "") &&
                (l.UtmSource == null || l.UtmSource == "") &&
                (l.UtmMedium == null || l.UtmMedium == "") &&
                (l.UtmCampaign == null || l.UtmCampaign == "") &&
                (l.MetaCampaignId == null || l.MetaCampaignId == "") &&
                (l.MetaAdSetId == null || l.MetaAdSetId == "") &&
                (l.MetaAdId == null || l.MetaAdId == "") &&
                (l.Fbclid == null || l.Fbclid == "") &&
                (l.Fbp == null || l.Fbp == "") &&
                (l.Fbc == null || l.Fbc == "") &&
                !l.TermsAccepted,

            TrafficQualityMode.RealHumanTraffic => l =>
                !(l.IsInternal ||
                  (l.Environment != null &&
                   l.Environment != "" &&
                   (l.Environment.ToLower().StartsWith("dev") ||
                    l.Environment.ToLower().StartsWith("stag") ||
                    l.Environment.ToLower().StartsWith("preview") ||
                    l.Environment.ToLower().StartsWith("sandbox") ||
                    l.Environment.ToLower().StartsWith("qa") ||
                    l.Environment.ToLower().StartsWith("test") ||
                    l.Environment.ToLower().StartsWith("local"))) ||
                  (l.Host != null &&
                   l.Host != "" &&
                   (l.Host.ToLower().Contains("localhost") ||
                    l.Host.StartsWith("127.0.0.1") ||
                    l.Host.StartsWith("::1") ||
                    l.Host.StartsWith("[::1]")))) &&
                !((l.ClientUserAgent ?? "").ToLower().Contains("bot") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("crawler") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("spider") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("headless") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("selenium") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("playwright") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("curl") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("wget") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("python-requests") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("httpclient")) &&
                !((l.SessionId == null || l.SessionId == "") &&
                  (l.VisitorId == null || l.VisitorId == "") &&
                  (l.UtmSource == null || l.UtmSource == "") &&
                  (l.UtmMedium == null || l.UtmMedium == "") &&
                  (l.UtmCampaign == null || l.UtmCampaign == "") &&
                  (l.MetaCampaignId == null || l.MetaCampaignId == "") &&
                  (l.MetaAdSetId == null || l.MetaAdSetId == "") &&
                  (l.MetaAdId == null || l.MetaAdId == "") &&
                  (l.Fbclid == null || l.Fbclid == "") &&
                  (l.Fbp == null || l.Fbp == "") &&
                  (l.Fbc == null || l.Fbc == "") &&
                  !l.TermsAccepted) &&
                l.SessionId != null &&
                l.SessionId != "" &&
                l.VisitorId != null &&
                l.VisitorId != "" &&
                (l.TermsAccepted ||
                 l.MarketingEmailConsent ||
                 l.CallTextConsent ||
                 (l.UtmSource != null && l.UtmSource != "") ||
                 (l.UtmMedium != null && l.UtmMedium != "") ||
                 (l.UtmCampaign != null && l.UtmCampaign != "") ||
                 (l.MetaCampaignId != null && l.MetaCampaignId != "") ||
                 (l.MetaAdSetId != null && l.MetaAdSetId != "") ||
                 (l.MetaAdId != null && l.MetaAdId != "") ||
                 (l.Fbclid != null && l.Fbclid != "") ||
                 (l.Fbp != null && l.Fbp != "") ||
                 (l.Fbc != null && l.Fbc != "")),

            TrafficQualityMode.LikelyHuman => l =>
                !(l.IsInternal ||
                  (l.Environment != null &&
                   l.Environment != "" &&
                   (l.Environment.ToLower().StartsWith("dev") ||
                    l.Environment.ToLower().StartsWith("stag") ||
                    l.Environment.ToLower().StartsWith("preview") ||
                    l.Environment.ToLower().StartsWith("sandbox") ||
                    l.Environment.ToLower().StartsWith("qa") ||
                    l.Environment.ToLower().StartsWith("test") ||
                    l.Environment.ToLower().StartsWith("local"))) ||
                  (l.Host != null &&
                   l.Host != "" &&
                   (l.Host.ToLower().Contains("localhost") ||
                    l.Host.StartsWith("127.0.0.1") ||
                    l.Host.StartsWith("::1") ||
                    l.Host.StartsWith("[::1]")))) &&
                !((l.ClientUserAgent ?? "").ToLower().Contains("bot") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("crawler") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("spider") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("headless") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("selenium") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("playwright") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("curl") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("wget") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("python-requests") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("httpclient")) &&
                !((l.SessionId == null || l.SessionId == "") &&
                  (l.VisitorId == null || l.VisitorId == "") &&
                  (l.UtmSource == null || l.UtmSource == "") &&
                  (l.UtmMedium == null || l.UtmMedium == "") &&
                  (l.UtmCampaign == null || l.UtmCampaign == "") &&
                  (l.MetaCampaignId == null || l.MetaCampaignId == "") &&
                  (l.MetaAdSetId == null || l.MetaAdSetId == "") &&
                  (l.MetaAdId == null || l.MetaAdId == "") &&
                  (l.Fbclid == null || l.Fbclid == "") &&
                  (l.Fbp == null || l.Fbp == "") &&
                  (l.Fbc == null || l.Fbc == "") &&
                  !l.TermsAccepted) &&
                !(l.SessionId != null &&
                  l.SessionId != "" &&
                  l.VisitorId != null &&
                  l.VisitorId != "" &&
                  (l.TermsAccepted ||
                   l.MarketingEmailConsent ||
                   l.CallTextConsent ||
                   (l.UtmSource != null && l.UtmSource != "") ||
                   (l.UtmMedium != null && l.UtmMedium != "") ||
                   (l.UtmCampaign != null && l.UtmCampaign != "") ||
                   (l.MetaCampaignId != null && l.MetaCampaignId != "") ||
                   (l.MetaAdSetId != null && l.MetaAdSetId != "") ||
                   (l.MetaAdId != null && l.MetaAdId != "") ||
                   (l.Fbclid != null && l.Fbclid != "") ||
                   (l.Fbp != null && l.Fbp != "") ||
                   (l.Fbc != null && l.Fbc != ""))) &&
                ((l.SessionId != null && l.SessionId != "") ||
                 (l.VisitorId != null && l.VisitorId != "") ||
                 (l.UtmSource != null && l.UtmSource != "") ||
                 (l.UtmMedium != null && l.UtmMedium != "") ||
                 (l.UtmCampaign != null && l.UtmCampaign != "") ||
                 (l.MetaCampaignId != null && l.MetaCampaignId != "") ||
                 (l.MetaAdSetId != null && l.MetaAdSetId != "") ||
                 (l.MetaAdId != null && l.MetaAdId != "") ||
                 (l.Fbclid != null && l.Fbclid != "") ||
                 (l.Fbp != null && l.Fbp != "") ||
                 (l.Fbc != null && l.Fbc != "") ||
                 l.TermsAccepted ||
                 l.MarketingEmailConsent ||
                 l.CallTextConsent),

            TrafficQualityMode.ReviewedNeeded => l =>
                !(l.IsInternal ||
                  (l.Environment != null &&
                   l.Environment != "" &&
                   (l.Environment.ToLower().StartsWith("dev") ||
                    l.Environment.ToLower().StartsWith("stag") ||
                    l.Environment.ToLower().StartsWith("preview") ||
                    l.Environment.ToLower().StartsWith("sandbox") ||
                    l.Environment.ToLower().StartsWith("qa") ||
                    l.Environment.ToLower().StartsWith("test") ||
                    l.Environment.ToLower().StartsWith("local"))) ||
                  (l.Host != null &&
                   l.Host != "" &&
                   (l.Host.ToLower().Contains("localhost") ||
                    l.Host.StartsWith("127.0.0.1") ||
                    l.Host.StartsWith("::1") ||
                    l.Host.StartsWith("[::1]")))) &&
                !((l.ClientUserAgent ?? "").ToLower().Contains("bot") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("crawler") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("spider") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("headless") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("selenium") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("puppeteer") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("playwright") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("curl") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("wget") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("python-requests") ||
                  (l.ClientUserAgent ?? "").ToLower().Contains("httpclient")) &&
                !((l.SessionId == null || l.SessionId == "") &&
                  (l.VisitorId == null || l.VisitorId == "") &&
                  (l.UtmSource == null || l.UtmSource == "") &&
                  (l.UtmMedium == null || l.UtmMedium == "") &&
                  (l.UtmCampaign == null || l.UtmCampaign == "") &&
                  (l.MetaCampaignId == null || l.MetaCampaignId == "") &&
                  (l.MetaAdSetId == null || l.MetaAdSetId == "") &&
                  (l.MetaAdId == null || l.MetaAdId == "") &&
                  (l.Fbclid == null || l.Fbclid == "") &&
                  (l.Fbp == null || l.Fbp == "") &&
                  (l.Fbc == null || l.Fbc == "") &&
                  !l.TermsAccepted) &&
                !(l.SessionId != null &&
                  l.SessionId != "" &&
                  l.VisitorId != null &&
                  l.VisitorId != "" &&
                  (l.TermsAccepted ||
                   l.MarketingEmailConsent ||
                   l.CallTextConsent ||
                   (l.UtmSource != null && l.UtmSource != "") ||
                   (l.UtmMedium != null && l.UtmMedium != "") ||
                   (l.UtmCampaign != null && l.UtmCampaign != "") ||
                   (l.MetaCampaignId != null && l.MetaCampaignId != "") ||
                   (l.MetaAdSetId != null && l.MetaAdSetId != "") ||
                   (l.MetaAdId != null && l.MetaAdId != "") ||
                   (l.Fbclid != null && l.Fbclid != "") ||
                   (l.Fbp != null && l.Fbp != "") ||
                   (l.Fbc != null && l.Fbc != ""))) &&
                !(((l.SessionId != null && l.SessionId != "") ||
                   (l.VisitorId != null && l.VisitorId != "") ||
                   (l.UtmSource != null && l.UtmSource != "") ||
                   (l.UtmMedium != null && l.UtmMedium != "") ||
                   (l.UtmCampaign != null && l.UtmCampaign != "") ||
                   (l.MetaCampaignId != null && l.MetaCampaignId != "") ||
                   (l.MetaAdSetId != null && l.MetaAdSetId != "") ||
                   (l.MetaAdId != null && l.MetaAdId != "") ||
                   (l.Fbclid != null && l.Fbclid != "") ||
                   (l.Fbp != null && l.Fbp != "") ||
                   (l.Fbc != null && l.Fbc != "") ||
                   l.TermsAccepted ||
                   l.MarketingEmailConsent ||
                   l.CallTextConsent)),

            _ => BuildLeadPredicate(TrafficQualityMode.RealHumanTraffic)
        };
    }

    public static string ToClientValue(TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.LikelyHuman => LikelyHumanClientValue,
            TrafficQualityMode.ReviewedNeeded => ReviewedNeededClientValue,
            TrafficQualityMode.SuspiciousActivity => SuspiciousActivityClientValue,
            TrafficQualityMode.LikelyBotsAutomation => LikelyBotsAutomationClientValue,
            TrafficQualityMode.InternalQa => InternalQaClientValue,
            TrafficQualityMode.AllTraffic => AllTrafficClientValue,
            _ => RealHumanTrafficClientValue
        };
    }

    public static TrafficQualityMode ParseClientOrEnumValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TrafficQualityMode.RealHumanTraffic;

        var normalized = value.Trim();
        if (Enum.TryParse<TrafficQualityMode>(normalized, ignoreCase: true, out var parsed))
            return parsed;

        return normalized.ToLowerInvariant() switch
        {
            RealHumanTrafficClientValue => TrafficQualityMode.RealHumanTraffic,
            LikelyHumanClientValue => TrafficQualityMode.LikelyHuman,
            ReviewedNeededClientValue => TrafficQualityMode.ReviewedNeeded,
            SuspiciousActivityClientValue => TrafficQualityMode.SuspiciousActivity,
            LikelyBotsAutomationClientValue => TrafficQualityMode.LikelyBotsAutomation,
            InternalQaClientValue => TrafficQualityMode.InternalQa,
            AllTrafficClientValue => TrafficQualityMode.AllTraffic,
            "real_human" => TrafficQualityMode.RealHumanTraffic,
            "review" => TrafficQualityMode.ReviewedNeeded,
            "suspicious" => TrafficQualityMode.SuspiciousActivity,
            "likely_bot" => TrafficQualityMode.LikelyBotsAutomation,
            "internal" => TrafficQualityMode.InternalQa,
            "all" => TrafficQualityMode.AllTraffic,
            _ => TrafficQualityMode.RealHumanTraffic
        };
    }
}

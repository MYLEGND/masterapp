using System;
using System.Linq;
using System.Linq.Expressions;
using AgentPortal.Models.Analytics;
using Domain.Entities;

namespace AgentPortal.Services.Analytics;

internal static class TrafficQualityBucketFilters
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

    public static Expression<Func<AnalyticsEvent, bool>> BuildEventPredicate(TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.AllTraffic => e => true,

            TrafficQualityMode.InternalQa => e =>
                e.IsInternal ||
                (e.Environment != null &&
                 e.Environment != "" &&
                 !e.Environment.ToLower().StartsWith("prod")) ||
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
                   !e.Environment.ToLower().StartsWith("prod")) ||
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
                   !e.Environment.ToLower().StartsWith("prod")) ||
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
                   !e.Environment.ToLower().StartsWith("prod")) ||
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
                   !e.Environment.ToLower().StartsWith("prod")) ||
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
                   !e.Environment.ToLower().StartsWith("prod")) ||
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

    public static Expression<Func<WebsiteLead, bool>> BuildLeadPredicate(TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.AllTraffic => l => true,

            TrafficQualityMode.InternalQa => l =>
                l.IsInternal ||
                (l.Environment != null &&
                 l.Environment != "" &&
                 !l.Environment.ToLower().StartsWith("prod")) ||
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
                   !l.Environment.ToLower().StartsWith("prod")) ||
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
                   !l.Environment.ToLower().StartsWith("prod")) ||
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
                   !l.Environment.ToLower().StartsWith("prod")) ||
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
                   !l.Environment.ToLower().StartsWith("prod")) ||
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
                   !l.Environment.ToLower().StartsWith("prod")) ||
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

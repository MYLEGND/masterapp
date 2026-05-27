using System.Collections.ObjectModel;

namespace Shared.Analytics;

public static class AnalyticsEventCatalog
{
    public const string ClientTrackingErrorEventName = "client_tracking_error";
    private static readonly string[] AllQuotes = ["all"];
    private static readonly string[] LifeQuotes = ["life", "term", "wholelife", "finalexpense", "mortgage", "iul"];

    private static readonly IReadOnlyList<AnalyticsEventDefinition> DefinitionsInternal =
    [
        Define("page_view", "page", AllQuotes, "landing", landing: true, allowBrowser: true, dashboardMetrics: ["page_view", "landing_view"]),
        Define("quote_landing_view", "quote", AllQuotes, "landing", landing: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["landing_view", "quote_landing_view"]),
        Define("thank_you_view", "quote", AllQuotes, "confirmation", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["thank_you_view", "funnel_completion"]),
        Define("page_exit", "page", AllQuotes, "exit", allowBrowser: true, dashboardMetrics: ["page_exit"]),
        Define("page_visibility_hidden", "page", AllQuotes, "engagement", allowBrowser: true, dashboardMetrics: ["page_visibility_hidden"]),
        Define("page_visibility_return", "page", AllQuotes, "engagement", allowBrowser: true, dashboardMetrics: ["page_visibility_return"]),
        Define("page_engaged_5s", "engagement", AllQuotes, "landing", meta: true, allowBrowser: true, dashboardMetrics: ["engaged_5s"]),
        Define("page_engaged_10s", "engagement", AllQuotes, "landing", meta: true, allowBrowser: true, dashboardMetrics: ["engaged_10s"]),
        Define("page_engaged_15s", "engagement", AllQuotes, "landing", meta: true, allowBrowser: true, dashboardMetrics: ["engaged_15s"]),
        Define("page_engaged_30s", "engagement", AllQuotes, "landing", meta: true, allowBrowser: true, dashboardMetrics: ["engaged_30s"]),
        Define("page_engaged_60s", "engagement", AllQuotes, "landing", meta: true, allowBrowser: true, dashboardMetrics: ["engaged_60s"]),
        Define("scroll_depth_25", "engagement", AllQuotes, "engagement", meta: true, allowBrowser: true, dashboardMetrics: ["scroll_depth_25"]),
        Define("scroll_depth_50", "engagement", AllQuotes, "engagement", meta: true, allowBrowser: true, dashboardMetrics: ["scroll_depth_50"]),
        Define("scroll_depth_75", "engagement", AllQuotes, "engagement", meta: true, allowBrowser: true, dashboardMetrics: ["scroll_depth_75"]),
        Define("scroll_depth_90", "engagement", AllQuotes, "engagement", meta: true, allowBrowser: true, dashboardMetrics: ["scroll_depth_90"]),
        Define("scroll_depth_100", "engagement", AllQuotes, "engagement", meta: true, allowBrowser: true, dashboardMetrics: ["scroll_depth_100"]),

        Define("cta_click", "cta", AllQuotes, "landing", cta: true, critical: true, allowBrowser: true, dashboardMetrics: ["cta_click"]),
        Define("quote_cta_click", "quote", AllQuotes, "landing", cta: true, critical: true, allowBrowser: true, dashboardMetrics: ["cta_click", "quote_cta_click", "primary_cta_click"]),
        Define("cta_clicked", "quote", AllQuotes, "landing", cta: true, critical: true, allowBrowser: true, dashboardMetrics: ["cta_click", "quote_cta_click", "primary_cta_click"]),
        Define("primary_cta_seen", "cta", AllQuotes, "landing", allowBrowser: true, dashboardMetrics: ["primary_cta_seen"]),
        Define("quote_entry_engaged", "quote", AllQuotes, "discovery", funnelStart: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_entry_engaged", "funnel_start"]),
        Define("quote_click", "cta", AllQuotes, "landing", cta: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_click", "cta_click"]),
        Define("quote_step_complete", "quote", AllQuotes, "discovery", funnelStart: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("quote_contact_step_view", "quote", AllQuotes, "contact", contactStep: true, critical: true, allowBrowser: true, dashboardMetrics: ["contact_step_view", "quote_contact_step_view"]),
        Define("risk_assessment_click", "cta", AllQuotes, "landing", cta: true, allowBrowser: true, dashboardMetrics: ["risk_assessment_click"]),
        Define("outbound_click", "cta", AllQuotes, "engagement", allowBrowser: true, dashboardMetrics: ["outbound_click"]),
        Define("carrier_trust_strip_view", "trust", AllQuotes, "landing", allowBrowser: true, dashboardMetrics: ["trust_view"]),
        Define("file_download", "engagement", AllQuotes, "engagement", allowBrowser: true, dashboardMetrics: ["file_download"]),
        Define("section_view", "engagement", AllQuotes, "engagement", allowBrowser: true, dashboardMetrics: ["section_view"]),
        Define("session_end", "session", AllQuotes, "exit", allowBrowser: true, dashboardMetrics: ["session_end"]),

        Define("form_start", "form", AllQuotes, "form", funnelStart: true, formStart: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("lead_form_start", "form", AllQuotes, "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["lead_form_start", "form_start", "funnel_start"]),
        Define("funnel_started", "quote", AllQuotes, "discovery", funnelStart: true, formStart: true, critical: true, allowBrowser: true, dashboardMetrics: ["funnel_start", "form_start"]),
        Define("lead_modal_open", "form", AllQuotes, "form", allowBrowser: true, dashboardMetrics: ["lead_modal_open"]),
        Define("lead_modal_close", "form", AllQuotes, "form", allowBrowser: true, dashboardMetrics: ["lead_modal_close"]),
        Define("form_field_focus", "field", AllQuotes, "form", allowBrowser: true, dashboardMetrics: ["field_focus"]),
        Define("form_field_complete", "field", AllQuotes, "form", allowBrowser: true, dashboardMetrics: ["field_complete"]),
        Define("form_field_error", "field", AllQuotes, "form", allowBrowser: true, dashboardMetrics: ["field_error", "validation_friction"]),
        Define("form_submit_attempt", "submit", AllQuotes, "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("lead_form_submit_attempt", "submit", AllQuotes, "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt", "lead_form_submit_attempt"]),
        Define("form_submit", "submit", AllQuotes, "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt", "form_submit"]),        Define("submit_failure", "submit", AllQuotes, "submit", critical: true, allowBrowser: true, dashboardMetrics: ["submit_failure"]),
        Define("form_abandon", "abandon", AllQuotes, "abandon", critical: true, allowBrowser: true, dashboardMetrics: ["form_abandon"]),
        Define("lead_form_submit_success", "submit", AllQuotes, "submit", confirmedLead: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_success", "confirmed_lead"]),
        Define("lead_form_submit_failed", "submit", AllQuotes, "submit", critical: true, allowBrowser: true, dashboardMetrics: ["submit_failure"]),
        Define("lead_form_submit_failure", "submit", AllQuotes, "submit", critical: true, allowBrowser: true, dashboardMetrics: ["submit_failure"]),
        Define("website_lead_submitted", "lead", AllQuotes, "confirmation", confirmedLead: true, critical: true, allowServer: true, dashboardMetrics: ["submit_success", "confirmed_lead", "lead_persisted"]),
        Define("lead_persisted", "lead", AllQuotes, "confirmation", confirmedLead: true, critical: true, allowServer: true, dashboardMetrics: ["lead_persisted", "confirmed_lead"]),
        Define("workstation_capture_attempt", "pipeline", AllQuotes, "pipeline", critical: true, allowServer: true, dashboardMetrics: ["workstation_capture_attempt"]),
        Define("workstation_capture_success", "pipeline", AllQuotes, "pipeline", critical: true, allowServer: true, dashboardMetrics: ["workstation_capture_success"]),
        Define("workstation_capture_failure", "pipeline", AllQuotes, "pipeline", critical: true, allowServer: true, dashboardMetrics: ["workstation_capture_failure"]),
        Define(AppointmentAnalyticsEventCatalog.EmbedViewed, "appointment", AllQuotes, "appointment", allowBrowser: true, dashboardMetrics: ["appointment_embed_viewed"]),
        Define(AppointmentAnalyticsEventCatalog.SlotSelected, "appointment", AllQuotes, "appointment", allowBrowser: true, dashboardMetrics: ["appointment_slot_selected"]),
        Define(AppointmentAnalyticsEventCatalog.Booked, "appointment", AllQuotes, "appointment", critical: true, allowBrowser: true, allowServer: true, dashboardMetrics: ["appointment_booked"]),
        Define(AppointmentAnalyticsEventCatalog.Abandoned, "appointment", AllQuotes, "appointment", allowBrowser: true, dashboardMetrics: ["appointment_abandoned"]),
        Define(AppointmentAnalyticsEventCatalog.BookingFallbackClicked, "appointment", AllQuotes, "appointment", allowBrowser: true, dashboardMetrics: ["appointment_booking_fallback_clicked"]),
        Define(AppointmentAnalyticsEventCatalog.Completed, "appointment", AllQuotes, "appointment", critical: true, allowServer: true, dashboardMetrics: ["appointment_completed"]),
        Define(AppointmentAnalyticsEventCatalog.NoShow, "appointment", AllQuotes, "appointment", critical: true, allowServer: true, dashboardMetrics: ["appointment_no_show"]),
        Define("meta_browser_event_attempt", "meta", AllQuotes, "meta", critical: true, allowBrowser: true, allowServer: true, dashboardMetrics: ["meta_browser_event_attempt"]),
        Define("meta_browser_event_success", "meta", AllQuotes, "meta", critical: true, allowBrowser: true, allowServer: true, dashboardMetrics: ["meta_browser_event_success"]),
        Define("capi_event_attempt", "meta", AllQuotes, "meta", critical: true, allowServer: true, dashboardMetrics: ["capi_event_attempt"]),
        Define("capi_event_success", "meta", AllQuotes, "meta", critical: true, allowServer: true, dashboardMetrics: ["capi_event_success"]),
        Define("capi_event_failure", "meta", AllQuotes, "meta", critical: true, allowServer: true, dashboardMetrics: ["capi_event_failure"]),
        Define(ClientTrackingErrorEventName, "diagnostic", AllQuotes, "diagnostic", critical: true, allowBrowser: true, allowServer: true, dashboardMetrics: ["tracking_error", "tracking_health"]),

        Define("disability_quote_step1_view", "quote", ["disability"], "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["step_view"]),
        Define("disability_quote_contact_step_view", "quote", ["disability"], "contact", contactStep: true, critical: true, allowBrowser: true, dashboardMetrics: ["contact_step_view", "quote_contact_step_view"]),

        Define("life_general_form_start", "quote", LifeQuotes, "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("life_term_form_start", "quote", ["term"], "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("life_whole_form_start", "quote", ["wholelife"], "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("life_finalexpense_form_start", "quote", ["finalexpense"], "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("life_mp_form_start", "quote", ["mortgage"], "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("life_iul_form_start", "quote", ["iul"], "form", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_start", "funnel_start"]),
        Define("life_general_submit", "quote", LifeQuotes, "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_term_submit", "quote", ["term"], "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_whole_submit", "quote", ["wholelife"], "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_finalexpense_submit", "quote", ["finalexpense"], "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_mp_submit", "quote", ["mortgage"], "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_iul_submit", "quote", ["iul"], "submit", submitAttempt: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_step1_intro_view", "quote", LifeQuotes, "discovery", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["form_shell_view", "first_question_view"]),
        Define("first_question_view", "quote", AllQuotes, "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["first_question_view"]),
        Define("quote_step_view", "quote", AllQuotes, "discovery", allowBrowser: true, dashboardMetrics: ["step_view"]),
        Define("life_step1_goal_view", "quote", LifeQuotes, "discovery", meta: true, allowBrowser: true, dashboardMetrics: ["first_question_view", "step_view"]),
        Define("first_question_answered", "quote", AllQuotes, "discovery", funnelStart: true, critical: true, allowBrowser: true, dashboardMetrics: ["first_question_answered", "quote_step_complete"]),
        Define("life_step1_goal_select", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["first_question_answered", "quote_step_complete"]),
        Define("goal_completed", "quote", LifeQuotes, "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_step1_protecting_view", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, allowBrowser: true, dashboardMetrics: ["step_view"]),
        Define("life_step1_protecting_select", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("protecting_who_completed", "quote", LifeQuotes, "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_step1_coverage_view", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, allowBrowser: true, dashboardMetrics: ["step_view"]),
        Define("life_step1_coverage_select", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_step1_age_view", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, allowBrowser: true, dashboardMetrics: ["step_view"]),
        Define("step1_age_entered", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_step1_age_continue", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["discovery_complete", "quote_step_complete"]),
        Define("age_completed", "quote", LifeQuotes, "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_step1_tobacco_view", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, allowBrowser: true, dashboardMetrics: ["step_view"]),
        Define("life_step1_tobacco_select", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_step1_tobacco_continue", "quote", LifeQuotes, "discovery", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["discovery_complete", "quote_step_complete"]),
        Define("tobacco_completed", "quote", LifeQuotes, "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("tobaccouse_completed", "quote", LifeQuotes, "discovery", critical: true, allowBrowser: true, dashboardMetrics: ["quote_step_complete"]),
        Define("life_processing_bridge_view", "quote", LifeQuotes, "processing", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["processing_view"]),
        Define("processing_bridge_viewed", "quote", LifeQuotes, "processing", critical: true, allowBrowser: true, dashboardMetrics: ["processing_view"]),
        Define("life_processing_bridge_complete", "quote", LifeQuotes, "processing", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["processing_complete"]),
        Define("life_value_bridge_view", "quote", LifeQuotes, "processing", meta: true, allowBrowser: true, dashboardMetrics: ["processing_view"]),
        Define("life_value_bridge_continue", "quote", LifeQuotes, "processing", meta: true, allowBrowser: true, dashboardMetrics: ["processing_complete"]),
        Define("mini_results_view", "quote", LifeQuotes, "results", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["recommendation_viewed"]),
        Define("recommendation_generated", "quote", LifeQuotes, "results", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["recommendation_viewed"]),
        Define("estimate_results_viewed", "quote", LifeQuotes, "results", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["recommendation_viewed", "estimate_results_viewed"]),
        Define("recommendation_viewed", "quote", LifeQuotes, "results", critical: true, allowBrowser: true, dashboardMetrics: ["recommendation_viewed", "estimate_results_viewed"]),
        Define("estimate_inline_contact_view", "quote", LifeQuotes, "results", meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["estimate_inline_contact_view"]),
        Define("contact_step_view", "quote", AllQuotes, "contact", contactStep: true, critical: true, allowBrowser: true, dashboardMetrics: ["contact_step_view"]),
        Define("contact_step_viewed", "quote", AllQuotes, "contact", contactStep: true, critical: true, allowBrowser: true, dashboardMetrics: ["contact_step_view", "quote_contact_step_view"]),
        Define("estimate_contact_continue", "quote", LifeQuotes, "contact", contactStep: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["contact_step_view", "quote_contact_step_view"]),
        Define("estimate_results_error", "quote", LifeQuotes, "results", critical: true, allowBrowser: true, dashboardMetrics: ["estimate_results_error"]),
        Define("life_step2_view", "quote", LifeQuotes, "contact", contactStep: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["contact_step_view", "quote_contact_step_view"]),
        Define("life_step2_back", "quote", LifeQuotes, "contact", meta: true, allowBrowser: true, dashboardMetrics: ["backtrack"]),
        Define("life_step2_submit_attempt", "quote", LifeQuotes, "submit", submitAttempt: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_step2_submit_success", "quote", LifeQuotes, "submit", confirmedLead: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_success", "confirmed_lead"]),
        Define("results_contact_submit", "quote", LifeQuotes, "submit", confirmedLead: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_success", "confirmed_lead"]),
        Define("life_contact_first_view", "quote", LifeQuotes, "landing", funnelStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["life_contact_first_view", "landing_view"]),
        Define("life_contact_first_start", "quote", LifeQuotes, "discovery", funnelStart: true, formStart: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["life_contact_first_start", "funnel_start"]),
        Define("life_contact_first_submit_attempt", "quote", LifeQuotes, "submit", submitAttempt: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_attempt"]),
        Define("life_contact_first_submit_success", "quote", LifeQuotes, "submit", confirmedLead: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["submit_success", "confirmed_lead"]),
        Define("life_contact_first_complete", "quote", LifeQuotes, "submit", confirmedLead: true, meta: true, critical: true, allowBrowser: true, dashboardMetrics: ["life_contact_first_complete", "submit_success", "confirmed_lead"]),

        Define("rage_click", "diagnostic", AllQuotes, "diagnostic", meta: true, allowBrowser: true, dashboardMetrics: ["rage_click"]),
        Define("dead_click", "diagnostic", AllQuotes, "diagnostic", meta: true, allowBrowser: true, dashboardMetrics: ["dead_click"])
    ];

    private static readonly ReadOnlyDictionary<string, AnalyticsEventDefinition> DefinitionsByNameInternal =
        new(
            DefinitionsInternal.ToDictionary(
                definition => definition.Name,
                definition => definition,
                StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyCollection<string> BrowserAllowedEventNamesInternal =
        DefinitionsInternal
            .Where(definition => definition.AllowBrowser)
            .Select(definition => definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly IReadOnlyCollection<string> CriticalBrowserEventNamesInternal =
        DefinitionsInternal
            .Where(definition => definition.AllowBrowser && definition.IsCritical)
            .Select(definition => definition.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<AnalyticsEventDefinition> Definitions => DefinitionsInternal;
    public static IReadOnlyDictionary<string, AnalyticsEventDefinition> DefinitionsByName => DefinitionsByNameInternal;
    public static IReadOnlyCollection<string> BrowserAllowedEventNames => BrowserAllowedEventNamesInternal;
    public static IReadOnlyCollection<string> CriticalBrowserEventNames => CriticalBrowserEventNamesInternal;

    public static bool IsKnown(string? eventName) => TryGet(eventName, out _);

    public static bool TryGet(string? eventName, out AnalyticsEventDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(eventName) &&
            DefinitionsByNameInternal.TryGetValue(eventName.Trim(), out definition!))
        {
            return true;
        }

        definition = null!;
        return false;
    }

    public static bool IsBrowserAllowed(string? eventName) =>
        TryGet(eventName, out var definition) && definition.AllowBrowser;

    public static bool IsServerAllowed(string? eventName) =>
        TryGet(eventName, out var definition) && definition.AllowServer;

    public static bool MatchesDashboardMetric(string? eventName, string metricKey)
    {
        if (string.IsNullOrWhiteSpace(metricKey) || !TryGet(eventName, out var definition))
        {
            return false;
        }

        return definition.DashboardMetrics.Contains(metricKey.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static AnalyticsEventDefinition Define(
        string name,
        string category,
        IReadOnlyList<string> quoteTypeApplicability,
        string funnelStage,
        bool landing = false,
        bool cta = false,
        bool funnelStart = false,
        bool formStart = false,
        bool contactStep = false,
        bool submitAttempt = false,
        bool confirmedLead = false,
        bool meta = false,
        bool critical = false,
        bool allowBrowser = false,
        bool allowServer = false,
        IReadOnlyList<string>? dashboardMetrics = null)
    {
        return new AnalyticsEventDefinition(
            Name: name,
            Category: category,
            QuoteTypeApplicability: quoteTypeApplicability,
            FunnelStage: funnelStage,
            CountsAsLandingView: landing,
            CountsAsCtaClick: cta,
            CountsAsFunnelStart: funnelStart,
            CountsAsFormStart: formStart,
            CountsAsContactStep: contactStep,
            CountsAsSubmitAttempt: submitAttempt,
            CountsAsConfirmedLead: confirmedLead,
            EligibleForMetaSignal: meta,
            IsCritical: critical,
            AllowBrowser: allowBrowser,
            AllowServer: allowServer,
            DashboardMetrics: dashboardMetrics ?? Array.Empty<string>());
    }
}

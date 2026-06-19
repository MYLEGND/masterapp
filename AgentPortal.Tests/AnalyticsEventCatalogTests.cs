using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsEventCatalogTests
{
    private static readonly HashSet<string> NonAnalyticsEventLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        "life_estimate_engine_unavailable",
        "life_funnel_start",
        "life_see_estimate",
        "contact_viewed",
        "discovery_steps_completed",
        "funnel_start",
        "form_first_focus",
        "form_started",
        "lead_confirmed",
        "page_load",
        "processing_viewed",
        "primary_cta_click",
        "meta_tracking_initialized",
        "meta_capi_result",
        "successful_event",
        "visibility_hidden",
        "visibility_visible",
        "contact_first_education",
        "contact_first_education_v1",
        "low_friction_options_v1",
        "quote_auto",
        "quote_commercial",
        "quote_disability",
        "quote_health",
        "quote_home",
        "quote_life",
        "quote_index_auto_start",
        "quote_index_commercial_start",
        "quote_index_disability_start",
        "quote_index_health_start",
        "quote_index_home_start",
        "quote_index_life_start"
    };

    [Fact]
    public void AnalyticsEventCatalog_CriticalLifeEvents_AreBrowserAllowed()
    {
        var requiredEvents = new[]
        {
            "primary_cta_seen",
            "quote_entry_engaged",
            "first_question_view",
            "first_question_answered",
            "contact_step_view",
            "life_step1_coverage_select",
            "life_contact_first_view",
            "life_contact_first_start",
            "life_contact_first_complete",
            "estimate_results_viewed",
            "estimate_contact_continue",
            "estimate_inline_contact_view",
            "lead_form_start",
            "lead_form_submit_attempt",
            "form_submit_attempt",
            "lead_form_submit_success",
            "lead_form_submit_failure",
            "form_abandon",
            "quote_cta_click",
            "quote_landing_view",
            "quote_step_complete",
            "quote_contact_step_view",
            AnalyticsEventCatalog.ClientTrackingErrorEventName
        };

        foreach (var eventName in requiredEvents)
        {
            Assert.True(AnalyticsEventCatalog.TryGet(eventName, out var definition), $"Catalog missing required event '{eventName}'.");
            Assert.True(definition.AllowBrowser, $"Required event '{eventName}' must be browser-allowed.");
        }
    }

    [Fact]
    public void AnalyticsEventCatalog_QuoteEntryEngaged_UsesUniversalQuoteContract()
    {
        Assert.True(AnalyticsEventCatalog.TryGet("quote_entry_engaged", out var definition));
        Assert.Equal("quote", definition.Category);
        Assert.Equal("discovery", definition.FunnelStage);
        Assert.True(definition.CountsAsFunnelStart);
        Assert.True(definition.IsCritical);
        Assert.True(definition.AllowBrowser);
        Assert.Contains("all", definition.QuoteTypeApplicability, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("quote_entry_engaged", definition.DashboardMetrics, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("funnel_start", definition.DashboardMetrics, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyticsEventCatalog_QuoteFunnelAliasEvents_AreBrowserAllowed()
    {
        var aliasEvents = new[]
        {
            "cta_clicked",
            "funnel_started",
            "protecting_who_completed",
            "goal_completed",
            "tobacco_completed",
            "tobaccouse_completed",
            "age_completed",
            "processing_bridge_viewed",
            "recommendation_viewed",
            "contact_step_viewed",
            "form_submit_success"
        };

        foreach (var eventName in aliasEvents)
        {
            Assert.True(AnalyticsEventCatalog.TryGet(eventName, out var definition), $"Catalog missing alias event '{eventName}'.");
            Assert.True(definition.AllowBrowser, $"Alias event '{eventName}' must remain browser-allowed.");
        }
    }

    [Fact]
    public void AnalyticsEventCatalog_LifeLeadPipelineEvents_UseExpectedTransportAllowance()
    {
        var browserEvents = new[]
        {
            "life_contact_first_complete",
            "lead_form_submit_success",
            "form_abandon"
        };

        var serverEvents = new[]
        {
            "lead_persisted",
            "workstation_capture_attempt",
            "workstation_capture_success",
            "workstation_capture_failure"
        };

        foreach (var eventName in browserEvents)
        {
            Assert.True(AnalyticsEventCatalog.TryGet(eventName, out var definition), $"Catalog missing browser event '{eventName}'.");
            Assert.True(definition.AllowBrowser, $"Expected '{eventName}' to remain browser-allowed.");
        }

        foreach (var eventName in serverEvents)
        {
            Assert.True(AnalyticsEventCatalog.TryGet(eventName, out var definition), $"Catalog missing server event '{eventName}'.");
            Assert.True(definition.AllowServer, $"Expected '{eventName}' to remain server-allowed.");
        }
    }

    [Fact]
    public void AnalyticsEventCatalog_PublicBookingEvents_AreKnownAndBrowserAllowed()
    {
        var browserEvents = new[]
        {
            AppointmentAnalyticsEventCatalog.EmbedViewed,
            AppointmentAnalyticsEventCatalog.SlotSelected,
            AppointmentAnalyticsEventCatalog.Abandoned,
            AppointmentAnalyticsEventCatalog.BookingFallbackClicked
        };

        foreach (var eventName in browserEvents)
        {
            Assert.True(AnalyticsEventCatalog.TryGet(eventName, out var definition), $"Catalog missing appointment event '{eventName}'.");
            Assert.True(definition.AllowBrowser, $"Appointment event '{eventName}' must be browser-allowed.");
        }

        Assert.True(AnalyticsEventCatalog.TryGet(AppointmentAnalyticsEventCatalog.Booked, out var bookedDefinition));
        Assert.True(bookedDefinition.AllowBrowser);
        Assert.True(bookedDefinition.AllowServer);
    }

    [Fact]
    public void AnalyticsEventCatalog_SourceFiles_OnlyReferenceKnownAnalyticsEvents()
    {
        var files = new[]
        {
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "Life.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "Auto.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "Home.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "Commercial.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "Disability.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "DentalVisionHearing.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "wwwroot", "js", "tracking.js"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "AutoQuoteController.cs"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "HomeQuoteController.cs"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "DentalVisionHearingQuoteController.cs"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "DisabilityQuoteController.cs"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "CommercialQuoteController.cs"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "LifeQuoteController.cs"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "Controllers", "ThankYouController.cs"),
            Path.Combine(GetRepoRoot(), "AgentPortal", "Services", "Analytics", "AnalyticsQueryService.cs")
        };

        var unknownEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regex = new Regex("['\"]([a-z]+(?:_[a-z0-9]+)+)['\"]", RegexOptions.Compiled);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            foreach (Match match in regex.Matches(content))
            {
                var value = match.Groups[1].Value;
                if (!LooksLikeAnalyticsEvent(value))
                {
                    continue;
                }

                if (!AnalyticsEventCatalog.IsKnown(value))
                {
                    unknownEvents.Add(value);
                }
            }
        }

        Assert.True(unknownEvents.Count == 0, $"Unknown analytics events referenced in source: {string.Join(", ", unknownEvents.OrderBy(x => x))}");
    }

    [Fact]
    public void MetaSignalEventCatalog_CoversBrowserPixelAndServerForwardEvents()
    {
        var requiredBrowserEvents = new[]
        {
            "ViewContent",
            "LeadFormStart",
            "DiscoveryComplete",
            "RecommendationViewed",
            "ContactStepReached",
            "HighIntentLeadSignal",
            "LeadReadySignal",
            "AbandonedHighIntentLead"
        };

        foreach (var eventName in requiredBrowserEvents)
        {
            Assert.True(MetaSignalEventCatalog.TryGet(eventName, out var definition), $"Meta signal catalog missing '{eventName}'.");
            Assert.True(definition.AllowBrowserPixel, $"Meta signal browser event '{eventName}' must remain browser-enabled.");
            Assert.False(definition.AllowServerForward, $"Meta signal browser event '{eventName}' must not be server-forwarded.");
            Assert.True(MetaSignalEventCatalog.IsBrowserSignalEvent(eventName), $"Meta signal browser event '{eventName}' must remain browser-classified.");
        }

        Assert.True(MetaSignalEventCatalog.TryGet("QualifiedLead", out var qualifiedLead));
        Assert.True(qualifiedLead.AllowServerForward);
        Assert.True(MetaSignalEventCatalog.IsServerAuthorityEvent("QualifiedLead"));
        Assert.True(MetaSignalEventCatalog.IsServerAuthorityEvent("PolicyPaid"));
    }

    [Fact]
    public void WebsiteLeadSignalClassifier_UsesUnifiedLeadReadinessRules()
    {
        var leadReady = WebsiteLeadSignalClassifier.Evaluate(
            new WebsiteLeadSignalInput(
                FunnelStartObserved: true,
                ContactStepReached: true,
                ContactInputStarted: true,
                RequiredContactFieldsCompleted: true,
                SubmitAttempted: false,
                ConfirmedWebsiteLead: false,
                Phone: "555-111-2222",
                Email: "",
                TotalSignalScore: 72));

        Assert.True(leadReady.LeadFormStarted);
        Assert.True(leadReady.ContactIntentCaptured);
        Assert.True(leadReady.LeadReady);
        Assert.False(leadReady.QualifiedLead);

        var qualifiedLead = WebsiteLeadSignalClassifier.Evaluate(
            new WebsiteLeadSignalInput(
                FunnelStartObserved: true,
                ContactStepReached: true,
                ContactInputStarted: true,
                RequiredContactFieldsCompleted: true,
                SubmitAttempted: true,
                ConfirmedWebsiteLead: true,
                Phone: "555-111-2222",
                Email: "",
                TotalSignalScore: 84));

        Assert.True(qualifiedLead.QualifiedLead);
        Assert.True(qualifiedLead.MetaOptimizationLead);
        Assert.True(qualifiedLead.ConfirmedWebsiteLead);
    }

    private static bool LooksLikeAnalyticsEvent(string value)
    {
        if (NonAnalyticsEventLiterals.Contains(value))
        {
            return false;
        }

        if (value is "cta_click" or "cta_clicked" or "quote_click" or "outbound_click" or "risk_assessment_click" or "page_view" or "thank_you_view")
        {
            return true;
        }

        if (value.StartsWith("funnel_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_start", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_started", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("primary_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_seen", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_click", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("first_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_view", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_answered", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("contact_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_view", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_viewed", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("quote_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_view", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_click", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_complete", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("life_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_view", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_start", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_submit", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_select", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_continue", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_complete", StringComparison.OrdinalIgnoreCase) ||
                   value.EndsWith("_back", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("appointment_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith("meta_", StringComparison.OrdinalIgnoreCase))
        {
            return value.StartsWith("meta_browser_event_", StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith("disability_", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("_view", StringComparison.OrdinalIgnoreCase);
        }

        return
               value.EndsWith("_completed", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("_viewed", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("estimate_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("form_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("lead_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("capi_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("workstation_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("page_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("scroll_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("results_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("client_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("carrier_", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRepoRoot([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}

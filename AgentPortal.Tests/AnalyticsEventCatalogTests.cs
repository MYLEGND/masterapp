using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class AnalyticsEventCatalogTests
{
    [Fact]
    public void AnalyticsEventCatalog_CriticalLifeEvents_AreBrowserAllowed()
    {
        var requiredEvents = new[]
        {
            "life_step1_coverage_select",
            "life_contact_first_view",
            "life_contact_first_start",
            "life_contact_first_complete",
            "estimate_results_viewed",
            "estimate_contact_continue",
            "estimate_inline_contact_view",
            "lead_form_start",
            "form_submit_attempt",
            "lead_form_submit_success",
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
    public void AnalyticsEventCatalog_SourceFiles_OnlyReferenceKnownAnalyticsEvents()
    {
        var files = new[]
        {
            Path.Combine(GetRepoRoot(), "Protect-Website", "Views", "Quote", "Life.cshtml"),
            Path.Combine(GetRepoRoot(), "Protect-Website", "wwwroot", "js", "tracking.js"),
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
        }

        Assert.True(MetaSignalEventCatalog.TryGet("QualifiedLead", out var qualifiedLead));
        Assert.True(qualifiedLead.AllowServerForward);
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
        if (value is "cta_click" or "quote_click" or "outbound_click" or "risk_assessment_click" or "page_view" or "thank_you_view")
        {
            return true;
        }

        return value.StartsWith("quote_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("life_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("estimate_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("form_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("lead_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("page_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("scroll_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("results_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("client_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("carrier_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("disability_", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "MASTERAPP.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

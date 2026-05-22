using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using Xunit;

namespace AgentPortal.Tests;

public class QuoteProductInstrumentationContractTests
{
    [Fact]
    public void NonLifeQuoteViews_ExposeGenericQuoteStageInstrumentation()
    {
        var repoRoot = GetRepoRoot();

        var autoView = Read(repoRoot, "Protect-Website", "Views", "Quote", "Auto.cshtml");
        var homeView = Read(repoRoot, "Protect-Website", "Views", "Quote", "Home.cshtml");
        var healthView = Read(repoRoot, "Protect-Website", "Views", "Quote", "Health.cshtml");
        var disabilityView = Read(repoRoot, "Protect-Website", "Views", "Quote", "Disability.cshtml");
        var commercialView = Read(repoRoot, "Protect-Website", "Views", "Quote", "Commercial.cshtml");

        Assert.Contains("'quote_step_complete'", autoView, StringComparison.Ordinal);
        Assert.Contains("'quote_contact_step_view'", autoView, StringComparison.Ordinal);

        Assert.Contains("'quote_step_complete'", homeView, StringComparison.Ordinal);
        Assert.Contains("'quote_contact_step_view'", homeView, StringComparison.Ordinal);

        Assert.Contains("'quote_contact_step_view'", healthView, StringComparison.Ordinal);

        Assert.Contains("'quote_step_complete'", disabilityView, StringComparison.Ordinal);
        Assert.Contains("'quote_contact_step_view'", disabilityView, StringComparison.Ordinal);

        Assert.Contains("'quote_step_complete'", commercialView, StringComparison.Ordinal);
        Assert.Contains("'quote_contact_step_view'", commercialView, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteControllers_EmitLeadPipelineAndMetaTelemetry()
    {
        var repoRoot = GetRepoRoot();
        var controllers = new[]
        {
            Read(repoRoot, "Protect-Website", "Controllers", "LifeQuoteController.cs"),
            Read(repoRoot, "Protect-Website", "Controllers", "AutoQuoteController.cs"),
            Read(repoRoot, "Protect-Website", "Controllers", "HomeQuoteController.cs"),
            Read(repoRoot, "Protect-Website", "Controllers", "HealthQuoteController.cs"),
            Read(repoRoot, "Protect-Website", "Controllers", "DisabilityQuoteController.cs"),
            Read(repoRoot, "Protect-Website", "Controllers", "CommercialQuoteController.cs")
        };

        foreach (var controller in controllers)
        {
            Assert.Contains("\"lead_persisted\"", controller, StringComparison.Ordinal);
            Assert.Contains("\"workstation_capture_attempt\"", controller, StringComparison.Ordinal);
            Assert.Contains("\"workstation_capture_success\"", controller, StringComparison.Ordinal);
            Assert.Contains("\"workstation_capture_failure\"", controller, StringComparison.Ordinal);
            Assert.Contains("\"capi_event_attempt\"", controller, StringComparison.Ordinal);
            Assert.Contains("capi_event_success", controller, StringComparison.Ordinal);
            Assert.Contains("capi_event_failure", controller, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LifeQuote_SourceFilesRequireExplicitContactConsentForHashEligibleLeadFlow()
    {
        var repoRoot = GetRepoRoot();
        var view = Read(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml");
        var controller = Read(repoRoot, "Protect-Website", "Controllers", "LifeQuoteController.cs");

        Assert.Contains("name=\"MarketingEmailConsent\" value=\"false\"", view, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\" id=\"MarketingEmailConsent\" name=\"MarketingEmailConsent\" value=\"true\"", view, StringComparison.Ordinal);
        Assert.Contains("Please check the box so we can send your estimate and options.", view, StringComparison.Ordinal);
        Assert.Contains("if (!model.MarketingEmailConsent)", controller, StringComparison.Ordinal);
        Assert.Contains("ModelState.AddModelError(nameof(LifeQuoteFormModel.MarketingEmailConsent)", controller, StringComparison.Ordinal);
        Assert.Contains("AllowHashedContactData = lead.TermsAccepted && lead.MarketingEmailConsent", controller, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(model.Email) && string.IsNullOrWhiteSpace(model.Phone)", controller, StringComparison.Ordinal);
        Assert.Contains("id=\"err-ContactMethod\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void LifeQuote_UsesAjaxManagedValidationAndNonBlockingTrackingGuards()
    {
        var repoRoot = GetRepoRoot();
        var view = Read(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml");
        var controller = Read(repoRoot, "Protect-Website", "Controllers", "LifeQuoteController.cs");
        var tracking = Read(repoRoot, "Protect-Website", "wwwroot", "js", "tracking.js");

        Assert.Contains("data-ajax-submit=\"true\" novalidate", view, StringComparison.Ordinal);
        Assert.Contains("window.legendFormTracking?.trackStart?.(form.dataset.formKey || pageKey)", view, StringComparison.Ordinal);
        Assert.Contains("const metaSignalApi = metaSignal && typeof metaSignal === 'object'", view, StringComparison.Ordinal);
        Assert.Contains("function safeTrackingCall(label, factory)", view, StringComparison.Ordinal);

        Assert.Contains("const isAjaxManagedSubmit = formEl.dataset.ajaxSubmit === 'true';", tracking, StringComparison.Ordinal);
        Assert.Contains("if (isAjaxManagedSubmit) return;", tracking, StringComparison.Ordinal);
        Assert.Contains("trackStart: fireTrackedFormStartOnce", tracking, StringComparison.Ordinal);

        Assert.Contains("model.FirstName = model.FirstName?.Trim() ?? string.Empty;", controller, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(model.FirstName)", controller, StringComparison.Ordinal);
        Assert.Contains("ModelState.AddModelError(nameof(LifeQuoteFormModel.FirstName), \"First name is required.\")", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralLifeLowFrictionVariant_ExposesConversionFocusedHeroAndExplicitFunnelEvents()
    {
        var repoRoot = GetRepoRoot();
        var view = Read(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml");
        var controller = Read(repoRoot, "Protect-Website", "Controllers", "LifeQuoteController.cs");

        Assert.Contains("low_friction_options_v1", controller, StringComparison.Ordinal);
        Assert.Contains("data-life-funnel-start=\"true\"", view, StringComparison.Ordinal);
        Assert.Contains("primary_cta_seen", view, StringComparison.Ordinal);
        Assert.Contains("first_question_view", view, StringComparison.Ordinal);
        Assert.Contains("first_question_answered", view, StringComparison.Ordinal);
        Assert.Contains("contact_step_view", view, StringComparison.Ordinal);
        Assert.Contains("lead_form_submit_attempt", view, StringComparison.Ordinal);
        Assert.Contains("lead_form_submit_success", view, StringComparison.Ordinal);
        Assert.Contains("lead_form_submit_failure", view, StringComparison.Ordinal);
        Assert.Contains("What you’ll get first", view, StringComparison.Ordinal);
    }

    [Fact]
    public void LifeFinalContactStep_UsesSimplifiedEstimateReviewPresentationAcrossOffers()
    {
        var repoRoot = GetRepoRoot();
        var view = Read(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml");
        var renderer = Read(repoRoot, "Protect-Website", "wwwroot", "js", "life-estimate-engine.js");

        Assert.Contains("showCarrierReviewBeforeContact = false;", view, StringComparison.Ordinal);
        Assert.Contains("showPreContactEducationSummary = false;", view, StringComparison.Ordinal);
        Assert.Contains("Where should we send your estimate?", view, StringComparison.Ordinal);
        Assert.Contains("Email is best for a written copy. Add a phone only if you want text or call help too.", view, StringComparison.Ordinal);
        Assert.Contains("Add the best contact method to keep a written copy of this estimate and the clearest next options. Help stays optional.", view, StringComparison.Ordinal);
        Assert.Contains("Send My Estimate", view, StringComparison.Ordinal);

        Assert.Contains("buildGeneralLifeContactSummaryHtml", renderer, StringComparison.Ordinal);
        Assert.Contains("return buildGeneralLifeContactSummaryHtml(normalized);", renderer, StringComparison.Ordinal);
        Assert.Contains("is-general-life-lite", renderer, StringComparison.Ordinal);
        Assert.Contains("Estimated monthly range", renderer, StringComparison.Ordinal);
        Assert.Contains("Recommended coverage range", renderer, StringComparison.Ordinal);
        Assert.Contains("Estimates are illustrative only and not a final quote.", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void LifeQuote_PostSubmitBookingExperience_StaysInlineAndUsesRequestedOnlyAppointmentFlow()
    {
        var repoRoot = GetRepoRoot();
        var view = Read(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml");
        var controller = Read(repoRoot, "Protect-Website", "Controllers", "LifeQuoteController.cs");

        Assert.Contains("id=\"lifePostSubmitSlot\" hidden", view, StringComparison.Ordinal);
        Assert.Contains("id=\"lifeBookingModal\"", view, StringComparison.Ordinal);
        Assert.Contains("showPostSubmitSuccess(", view, StringComparison.Ordinal);
        Assert.Contains("bookingExperienceUrl", view, StringComparison.Ordinal);
        Assert.Contains("'appointment_embed_viewed'", view, StringComparison.Ordinal);
        Assert.Contains("'appointment_abandoned'", view, StringComparison.Ordinal);
        Assert.Contains("'appointment_booking_fallback_clicked'", view, StringComparison.Ordinal);
        Assert.DoesNotContain("window.location.href = resolveThankYouPath();", view, StringComparison.Ordinal);

        Assert.Contains("[HttpPost(\"Life/booking-experience\")]", controller, StringComparison.Ordinal);
        Assert.Contains("UpsertRequestedPublicAppointmentAsync", controller, StringComparison.Ordinal);
        Assert.Contains("LeadAppointmentStatus.Requested", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackingAssets_UseCacheBustingAndLiveAnalyticsConfigInjection()
    {
        var repoRoot = GetRepoRoot();
        var layout = Read(repoRoot, "Protect-Website", "Views", "Shared", "_Layout.cshtml");
        var lifeView = Read(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml");

        Assert.Contains("window.LEGEND_ANALYTICS_CONFIG =", layout, StringComparison.Ordinal);
        Assert.Contains("<script src=\"~/js/tracking.js\" asp-append-version=\"true\"></script>", layout, StringComparison.Ordinal);
        Assert.Contains("<script src=\"~/js/lead-modal.js\" asp-append-version=\"true\"></script>", layout, StringComparison.Ordinal);
        Assert.Contains("<script src=\"~/js/meta-signal-intelligence.js\" asp-append-version=\"true\"></script>", lifeView, StringComparison.Ordinal);
        Assert.Contains("<script src=\"~/js/life-estimate-engine.js\" asp-append-version=\"true\"></script>", lifeView, StringComparison.Ordinal);
        Assert.Contains("<script src=\"~/js/trusted-carrier-strip.js\" asp-append-version=\"true\"></script>", lifeView, StringComparison.Ordinal);
    }

    private static string Read(string repoRoot, params string[] parts)
    {
        return File.ReadAllText(Path.Combine(parts.Prepend(repoRoot).ToArray()));
    }

    private static string GetRepoRoot([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}

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
        Assert.Contains("Please authorize us to contact you about this request.", view, StringComparison.Ordinal);
        Assert.Contains("if (!model.MarketingEmailConsent)", controller, StringComparison.Ordinal);
        Assert.Contains("ModelState.AddModelError(nameof(LifeQuoteFormModel.MarketingEmailConsent)", controller, StringComparison.Ordinal);
        Assert.Contains("AllowHashedContactData = lead.TermsAccepted && lead.MarketingEmailConsent", controller, StringComparison.Ordinal);
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

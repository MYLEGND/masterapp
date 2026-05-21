using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public class ThankYouMetaFallbackContractTests
{
    [Fact]
    public void ThankYouAndLifeControllers_PreserveQuoteContextAndMetaFallbackState()
    {
        var repoRoot = GetRepoRoot();
        var thankYouController = File.ReadAllText(Path.Combine(repoRoot, "Protect-Website", "Controllers", "ThankYouController.cs"));
        var lifeQuoteController = File.ReadAllText(Path.Combine(repoRoot, "Protect-Website", "Controllers", "LifeQuoteController.cs"));
        var thankYouView = File.ReadAllText(Path.Combine(repoRoot, "Protect-Website", "Views", "Quote", "ThankYou.cshtml"));
        var lifeView = File.ReadAllText(Path.Combine(repoRoot, "Protect-Website", "Views", "Quote", "Life.cshtml"));

        Assert.Contains("TempData[\"MetaLeadEventId\"] = metaLeadEventId;", lifeQuoteController, StringComparison.Ordinal);
        Assert.Contains("TempData[\"MetaLeadLeadId\"] = lead.LeadId.ToString(\"D\");", lifeQuoteController, StringComparison.Ordinal);

        Assert.Contains("ViewData[\"PageKey\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"PageVariant\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"PageMode\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"PageCategory\"] = \"quote\";", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"QuoteTypeForTracking\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"LeadId\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"MetaLeadEventId\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"MetaLeadLeadId\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"Source\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"Campaign\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"Fbclid\"]", thankYouController, StringComparison.Ordinal);
        Assert.Contains("ViewData[\"SessionId\"]", thankYouController, StringComparison.Ordinal);

        Assert.Contains("const browserAckUrl = '/ThankYou/meta-browser-ack';", thankYouView, StringComparison.Ordinal);
        Assert.Contains("const metaBrowserAckUrl = '/Quote/Life/meta-browser-ack';", lifeView, StringComparison.Ordinal);
        Assert.Contains("window.fbq('track', 'Lead'", lifeView, StringComparison.Ordinal);
        Assert.Contains("eventID: metaLeadEventId", lifeView, StringComparison.Ordinal);
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

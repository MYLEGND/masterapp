using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingSetupCentralizationTests
{
    [Fact]
    public void AgentProfile_NoLongerOwnsMarketingOrBookingConfigurationUi()
    {
        var view = Read("AgentPortal", "Views", "Account", "ManageProfile.cshtml");
        var controller = Read("AgentPortal", "Controllers", "AccountController.cs");

        Assert.DoesNotContain("asp-for=\"MetaPixelId\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-for=\"BookingEnabled\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("MicrosoftBookingsEmbedUrl", view, StringComparison.Ordinal);
        Assert.DoesNotContain("FallbackBookingUrl", view, StringComparison.Ordinal);
        Assert.DoesNotContain("BookingPageIdOrMailbox", view, StringComparison.Ordinal);
        Assert.DoesNotContain("CalendarEmail", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Meta CAPI:", view, StringComparison.Ordinal);
        Assert.DoesNotContain("_marketing.SavePixelAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("profile.BookingEnabled = vm.BookingEnabled", controller, StringComparison.Ordinal);
        Assert.Contains("Website Analytics > Marketing Setup owns", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void WebsiteAnalytics_OwnsOneScopedMarketingSetupSurface()
    {
        var controller = Read("AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var view = Read("AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read("AgentPortal", "wwwroot", "js", "website-analytics.js");
        var css = Read("AgentPortal", "wwwroot", "css", "website-analytics.css");

        Assert.Contains("[HttpGet(\"marketing-setup\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"marketing-setup\")]", controller, StringComparison.Ordinal);
        Assert.Contains("AgentMarketingProfileService", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveMarketingSetupTrackingAsync", controller, StringComparison.Ordinal);
        Assert.Contains("profile.BookingEnabled = request.BookingEnabled", controller, StringComparison.Ordinal);
        Assert.Contains("metaCapiManagedAutomatically = true", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("replacementCapiToken", controller, StringComparison.OrdinalIgnoreCase);

        var titleIndex = view.IndexOf("Marketing Links", StringComparison.Ordinal);
        var setupIndex = view.IndexOf("Marketing Setup", titleIndex, StringComparison.Ordinal);
        Assert.True(titleIndex >= 0 && setupIndex > titleIndex);
        Assert.Contains("data-bs-target=\"#marketingSetupModal\"", view, StringComparison.Ordinal);
        Assert.Contains("modal-dialog-centered", view, StringComparison.Ordinal);
        Assert.Contains("Meta CAPI credentials are never entered here", view, StringComparison.Ordinal);

        Assert.Contains("marketingSetup: analyticsEndpoint('/marketing-setup')", js, StringComparison.Ordinal);
        Assert.Contains("state.agentProfileId || callerProfileId", js, StringComparison.Ordinal);
        Assert.Contains("marketingSetupConnectUrl", js, StringComparison.Ordinal);
        Assert.DoesNotContain("capiToken", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".marketing-setup-trigger", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualCapiEntry_IsRemovedFromBusinessWebsiteManagement_AndOauthRemainsAuthority()
    {
        var management = Read("Legend-Design", "legend-website-management.js");
        var businessProfile = Read("Infrastructure", "WebsiteEditing", "BusinessWebsiteProfileService.cs");
        var connectionStore = Read("Infrastructure", "Analytics", "MarketingConnectionStore.cs");

        Assert.DoesNotContain("Replace secure CAPI token", management, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replacementCapiToken", management, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReplacementCapiToken", businessProfile, StringComparison.Ordinal);
        Assert.Contains("replacementCapiToken: null", businessProfile, StringComparison.Ordinal);
        Assert.Contains("row.CapiAccessTokenCiphertext ?? row.AdsAccessTokenCiphertext", connectionStore, StringComparison.Ordinal);
    }

    [Fact]
    public void BookingRuntime_StillConsumesTheSameCanonicalAgentProfileFields()
    {
        var resolver = Read("Infrastructure", "Bookings", "PublicBookingResolver.cs");
        var controller = Read("AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");

        foreach (var field in new[]
                 {
                     "BookingEnabled",
                     "MicrosoftBookingsEmbedUrl",
                     "FallbackBookingUrl",
                     "BookingPageIdOrMailbox",
                     "CalendarEmail"
                 })
        {
            Assert.Contains(field, resolver, StringComparison.Ordinal);
            Assert.Contains(field, controller, StringComparison.Ordinal);
        }
    }

    private static string Read(params string[] parts)
    {
        var root = FindRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(githubWorkspace) &&
            File.Exists(Path.Combine(githubWorkspace, "MASTERAPP.sln")))
            return Path.GetFullPath(githubWorkspace);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MASTERAPP.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}

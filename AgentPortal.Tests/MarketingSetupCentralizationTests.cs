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
        Assert.Contains("MarketingConnections.GetAdsAsync(owner", controller, StringComparison.Ordinal);
        Assert.Contains("var adsConnected = setup.Meta.Connected;", controller, StringComparison.Ordinal);
        Assert.Contains("var secureCapi = setup.Meta.CapiConfigured;", controller, StringComparison.Ordinal);
        Assert.Contains("profile.BookingEnabled = request.BookingEnabled", controller, StringComparison.Ordinal);
        Assert.Contains("metaCapiManagedAutomatically = true", controller, StringComparison.Ordinal);
        Assert.Contains("MetaTestEventCode", controller, StringComparison.Ordinal);
        Assert.Contains("metaTestEventCode = marketing.TestEventCode", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("replacementCapiToken", controller, StringComparison.OrdinalIgnoreCase);

        var titleIndex = view.IndexOf("Marketing Links", StringComparison.Ordinal);
        var setupIndex = view.IndexOf("Marketing Setup", titleIndex, StringComparison.Ordinal);
        Assert.True(titleIndex >= 0 && setupIndex > titleIndex);
        Assert.Contains("data-bs-target=\"#marketingSetupModal\"", view, StringComparison.Ordinal);
        Assert.Contains("modal-dialog-centered", view, StringComparison.Ordinal);
        Assert.Contains("Meta CAPI credentials are never entered here", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-test-code\"", view, StringComparison.Ordinal);
        Assert.Contains("Meta Events Manager", view, StringComparison.Ordinal);

        Assert.Contains("marketingSetup: analyticsEndpoint('/marketing-setup')", js, StringComparison.Ordinal);
        Assert.Contains("state.scope.agentProfileId || callerProfileId", js, StringComparison.Ordinal);
        Assert.Contains("marketingSetupConnectUrl", js, StringComparison.Ordinal);
        var fetchHelperIndex = js.IndexOf("async function fetchJson", StringComparison.Ordinal);
        var marketingSetupIndex = js.IndexOf("// Centralized marketing + booking configuration.", StringComparison.Ordinal);
        var firstModuleCloseAfterSetup = js.IndexOf("})();", marketingSetupIndex, StringComparison.Ordinal);
        var deviceModuleIndex = js.IndexOf("async function loadDeviceIntelligence", marketingSetupIndex, StringComparison.Ordinal);
        Assert.True(fetchHelperIndex >= 0 && marketingSetupIndex > fetchHelperIndex);
        Assert.True(firstModuleCloseAfterSetup > marketingSetupIndex);
        Assert.True(deviceModuleIndex > marketingSetupIndex && deviceModuleIndex < firstModuleCloseAfterSetup);
        Assert.Contains("let marketingSetupLoaded = false;", js, StringComparison.Ordinal);
        Assert.Contains("marketingSetupSave.disabled = !marketingSetupLoaded", js, StringComparison.Ordinal);
        Assert.Contains("marketing.metaTestEventCode", js, StringComparison.Ordinal);
        Assert.Contains("metaTestEventCode: testEventCode || null", js, StringComparison.Ordinal);
        Assert.DoesNotContain("Reload Marketing Setup before saving.", js, StringComparison.Ordinal);
        Assert.DoesNotContain("capiToken", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".marketing-setup-trigger", css, StringComparison.Ordinal);

        // ChatGPT Ads management is a projection of the Step 1-4 authorities,
        // never a second settings/credential model.
        Assert.Contains("IOpenAiAdsAccountConnectionAuthority", controller, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderSetupProjection", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveMarketingOwnerAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadOpprefLineageVisibilityAsync", controller, StringComparison.Ordinal);
        Assert.Contains("var evidence = setup.Evidence", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"openai-disconnect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("authority.DisconnectAsync", controller, StringComparison.Ordinal);
        Assert.Contains("openAiConnection.Permissions", controller, StringComparison.Ordinal);
        Assert.Contains("openAiHealth.PendingDeliveries", controller, StringComparison.Ordinal);
        Assert.Contains("openAiHealth.RetryableDeliveries", controller, StringComparison.Ordinal);
        Assert.Contains("openAiHealth.FailedDeliveries", controller, StringComparison.Ordinal);
        Assert.Contains("openAiHealth.SentDeliveries", controller, StringComparison.Ordinal);
        Assert.Contains("evidenceError = setup.EvidenceError", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ConversionsApiKey =", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagementApiKey =", controller, StringComparison.Ordinal);

        Assert.Contains("data-status-key=\"openAiReady\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-account\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-pixel\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-capi\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-pending\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-retrying\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-failed\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-sent\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-openai-disconnect\"", view, StringComparison.Ordinal);
        Assert.Contains("Measurement evidence", view, StringComparison.Ordinal);
        Assert.Contains("A click reference is not proof of campaign credit.", view, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI API key", view, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("openAiDisconnect: analyticsEndpoint('/openai-disconnect')", js, StringComparison.Ordinal);
        Assert.Contains("status.openAiReady", js, StringComparison.Ordinal);
        Assert.Contains("openAi.health", js, StringComparison.Ordinal);
        Assert.Contains("attributionObserved", js, StringComparison.Ordinal);
        Assert.Contains("fetchPostJson('openAiDisconnect'", js, StringComparison.Ordinal);
        Assert.Contains(".marketing-setup-openai", css, StringComparison.Ordinal);
        Assert.Contains(".marketing-setup-lineage-grid", css, StringComparison.Ordinal);

        Assert.Contains("[HttpPost(\"openai-connect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsDirectConnectionService", controller, StringComparison.Ordinal);
        Assert.Contains("advertiserApiKey", js, StringComparison.Ordinal);
        Assert.Contains("openAiConnect: analyticsEndpoint('/openai-connect')", js, StringComparison.Ordinal);
        Assert.Contains("marketing-setup-openai-api-key", view, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", view, StringComparison.Ordinal);
        Assert.Contains("Verify &amp; connect", view, StringComparison.Ordinal);
        Assert.DoesNotContain("AdvertiserApiKey = openAi", controller, StringComparison.Ordinal);

        Assert.Contains("class=\"modal-content wa-modal-shell\"", view, StringComparison.Ordinal);
        Assert.Contains(".wa-modal-shell", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".marketing-setup-modal-dialog", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".fa-modal .modal-content", css, StringComparison.Ordinal);
        Assert.Contains(".marketing-setup-form-grid", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains(".is-critical", css, StringComparison.Ordinal);
        Assert.Contains(".is-warn", css, StringComparison.Ordinal);
        Assert.Contains(".is-good", css, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"openai-refresh\")]", controller, StringComparison.Ordinal);
        Assert.Contains("openAiRefresh: analyticsEndpoint('/openai-refresh')", js, StringComparison.Ordinal);
        Assert.Contains("pixelProvisioning = response?.pixelProvisioning", js, StringComparison.Ordinal);
        Assert.Contains("MarketingCommandReceiptAsync(tracking.Id, cancellationToken, refresh.PixelProvisioning)", controller, StringComparison.Ordinal);
        Assert.Contains("providerStatusFresh", controller, StringComparison.Ordinal);
        Assert.Contains("accountStatus = openAiProvider?.Status", controller, StringComparison.Ordinal);
        Assert.Contains("currencyCode = openAiProvider?.CurrencyCode", controller, StringComparison.Ordinal);
        Assert.Contains("timezone = openAiProvider?.Timezone", controller, StringComparison.Ordinal);
        Assert.Contains("reviewReason = openAiProvider?.ReviewReason", controller, StringComparison.Ordinal);
        Assert.Contains("Advertiser API key verified", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("API key verified' : '—'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void BusinessAnalytics_UsesTheSameCanonicalMarketingSetupAuthorities()
    {
        var business = Read("Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");
        var profile = Read("Infrastructure", "WebsiteEditing", "BusinessWebsiteProfileService.cs");

        Assert.Contains("[HttpGet(\"analytics/marketing-setup\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/marketing-setup\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/openai-connect\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/openai-refresh\")]", business, StringComparison.Ordinal);
        Assert.Contains("MarketingCommandReceiptAsync(businessId, cancellationToken, refresh.PixelProvisioning)", business, StringComparison.Ordinal);
        Assert.Contains("setupStatus = refreshed is JsonResult", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/openai-disconnect\")]", business, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business(businessId)", business, StringComparison.Ordinal);
        Assert.Contains("BusinessWebsiteProfileService", business, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsAccountConnectionAuthority", business, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsDirectConnectionService", business, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderSetupProjection", business, StringComparison.Ordinal);
        Assert.Contains("IPlatformConnectionHealthAuthority", Read("Infrastructure", "Analytics", "MarketingProviderSetupProjection.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("GetAdsAsync(owner", Read("Infrastructure", "Analytics", "MarketingProviderSetupProjection.cs"), StringComparison.Ordinal);
        Assert.Contains("canonical_business_marketing_setup", business, StringComparison.Ordinal);
        Assert.Contains("evidenceError = setup.EvidenceError", business, StringComparison.Ordinal);
        Assert.Contains("OpenAiClickReference.Normalize", Read("Infrastructure", "Analytics", "MarketingMeasurementEvidenceService.cs"), StringComparison.Ordinal);

        var specificRoute = business.IndexOf("[HttpGet(\"analytics/marketing-setup\")]", StringComparison.Ordinal);
        var catchAllRoute = business.IndexOf("[HttpGet(\"analytics/{**section}\")]", StringComparison.Ordinal);
        Assert.True(specificRoute >= 0 && catchAllRoute > specificRoute);

        Assert.Contains("connections.SaveSettingsAsync(MarketingOwnerScope.Business(businessId)", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("new MarketingConnection", business, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyProfileCapi_CannotOverrideOrBlockCanonicalOauthConnection()
    {
        var service = Read("Infrastructure", "Analytics", "AgentMarketingProfileService.cs");

        Assert.Contains("string.IsNullOrWhiteSpace(row.AdsAccessTokenCiphertext)", service, StringComparison.Ordinal);
        Assert.Contains("catch (CryptographicException)", service, StringComparison.Ordinal);
        Assert.Contains("Pixel/test-code migration must still complete", service, StringComparison.Ordinal);
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
        Assert.Contains("connections.SaveSettingsAsync(MarketingOwnerScope.Business(businessId), input.MetaPixelId", businessProfile, StringComparison.Ordinal);
        Assert.Contains("input.MetaTestEventCode, null, input.ConnectionRevision", businessProfile, StringComparison.Ordinal);
        Assert.Contains("protector.Unprotect(owner, row.CapiAccessTokenCiphertext)", connectionStore, StringComparison.Ordinal);
        Assert.Contains("row.AccessTokenExpiresUtc <= DateTime.UtcNow", connectionStore, StringComparison.Ordinal);
    }

    [Fact]
    public void MicrosoftCalendarAuth_UsesOneOwnerScopedConnectionAuthority_AndKeepsManualTargets()
    {
        var authority = Read("Infrastructure", "Bookings", "MicrosoftCalendarConnectionAuthority.cs");
        var registration = Read("Infrastructure", "Analytics", "MarketingConnectionStore.cs");
        var controller = Read("AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read("Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");
        var view = Read("AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read("AgentPortal", "wwwroot", "js", "website-analytics.js");
        var subscriptions = Read("AgentPortal", "Services", "GraphCalendarSubscriptionHostedService.cs");
        var confirmation = Read("Protect-Website", "Services", "Booking", "PublicBookingConfirmationService.cs");

        Assert.Contains("Provider = \"microsoft-calendar\"", authority, StringComparison.Ordinal);
        Assert.Contains("MarketingConnection", authority, StringComparison.Ordinal);
        Assert.Contains("MarketingCredentialProtector", authority, StringComparison.Ordinal);
        Assert.Contains("delegated_oauth", authority, StringComparison.Ordinal);
        Assert.Contains("refresh_token", authority, StringComparison.Ordinal);
        Assert.Contains("TryImportFounderApplicationConnectionAsync", authority, StringComparison.Ordinal);
        Assert.Contains("IMicrosoftCalendarConnectionAuthority", registration, StringComparison.Ordinal);

        Assert.Contains("calendarLinked = calendarConnection.Connected", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"calendar-connect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"calendar-disconnect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("calendarLinked = calendarConnection.Connected", business, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/calendar-connect\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/calendar-disconnect\")]", business, StringComparison.Ordinal);

        Assert.Contains("id=\"marketing-setup-calendar-connect\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-calendar-disconnect\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-mailbox\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-calendar\"", view, StringComparison.Ordinal);
        Assert.Contains("calendarConnect: analyticsEndpoint('/calendar-connect')", js, StringComparison.Ordinal);
        Assert.Contains("calendarDisconnect: analyticsEndpoint('/calendar-disconnect')", js, StringComparison.Ordinal);
        Assert.Contains("Manual fields below select a specific target but do not authenticate it.", js, StringComparison.Ordinal);

        Assert.Contains("calendarConnections.GetAccessTokenAsync(owner", subscriptions, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecretCredential", subscriptions, StringComparison.Ordinal);
        Assert.Contains("_calendarConnections.GetAccessTokenAsync(owner", confirmation, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecretCredential", confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public void BusinessPublicBooking_UsesBusinessMarketingSetup_NotAgentFallback()
    {
        var resolver = Read("Infrastructure", "Bookings", "PublicBookingResolver.cs");

        Assert.Contains("CommerceBusinessStorefrontSettings", resolver, StringComparison.Ordinal);
        Assert.Contains("BuildBusinessProfileResolution", resolver, StringComparison.Ordinal);
        Assert.Contains("PublicBookingConfigurationSources.BusinessProfile", resolver, StringComparison.Ordinal);
        Assert.Contains("settings.BookingCalendarEmail", resolver, StringComparison.Ordinal);
        Assert.Contains("settings.BookingMailboxId", resolver, StringComparison.Ordinal);
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

    [Fact]
    public void PublishedCustomDomains_UseVerifiedHttpsAndCanonicalSeoAuthority()
    {
        var middleware = Read("Infrastructure", "WebsiteRuntime", "BusinessWebsiteMiddleware.cs");
        var domains = Read("Infrastructure", "WebsiteEditing", "WebsiteDomainService.cs");
        var health = Read("Infrastructure", "WebsiteEditing", "WebsiteDomainHealthWorker.cs");

        Assert.Contains("binding.Status == \"active\" && binding.CertificateStatus == \"active\"", domains, StringComparison.Ordinal);
        Assert.Contains("https://\" + binding.Hostname + \"/.well-known/legend-website", domains, StringComparison.Ordinal);
        Assert.Contains("pendingCutoff = DateTime.UtcNow.AddMinutes(-10)", health, StringComparison.Ordinal);
        Assert.Contains("X-Robots-Tag", middleware, StringComparison.Ordinal);
        Assert.Contains("/sitemap.xml", middleware, StringComparison.Ordinal);
        Assert.Contains("lastmod", middleware, StringComparison.Ordinal);
        Assert.Contains("__LEGEND_CANONICAL_URL__", middleware, StringComparison.Ordinal);
        Assert.Contains("PublishedBusinessAsync", middleware, StringComparison.Ordinal);
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

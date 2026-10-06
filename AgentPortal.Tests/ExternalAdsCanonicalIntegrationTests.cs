using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Infrastructure.Analytics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ExternalAdsCanonicalIntegrationTests
{
    [Fact]
    public void GoogleAndTikTokClickIdsUseCanonicalEventMetadataAndChannelResolution()
    {
        var google = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "Purchase",
            EventUtc = DateTime.UtcNow,
            IsServerAuthority = true,
            Gclid = "google-click-1",
            Metadata = new { valueCents = 12500, clientUserId = "customer-google" }
        });
        var tiktok = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "Purchase",
            EventUtc = DateTime.UtcNow,
            IsServerAuthority = true,
            Ttclid = "tiktok-click-1",
            Metadata = new { valueCents = 9900, clientUserId = "customer-tiktok" }
        });

        Assert.Equal("google-click-1", CanonicalAdvertisingEventProjection.ReadString(google.MetadataJson, "gclid"));
        Assert.Equal("tiktok-click-1", CanonicalAdvertisingEventProjection.ReadString(tiktok.MetadataJson, "ttclid"));
        Assert.Equal(MarketingChannels.GoogleAds, CanonicalMarketingOutcomeProjection.ChannelFor(google));
        Assert.Equal(MarketingChannels.TikTokAds, CanonicalMarketingOutcomeProjection.ChannelFor(tiktok));
        Assert.Equal(TrafficType.PaidAds, TrafficAttribution.Classify(null, null, null, null, gclid: "google-click-1"));
        Assert.Equal(TrafficType.PaidAds, TrafficAttribution.Classify(null, null, null, null, ttclid: "tiktok-click-1"));
    }

    [Fact]
    public void ExternalAdsStayOnTheCanonicalConnectionAnalyticsAndReportingAuthorities()
    {
        var root = Root();
        var store = Read(root, "Infrastructure", "Analytics", "MarketingConnectionStore.cs");
        var oauth = Read(root, "Infrastructure", "Analytics", "MarketingExternalAdsOAuthService.cs");
        var reporting = Read(root, "Infrastructure", "Analytics", "MarketingExternalAdsReportingService.cs");
        var projection = Read(root, "Infrastructure", "Analytics", "MarketingProviderSetupProjection.cs");
        var controller = Read(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");

        Assert.Contains("MarketingDestinationKeys.Google", store, StringComparison.Ordinal);
        Assert.Contains("MarketingDestinationKeys.TikTok", store, StringComparison.Ordinal);
        Assert.Contains("protector.Protect(owner, key, primarySecret)", store, StringComparison.Ordinal);
        Assert.Contains("Marketing.ExternalAds.OAuthState.v1", oauth, StringComparison.Ordinal);
        Assert.Contains("customers:listAccessibleCustomers", oauth, StringComparison.Ordinal);
        Assert.Contains("oauth2/access_token/", oauth, StringComparison.Ordinal);
        Assert.Contains("report/integrated/get/", reporting, StringComparison.Ordinal);
        Assert.Contains("MarketingChannels.GoogleAds", reporting, StringComparison.Ordinal);
        Assert.Contains("MarketingChannels.TikTokAds", reporting, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderConnectionSnapshot Google", projection, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderConnectionSnapshot TikTok", projection, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"external-ads/connect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/external-ads/connect\")]", business, StringComparison.Ordinal);
        Assert.Contains("marketing-setup-google-connect", view, StringComparison.Ordinal);
        Assert.Contains("marketing-setup-tiktok-connect", view, StringComparison.Ordinal);
        Assert.Contains("externalAdsConnect: analyticsEndpoint('/external-ads/connect')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<Google", store, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<TikTok", store, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static string Root([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}

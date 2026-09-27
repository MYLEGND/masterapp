using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AdvertisingCommandCenterCentralizationTests
{
    [Fact]
    public void SharedAnalyticsUi_ProjectsCanonicalAdvertisingCommandCenter()
    {
        var root = Root();
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");
        var css = Read(root, "AgentPortal", "wwwroot", "css", "website-analytics.css");

        Assert.Contains("Advertising Command Center", view, StringComparison.Ordinal);
        Assert.Contains("advertisingCommandModal", view, StringComparison.Ordinal);
        Assert.Contains("Promote This", view, StringComparison.Ordinal);
        Assert.Contains("Approval Queue &amp; Audit History", view, StringComparison.Ordinal);

        Assert.Contains("advertising: analyticsEndpoint('/advertising')", js, StringComparison.Ordinal);
        Assert.Contains("advertisingPromotionDraft: analyticsEndpoint('/advertising/promote/draft')", js, StringComparison.Ordinal);
        Assert.Contains("advertisingPromotionPropose: analyticsEndpoint('/advertising/promote/propose')", js, StringComparison.Ordinal);
        Assert.Contains("advertisingApprove: analyticsEndpoint('/advertising/approve')", js, StringComparison.Ordinal);
        Assert.Contains("advertisingExecute: analyticsEndpoint('/advertising/execute')", js, StringComparison.Ordinal);
        Assert.Contains("advertisingReject: analyticsEndpoint('/advertising/reject')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("api.ads.openai.com", js, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(".advertising-command-modal", css, StringComparison.Ordinal);
        Assert.Contains(".advertising-command-grid", css, StringComparison.Ordinal);
        Assert.Contains(".advertising-command-ledger", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentAndBusinessAnalytics_AreThinAdaptersOverOneCommandCenter()
    {
        var root = Root();
        var agent = Read(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");

        Assert.Contains("IAdvertisingCommandCenterService", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"advertising\")]", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"advertising/promote/propose\")]", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"advertising/approve\")]", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"advertising/execute\")]", agent, StringComparison.Ordinal);
        Assert.Contains("ResolveOpenAiMarketingOwner(tracking)", agent, StringComparison.Ordinal);

        Assert.Contains("IAdvertisingCommandCenterService", business, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/advertising\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/advertising/promote/propose\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/advertising/approve\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/advertising/execute\")]", business, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business(businessId)", business, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionAuthority_ResolvesPublishedTruthFromMarketingOwnerScope()
    {
        var root = Root();
        var promotion = Read(root, "Infrastructure", "WebsiteEditing", "PromotionOrchestrationService.cs");
        var command = Read(root, "Infrastructure", "Analytics", "AdvertisingCommandCenterService.cs");
        var registration = Read(root, "Infrastructure", "Analytics", "MarketingConnectionStore.cs");

        Assert.DoesNotContain("WebsiteEditorTicket actor", promotion, StringComparison.Ordinal);
        Assert.Contains("PublishedStateAsync(owner", promotion, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorSiteKeys.BusinessOwnerKey(businessId)", promotion, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope owner", promotion, StringComparison.Ordinal);
        Assert.Contains("IAdvertisingActionAuthorizationService", command, StringComparison.Ordinal);
        Assert.Contains("IPromotionOrchestrationService", command, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsExecutionService", command, StringComparison.Ordinal);
        Assert.Contains("IAdvertisingCommandCenterService, AdvertisingCommandCenterService", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSpecificHosts_DoNotOwnParallelAdvertisingCommandCenters()
    {
        var root = Root();
        foreach (var path in new[]
        {
            Path.Combine(root, "AgentPortal", "Services", "AdvertisingCommandCenterService.cs"),
            Path.Combine(root, "ClientApp", "Services", "AdvertisingCommandCenterService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "AdvertisingCommandCenterService.cs"),
            Path.Combine(root, "AgentPortal", "Services", "PromotionOrchestrationService.cs"),
            Path.Combine(root, "ClientApp", "Services", "PromotionOrchestrationService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "PromotionOrchestrationService.cs")
        })
            Assert.False(File.Exists(path), $"Parallel advertising authority is forbidden: {path}");
    }

    [Fact]
    public void Parfait_RemainsACommerceBusinessConsumer_NotAnAdvertisingOwnerType()
    {
        var root = Root();
        var parfait = Read(root, "ParfaitApp", "Services", "ParfaitInternalAnalyticsService.cs");
        var profile = Read(root, "ParfaitApp", "Services", "ParfaitBusinessProfileService.cs");

        Assert.Contains("MarketingOwnerScope.Business", parfait, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketingOwnerScope.Parfait", parfait, StringComparison.Ordinal);
        Assert.DoesNotContain("ParfaitAdvertising", profile, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static string Root()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && File.Exists(Path.Combine(workspace, "MASTERAPP.sln")))
            return workspace;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MASTERAPP.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}

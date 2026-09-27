using System;
using System.IO;
using Xunit;

namespace AgentPortal.Tests;

public sealed class MarketingManagerCentralizationTests
{
    [Fact]
    public void SharedAnalyticsUi_UsesOneMarketingManagerAndUnifiedPerformanceSurface()
    {
        var root = Root();
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");
        var css = Read(root, "AgentPortal", "wwwroot", "css", "website-analytics.css");

        Assert.Contains("id=\"marketingManagerModal\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"channel-performance-grid\"", view, StringComparison.Ordinal);
        Assert.Contains("Marketing Manager", view, StringComparison.Ordinal);
        Assert.Contains("Channel Outcomes", view, StringComparison.Ordinal);

        Assert.Contains("marketingManagerPlan: analyticsEndpoint('/marketing-manager/plan')", js, StringComparison.Ordinal);
        Assert.Contains("marketingManagerPerformance: analyticsEndpoint('/marketing-manager/performance')", js, StringComparison.Ordinal);
        Assert.Contains("loadMarketingPerformance()", js, StringComparison.Ordinal);
        Assert.Contains("buildMarketingManagerPlan()", js, StringComparison.Ordinal);
        Assert.Contains("Open governed ad workflow", js, StringComparison.Ordinal);

        Assert.Contains(".marketing-manager-modal", css, StringComparison.Ordinal);
        Assert.Contains(".wa-channel-grid", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentAndBusinessAdapters_DelegateToCanonicalInfrastructureServices()
    {
        var root = Root();
        var agent = Read(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");
        var manager = Read(root, "Infrastructure", "Analytics", "MarketingManagerService.cs");
        var performance = Read(root, "Infrastructure", "Analytics", "UnifiedMarketingPerformanceService.cs");

        Assert.Contains("IMarketingManagerService", agent, StringComparison.Ordinal);
        Assert.Contains("IUnifiedMarketingPerformanceService", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"marketing-manager/plan\")]", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"marketing-manager/performance\")]", agent, StringComparison.Ordinal);

        Assert.Contains("IMarketingManagerService", business, StringComparison.Ordinal);
        Assert.Contains("IUnifiedMarketingPerformanceService", business, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/marketing-manager/plan\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/marketing-manager/performance\")]", business, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business(businessId)", business, StringComparison.Ordinal);

        Assert.Contains("IAdvertisingCommandCenterService", manager, StringComparison.Ordinal);
        Assert.Contains("advertising.ProposePromotionAsync", manager, StringComparison.Ordinal);
        Assert.Contains("IOpenAiAdsExecutionService", performance, StringComparison.Ordinal);
        Assert.Contains("IMetaAdsService", performance, StringComparison.Ordinal);
        Assert.Contains("OpenAiClickReference.Normalize", performance, StringComparison.Ordinal);
        Assert.Contains("Campaign-level revenue is not inferred", performance, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHosts_DoNotOwnParallelMarketingManagerImplementations()
    {
        var root = Root();
        foreach (var path in new[]
        {
            Path.Combine(root, "AgentPortal", "Services", "MarketingManagerService.cs"),
            Path.Combine(root, "ClientApp", "Services", "MarketingManagerService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "MarketingManagerService.cs"),
            Path.Combine(root, "Protect-Website", "Services", "MarketingManagerService.cs"),
            Path.Combine(root, "AgentPortal", "Services", "UnifiedMarketingPerformanceService.cs"),
            Path.Combine(root, "ParfaitApp", "Services", "UnifiedMarketingPerformanceService.cs")
        })
        {
            Assert.False(File.Exists(path), $"Parallel marketing authority is forbidden: {path}");
        }
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(root, Path.Combine(parts)));

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

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
    public void SharedAnalyticsUi_LoadersLiveInCanonicalAdvertisingScope()
    {
        var root = Root();
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");

        var scope = js.IndexOf("function advertisingScopeParams()", StringComparison.Ordinal);
        var performance = js.IndexOf("async function loadMarketingPerformance()", StringComparison.Ordinal);
        var onboarding = js.IndexOf("async function loadOpenAiOnboarding()", StringComparison.Ordinal);
        var advertisingBody = js.IndexOf("function advertisingBody(", StringComparison.Ordinal);

        Assert.True(scope >= 0, "Canonical advertising scope helper is required.");
        Assert.True(performance > scope, "Marketing performance loader must share the top-level advertising scope.");
        Assert.True(onboarding > scope, "OpenAI onboarding loader must share the top-level advertising scope.");
        Assert.True(advertisingBody > onboarding, "Read-side loaders must be established before advertising body helpers and callers.");
        Assert.Equal(1, js.Split("async function loadMarketingPerformance()", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, js.Split("async function loadOpenAiOnboarding()", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SharedAnalyticsUi_SupportingModulesCannotEraseCanonicalSummary()
    {
        var root = Root();
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");

        var summaryStart = js.IndexOf("async function loadSummary()", StringComparison.Ordinal);
        var summaryEnd = js.IndexOf("async function loadMarketingHealth()", summaryStart, StringComparison.Ordinal);
        Assert.True(summaryStart >= 0 && summaryEnd > summaryStart, "Canonical summary loader is required.");

        var summary = js[summaryStart..summaryEnd];
        var catchStart = summary.IndexOf("} catch (err) {", StringComparison.Ordinal);
        var supportStart = summary.IndexOf("void Promise.resolve().then(() => loadMarketingHealth())", StringComparison.Ordinal);

        Assert.True(catchStart >= 0, "Summary fetch/render failure boundary is required.");
        Assert.True(supportStart > catchStart, "Supporting module refreshes must execute outside the canonical summary try/catch.");
        Assert.Contains("console.error(err);\n      return;", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("renderSummaryUnavailable", summary[supportStart..], StringComparison.Ordinal);
        Assert.Contains("loadMarketingPerformance()", summary[supportStart..], StringComparison.Ordinal);
        Assert.Contains("loadGrowthEconomics()", summary[supportStart..], StringComparison.Ordinal);
    }

    [Fact]
    public void WebsiteAnalyticsController_DoesNotMisclassifyDatabaseFailuresAsTimeouts()
    {
        var root = Root();
        var controller = Read(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");

        var classifierStart = controller.IndexOf("private static bool IsAnalyticsTimeout(Exception ex)", StringComparison.Ordinal);
        var classifierEnd = controller.IndexOf("private static string ToClientQualityMode", classifierStart, StringComparison.Ordinal);
        Assert.True(classifierStart >= 0 && classifierEnd > classifierStart, "Analytics timeout classifier is required.");

        var classifier = controller[classifierStart..classifierEnd];
        Assert.Contains("current is TimeoutException", classifier, StringComparison.Ordinal);
        Assert.DoesNotContain("current is TimeoutException or DbException", classifier, StringComparison.Ordinal);
        Assert.Contains("catch (DbException ex)", controller, StringComparison.Ordinal);
        Assert.Contains("Summary database query failed.", controller, StringComparison.Ordinal);
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

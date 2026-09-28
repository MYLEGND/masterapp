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

        Assert.Contains("modal-content wa-modal-shell", view, StringComparison.Ordinal);
        Assert.DoesNotContain(".marketing-manager-modal", css, StringComparison.Ordinal);
        Assert.Contains(".wa-channel-grid", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedAnalyticsUi_CommandRailAndEventMapUseCanonicalAnalyticsPresentation()
    {
        var root = Root();
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var eventMap = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "EventMap.cshtml");
        var css = Read(root, "AgentPortal", "wwwroot", "css", "website-analytics.css");

        Assert.Contains("class=\"hero-link-command-actions\"", view, StringComparison.Ordinal);
        Assert.Contains("class=\"btn event-map-trigger\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-setup-open\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"advertising-command-open\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"marketing-manager-open\"", view, StringComparison.Ordinal);

        Assert.Contains("href=\"~/css/website-analytics.css\"", eventMap, StringComparison.Ordinal);
        Assert.Contains("wa-event-map-page", eventMap, StringComparison.Ordinal);
        Assert.Contains("wa-event-map-table", eventMap, StringComparison.Ordinal);
        Assert.Contains("Back to Website Analytics", eventMap, StringComparison.Ordinal);

        Assert.Contains(".hero-link-command-actions", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: max-content minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains("overflow: visible;", css, StringComparison.Ordinal);
        Assert.Contains(".event-map-trigger", css, StringComparison.Ordinal);
        Assert.Contains(".legend-workspace-page.wa-event-map-page", css, StringComparison.Ordinal);
        Assert.Contains(".wa-event-map-table", css, StringComparison.Ordinal);
        Assert.Contains("color: #8dffc5;", css, StringComparison.Ordinal);
        Assert.Contains("color: #6ee7ff;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedAnalyticsUi_UsesOneCleanModalAuthority_AndMobileCardsDoNotOverflow()
    {
        var root = Root();
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var incident = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "_AnalyticsIncidentModal.cshtml");
        var css = Read(root, "AgentPortal", "wwwroot", "css", "website-analytics.css");
        var kpiJs = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics-kpi-modal.js");

        Assert.True(view.Split("modal-content wa-modal-shell", StringSplitOptions.None).Length - 1 >= 21);
        Assert.Equal(3, view.Split("class=\"wa-standalone-modal\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("modal-content wa-modal-shell", incident, StringComparison.Ordinal);

        foreach (var legacy in new[]
        {
            ".fa-modal .modal-content",
            ".marketing-setup-modal-dialog",
            ".advertising-command-modal",
            ".marketing-manager-modal",
            ".meta-signal-shell",
            ".meta-signal-health-shell",
            ".wa-device-modal-shell",
            ".vc-modal-panel",
            ".vc-modal-backdrop",
            ".kpi-detail-panel",
            ".kpi-detail-backdrop"
        })
            Assert.DoesNotContain(legacy, css, StringComparison.Ordinal);

        Assert.DoesNotContain("vc-modal-panel", view, StringComparison.Ordinal);
        Assert.DoesNotContain("kpiDetailBackdrop", view, StringComparison.Ordinal);
        Assert.DoesNotContain("vc-modal-panel", kpiJs, StringComparison.Ordinal);
        Assert.DoesNotContain("kpiDetailBackdrop", kpiJs, StringComparison.Ordinal);

        Assert.Contains("/* Canonical Website Analytics modal system.", css, StringComparison.Ordinal);
        Assert.Contains(".wa-standalone-modal", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 1fr;\n        align-items: stretch;", css, StringComparison.Ordinal);
        Assert.Contains(".wa-growth-economics-grid {\n    grid-template-columns: 1fr;\n    overflow: visible;", css, StringComparison.Ordinal);
        Assert.Contains(".wa-growth-economics-card {\n    min-width: 0;", css, StringComparison.Ordinal);

        Assert.Contains("/* Canonical Core Metrics card system. */", css, StringComparison.Ordinal);
        Assert.Equal(
            1,
            css.Split(".wa-kpi-command-surface .fa-kpi-grid .kpi-card {", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            0,
            css.Split("\n.fa-kpi-grid .kpi-card {", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("grid-template-columns: repeat(5, minmax(190px, 1fr));", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));", css, StringComparison.Ordinal);
        Assert.Contains(".wa-channel-grid {\n    grid-template-columns: 1fr;", css, StringComparison.Ordinal);
        Assert.Contains(".wa-channel-loading {\n    min-width: 0;", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".wa-channel-card {\n  min-width: 190px;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("min-height: 176px;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("min-height: 150px;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void WebsiteAnalyticsMobileCascadeRelease_RemainsScopedToSharedAnalyticsHosts()
    {
        var root = Root();
        var request = Read(root, "Docs", "releases", "direct-release-request.json");

        Assert.Contains("\"masterapp-portal\"", request, StringComparison.Ordinal);
        Assert.Contains("\"masterapp-client\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"masterapp-protect\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"masterapp-parfait\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"masterapp-website\"", request, StringComparison.Ordinal);
        Assert.Contains("\"cloudflareWebsiteRouting\": false", request, StringComparison.Ordinal);
        Assert.Contains("\"preserveLiveTargets\": false", request, StringComparison.Ordinal);
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
        Assert.Contains("CanonicalMarketingOutcomeProjection.ConfirmedOutcomes", performance, StringComparison.Ordinal);
        Assert.DoesNotContain("MetaSignalEvents", performance, StringComparison.Ordinal);
        Assert.Contains("OpenAiClickReference.Normalize", Read(root, "Infrastructure", "Analytics", "CanonicalMarketingOutcomeProjection.cs"), StringComparison.Ordinal);
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
    public void SharedAnalyticsUi_MarketingManagerRuntimeContract_IsCanonicalAndLive()
    {
        var root = Root();
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");
        var agent = Read(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");

        var postHelper = js.IndexOf("async function fetchPostJson(", StringComparison.Ordinal);
        var scopeHelper = js.IndexOf("function advertisingScopeParams()", StringComparison.Ordinal);
        var planBuilder = js.IndexOf("async function buildMarketingManagerPlan()", StringComparison.Ordinal);
        var initializer = js.IndexOf("function initMarketingManager()", StringComparison.Ordinal);

        Assert.True(postHelper >= 0, "Canonical POST helper is required.");
        Assert.True(scopeHelper > postHelper, "Advertising scope must remain in the same canonical IIFE after the shared POST helper.");
        Assert.True(planBuilder > scopeHelper, "Marketing Manager plan builder must remain in the same canonical IIFE.");
        Assert.True(initializer > planBuilder, "Marketing Manager initializer must remain in the same canonical IIFE.");
        Assert.Equal(1, js.Split("async function fetchPostJson(", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, js.Split("async function buildMarketingManagerPlan()", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, js.Split("function initMarketingManager()", StringSplitOptions.None).Length - 1);

        foreach (var id in new[]
        {
            "marketing-manager-build",
            "marketing-manager-refresh",
            "marketing-manager-open-advertising",
            "channel-performance-refresh",
            "growth-economics-refresh"
        })
        {
            Assert.Contains($"id=\"{id}\"", view, StringComparison.Ordinal);
            Assert.Contains($"getElementById('{id}')?.addEventListener", js, StringComparison.Ordinal);
        }

        Assert.Contains("fetchPostJson(\n        'marketingManagerPlan'", js, StringComparison.Ordinal);
        Assert.Contains("endpoints.marketingManagerPlan", js, StringComparison.Ordinal);
        Assert.Contains("const performanceOk = await loadMarketingPerformance()", js, StringComparison.Ordinal);
        Assert.Contains("Zero activity is valid evidence.", js, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"marketing-manager/plan\")]", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"marketing-manager/performance\")]", agent, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"analytics/marketing-manager/plan\")]", business, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/marketing-manager/performance\")]", business, StringComparison.Ordinal);
    }

    [Fact]
    public void MarketingManager_ZeroEvidenceAndSlowEvidence_DoNotBlockCanonicalOperatorFlow()
    {
        var root = Root();
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");
        var manager = Read(root, "Infrastructure", "Analytics", "MarketingManagerService.cs");

        Assert.Contains("async function fetchJson(key, url, params = {}, timeoutMs = 0)", js, StringComparison.Ordinal);
        Assert.Contains("async function fetchPostJson(key, url, body = null, timeoutMs = 0)", js, StringComparison.Ordinal);
        Assert.Contains("marketingManagerRequestBody(),\n        15000)", js, StringComparison.Ordinal);
        Assert.Contains("marketingManagerRequestBody({ goal }),\n        20000)", js, StringComparison.Ordinal);
        Assert.Contains("Zero activity is valid evidence.", js, StringComparison.Ordinal);
        Assert.Contains("The Growth Plan can still be built from the goal and available canonical evidence.", js, StringComparison.Ordinal);

        var refreshStart = js.IndexOf("getElementById('marketing-manager-refresh')?.addEventListener", StringComparison.Ordinal);
        var refreshEnd = js.IndexOf("getElementById('marketing-manager-open-advertising')?.addEventListener", refreshStart, StringComparison.Ordinal);
        Assert.True(refreshStart >= 0 && refreshEnd > refreshStart, "Canonical Marketing Manager refresh handler is required.");
        var refresh = js[refreshStart..refreshEnd];
        Assert.Contains("await loadMarketingPerformance()", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("loadGrowthEconomics()", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Promise.all(", refresh, StringComparison.Ordinal);

        Assert.Contains("new(\"verified_leads\", \"Verified leads\", summary.VerifiedLeads.ToString()", manager, StringComparison.Ordinal);
        Assert.Contains("summary.TopSource ?? \"No source in range\"", manager, StringComparison.Ordinal);
        Assert.Contains("The selected range contains {summary.VerifiedLeads:N0} verified leads.", manager, StringComparison.Ordinal);
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

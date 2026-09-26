using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ScopedParfaitCommerceAuthorityTests
{
    [Fact]
    public void ScopedCommerce_ReusesCanonicalParfaitViews_AndHasNoMiniWorkspace()
    {
        var root = FindRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(root, "ParfaitApp", "Views", "CommerceManagement", "Workspace.cshtml")));
        Assert.False(File.Exists(Path.Combine(root, "ParfaitApp", "Models", "CommerceManagementWorkspaceViewModel.cs")));

        var controller = ReadSource("ParfaitApp", "Controllers", "CommerceManagementController.cs");
        Assert.Contains("~/Views/InternalModules/Products.cshtml", controller, StringComparison.Ordinal);
        Assert.Contains("~/Views/InternalModules/Orders.cshtml", controller, StringComparison.Ordinal);
        Assert.Contains("~/Views/InternalModules/Automations.cshtml", controller, StringComparison.Ordinal);
        Assert.Contains("~/Views/InternalModules/Analytics.cshtml", controller, StringComparison.Ordinal);
        Assert.Contains("~/Views/Dashboard/Index.cshtml", controller, StringComparison.Ordinal);
        Assert.Contains("store.CommerceBusinessId", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("CommerceManagementWorkspaceViewModel", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductsOrdersAndAutomations_StayOnExistingParfaitAuthorities_WithBusinessScope()
    {
        var products = ReadSource("ParfaitApp", "Services", "ParfaitProductService.cs");
        var orders = ReadSource("ParfaitApp", "Services", "ParfaitOrderService.cs");
        var automations = ReadSource("ParfaitApp", "Services", "ParfaitCustomerAutomationService.cs");

        Assert.Contains("GetAllProducts(Guid businessId)", products, StringComparison.Ordinal);
        Assert.Contains("GetAllOrders(Guid businessId)", orders, StringComparison.Ordinal);
        Assert.Contains("GetWorkspaceViewModel(Guid businessId)", automations, StringComparison.Ordinal);
        Assert.Contains("GetCustomerAutomationsPath(key)", automations, StringComparison.Ordinal);
    }

    [Fact]
    public void WebsiteStudio_HandsOffToParfait_AndSharedTicketAuthorityIsExplicit()
    {
        var platform = ReadSource("Infrastructure", "WebsiteEditing", "WebsitePlatformController.cs");
        var tickets = ReadSource("Infrastructure", "WebsiteEditing", "WebsiteEditorTicketProtector.cs");
        var workflow = ReadSource(".github", "workflows", "all-intentional-direct-release-20260918.yml");

        Assert.Contains("/commerce/manage/products?ticket=", platform, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorDataProtection:BlobUri", tickets, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorDataProtection:KeyVaultKeyId", tickets, StringComparison.Ordinal);
        Assert.Contains("id: editorauth", workflow, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorDataProtection__BlobUri", workflow, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorDataProtection__KeyVaultKeyId", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CommerceAnalyticsMetaAndPixel_UseTheCentralBusinessAuthorities()
    {
        var root = FindRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(root, "ParfaitApp", "Services", "ParfaitMetaAdsConnectionStoreAdapter.cs")));
        Assert.False(File.Exists(Path.Combine(root, "ParfaitApp", "Services", "ParfaitMetaAdsOAuthService.cs")));

        var program = ReadSource("ParfaitApp", "Program.cs");
        var analytics = ReadSource("ParfaitApp", "Services", "ParfaitInternalAnalyticsService.cs");
        var controller = ReadSource("ParfaitApp", "Controllers", "CommerceManagementController.cs");
        var tracking = ReadSource("ParfaitApp", "Views", "Shared", "_ParfaitCommerceTracking.cshtml");
        var signals = ReadSource("Infrastructure", "Commerce", "CommerceSignalService.cs");

        Assert.DoesNotContain("ParfaitMetaAdsConnectionStoreAdapter", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IParfaitMetaAdsOAuthService", program, StringComparison.Ordinal);
        Assert.Contains("ScopeContext.ForBusiness(businessId)", analytics, StringComparison.Ordinal);
        Assert.Contains("MarketingOwnerScope.Business(store.CommerceBusinessId)", controller, StringComparison.Ordinal);
        Assert.Contains("MarketingMetaAdsOAuthService", controller, StringComparison.Ordinal);
        Assert.Contains("MarketingConnectionStore", controller, StringComparison.Ordinal);
        Assert.Contains("MarketingConnections.GetStatusAsync", tracking, StringComparison.Ordinal);
        Assert.Contains("MetaSignalEventCatalog", signals, StringComparison.Ordinal);
        Assert.Contains("UnifiedMetaSignalWriter", signals, StringComparison.Ordinal);
    }

    [Fact]
    public void CommerceEventsAndAutomations_RemainLiveForEveryScopedStore()
    {
        var checkout = ReadSource("ParfaitApp", "Controllers", "StoreCheckoutController.cs");
        var analyticsController = ReadSource("ParfaitApp", "Controllers", "ParfaitAnalyticsController.cs");
        var automations = ReadSource("ParfaitApp", "Services", "ParfaitCustomerAutomationService.cs");
        var hosted = ReadSource("ParfaitApp", "Services", "ParfaitCustomerAutomationHostedService.cs");

        Assert.Contains("_automations.CaptureCheckoutLead(store.CommerceBusinessId", checkout, StringComparison.Ordinal);
        Assert.Contains("_automations.MarkOrderConverted(store.CommerceBusinessId", checkout, StringComparison.Ordinal);
        Assert.Contains("\"InitiateCheckout\"", checkout, StringComparison.Ordinal);
        Assert.Contains("\"Purchase\"", checkout, StringComparison.Ordinal);
        Assert.Contains("commerceSignals.RecordAsync", analyticsController, StringComparison.Ordinal);
        Assert.Contains("GetDueDispatchCandidatesForAllBusinesses", automations, StringComparison.Ordinal);
        Assert.Contains("GetDueDispatchCandidatesForAllBusinesses", hosted, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopedParfaitLayout_UsesWebsiteThemeInsteadOfASecondThemeSystem()
    {
        var layout = ReadSource("ParfaitApp", "Views", "Shared", "_InternalLayout.cshtml");
        Assert.Contains("CommerceStoreContext", layout, StringComparison.Ordinal);
        Assert.Contains("theme?.GoldStrong", layout, StringComparison.Ordinal);
        Assert.Contains("theme?.NavyDeep", layout, StringComparison.Ordinal);
        Assert.Contains("--pf-cocoa", layout, StringComparison.Ordinal);
        Assert.Contains("--pf-nav-bg", layout, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] path)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (IsRepositoryRoot(githubWorkspace))
            return Path.GetFullPath(githubWorkspace!);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static bool IsRepositoryRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "MASTERAPP.sln")) &&
        Directory.Exists(Path.Combine(path, "ParfaitApp")) &&
        Directory.Exists(Path.Combine(path, "AgentPortal.Tests"));
}

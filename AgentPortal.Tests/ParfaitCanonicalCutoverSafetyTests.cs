using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Stage-1 hold gates: protect active commerce transport and legacy data until
/// the shared executor has a tested, approved migration and rollback path.
/// These assertions are intentionally temporary cutover gates, not a second
/// routing or payment implementation.
/// </summary>
public sealed class ParfaitCanonicalCutoverSafetyTests
{
    [Fact]
    public void CommerceOrigin_StillServesExistingCrossTenantStorePaths()
    {
        var bridge = Read("Legend-Cloudflare", "src", "website-routing", "bridge.mjs");
        Assert.Contains("masterapp-parfait.azurewebsites.net", bridge, StringComparison.Ordinal);
        Assert.Contains("path === \"/store\"", bridge, StringComparison.Ordinal);
        Assert.Contains("path.startsWith(\"/store/\")", bridge, StringComparison.Ordinal);
        Assert.Contains("path.startsWith(\"/commerce/manage/\")", bridge, StringComparison.Ordinal);
        Assert.Contains("path.startsWith(\"/uploads/parfait-products/\")", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCommerceCheckout_RetainsExistingSquareAndAttributionConnections()
    {
        var checkout = Read("ParfaitApp", "Controllers", "StoreCheckoutController.cs");
        var program = Read("ParfaitApp", "Program.cs");
        Assert.Contains("ExecuteCommerceOneTimePaymentAsync", checkout, StringComparison.Ordinal);
        Assert.Contains("BuildPaymentIdempotencyKey", checkout, StringComparison.Ordinal);
        Assert.Contains("_orders.MarkPaymentCaptured", checkout, StringComparison.Ordinal);
        Assert.Contains("commerceSignals.RecordAsync", Read("ParfaitApp", "Controllers", "StoreCartController.cs"), StringComparison.Ordinal);
        Assert.Contains("AddMasterAppBilling", program, StringComparison.Ordinal);
        Assert.Contains("AddMasterAppFinancialIntelligence", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingParfaitCustomerStateAndImages_CannotBeDiscardedEarly()
    {
        var paths = Read("ParfaitApp", "Services", "ParfaitStoragePaths.cs");
        Assert.Contains("parfait-customer-automations.json", paths, StringComparison.Ordinal);
        Assert.Contains("parfait-team-access.json", paths, StringComparison.Ordinal);
        Assert.Contains("parfait-business-profile.json", paths, StringComparison.Ordinal);
        Assert.Contains("MigrateUploads()", paths, StringComparison.Ordinal);
        Assert.Contains("LegacyUploadRoot", paths, StringComparison.Ordinal);
        Assert.Contains("GetCustomerAutomationsPath", paths, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(path).ToArray()));

    private static string RepositoryRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (IsRoot(workspace)) return Path.GetFullPath(workspace!);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (IsRoot(directory.FullName)) return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root unavailable.");
    }

    private static bool IsRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "MASTERAPP.sln"));
}

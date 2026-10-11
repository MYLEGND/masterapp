using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// The same internal Razor source serves Parfait and every opted-in scoped
/// business. A non-Parfait merchant must not silently inherit Parfait assets.
/// </summary>
public sealed class CommerceCanonicalInternalBrandIsolationTests
{
    [Fact]
    public void SharedInternalShellSelectsTheActualCommerceTenantIdentity()
    {
        var layout = Read("ParfaitApp", "Views", "Shared", "_InternalLayout.cshtml");
        Assert.Contains("scopedStore?.StoreName", layout, StringComparison.Ordinal);
        Assert.Contains("scopedStore?.LogoUrl", layout, StringComparison.Ordinal);
        Assert.Contains("scopedStore?.IsParfait == true", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"@faviconUrl\"", layout, StringComparison.Ordinal);
        Assert.Contains("src=\"@scopedLogoUrl\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<link rel=\"icon\" type=\"image/png\" href=\"~/images/favicon/parfait-logo.png\" />",
            layout, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalProductEditorModalsUseTheSharedTenantFallback()
    {
        var products = Read("ParfaitApp", "Views", "InternalModules", "Products.cshtml");
        Assert.Contains("scopedStoreContext?.BusinessKey", products, StringComparison.Ordinal);
        Assert.Contains("productImageFallback", products, StringComparison.Ordinal);
        Assert.Contains("productSkuFallback", products, StringComparison.Ordinal);
        Assert.DoesNotContain("primaryImage?.ImageUrl ?? \"/images/favicon/parfait-logo.png\"",
            products, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedManagementRoutesRenderTheOriginalSingleRazorImplementations()
    {
        var controller = Read("CommerceCore", "Controllers", "CommerceManagementController.cs");
        Assert.Contains("View(\"~/Views/InternalModules/Products.cshtml\"", controller, StringComparison.Ordinal);
        Assert.Contains("View(\"~/Views/InternalModules/Orders.cshtml\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"~/Views/InternalModules/Automations.cshtml\"", controller, StringComparison.Ordinal);
        Assert.Contains("View(\"~/Views/InternalModules/Analytics.cshtml\"", controller, StringComparison.Ordinal);
        Assert.Contains("CommerceManagerTicket", controller, StringComparison.Ordinal);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shared.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendSiteToolBridgeTests
{
    [Fact]
    public void DisclosureAuthority_DropsPrivateAndUnboundedBrowserMaterial()
    {
        var snapshot = new LegendSitePageSnapshot(
            Application: "AgentPortal",
            SourceRevision: new string('a', 40),
            Path: "/Clients/Index",
            ViewportWidth: 390,
            ViewportHeight: 844,
            DevicePixelRatio: 3,
            Breakpoint: "xs",
            ComponentIds: new[] { "website.editor", "customer@example.com", "123456789-private" },
            ActionKeys: new[] { "contact.submit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            ModalIds: new[] { "website-modal", "550e8400-e29b-41d4-a716-446655440000" },
            AssetPaths: new[] { "/js/app.js", "/js/private.js?token=secret", "https://evil.invalid/x.js" },
            Issues: new[]
            {
                new LegendSitePageIssue("TypeError", 500, "SuspectedDefect", "ui_error", "/js/app.js"),
                new LegendSitePageIssue("PRIVATE_ERROR_TEXT", 999, "PRIVATE_CATEGORY", "bad operation!", "/js/private.js?token=secret")
            });

        var json = JsonSerializer.Serialize(LegendSiteToolDisclosureAuthority.SanitizePage(
            snapshot, "AgentPortal", "founder_system", new string('a', 40),
            new LegendRouteAuthority("/Clients/Index", "ClientsController", "Index", "AgentPortal")));

        Assert.Contains("\"website.editor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"contact.submit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"/js/app.js\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("customer@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("550e8400", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.invalid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_", json, StringComparison.Ordinal);
        Assert.Contains("\"domTextIncluded\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"inputValuesIncluded\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"cookiesIncluded\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"authorizationMaterialIncluded\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"privateCustomerDataIncluded\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserBridge_IsStructuralOnly_AndUsesSupportedFeatureDetectedWebMcp()
    {
        var source = Read("SHARED", "wwwroot", "js", "legend-site-tools.js");
        Assert.Contains("document.modelContext", source, StringComparison.Ordinal);
        Assert.Contains("registerTool", source, StringComparison.Ordinal);
        Assert.Contains("readOnlyHint: true", source, StringComparison.Ordinal);
        Assert.Contains("/api/legend-site-tools", source, StringComparison.Ordinal);
        Assert.Contains("RequestVerificationToken", source, StringComparison.Ordinal);
        Assert.Contains("legend_site_tool_antiforgery_unavailable", source, StringComparison.Ordinal);
        Assert.Contains("LegendPageHealth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("textContent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", source, StringComparison.Ordinal);
        Assert.DoesNotContain("document.cookie", source, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.getItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.search", source, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FounderBridge_ReusesFounderAuthority_AndPublishesNoMutationTools()
    {
        var source = Read("AgentPortal", "Controllers", "FounderDiagnosticsController.cs");
        Assert.Contains("[Authorize]", source, StringComparison.Ordinal);
        Assert.Contains("[FounderOnly]", source, StringComparison.Ordinal);
        Assert.Contains("FounderGuard.EnsureFounderOrThrow(User)", source, StringComparison.Ordinal);
        Assert.Contains("new LegendFounderToolAuthority", source, StringComparison.Ordinal);
        Assert.Contains("authority.IsReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("LegendConnectExternalProviderPolicy.CloudflareFoundation", source, StringComparison.Ordinal);
        Assert.Contains("mutationToolsExposed = false", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateAntiForgeryToken]", SiteToolSection(source), StringComparison.Ordinal);
        Assert.DoesNotContain("legend_release_approved_repair", SiteToolSection(source), StringComparison.Ordinal);
        Assert.DoesNotContain("legend_prepare_software_repair", SiteToolSection(source), StringComparison.Ordinal);
    }

    [Fact]
    public void ClientBridge_CannotReachFounderRepositoryOrRepairAuthority()
    {
        var source = Read("ClientApp", "Controllers", "HomeController.cs");
        var section = source[source.IndexOf("[Authorize]\n    [HttpGet(\"/api/legend-site-tools/catalog\")", StringComparison.Ordinal)..];
        Assert.Contains("LegendSiteToolDisclosureAuthority.CurrentPageTool", section, StringComparison.Ordinal);
        Assert.Contains("\"authenticated_client\"", section, StringComparison.Ordinal);
        Assert.Contains("mutationToolsExposed = false", section, StringComparison.Ordinal);
        Assert.Contains("[ValidateAntiForgeryToken]", section, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendFounderToolAuthority", section, StringComparison.Ordinal);
        Assert.DoesNotContain("IFounderSoftwareRemediationService", section, StringComparison.Ordinal);
        Assert.DoesNotContain("legend_inspect_repository", section, StringComparison.Ordinal);
        Assert.DoesNotContain("legend_prepare_software_repair", section, StringComparison.Ordinal);
        Assert.DoesNotContain("legend_release_approved_repair", section, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPageHealth_OwnsSingleBridgeLoadForAllDotNetWebApps()
    {
        var partial = Read("SHARED", "Views", "Diagnostics", "_PageHealth.cshtml");
        Assert.Equal(1, CountOccurrences(partial, "legend-site-tools.js"));
        foreach (var app in new[] { "AgentPortal", "ClientApp", "ProtectWebsite", "ParfaitApp" })
            Assert.Contains(app, partial, StringComparison.Ordinal);

        foreach (var path in new[]
        {
            new[] { "AgentPortal", "Views", "Shared", "_Layout.cshtml" },
            new[] { "ClientApp", "Views", "Shared", "_Layout.cshtml" },
            new[] { "Protect-Website", "Views", "Shared", "_Layout.cshtml" },
            new[] { "ParfaitApp", "Views", "Shared", "_Layout.cshtml" }
        })
        {
            var layout = Read(path);
            Assert.Contains("_PageHealth.cshtml", layout, StringComparison.Ordinal);
            Assert.DoesNotContain("legend-site-tools.js", layout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublicWebBridge_ExposesOnlyStructuralPageTool_AndNoFounderAuthority()
    {
        var source = Read("Infrastructure", "Diagnostics", "PublicLegendSiteToolsController.cs");
        Assert.Contains("[AllowAnonymous]", source, StringComparison.Ordinal);
        Assert.Contains("CurrentPageTool", source, StringComparison.Ordinal);
        Assert.Contains("public_structural_only", source, StringComparison.Ordinal);
        Assert.Contains("mutationToolsExposed = false", source, StringComparison.Ordinal);
        Assert.Contains("systemToolsExposed = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LegendFounderToolAuthority", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFounderSoftwareRemediationService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MasterAppDbContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("legend_inspect_repository", source, StringComparison.Ordinal);
        Assert.DoesNotContain("legend_prepare_software_repair", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeMasterAppInventory_CoversWebNativeEdgeAndSharedAuthorities()
    {
        var json = JsonSerializer.SerializeToElement(LegendSiteToolDisclosureAuthority.SystemInventory());
        var surfaces = json.GetProperty("surfaces").EnumerateArray().ToArray();
        var keys = surfaces.Select(item => item.GetProperty("key").GetString()).ToArray();
        foreach (var required in new[]
        {
            "agent-portal", "client-app", "protect-website", "parfait-app", "legend-website",
            "legend-ios", "legend-android", "legend-cloudflare", "infrastructure", "shared", "domain", "legend-design"
        })
            Assert.Contains(required, keys);

        foreach (var surface in surfaces)
        {
            var repositoryPath = surface.GetProperty("repositoryPath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(repositoryPath));
            Assert.True(Directory.Exists(Path.Combine(RepoRoot, repositoryPath!)), repositoryPath);
        }

        var raw = json.GetRawText();
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authenticated /api/v1/mobile/runtime-diagnostics", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeApps_ReuseExistingSanitizedAuthenticatedDiagnosticTransports()
    {
        var ios = Read("Legend-ios", "Legend", "Core", "LegendDiagnostics.swift");
        var android = Read("Legend-Android", "app", "src", "main", "java", "com", "mylegnd", "legend", "registered", "core", "diagnostics", "RuntimeDiagnostics.kt");
        var mobile = Read("AgentPortal", "Mobile", "MobileRuntimeDiagnosticsController.cs");
        Assert.Contains("RuntimeDiagnosticEvent", ios, StringComparison.Ordinal);
        Assert.Contains("RuntimeDiagnostic", android, StringComparison.Ordinal);
        Assert.Contains("MobileApiAuthorization.PolicyName", mobile, StringComparison.Ordinal);
        Assert.Contains("RuntimeDiagnosticStore", mobile, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", mobile, StringComparison.Ordinal);
    }

    [Fact]
    public void LegendStaticWebsite_CopiesSharedBridge_AndUsesApprovedPublicDiagnosticsOrigin()
    {
        var build = Read("Legend-Website", "scripts", "build.mjs");
        var check = Read("Legend-Website", "scripts", "check.mjs");
        Assert.Contains("SHARED/wwwroot/js/legend-site-tools.js", build, StringComparison.Ordinal);
        Assert.Contains("/api/legend-public-site-tools", build, StringComparison.Ordinal);
        Assert.Contains("legend-site-tools.js", check, StringComparison.Ordinal);
        Assert.Contains("approved public diagnostics origin", check, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string SiteToolSection(string source)
    {
        var index = source.IndexOf("[HttpGet(\"~/api/legend-site-tools/catalog\")", StringComparison.Ordinal);
        Assert.True(index >= 0);
        return source[index..];
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(path).ToArray()));

    private static string RepoRoot => ResolveRepoRoot();

    private static string ResolveRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var candidates = new List<string> { Directory.GetCurrentDirectory() };
        var sourceDirectory = Path.GetDirectoryName(sourceFile);
        if (!string.IsNullOrWhiteSpace(sourceDirectory)) candidates.Add(sourceDirectory);
        foreach (var candidate in candidates)
        {
            var current = new DirectoryInfo(candidate);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "MASTERAPP.sln"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

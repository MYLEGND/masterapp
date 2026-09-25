using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

public sealed class PageHealthContractTests
{
    [Theory]
    [InlineData("AgentPortal", "Views", "Shared", "_Layout.cshtml")]
    [InlineData("AgentPortal", "Views", "Shared", "_ClientWorkspaceLayout.cshtml")]
    [InlineData("ClientApp", "Views", "Shared", "_Layout.cshtml")]
    [InlineData("ParfaitApp", "Views", "Shared", "_Layout.cshtml")]
    [InlineData("Protect-Website", "Views", "Shared", "_Layout.cshtml")]
    public void RenderedLayouts_LoadOneSharedPageHealthSystemBeforePageContent(params string[] path)
    {
        var source = Read(path);
        const string partial = "~/Views/Diagnostics/_PageHealth.cshtml";

        Assert.Equal(1, CountOccurrences(source, partial));
        Assert.True(source.IndexOf(partial, StringComparison.Ordinal) < source.IndexOf("@RenderBody()", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedPageHealth_PreservesOneObserverWithoutPublicDiagnosticsOrPrivateStorage()
    {
        var source = Read("SHARED", "wwwroot", "js", "page-health.js");
        var host = Read("SHARED", "Views", "Diagnostics", "_PageHealth.cshtml");
        var stylesheet = Read("SHARED", "wwwroot", "css", "page-health.css");

        Assert.DoesNotContain("~/_content/Shared/css/page-health.css", host, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(host, "~/_content/Shared/js/page-health.js"));
        Assert.Contains("GetAndStoreTokens(Context)", host, StringComparison.Ordinal);
        Assert.Contains("data-csrf=", host, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPortal.", host, StringComparison.Ordinal);
        Assert.Contains("window.LegendPageHealth = Object.freeze({ current });", source, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"error\"", source, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"unhandledrejection\"", source, StringComparison.Ordinal);
        Assert.Contains("window.fetch = async function", source, StringComparison.Ordinal);
        // Behavior, bounded retries and private-content exclusion are executed in
        // tests/layout/page-health.test.mjs. Classification belongs to the server.
        Assert.Equal(1, CountOccurrences(source, "window.fetch = async function"));
        Assert.Contains("/api/runtime-diagnostics", source, StringComparison.Ordinal);
        Assert.Contains("RequestVerificationToken", source, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.getItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.setItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("createElement", source, StringComparison.Ordinal);
        Assert.DoesNotContain("legend-page-health-bottom-reserved", stylesheet, StringComparison.Ordinal);
        Assert.DoesNotContain("explorePageHealth", Read("AgentPortal", "Views", "Shared", "_Layout.cshtml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AgentPortal", "Views", "Clients", "Index.cshtml")]
    [InlineData("AgentPortal", "Views", "Leads", "Index.cshtml")]
    [InlineData("AgentPortal", "wwwroot", "js", "clients-index.js")]
    [InlineData("AgentPortal", "wwwroot", "js", "leads-index.js")]
    public void CrmPages_UseTheSharedPageHealthSystemAndNoLocalDiagnosticAssets(params string[] path)
    {
        var source = Read(path);

        Assert.DoesNotContain("crm-diagnostics", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LegendCrmDiagnostics", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CrmScripts_SendTheirExistingSignalsToTheSharedPageHealthInstance()
    {
        Assert.Contains("const quickViewDiagnostics = window.LegendPageHealth.current;", Read("AgentPortal", "wwwroot", "js", "clients-index.js"), StringComparison.Ordinal);
        Assert.Contains("const quickViewDiagnostics = window.LegendPageHealth.current;", Read("AgentPortal", "wwwroot", "js", "leads-index.js"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(RepoRoot, "AgentPortal", "wwwroot", "js", "crm-diagnostics.js")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "AgentPortal", "wwwroot", "css", "crm-diagnostics.css")));
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepoRoot, .. path]));

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

    private static string RepoRoot => ResolveRepoRoot();

    private static string ResolveRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var candidates = new List<string> { Directory.GetCurrentDirectory() };
        var sourceDirectory = Path.GetDirectoryName(sourceFile);
        if (!string.IsNullOrWhiteSpace(sourceDirectory)) candidates.Add(Path.Combine(sourceDirectory, ".."));

        return candidates.Select(Path.GetFullPath).First(candidate => File.Exists(Path.Combine(candidate, "MASTERAPP.sln")));
    }
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteEditorTicketAuthorityIsolationTests
{
    [Fact]
    public void ClientApp_CannotMintWebsiteEditorTickets()
    {
        var clientProgram = File.ReadAllText(Source("ClientApp", "Program.cs"));
        var clientProfile = File.ReadAllText(Source("ClientApp", "Controllers", "ProfileController.cs"));

        Assert.DoesNotContain("WebsiteEditorTicketProtector.CreateShared", clientProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("WebsiteEditorTicketProtector", clientProfile, StringComparison.Ordinal);
        Assert.DoesNotContain(".Protect(new WebsiteEditorTicket", clientProfile, StringComparison.Ordinal);
        Assert.Contains("CreateWebsiteEditorHandoffAsync", clientProfile, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterAppPlatform_IsTheBusinessWebsiteTicketMintingAuthority()
    {
        var platform = File.ReadAllText(Source("Infrastructure", "WebsiteEditing", "WebsitePlatformController.cs"));

        Assert.Contains("[HttpPost(\"handoff\")]", platform, StringComparison.Ordinal);
        Assert.Contains("WebsiteBusinessAccess.CanManageAsActorAsync", platform, StringComparison.Ordinal);
        Assert.Contains("ExecuteUpdateAsync", platform, StringComparison.Ordinal);
        Assert.Contains("_tickets.Protect(new WebsiteEditorTicket", platform, StringComparison.Ordinal);
        Assert.Contains("ClientIdentityContinuationPurpose.WebsiteEditor", platform, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedWebsiteManager_ExchangesOpaqueHandoffWithProtect()
    {
        var manager = File.ReadAllText(Source("Legend-Design", "legend-website-management.js"));

        Assert.Contains("bootstrap?.handoffUrl", manager, StringComparison.Ordinal);
        Assert.Contains("method: 'POST'", manager, StringComparison.Ordinal);
        Assert.Contains("session = await exchange.json()", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void AppShellFavicons_ReadThePublishedWebsiteParityService()
    {
        var portal = File.ReadAllText(Source("AgentPortal", "Views", "Shared", "_Layout.cshtml"));
        var faviconParity = File.ReadAllText(Source("Infrastructure", "WebsiteEditing", "WebsiteFaviconParity.cs"));
        var workspace = File.ReadAllText(Source("AgentPortal", "Views", "Shared", "_ClientWorkspaceLayout.cshtml"));
        var client = File.ReadAllText(Source("ClientApp", "Views", "Shared", "_Layout.cshtml"));
        var controller = File.ReadAllText(Source("Infrastructure", "WebsiteEditing", "WebsitePlatformController.cs"));

        Assert.Contains("WebsiteFaviconParity.PublicUrl", portal, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorSiteKeys.Protect", portal, StringComparison.Ordinal);
        Assert.Contains("faviconTrackingProfile?.Slug", portal, StringComparison.Ordinal);
        Assert.Contains("agentSlug=", faviconParity, StringComparison.Ordinal);
        Assert.Contains("WebsiteFaviconParity.PublicUrl", workspace, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorSiteKeys.Protect", workspace, StringComparison.Ordinal);
        Assert.Contains("WebsiteFaviconParity.PublicUrl", client, StringComparison.Ordinal);
        Assert.Contains("WebsiteEditorSiteKeys.Legend", client, StringComparison.Ordinal);
        Assert.DoesNotContain("~/images/favicon/legend-favicon.svg", portal, StringComparison.Ordinal);
        Assert.DoesNotContain("~/images/favicon/legend-favicon.svg", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("~/images/favicon/legend-favicon.svg", client, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"public/{siteKey}/favicon\")]", controller, StringComparison.Ordinal);
        Assert.Contains("LoadAsync(ownerKey, siteKey", controller, StringComparison.Ordinal);
    }

    private static string Source(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentSource())!,
            ".."));
        return Path.Combine(new[] { root }.Concat(segments).ToArray());
    }

    private static string CurrentSource([CallerFilePath] string source = "") => source;
}

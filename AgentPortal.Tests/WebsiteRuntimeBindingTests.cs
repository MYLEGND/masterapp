using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.WebsitePublishing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class WebsiteRuntimeBindingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "website-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly WebsitePageCompiler compiler;
    public WebsiteRuntimeBindingTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "WebsiteCompiler", "dist"));
        compiler = new WebsitePageCompiler(Mock.Of<IWebHostEnvironment>(e => e.ContentRootPath == root), new ConfigurationBuilder().Build());
    }
    private string Asset(string name, string contents)
    {
        File.WriteAllText(Path.Combine(root, "WebsiteCompiler", "dist", name), contents);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
    }
    [Fact]
    public async Task BindsCurrentBytesPreservingOtherQueriesFragmentsAndMarkup()
    {
        var hash = Asset("legend-public-cms.js", "current package");
        const string html = "<main>LEGEND® 😀</main><script data-x='>' src='/legend-public-cms.js?mode=public&amp;v=old#anchor' defer></script>";
        var bound = await compiler.BindRuntimeAssetsAsync(html, CancellationToken.None);
        Assert.Equal(html.Replace("v=old", "v=" + hash), bound);
        Assert.Equal(bound, await compiler.BindRuntimeAssetsAsync(bound, CancellationToken.None));
        var next = Asset("legend-public-cms.js", "successor package with different bytes");
        Assert.Contains("v=" + next, await compiler.BindRuntimeAssetsAsync(bound, CancellationToken.None));
    }
    [Fact]
    public async Task RebindsOnlyRuntimeJsonSlotsPreservingAuthoredBytesAndCompilerProvenance()
    {
        var hash = Asset("legend-public-tracking.js", "tracking");
        const string authored = "{\"title\":\"LEGEND® 😀\",\"trackingAsset\":\"/legend-public-tracking.js?v=authored\"}";
        var html = "<script type='application/json' id='legend-cms-published-document'>{\"document\":" + authored +
            ",\"runtime\":{\"trackingAsset\":\"/legend-public-tracking.js?v=old\",\"apiBase\":\"https://example.test\"}}</script>";
        var bound = await compiler.BindRuntimeAssetsAsync(html, CancellationToken.None);
        Assert.Equal(html.Replace("v=old", "v=" + hash), bound);
        Assert.Contains(authored, bound);
    }
    [Fact]
    public async Task LeavesCommentsRawTextAuthoredLinksAndExternalScriptsUntouched()
    {
        const string html = "<!-- <script src='/legend-public-cms.js'></script> -->" +
            "<script type='application/json'>{\"text\":\"<link rel='stylesheet' href='/site.css'>\"}</script>" +
            "<textarea><script src='/legend-public-cms.js'></script></textarea>" +
            "<a href='/site.css'>Download</a><script src='https://other.test/legend-public-cms.js?v=old'></script>";
        Assert.Equal(html, await compiler.BindRuntimeAssetsAsync(html, CancellationToken.None));
    }
    [Fact]
    public async Task VersionsTheModulesTransitiveInquiryDependency()
    {
        var module = Asset("public-inquiry-form.mjs", "module");
        var inquiry = Asset("legend-public-inquiry.js", "inquiry");
        var html = "<script type='module' src='/public-inquiry-form.mjs?v=old'></script>";
        var bound = await compiler.BindRuntimeAssetsAsync(html, CancellationToken.None);
        Assert.Contains("/public-inquiry-form.mjs?v=" + module, bound);
        Assert.Contains("data-inquiry-runtime=\"/legend-public-inquiry.js?v=" + inquiry + "\"", bound);
        Assert.Equal(bound, await compiler.BindRuntimeAssetsAsync(bound, CancellationToken.None));
    }
    [Fact]
    public async Task MissingOrRemovedPackageAssetNeverReusesAnOldHash()
    {
        const string html = "<script src='/legend-public-cms.js?v=old'></script>";
        await Assert.ThrowsAsync<InvalidOperationException>(() => compiler.BindRuntimeAssetsAsync(html, CancellationToken.None));
        Asset("legend-public-cms.js", "present");
        await compiler.BindRuntimeAssetsAsync(html, CancellationToken.None);
        File.Delete(Path.Combine(root, "WebsiteCompiler", "dist", "legend-public-cms.js"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => compiler.BindRuntimeAssetsAsync(html, CancellationToken.None));
    }
    [Fact]
    public async Task CancellationDoesNotServeAnUnboundPage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiler.BindRuntimeAssetsAsync("<script src='/legend-public-cms.js'></script>", cancellation.Token));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}

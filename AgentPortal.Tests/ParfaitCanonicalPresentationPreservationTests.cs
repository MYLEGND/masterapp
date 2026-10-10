using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using ParfaitApp.Services;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Pins the original Parfait visual source while moving infrastructure ownership.
/// Reviewed design changes must update the baseline explicitly.
/// </summary>
public sealed class ParfaitCanonicalPresentationPreservationTests
{
    [Theory]
    [InlineData("Views/Home/Terms.cshtml", "4de5260fa3f13dcb01c6a2398e6ffd214bfffc73")]
    [InlineData("Views/Home/Privacy.cshtml", "97c9060926b2d10f796ff274691b1a25ef2a7484")]
    [InlineData("Views/Shared/_Layout.cshtml", "b677436132bd4242bb52e70acf8a80a0cbac48dc")]
    [InlineData("Views/Shared/_ScopedWebsiteStoreLayout.cshtml", "64d65583c778f715af5bde408577a0fd7b155ffc")]
    [InlineData("Views/Home/Index.cshtml", "9abd970701d5f915d6f0431255869c68e479aed4")]
    [InlineData("Views/Store/Index.cshtml", "25ef81c066dd246e5b192b85bedd1381a3390ae5")]
    [InlineData("Views/Store/Cart.cshtml", "a635fd5d545607201878271fd5697fd667e745be")]
    [InlineData("Views/Store/Product.cshtml", "d320b04f64562136f6cd0e2dee5855d7c67c541c")]
    [InlineData("Views/Store/Checkout.cshtml", "49f11898cbdc507ae356741e88b7e8e9b8e70d61")]
    [InlineData("Views/Store/Success.cshtml", "7b1c36703db8aef40b34b7e5e54cad04e7761262")]
    [InlineData("wwwroot/css/site.css", "10f00e83c8e9b8a9e7428efebe01a40a0072903d")]
    [InlineData("wwwroot/css/home.css", "00f32a42b0f2f1ea1324c6f8897abe91a130ec94")]
    [InlineData("wwwroot/css/storefront.css", "eae71bcaef491f7d0f4ff0957b6e6b625fb034b2")]
    [InlineData("wwwroot/css/internal.css", "8945595525d5973c194367dc85bcb135abbd3fca")]
    [InlineData("wwwroot/css/parfait-agentportal-analytics.css", "a9801e8de3767e36e8fdfe287e4c26575f9700dd")]
    [InlineData("wwwroot/js/storefront.js", "ee0256aa52937cc25831c54b80141b768be72e96")]
    [InlineData("wwwroot/js/parfait-agentportal-analytics.js", "b6e665f146f93ffdd49251d2fd5c27fbb79221bc")]
    [InlineData("wwwroot/images/favicon/parfait-logo.png", "805aa941827b0bcdeab370d118bec2d297c2e317")]
    public void ParfaitVisualAssets_MatchPreMigrationGitBlob(string path, string expectedBlobSha)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "ParfaitApp", path.Replace('/', Path.DirectorySeparatorChar)));
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        var gitBlob = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, gitBlob, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, gitBlob, header.Length, bytes.Length);
        Assert.Equal(expectedBlobSha, Convert.ToHexString(SHA1.HashData(gitBlob)).ToLowerInvariant());
    }

    [Fact]
    public void ParfaitPreservesOriginalVisualLayout_RegardlessOfPublishedShell()
    {
        Assert.Equal("_Layout", CommerceStorefrontPresentation.ResolveLayout(null));
        Assert.Equal("_Layout", CommerceStorefrontPresentation.ResolveLayout(Context(true, null, null)));
        Assert.Equal("_Layout", CommerceStorefrontPresentation.ResolveLayout(Context(true, "<main>", "</main>")));
        Assert.Equal("_ScopedWebsiteStoreLayout",
            CommerceStorefrontPresentation.ResolveLayout(Context(false, "<main>", "</main>")));
        Assert.Equal("_Layout", CommerceStorefrontPresentation.ResolveLayout(Context(false, null, "</main>")));
    }

    [Fact]
    public void ParfaitLayoutSelection_HasOnlyOneInfrastructureOwner()
    {
        var viewStart = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ParfaitApp", "Views", "_ViewStart.cshtml"));
        Assert.Contains("Infrastructure.Commerce.CommerceStorefrontPresentation.ResolveLayout(commerce)", viewStart, StringComparison.Ordinal);
        Assert.DoesNotContain("commerce is { IsParfait: false }", viewStart, StringComparison.Ordinal);
    }

    private static CommerceStoreContext Context(bool isParfait, string? prefix, string? suffix) =>
        new(Guid.NewGuid(), null, null, "business", isParfait ? "parfait" : "other",
            "Test Store", "Store", "cart", "", "", "/store", "/store/cart",
            "/store/checkout", "/store/success", "cart-key", isParfait, "",
            null, null, new WebsiteDesignTheme(), prefix, suffix);

    private static string FindRepositoryRoot()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (IsRoot(githubWorkspace)) return Path.GetFullPath(githubWorkspace!);

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (IsRoot(current.FullName)) return current.FullName;
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static bool IsRoot(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) &&
        File.Exists(Path.Combine(dir, "MASTERAPP.sln")) &&
        Directory.Exists(Path.Combine(dir, "ParfaitApp"));
}

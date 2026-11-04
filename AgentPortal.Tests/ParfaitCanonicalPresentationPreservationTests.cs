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
/// Pins reviewed Parfait visual source while moving infrastructure ownership.
/// Reviewed design changes must update the baseline explicitly.
/// </summary>
public sealed partial class ParfaitCanonicalPresentationPreservationTests
{
    [Theory]
    [InlineData("Views/Home/Terms.cshtml", "4de5260fa3f13dcb01c6a2398e6ffd214bfffc73")]
    [InlineData("Views/Home/Privacy.cshtml", "97c9060926b2d10f796ff274691b1a25ef2a7484")]
    // Reviewed shared theme contrast and versioned canonical Store assets.
    [InlineData("Views/Shared/_Layout.cshtml", "80333ca55c42e8a8e8ec3dcf6c43ddb4c547f605")]
    [InlineData("Views/Shared/_ScopedWebsiteStoreLayout.cshtml", "0aba44aa950901314854d30b3b470d39c6185ec5")]
    [InlineData("Views/Home/Index.cshtml", "9abd970701d5f915d6f0431255869c68e479aed4")]
    // Reviewed Razor invocation and neutral no-photo presentation preserve real images.
    // Both authored stores and the original Parfait layout
    // render _CatalogCards; the original catalog loop is pinned separately below.
    [InlineData("Views/Store/Index.cshtml", "fd7f8ca10dd9854c1830d75c7cc599b90fb85320")]
    // Reviewed compact product/cart presentation and atomic browser cart ownership.
    [InlineData("Views/Store/Cart.cshtml", "6b8850bad083b8c5a13a8a741ef377e7d003baf6")]
    [InlineData("Views/Store/Product.cshtml", "84cc90c78062604b8ead7f40cf3facafdc7d0256")]
    // Reviewed embedded payment, same-intent recovery, compact receipt and optional
    // default-selected marketing preference; prior unsubscribe remains authoritative.
    [InlineData("Views/Store/Checkout.cshtml", "8c4479bef8ba0d4272331cb4252b68c97c8f7db3")]
    [InlineData("Views/Store/Success.cshtml", "0d2aa2e9953dd908ed26115f2e881789d58d47bf")]
    [InlineData("wwwroot/css/site.css", "3d421e41b9070fb34c0698262587db696502bdda")]
    [InlineData("wwwroot/css/home.css", "2b1c97a3012f53f9f242e4656822f28a6ae944a2")]
    // Reviewed responsive Store hierarchy and canonical editor theme tokens;
    // hidden summary rows and original Parfait identity remain preserved.
    // Reviewed recovered training hierarchy uses the same Store tokens.
    [InlineData("wwwroot/css/storefront.css", "763842a036ea6c2456f41d5b3933a32a24bb330d")]
    // Reviewed be032 refines internal header typography and responsive payment-provider cards.
    // Reviewed shared premium compact layout; single shell, page-only light mode and right navigation remain intact.
    // Reviewed native Team metrics use two phone/four desktop columns.
    [InlineData("wwwroot/css/internal.css", "9824817a29b228a616d7476f4be5e1fcb8e82efc")]
    [InlineData("wwwroot/js/storefront.js", "fe8a36a50964b16547aa674d5918d7c480dfe123")]
    // Reviewed internal health behavior: unavailable identity evidence must not render as zero.
    // CommerceCoreOwnershipContractTests covers the payload; public design assets remain pinned.
    [InlineData("wwwroot/images/favicon/parfait-logo.png", "805aa941827b0bcdeab370d118bec2d297c2e317")]
    public void ParfaitVisualAssets_MatchReviewedGitBlob(string path, string expectedBlobSha)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "ParfaitApp", path.Replace('/', Path.DirectorySeparatorChar)));
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        var gitBlob = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, gitBlob, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, gitBlob, header.Length, bytes.Length);
        Assert.Equal(expectedBlobSha, Convert.ToHexString(SHA1.HashData(gitBlob)).ToLowerInvariant());
    }

    [Fact]
    public void SharedCatalogRetainsOriginalProductMarkupAndCalculations()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ParfaitApp", "Views", "Store", "_CatalogCards.cshtml"));
        var loop = source[source.IndexOf("@foreach", StringComparison.Ordinal)..].Trim();
        // Reviewed catalog adds presentation targets and a neutral missing-photo state.
        loop = System.Text.RegularExpressions.Regex.Replace(loop, " data-cms-store-style=\"[a-z]+\"", "");
        Assert.Equal("c97d1cc1c182f73d840f1ab28be82892f59be3d499d90ef62d15224463eea7cd",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loop))).ToLowerInvariant());
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

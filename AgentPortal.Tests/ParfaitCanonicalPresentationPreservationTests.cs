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
    // Reviewed runtime-only change: version the inquiry module's child dependency;
    // the layout, styles and authored presentation remain unchanged.
    [InlineData("Views/Shared/_Layout.cshtml", "cd6992a223ce15675a287899cbe9fa3bb2946ec3")]
    [InlineData("Views/Shared/_ScopedWebsiteStoreLayout.cshtml", "64d65583c778f715af5bde408577a0fd7b155ffc")]
    [InlineData("Views/Home/Index.cshtml", "9abd970701d5f915d6f0431255869c68e479aed4")]
    // Reviewed Razor invocation and neutral no-photo presentation preserve real images.
    // Both authored stores and the original Parfait layout
    // render _CatalogCards; the original catalog loop is pinned separately below.
    [InlineData("Views/Store/Index.cshtml", "78af8fa9ae2a6584b3a5aae2b60bcd7be40a3e15")]
    // Reviewed script-only fix: failed or malformed quotes preserve cart state.
    [InlineData("Views/Store/Cart.cshtml", "cf1a1eb2a7229700b146b83ef558e8d8ed89ac96")]
    [InlineData("Views/Store/Product.cshtml", "dbc104981c030c42a61153a83e5b3e995781fd78")]
    // Reviewed payment-flow changes only: hosted-provider handoff retains the
    // original form/layout and legacy Square wording; cart clearing waits for
    // server-confirmed payment on success. All other visual pins stay unchanged.
    // Reviewed canonical inquiry correction: explicit consent replaces silent autosave.
    [InlineData("Views/Store/Checkout.cshtml", "c17f1ce21aac9ff68fe376227f5b941d1229cd59")]
    [InlineData("Views/Store/Success.cshtml", "c065e8d011b6fc8e74d6293aaf0496244915d75d")]
    [InlineData("wwwroot/css/site.css", "10f00e83c8e9b8a9e7428efebe01a40a0072903d")]
    [InlineData("wwwroot/css/home.css", "00f32a42b0f2f1ea1324c6f8897abe91a130ec94")]
    // Founder-requested, independently reviewed Store refinement (5c4617e4e):
    // compact catalog and management components; scoped palette retains Parfait identity.
    // Authored presentation remains authoritative. All other visual pins are unchanged.
    [InlineData("wwwroot/css/storefront.css", "c258f0f30cd691c9769679968028d2e653decd34")]
    // Founder-requested page-only light mode and right-side navigation; component palette retained.
    [InlineData("wwwroot/css/internal.css", "9568b4d1db7fbfbf4aa3f3e3cfaff144fc6c3a78")]
    [InlineData("wwwroot/js/storefront.js", "ee0256aa52937cc25831c54b80141b768be72e96")]
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

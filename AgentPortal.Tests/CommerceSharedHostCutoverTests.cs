using System;
using System.Collections.Generic;
using Infrastructure.WebsiteRuntime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceSharedHostCutoverTests
{
    private static readonly Guid Business = Guid.Parse("9be71dc0-459f-49a4-866e-3cdb7c9b341d");

    private static IConfiguration Configuration(bool enabled = true, string? host = "shopparfait.com",
        string? proof = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Commerce:SharedHostCutover:Enabled"] = enabled ? "true" : "false",
            ["Commerce:SharedHostCutover:BusinessId"] = Business.ToString(),
            ["Commerce:SharedHostCutover:Hostname"] = host,
            ["Commerce:SharedHostCutover:ReconciledManifestSha256"] = proof ?? new string('a', 64),
            ["Parfait:StorageRoot"] = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
        }).Build();

    [Fact]
    public void DisabledOrIncompleteProofNeverAuthorizesCheckout()
    {
        Assert.False(CommerceSharedHostCutoverGate.MayRoute(Configuration(false),
            Business, "shopparfait.com", "/store/checkout/pay", "POST"));
        Assert.False(CommerceSharedHostCutoverGate.IsConfigured(Configuration(true, proof: "missing")));
        Assert.False(CommerceSharedHostCutoverGate.IsConfigured(Configuration(true, host: "portal.mylegnd.com")));
    }

    [Theory]
    [InlineData("/store")]
    [InlineData("/store/cart")]
    [InlineData("/store/checkout")]
    [InlineData("/store/product/shirt")]
    [InlineData("/store/success")]
    [InlineData("/store-assets/css/storefront.css")]
    [InlineData("/uploads/parfait-products/shirt/front.png")]
    public void ValidatedBusinessMayReadOnlyItsOriginalStore(string path)
    {
        Assert.True(CommerceSharedHostCutoverGate.MayRoute(Configuration(),
            Business, "shopparfait.com", path, "GET"));
        Assert.False(CommerceSharedHostCutoverGate.MayRoute(Configuration(),
            Guid.NewGuid(), "shopparfait.com", path, "GET"));
        Assert.False(CommerceSharedHostCutoverGate.MayRoute(Configuration(),
            Business, "camoexterior.com", path, "GET"));
    }

    [Theory]
    [InlineData("/store/cart/items")]
    [InlineData("/store/checkout/quote")]
    [InlineData("/store/checkout/lead")]
    [InlineData("/store/checkout/pay")]
    public void OnlyExpectedCheckoutCommandsAreAdmitted(string path)
    {
        Assert.True(CommerceSharedHostCutoverGate.MayRoute(Configuration(),
            Business, "shopparfait.com", path, "POST"));
        Assert.False(CommerceSharedHostCutoverGate.MayRoute(Configuration(),
            Business, "shopparfait.com", path, "PUT"));
    }

    [Theory]
    [InlineData("/commerce/manage/products")]
    [InlineData("/store/s/other-business")]
    [InlineData("/store/product/a/b")]
    [InlineData("/store/checkout/pay")]
    [InlineData("/uploads/parfait-products/%2e%2e/secret.png")]
    public void PublicReadGateNeverOpensPrivateOrUnsafePaths(string path) =>
        Assert.False(CommerceSharedHostCutoverGate.MayRoute(Configuration(),
            Business, "shopparfait.com", path, "GET"));
}

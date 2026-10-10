using System;
using System.Collections.Generic;
using Infrastructure.WebsiteRuntime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceSharedHostPreviewGateTests
{
    private static IConfiguration Config(bool enabled = true, string? business = null, string? host = "shopparfait.com")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Commerce:SharedHostPreview:Enabled"] = enabled ? "true" : "false",
            ["Commerce:SharedHostPreview:BusinessId"] = business,
            ["Commerce:SharedHostPreview:Hostname"] = host
        }).Build();

    [Fact]
    public void PreviewHasNoImplicitActivation()
    {
        var id = Guid.NewGuid();
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(Config(false, id.ToString()),
            id, "shopparfait.com", "/store", "GET"));
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(Config(true),
            id, "shopparfait.com", "/store", "GET"));
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(Config(true, id.ToString(), "portal.mylegnd.com"),
            id, "portal.mylegnd.com", "/store", "GET"));
    }

    [Theory]
    [InlineData("/store")]
    [InlineData("/store/")]
    [InlineData("/store/cart")]
    [InlineData("/store/product/valid-item")]
    [InlineData("/store/terms")]
    [InlineData("/store/privacy")]
    [InlineData("/store-assets/css/storefront.css")]
    public void OnlyExactVerifiedBusinessCanPreviewPublicGetRoutes(string path)
    {
        var id = Guid.NewGuid();
        var config = Config(true, id.ToString());
        Assert.True(CommerceSharedHostPreviewGate.MayRoute(config,
            id, "shopparfait.com", path, "GET"));
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(config,
            Guid.NewGuid(), "shopparfait.com", path, "GET"));
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(config,
            id, "untrusted.example.com", path, "GET"));
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(config,
            id, "shopparfait.com", path, "POST"));
    }

    [Theory]
    [InlineData("/store/checkout")]
    [InlineData("/store/checkout/pay")]
    [InlineData("/store/cart/items")]
    [InlineData("/store/s/other")]
    [InlineData("/store/product/")]
    [InlineData("/store/product/a/b")]
    [InlineData("/commerce/manage/products")]
    [InlineData("/uploads/parfait-products/product/secret")]
    [InlineData("/internal")]
    [InlineData("/uploads/parfait-products/%2e%2e/sensitive.png")]
    [InlineData("/uploads/parfait-products/product-1/script.svg")]
    public void PreviewNeverAdmitsPaymentManagementOrMutableStorage(string path)
    {
        var id = Guid.NewGuid();
        Assert.False(CommerceSharedHostPreviewGate.MayRoute(Config(true, id.ToString()),
            id, "shopparfait.com", path, "GET"));
    }
}

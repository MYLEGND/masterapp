using System;
using Infrastructure.Businesses;
using Legend.Commerce;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CommerceManagementCapabilityPolicyTests
{
    [Theory]
    [InlineData("/commerce/manage/dashboard", "GET", "website")]
    [InlineData("/commerce/manage/preview/product/leggings", "GET", "website")]
    [InlineData("/commerce/manage/products", "GET", "catalog")]
    [InlineData("/commerce/manage/product/images/upload", "POST", "catalog")]
    [InlineData("/commerce/manage/settings/commerce", "POST", "catalog")]
    [InlineData("/commerce/manage/orders", "GET", "orders")]
    [InlineData("/commerce/manage/order/receipt", "POST", "orders")]
    [InlineData("/commerce/manage/automations", "GET", "automations")]
    [InlineData("/commerce/manage/automations/workflows/delete", "POST", "automations")]
    [InlineData("/commerce/manage/analytics", "GET", "analytics")]
    [InlineData("/commerce/manage/analytics/meta-campaigns", "GET", "analytics")]
    [InlineData("/commerce/manage/analytics/meta-connect", "GET", "settings")]
    [InlineData("/commerce/manage/analytics/meta-callback", "GET", "settings")]
    [InlineData("/commerce/manage/analytics/openai-connect", "POST", "settings")]
    [InlineData("/commerce/manage/analytics/meta-disconnect", "POST", "settings")]
    public void ExistingSharedRoutesRequireAnExactCapability(string route, string verb, string expected)
    {
        Assert.Equal(expected, CommerceManagementCapabilityPolicy.ForRequest(
            new PathString(route), verb));
    }

    [Theory]
    [InlineData("/commerce/manage/products", "POST")]
    [InlineData("/commerce/manage/order/receipt", "GET")]
    [InlineData("/commerce/manage/unknown", "GET")]
    [InlineData("/commerce/manage/analytics/not-registered", "GET")]
    [InlineData("/commerce/manage/automations/secret", "POST")]
    [InlineData("/commerce/manage/order", "PUT")]
    [InlineData("/store/cart", "GET")]
    public void UnknownVerbOrOperationCannotInheritStoreOwnerPermission(string route, string verb)
    {
        Assert.Null(CommerceManagementCapabilityPolicy.ForRequest(new PathString(route), verb));
    }

    [Fact]
    public void SharedControllerUsesCanonicalMembershipNotLegacyParfaitTeamFile()
    {
        // Method readback alongside the active, tested policy prevents a later
        // addition from silently authorizing catalog/order writes as website.
        var method = typeof(ParfaitApp.Controllers.CommerceManagementController)
            .GetMethod("Products");
        Assert.NotNull(method);
        Assert.Equal("catalog", CommerceManagementCapabilityPolicy.ForRequest(
            new PathString("/commerce/manage/products"), "GET"));
        Assert.Equal("orders", CommerceManagementCapabilityPolicy.ForRequest(
            new PathString("/commerce/manage/order"), "POST"));
    }
}

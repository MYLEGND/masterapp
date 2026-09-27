using System;
using System.Text.Json;
using Infrastructure.Commerce;
using Microsoft.AspNetCore.Http;
using Xunit;
using ParfaitApp.Services;
using ParfaitApp.Models;

namespace AgentPortal.Tests;

public sealed class CommerceSignalAttributionTests
{
    private static CommerceSignalContext Context(Guid store) => new(store, null, null, "business", "store", "Store",
        "https://shop.example.com/checkout", SessionId: "current-session", Oppref: "legacy-unscoped", Fbclid: "legacy-click");

    private static DefaultHttpContext Request(Guid store, string session = "current-session", int ageMinutes = 0)
    {
        var http = new DefaultHttpContext();
        var value = JsonSerializer.Serialize(new
        {
            scope = store.ToString("N"), sessionId = session,
            updatedAt = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes).ToUnixTimeMilliseconds(),
            attribution = new { utm_campaign = "first-ad", fbclid = "meta-click", oppref = "op-click" }
        });
        http.Request.Headers.Cookie = "pf_attribution=" + Uri.EscapeDataString(value);
        return http;
    }

    [Fact]
    public void CurrentValidatedStoreSessionRetainsItsCampaign()
    {
        var store = Guid.NewGuid();
        var value = CommerceSignalAttribution.Apply(Context(store), Request(store).Request);
        Assert.Equal("first-ad", value.UtmCampaign);
        Assert.Equal("meta-click", value.Fbclid);
        Assert.Equal("op-click", value.Oppref);
        Assert.Equal(store, value.CommerceBusinessId);
    }

    [Theory]
    [InlineData(true, "current-session", 0)]
    [InlineData(false, "previous-session", 0)]
    [InlineData(false, "current-session", 31)]
    [InlineData(false, "current-session", -5)]
    public void ForeignStaleOrFutureEnvelopeCannotSupplyCurrentPaidAttribution(bool foreign, string session, int age)
    {
        var store = Guid.NewGuid();
        var value = CommerceSignalAttribution.Apply(Context(store), Request(foreign ? Guid.NewGuid() : store, session, age).Request);
        Assert.Null(value.UtmCampaign);
        Assert.Null(value.Fbclid);
        Assert.Null(value.Oppref);
    }

    [Fact]
    public void FreshQueryReplacesWholeCampaignWithoutMixingProviderIdentifiers()
    {
        var store = Guid.NewGuid();
        var request = Request(store);
        request.Request.QueryString = new QueryString("?utm_campaign=new-ad&oppref=new-op-click");
        var value = CommerceSignalAttribution.Apply(Context(store), request.Request);
        Assert.Equal("new-ad", value.UtmCampaign);
        Assert.Equal("new-op-click", value.Oppref);
        Assert.Null(value.Fbclid);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"oppref\":\"old-first-touch\"}")]
    public void MalformedOrHistoricalUnscopedCookieCannotSupplyPaidAttribution(string cookie)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = "pf_attribution=" + Uri.EscapeDataString(cookie);
        var value = CommerceSignalAttribution.Apply(Context(Guid.NewGuid()), http.Request);
        Assert.Null(value.Oppref);
        Assert.Null(value.Fbclid);
    }
    [Theory]
    [InlineData(false, false, 0, true)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, false, 31, false)]
    [InlineData(true, false, 0, false)]
    public void PersistedOrderUsesValidatedStoreSessionNotLegacyOppref(bool legacyOnly, bool foreign, int age, bool accepted)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var store = Guid.NewGuid();
        var request = Request(foreign ? Guid.NewGuid() : store, ageMinutes: age);
        request.Request.Headers.Cookie = (legacyOnly ? string.Empty : request.Request.Headers.Cookie.ToString() + "; ") +
            "pf_sid=current-session; pf_oppref=legacy-foreign-click";
        var orders = new ParfaitOrderService(db);
        var customer = new ParfaitCheckoutCustomerRequest();
        var items = Array.Empty<ParfaitValidatedCartItem>();
        var pending = orders.CreatePendingOrder(store, customer, items, 0, null, null, 0, 0, 0, request);
        Assert.Equal(accepted ? "op-click" : null, pending.Oppref);
        var checkout = orders.BeginCheckoutPayment(store, "attempt-current", customer, items, 0, null, null, 0, 0, 0, request);
        Assert.Equal(accepted ? "op-click" : null, checkout.Order.Oppref);
    }

}

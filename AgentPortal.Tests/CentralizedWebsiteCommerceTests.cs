using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Businesses;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ParfaitApp.Services;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CentralizedWebsiteCommerceTests
{
    [Fact]
    public void StoreSettingsRoundTripThroughCanonicalWebsiteSanitizer()
    {
        var input = new WebsiteContentDocument
        {
            Store = new WebsiteStoreSettings
            {
                Enabled = true,
                NavigationLabel = "  Shop The Collection  "
            }
        };

        var clean = WebsiteContentSanitizer.Sanitize(input);

        Assert.True(clean.Store.Enabled);
        Assert.Equal("Shop The Collection", clean.Store.NavigationLabel);

        input.Store.NavigationLabel = new string('X', 80);
        clean = WebsiteContentSanitizer.Sanitize(input);
        Assert.Equal(40, clean.Store.NavigationLabel.Length);

        input.Store.NavigationLabel = "   ";
        clean = WebsiteContentSanitizer.Sanitize(input);
        Assert.Equal("Store", clean.Store.NavigationLabel);
    }

    [Fact]
    public async Task ProtectCommerceSignalUsesAgentMarketingOwnerNotCommerceMarketingOwner()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var service = new CommerceSignalService(db);
        var transactionBusinessId = Guid.NewGuid();
        var agentTrackingProfileId = Guid.NewGuid();

        var recorded = await service.RecordAsync(
            "Purchase",
            "order-protect-1",
            new CommerceSignalContext(
                transactionBusinessId,
                agentTrackingProfileId,
                Guid.NewGuid(),
                WebsiteEditorSiteKeys.Protect,
                "website-store-protect",
                "Protect Store",
                "https://protect.mylegnd.com/store/s/protect/checkout"),
            new CommerceSignalProduct("p1", "Product", "product", "M", 1, 2500),
            new CommerceSignalCustomer("Jane", "Buyer", "jane@example.test", "5551234567", "Phoenix", "AZ", "85001"),
            "PF-1");

        Assert.True(recorded);
        var row = await db.MetaSignalEvents.SingleAsync();
        Assert.Null(row.CommerceBusinessId);
        Assert.Equal(agentTrackingProfileId, row.AgentTrackingProfileId);
        Assert.Equal("Purchase", row.EventName);
        Assert.True(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(row.EventName, row.MetadataJson));

        using var metadata = JsonDocument.Parse(row.MetadataJson!);
        Assert.Equal(transactionBusinessId.ToString(), metadata.RootElement.GetProperty("commerceBusinessId").GetString());
        Assert.Equal(WebsiteEditorSiteKeys.Protect, metadata.RootElement.GetProperty("siteKey").GetString());
    }

    [Fact]
    public async Task FounderCommerceSignalKeepsFounderMarketingOwnerAndTransactionScopeSeparate()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var service = new CommerceSignalService(db);
        var transactionBusinessId = Guid.NewGuid();

        await service.RecordAsync(
            "InitiateCheckout",
            "checkout-founder-1",
            new CommerceSignalContext(
                transactionBusinessId,
                null,
                Guid.NewGuid(),
                WebsiteEditorSiteKeys.Legend,
                "website-store-founder",
                "LEGEND Store",
                "https://mylegnd.com/store/checkout"),
            new CommerceSignalProduct("p1", "Product", "product", "L", 1, 5000));

        var row = await db.MetaSignalEvents.SingleAsync();
        Assert.Null(row.CommerceBusinessId);
        Assert.Null(row.AgentTrackingProfileId);
        Assert.True(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(row.EventName, row.MetadataJson));

        using var metadata = JsonDocument.Parse(row.MetadataJson!);
        Assert.Equal(transactionBusinessId.ToString(), metadata.RootElement.GetProperty("commerceBusinessId").GetString());
        Assert.Equal(WebsiteEditorSiteKeys.Legend, metadata.RootElement.GetProperty("siteKey").GetString());
    }

    [Fact]
    public async Task BusinessCommerceSignalUsesBusinessMarketingOwnerAndDedupesByStableIdentity()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var service = new CommerceSignalService(db);
        var businessId = Guid.NewGuid();
        var context = new CommerceSignalContext(
            businessId,
            null,
            Guid.NewGuid(),
            WebsiteEditorSiteKeys.Business,
            "camo-exterior",
            "CAMO Exterior",
            "https://camoexterior.com/store/product/window-cleaning");

        var first = await service.RecordAsync(
            "AddToCart",
            "evt-1",
            context,
            new CommerceSignalProduct("p1", "Window Cleaning", "window-cleaning", "N/A", 1, 10000));
        var duplicate = await service.RecordAsync(
            "AddToCart",
            "evt-1",
            context,
            new CommerceSignalProduct("p1", "Window Cleaning", "window-cleaning", "N/A", 1, 10000));

        Assert.True(first);
        Assert.False(duplicate);
        var row = await db.MetaSignalEvents.SingleAsync();
        Assert.Equal(businessId, row.CommerceBusinessId);
        Assert.Null(row.AgentTrackingProfileId);
        Assert.True(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(row.EventName, row.MetadataJson));
    }

    [Fact]
    public async Task PurchaseWritesAnalyticsAndMetaAsOneCanonicalDedupedOutcome()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var service = new CommerceSignalService(db);
        var businessId = Guid.NewGuid();
        var context = new CommerceSignalContext(
            businessId,
            null,
            Guid.NewGuid(),
            WebsiteEditorSiteKeys.Business,
            "business-store",
            "Business Store",
            "https://business.example.com/store/checkout",
            SessionId: "session-1",
            VisitorId: "visitor-1");

        var first = await service.RecordAsync(
            "Purchase",
            "order-1001",
            context,
            new CommerceSignalProduct("p1", "Product", "product", "M", 1, 2500),
            new CommerceSignalCustomer("Jane", "Buyer", "jane@example.test", "5551234567", "Phoenix", "AZ", "85001"),
            "ORDER-1001");

        var duplicate = await service.RecordAsync(
            "Purchase",
            "order-1001",
            context,
            new CommerceSignalProduct("p1", "Product", "product", "M", 1, 2500),
            new CommerceSignalCustomer("Jane", "Buyer", "jane@example.test", "5551234567", "Phoenix", "AZ", "85001"),
            "ORDER-1001");

        Assert.True(first);
        Assert.False(duplicate);

        var analytics = await db.AnalyticsEvents.SingleAsync();
        var meta = await db.MetaSignalEvents.SingleAsync();

        Assert.Equal("Purchase", analytics.EventType);
        Assert.Equal(businessId, analytics.CommerceBusinessId);
        Assert.Null(analytics.AgentTrackingProfileId);
        Assert.Equal("session-1", analytics.SessionId);
        Assert.Equal("visitor-1", analytics.VisitorId);
        Assert.NotNull(analytics.ClientEventId);
        Assert.Equal("commerce-server-authority-v1", analytics.TrackingVersion);

        Assert.Equal("Purchase", meta.EventName);
        Assert.Equal(businessId, meta.CommerceBusinessId);
        Assert.Null(meta.AgentTrackingProfileId);
        Assert.StartsWith("commerce:", meta.MetaDeduplicationKey);
        Assert.True(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(meta.EventName, meta.MetadataJson));
    }

    [Fact]
    public async Task BusinessStoreResolvesFromAuthenticatedCustomDomainAtRootStorePath()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness
        {
            Key = "camo",
            DisplayName = "CAMO Exterior",
            LegalName = "CAMO Exterior",
            BusinessType = "Exterior Services",
            Status = "Active",
            IsActive = true
        };
        var state = new WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(business.Id),
            SiteKey = WebsiteEditorSiteKeys.Business,
            CommerceBusinessId = business.Id
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            DocumentJson = JsonSerializer.Serialize(new WebsiteContentDocument
            {
                Store = new WebsiteStoreSettings { Enabled = true, NavigationLabel = "Store" }
            })
        };
        state.PublishedVersionId = version.Id;
        db.AddRange(
            business,
            state,
            version,
            new WebsiteDomainBinding
            {
                CommerceBusinessId = business.Id,
                Hostname = "camoexterior.com",
                Status = "active",
                CertificateStatus = "active",
                LastCheckedUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var secret = new string('s', 40);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebsiteRouting:BridgeSecret"] = secret,
                ["WebsiteRouting:BridgeOriginHost"] = "masterapp-parfait.azurewebsites.net"
            })
            .Build();
        var domains = new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), configuration);
        var service = new CommerceStoreContextService(
            db,
            new CommerceBusinessScopeResolver(db),
            new ParfaitBusinessScopeService(db),
            domains,
            configuration);
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("masterapp-parfait.azurewebsites.net");
        http.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "camoexterior.com";
        http.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;

        var store = await service.ResolvePublicAsync(http, null);

        Assert.NotNull(store);
        Assert.Equal(business.Id, store!.CommerceBusinessId);
        Assert.Equal(WebsiteEditorSiteKeys.Business, store.WebsiteSiteKey);
        Assert.Equal("/store", store.StoreRootPath);
        Assert.Equal("/store/cart", store.CartPath);
        Assert.Equal("/store/checkout", store.CheckoutPath);
        Assert.Equal("https://camoexterior.com/store", await service.ResolveCanonicalPublicRootAsync(store));
    }

    [Fact]
    public async Task LegendStoreResolvesFromAuthenticatedLegendHostAtRootStorePath()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = new CommerceBusiness
        {
            Key = "website-store-legend",
            DisplayName = "LEGEND Store",
            LegalName = "LEGEND Store",
            BusinessType = "Ecommerce",
            Status = "Active",
            IsActive = true
        };
        var state = new WebsiteContentState
        {
            OwnerKey = WebsiteEditorSiteKeys.GlobalOwnerKey,
            SiteKey = WebsiteEditorSiteKeys.Legend,
            CommerceBusinessId = business.Id
        };
        var version = new WebsiteContentVersion
        {
            StateId = state.Id,
            DocumentJson = JsonSerializer.Serialize(new WebsiteContentDocument
            {
                Store = new WebsiteStoreSettings { Enabled = true, NavigationLabel = "Store" }
            })
        };
        state.PublishedVersionId = version.Id;
        db.AddRange(business, state, version);
        await db.SaveChangesAsync();

        var secret = new string('l', 40);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebsiteRouting:BridgeSecret"] = secret,
                ["WebsiteRouting:BridgeOriginHost"] = "masterapp-parfait.azurewebsites.net"
            })
            .Build();
        var service = new CommerceStoreContextService(
            db,
            new CommerceBusinessScopeResolver(db),
            new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), configuration),
            configuration);
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("masterapp-parfait.azurewebsites.net");
        http.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "mylegnd.com";
        http.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;

        var store = await service.ResolvePublicAsync(http, null);

        Assert.NotNull(store);
        Assert.Equal(WebsiteEditorSiteKeys.Legend, store!.WebsiteSiteKey);
        Assert.Equal("/store", store.StoreRootPath);
        Assert.Equal("https://mylegnd.com/store", await service.ResolveCanonicalPublicRootAsync(store));
    }

    [Fact]
    public async Task ProtectHostNeverSelectsAnAgentStoreWithoutExplicitScopedKey()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var secret = new string('p', 40);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebsiteRouting:BridgeSecret"] = secret,
                ["WebsiteRouting:BridgeOriginHost"] = "masterapp-parfait.azurewebsites.net"
            })
            .Build();
        var service = new CommerceStoreContextService(
            db,
            new CommerceBusinessScopeResolver(db),
            new ParfaitBusinessScopeService(db),
            new WebsiteDomainService(db, Mock.Of<IHttpClientFactory>(), configuration),
            configuration);
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("masterapp-parfait.azurewebsites.net");
        http.Request.Headers[WebsiteRequestHostResolver.OriginalHostHeader] = "protect.mylegnd.com";
        http.Request.Headers[WebsiteRequestHostResolver.BridgeSecretHeader] = secret;

        Assert.Null(await service.ResolvePublicAsync(http, null));
    }

}

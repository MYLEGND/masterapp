using System;
using System.Threading.Tasks;
using System.Text.Json;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
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
                "https://shop.example.test/store/s/protect/checkout"),
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
                "https://shop.example.test/store/s/legend/checkout"),
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
            "https://shop.example.test/store/s/camo-exterior/product/window-cleaning");

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
}

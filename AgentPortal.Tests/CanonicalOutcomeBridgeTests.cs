using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Commerce;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalOutcomeBridgeTests
{
    [Theory]
    [InlineData("AddToCart")]
    [InlineData("InitiateCheckout")]
    [InlineData("Purchase")]
    public async Task CommercePersistsCanonicalAnalyticsBeforeDerivingTheSameOutcome(string eventName)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var context = new CommerceSignalContext(businessId, null, Guid.NewGuid(),
            WebsiteEditorSiteKeys.Business, "test-store", "Test", "https://store.example.test/store/checkout",
            SessionId: "one-session", VisitorId: "one-visitor");
        var producer = new CommerceSignalService(db);
        await producer.RecordAsync(eventName, "first", context,
            new CommerceSignalProduct("p1", "Product", "product", "M", 1, 2500));
        var source = await db.AnalyticsEvents.SingleAsync();
        Assert.Empty(db.MetaSignalEvents); // Provider projection is not a source-persistence dependency.
        Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        var signal = await db.MetaSignalEvents.SingleAsync();
        Assert.Equal(eventName, source.EventType);
        Assert.Equal(eventName, signal.EventName);
        Assert.Equal(businessId, source.CommerceBusinessId);
        Assert.Equal(businessId, signal.CommerceBusinessId);
        using var metadata = JsonDocument.Parse(signal.MetadataJson!);
        Assert.Equal("analytics_events", metadata.RootElement.GetProperty("bridgeSource").GetString());
        Assert.Equal(source.Id, metadata.RootElement.GetProperty("sourceAnalyticsEventId").GetInt64());
        Assert.Equal(2500, metadata.RootElement.GetProperty("valueCents").GetInt32());
        Assert.True(MetaSignalSingleTruthPolicy.IsTrustedCommerceBridgeProducer(signal.TrafficType, signal.MetadataJson));
        Assert.True(MetaSignalSingleTruthPolicy.CanDispatchServerAuthority(signal.EventName, signal.MetadataJson));
        Assert.False(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        Assert.Single(await db.MetaSignalEvents.ToListAsync());

        // Distinct orders/actions in one minute and session must not collapse.
        await producer.RecordAsync(eventName, "second", context);
        foreach (var accepted in await db.AnalyticsEvents.AsNoTracking().ToListAsync())
            await MetaSignalAnalyticsBridge.PersistAsync(db, accepted);
        Assert.Equal(2, await db.AnalyticsEvents.CountAsync());
        Assert.Equal(2, await db.MetaSignalEvents.CountAsync());
        Assert.Equal(2, await db.MetaSignalEvents.Select(x => x.MetaDeduplicationKey).Distinct().CountAsync());
    }

    [Theory]
    [InlineData("protect", "AddToCart")]
    [InlineData("protect", "InitiateCheckout")]
    [InlineData("protect", "Purchase")]
    [InlineData("legend", "AddToCart")]
    [InlineData("legend", "InitiateCheckout")]
    [InlineData("legend", "Purchase")]
    [InlineData("business", "AddToCart")]
    [InlineData("business", "InitiateCheckout")]
    [InlineData("business", "Purchase")]
    public async Task CommerceLineageSurvivesCanonicalPersistenceAndProjection(string owner, string eventName)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var business = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var version = Guid.NewGuid();
        var order = Guid.NewGuid();
        var eventUtc = new DateTime(2026, 8, 19, 12, 30, 5, DateTimeKind.Utc);
        var site = owner == "protect" ? WebsiteEditorSiteKeys.Protect : owner == "legend" ? WebsiteEditorSiteKeys.Legend : WebsiteEditorSiteKeys.Business;
        var context = new CommerceSignalContext(business, owner == "protect" ? agent : null, version,
            site, "store", "Store", "https://store.example.test/store/checkout",
            SessionId: "session-origin", VisitorId: "visitor-origin", Referrer: "https://campaign.example.test/",
            UserAgent: "lineage-agent", ClientIpAddress: "192.0.2.5", Fbclid: "fb-click", Oppref: "openai-click",
            Fbc: "fb.1.click", Fbp: "fb.1.browser", EventUtc: eventUtc, WebsiteBindingId: "binding-origin",
            OrderId: order, PurchaseId: "payment-origin", OrderValueCents: 7500, Currency: "USD",
            UtmSource: "source", UtmMedium: "medium", UtmCampaign: "campaign", UtmId: "campaign-id", UtmContent: "content",
            MetaCampaignId: "meta-campaign", MetaAdSetId: "meta-adset", MetaAdId: "meta-ad");
        var producer = new CommerceSignalService(db);
        var items = new[] { new CommerceSignalProduct("sku-a", "A", "a", "M", 2, 5000), new CommerceSignalProduct("sku-b", "B", "b", "L", 1, 2500) };
        Assert.True(await producer.RecordAsync(eventName, "original-action", context, items: items, orderNumber: "order-number"));
        Assert.False(await producer.RecordAsync(eventName, "original-action", context, items: items));
        var source = Assert.Single(await db.AnalyticsEvents.ToListAsync());
        Assert.Equal(eventUtc, source.EventUtc);
        Assert.Equal(owner == "business" ? business : (Guid?)null, source.CommerceBusinessId);
        Assert.Equal(owner == "protect" ? agent : (Guid?)null, source.AgentTrackingProfileId);
        Assert.Equal(version, source.WebsiteContentVersionId);
        Assert.Equal("binding-origin", source.WebsiteBindingId);
        Assert.Equal("session-origin", source.SessionId);
        Assert.Equal("visitor-origin", source.VisitorId);
        Assert.Equal("openai-click", source.Oppref);
        Assert.Equal("fb-click", source.Fbclid);
        Assert.Equal("campaign", source.UtmCampaign);
        Assert.Equal("meta-campaign", source.MetaCampaignId);
        Assert.Equal("meta-adset", source.MetaAdSetId);
        Assert.Equal("meta-ad", source.MetaAdId);
        Assert.Equal("original-action", CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "originalEventIdentity"));
        Assert.Equal(order.ToString(), CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "orderId"));
        Assert.Equal("payment-origin", CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "purchaseId"));
        Assert.Equal(7500, CanonicalAdvertisingEventProjection.ReadInt64(source.MetadataJson, "valueCents"));
        Assert.Equal(3, CanonicalAdvertisingEventProjection.ReadInt64(source.MetadataJson, "quantity"));
        Assert.Equal("USD", CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "currency"));
        var canonicalId = CanonicalAdvertisingEventProjection.ResolveEventId(source);
        Assert.True(CanonicalAdvertisingEventProjection.CanProjectServer(source));
        Assert.Empty(db.MetaSignalEvents);
        Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        var signal = Assert.Single(await db.MetaSignalEvents.ToListAsync());
        Assert.Equal(canonicalId, signal.EventId);
        Assert.Equal(eventUtc, signal.CreatedUtc);
        Assert.Equal(source.CommerceBusinessId, signal.CommerceBusinessId);
        Assert.Equal(source.AgentTrackingProfileId, signal.AgentTrackingProfileId);
        Assert.Equal(source.WebsiteContentVersionId, signal.WebsiteContentVersionId);
        Assert.Equal(source.WebsiteBindingId, signal.WebsiteBindingId);
        Assert.Equal(source.SessionId, signal.SessionId);
        Assert.Equal(source.VisitorId, signal.VisitorId);
        Assert.Equal(source.Oppref, CanonicalAdvertisingEventProjection.ReadString(signal.MetadataJson, "oppref"));
        Assert.Equal(source.Host, signal.Host);
        Assert.Equal(source.Id, CanonicalAdvertisingEventProjection.ReadInt64(signal.MetadataJson, "sourceAnalyticsEventId"));
        Assert.Equal("original-action", CanonicalAdvertisingEventProjection.ReadString(signal.MetadataJson, "originalEventIdentity"));
        Assert.Equal(7500, CanonicalAdvertisingEventProjection.ReadInt64(signal.MetadataJson, "valueCents"));
        Assert.Equal(CanonicalAdvertisingEventProjection.ReadString(source.MetadataJson, "canonicalDeduplicationKey"), signal.MetaDeduplicationKey);
        using var productJson = JsonDocument.Parse(CanonicalAdvertisingEventProjection.ReadString(signal.MetadataJson, "items")!);
        Assert.Equal(2, productJson.RootElement.GetArrayLength());
        Assert.False(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        Assert.Single(db.AnalyticsEvents);
        Assert.Single(db.MetaSignalEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctBrowserActionsInOneMinuteKeepTheirOriginalEnvelopeIds(bool businessScope)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = Guid.NewGuid();
        var when = DateTime.UtcNow;
        for (var index = 0; index < 2; index++)
        {
            var id = Guid.NewGuid();
            var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
            {
                EventName = "page_view", EventUtc = when, SessionId = "same-session", VisitorId = "same-visitor",
                CommerceBusinessId = businessScope ? owner : null, AgentTrackingProfileId = businessScope ? null : owner,
                IsBrowserSignal = true, IsServerAuthority = false
            });
            source.ClientEventId = id;
            Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Accepted, await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, source));
            Assert.Equal(id, source.EventId);
            Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
            var signal = await db.MetaSignalEvents.SingleAsync(x => x.EventId == id.ToString("D"));
            Assert.Equal(source.Id, CanonicalAdvertisingEventProjection.ReadInt64(signal.MetadataJson, "sourceAnalyticsEventId"));
            Assert.False(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        }
        Assert.Equal(2, await db.AnalyticsEvents.CountAsync());
        Assert.Equal(2, await db.MetaSignalEvents.CountAsync());
    }

    [Fact]
    public void CheckoutCampaignAttributionPreservesFirstTouchWithoutSelectingAnOwner()
    {
        var owner = Guid.NewGuid();
        var context = new CommerceSignalContext(owner, null, null, WebsiteEditorSiteKeys.Business, "store", "Store", "https://store.example.test/checkout");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?utm_campaign=later&meta_ad_id=ad-one");
        http.Request.Headers.Cookie = "pf_attribution=" + Uri.EscapeDataString("{\"utm_campaign\":\"original\",\"utm_source\":\"meta\",\"fbclid\":\"click-origin\",\"commerceBusinessId\":\"forged\"}");
        var enriched = CommerceSignalAttribution.Apply(context, http.Request);
        Assert.Equal(owner, enriched.CommerceBusinessId);
        Assert.Equal("original", enriched.UtmCampaign);
        Assert.Equal("meta", enriched.UtmSource);
        Assert.Equal("click-origin", enriched.Fbclid);
        Assert.Equal("ad-one", enriched.MetaAdId);
        http.Request.Headers.Cookie = "pf_attribution=not-json";
        Assert.Equal("later", CommerceSignalAttribution.Apply(context, http.Request).UtmCampaign);
    }

    [Fact]
    public async Task UnpersistedEventCannotEnterSignalPersistenceBoundary()
    {
        using var db = ControllerTestHelpers.BuildDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MetaSignalAnalyticsBridge.PersistAsync(db, new AnalyticsEvent { EventType = "ViewContent" }));
        Assert.Empty(db.MetaSignalEvents);
    }

    [Fact]
    public async Task BrowserCannotClaimCommerceServerAuthorityUsingTrackingVersion()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "Purchase", IsBrowserSignal = true, IsServerAuthority = false,
            MetaServerAuthorityEligible = false, CommerceBusinessId = Guid.NewGuid()
        });
        source.TrackingVersion = "commerce-server-authority-v1";
        UnifiedAnalyticsWriter.Write(db, source);
        await db.SaveChangesAsync();
        // A caller may hold the tracked object, but only the durable source is authority.
        source.MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
            "Purchase", null, null,
            new { upstreamMetaEventId = "forged", metaDeduplicationKey = "forged" },
            false, true, true, true, "CommercePurchaseBridge");
        Assert.False(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
        Assert.Empty(db.MetaSignalEvents);
    }
}

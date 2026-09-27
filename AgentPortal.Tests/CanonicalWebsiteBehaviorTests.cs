using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Infrastructure.WebsiteEditing;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalWebsiteBehaviorTests
{
    [Theory]
    [InlineData("page_view", "page_view", true, null)]
    [InlineData("cta_click", "cta_click", true, null)]
    [InlineData("form_start", "form_start", true, null)]
    [InlineData("contact_input_started", "form_field_focus", true, null)]
    [InlineData("meaningful_scroll", "scroll_depth_50", true, null)]
    [InlineData("submit_attempt", "form_submit_attempt", true, null)]
    [InlineData("lead_created", "website_lead_submitted", false, "Lead")]
    [InlineData("qualified_lead", "QualifiedLead", false, "QualifiedLead")]
    [InlineData("appointment_booked", "appointment_booked", false, "AppointmentBooked")]
    [InlineData("appointment_completed", "appointment_completed", false, "AppointmentCompleted")]
    [InlineData("application_submitted", "ApplicationSubmitted", false, "ApplicationSubmitted")]
    [InlineData("outcome_completed", "PolicyIssued", false, "PolicyIssued")]
    [InlineData("payment_completed", "PolicyPaid", false, "PolicyPaid")]
    [InlineData("product_viewed", "ProductViewed", true, null)]
    [InlineData("add_to_cart", "AddToCart", false, "AddToCart")]
    [InlineData("checkout_started", "InitiateCheckout", false, "InitiateCheckout")]
    [InlineData("purchase_completed", "Purchase", false, "Purchase")]
    public void BehaviorMatrixKeepsPersistedNamesAndProviderProjectionSeparate(string key, string persisted, bool browser, string? conversion)
    {
        Assert.True(AnalyticsEventCatalog.TryGetBehavior(key, out var behavior));
        Assert.Equal(persisted, behavior.EventName);
        Assert.Equal(browser, behavior.BrowserAllowed);
        Assert.Equal(!browser, behavior.RequiresServerAuthority);
        Assert.Equal(conversion, behavior.ConversionEventName);
        Assert.True(AnalyticsEventCatalog.TryGet(persisted, out var definition));
        Assert.Equal(browser, definition.AllowBrowser);
        Assert.NotEmpty(behavior.AutomaticTrigger);
        foreach (var alias in behavior.Aliases.Append(persisted))
        {
            Assert.True(AnalyticsEventCatalog.TryGetBehavior(alias, out var historical));
            Assert.Equal(key, historical.Key);
            Assert.Equal(conversion, CanonicalAdvertisingEventProjection.ResolveEventName(new() { EventType = alias }));
        }
        if (conversion is not null)
        {
            Assert.NotNull(MarketingConversionDestinationCatalog.ResolveMeta(conversion));
            Assert.NotNull(MarketingConversionDestinationCatalog.ResolveOpenAi(conversion));
            Assert.Empty(behavior.EditorTriggers);
            foreach (var trigger in new[] { "click", "submission_saved", "booking_confirmed", "payment_confirmed" })
                Assert.Throws<ArgumentException>(() => WebsiteSignalBindingPolicy.Validate([
                    new() { EventName = persisted, Trigger = trigger, DeliveryMode = "destinations" }]));
            Assert.Throws<InvalidOperationException>(() => UnifiedEventMapper.ToAnalytics(new()
                { EventName = persisted, IsBrowserSignal = true, IsServerAuthority = true }));
        }
    }

    [Fact]
    public async Task PresentationChangesPreserveActionBindingDedupeAndMetaIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = Guid.NewGuid();
        var context = new UnifiedEventContext
        {
            EventName = "form_start", ActionKey = "business_form_start", ButtonLabel = "Book My Estimate",
            IsBrowserSignal = true, IsServerAuthority = false, SessionId = "session", VisitorId = "visitor",
            WebsiteContentVersionId = Guid.NewGuid(), WebsiteBindingId = "binding", CommerceBusinessId = Guid.NewGuid(),
            PageKey = "contact", Host = "business.example"
        };
        var first = UnifiedEventMapper.ToAnalytics(context); first.ClientEventId = id;
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Accepted, await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, first));
        var renamed = UnifiedEventMapper.ToAnalytics(context with { ButtonLabel = "Reserve a Consultation" }); renamed.ClientEventId = id;
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Duplicate, await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, renamed));
        Assert.Equal(first.EventId, renamed.EventId);
        Assert.Equal("business_form_start", CanonicalAdvertisingEventProjection.ReadString(renamed.MetadataJson, "actionKey"));
        Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, first));
        Assert.False(await MetaSignalAnalyticsBridge.PersistAsync(db, first));
        var signal = await db.MetaSignalEvents.SingleAsync();
        Assert.Equal(id.ToString("D"), signal.EventId);
        Assert.Equal("LeadFormStart", signal.EventName);
        Assert.Equal(first.WebsiteBindingId, signal.WebsiteBindingId);
        Assert.Equal("business_form_start", CanonicalAdvertisingEventProjection.ReadString(signal.MetadataJson, "actionKey"));
        Assert.Single(db.AnalyticsEvents);
    }

    [Fact]
    public void HistoricalPublishedBindingsRemainReadableButCannotCreateManualConfirmedOutcomes()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var document = new WebsiteContentDocument { Elements = new() { ["old-form"] = new() {
            Signals = [new() { EventName = "Lead", Trigger = "submission_saved", DeliveryMode = "meta" },
                new() { EventName = "LeadFormStart", Trigger = "form_started", DeliveryMode = "meta" }] } } };
        Assert.Throws<ArgumentException>(() => WebsiteContentSanitizer.Sanitize(document));
        var json = JsonSerializer.Serialize(document, options);
        var read = WebsiteContentSanitizer.ReadPersisted(json, options);
        var binding = Assert.Single(read.Elements["old-form"].Signals);
        Assert.Equal("form_start", binding.EventName);
        Assert.Equal("form_start", binding.ActionKey);
        Assert.Equal("destinations", binding.DeliveryMode);
        Assert.Equal(2, document.Elements["old-form"].Signals.Count);
    }

    [Fact]
    public void BrowserObservationsNeverCountAsConfirmedLeads()
    {
        Assert.All(AnalyticsEventCatalog.Definitions.Where(d => d.AllowBrowser),
            definition => Assert.False(definition.CountsAsConfirmedLead, definition.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoricalReceiptKeepsIdentityWhileNewActionConflictsAreRejected(bool historical)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var id = Guid.NewGuid();
        var context = new UnifiedEventContext { EventName = "cta_click", ActionKey = "contact", IsBrowserSignal = true,
            AgentTrackingProfileId = Guid.NewGuid(), Host = "protect.mylegnd.com", PageKey = "contact" };
        var prior = UnifiedEventMapper.ToAnalytics(context); prior.ClientEventId = id;
        if (historical) prior.MetadataJson = "{}";
        Assert.Equal(UnifiedAnalyticsWriter.BrowserWriteResult.Accepted, await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, prior));
        var retry = UnifiedEventMapper.ToAnalytics(context with { ActionKey = historical ? "contact" : "quote" }); retry.ClientEventId = id;
        Assert.Equal(historical ? UnifiedAnalyticsWriter.BrowserWriteResult.Duplicate : UnifiedAnalyticsWriter.BrowserWriteResult.Conflict,
            await UnifiedAnalyticsWriter.PersistBrowserEventAsync(db, retry));
        Assert.Single(db.AnalyticsEvents);
        if (historical) { Assert.Equal(prior.EventId, retry.EventId); Assert.Equal("{}", prior.MetadataJson); }
    }

    [Fact]
    public void ProviderCatalogsCannotOwnWebsiteBehaviorOrInterpretAnotherProvidersTruth()
    {
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!;
        var catalog = File.ReadAllText(Path.Combine(root, "SHARED/Analytics/AnalyticsEventCatalog.cs"));
        Assert.DoesNotContain("MetaSignalEventCatalog", catalog);
        Assert.DoesNotContain("MarketingConversionDestinationCatalog", catalog);
        var policy = File.ReadAllText(Path.Combine(root, "Infrastructure/WebsiteEditing/WebsiteSignalBinding.cs"));
        Assert.DoesNotContain("Options => MetaSignalEventCatalog", policy);
        var projection = File.ReadAllText(Path.Combine(root, "Infrastructure/Analytics/CanonicalAdvertisingEventProjection.cs"));
        Assert.DoesNotContain("MetaSignalAnalyticsAliasCatalog", projection);
        Assert.DoesNotContain("OpenAiMeasurementEventMapper", projection);
        var metaRuntime = File.ReadAllText(Path.Combine(root, "SHARED/WebsitePlatform/meta-signal-intelligence.js"));
        Assert.DoesNotContain("trackConfiguredEvent", metaRuntime);
    }
}

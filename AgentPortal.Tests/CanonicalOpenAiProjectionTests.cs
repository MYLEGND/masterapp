using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Infrastructure.Leads;
using Shared.Meta;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalOpenAiProjectionTests
{
    [Fact]
    public async Task OwnerResolutionUsesPermanentProfilesAndRejectsMixedOrSpoofedScope()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founder = Profile("founder@example.com");
        var agent = Profile("agent@example.com");
        var business = new CommerceBusiness { Id = Guid.NewGuid(), IsActive = true, Status = "Active" };
        db.AddRange(founder, agent, business);
        await db.SaveChangesAsync();
        var config = Config();
        async Task<MarketingOwnerScope?> Owner(AnalyticsEvent row) => await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, config, row);
        Assert.Equal(MarketingOwnerScope.Founder, await Owner(new() { AgentTrackingProfileId = founder.Id }));
        Assert.Equal(MarketingOwnerScope.Agent(agent.Id), await Owner(new() { AgentTrackingProfileId = agent.Id }));
        Assert.Equal(MarketingOwnerScope.Business(business.Id), await Owner(new() { CommerceBusinessId = business.Id }));
        Assert.Null(await Owner(new() { AgentTrackingProfileId = agent.Id, CommerceBusinessId = business.Id }));
        Assert.Null(await Owner(new() { AgentSlug = agent.Slug, CommerceBusinessId = business.Id }));
        Assert.Null(await Owner(new() { AgentTrackingProfileId = Guid.NewGuid(), MetadataJson = "{\"siteKey\":\"LegendWebsite\"}" }));
        Assert.Null(await Owner(new() { MetadataJson = "{\"siteKey\":\"LegendWebsite\"}", Host = "foreign.example" }));
        Assert.Equal(MarketingOwnerScope.Founder, await Owner(new() { Id = 42, Host = "mylegnd.com", MetadataJson = "{\"siteKey\":\"" + WebsiteEditorSiteKeys.Legend + "\"}" }));
    }

    [Theory]
    [InlineData("agent", true, null, true)]
    [InlineData("agent", false, null, true)]
    [InlineData("founder", true, null, true)]
    [InlineData("founder", false, null, true)]
    [InlineData("business", true, null, true)]
    [InlineData("business", false, null, true)]
    [InlineData("agent", false, "linked", true)]
    [InlineData("agent", false, "first_link", true)]
    [InlineData("agent", false, "unverified", true)]
    [InlineData("agent", false, "changed_account", true)]
    [InlineData("agent", false, "changed_datasource", true)]
    [InlineData("agent", false, "changed_pixel", true)]
    [InlineData("agent", true, null, false)]
    [InlineData("founder", true, null, false)]
    [InlineData("business", true, null, false)]
    public async Task OpenAiDispatchDoesNotRequireMetaAndRetainsResultAndRetryIdentity(string ownerType, bool accepted, string? historicalMode, bool humanEvidence)
    {
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(sqlite).Options);
        await db.Database.EnsureCreatedAsync();
        var agent = Profile(ownerType == "founder" ? "founder@example.com" : "agent@example.com");
        db.AgentTrackingProfiles.Add(agent);
        var business = new CommerceBusiness { Id = Guid.NewGuid(), Key = "test-business", DisplayName = "Test Business" };
        if (ownerType == "business") db.CommerceBusinesses.Add(business);
        await db.SaveChangesAsync();
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "Purchase", EventUtc = DateTime.UtcNow.AddMinutes(-1), AgentTrackingProfileId = ownerType == "business" ? null : agent.Id,
            CommerceBusinessId = ownerType == "business" ? business.Id : null,
            AgentSlug = ownerType == "business" ? null : agent.Slug, Host = "shop.example.com", UserAgent = "Mozilla/5.0", IpAddress = "1.2.3.4", IsServerAuthority = true, IsBrowserSignal = false,
            MetaServerAuthorityEligible = true, Oppref = "oppref-first-touch"
        });
        if (humanEvidence) { source.HumanInteractionCount = 3; source.EngagedMilliseconds = 15000; source.DwellMilliseconds = 20000; source.ScrollPercent = 50; }
        source.MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson("Purchase", null, "session", new
        {
            canonicalOutcomeEventId = "original-order-event", canonicalDeduplicationKey = "order:confirmed:1",
            measurementConsentAllowed = true, orderId = "order-1", purchaseId = "purchase-1", valueCents = 8900, currency = "USD",
            items = new[] { new { ProductId = "sku-1", ProductName = "Item", Quantity = 2, ValueCents = 8900 } }
        }, false, true, true, false);
        UnifiedAnalyticsWriter.Write(db, source);
        await db.SaveChangesAsync();
        var originalId = source.EventId;
        var owner = ownerType == "business" ? MarketingOwnerScope.Business(business.Id) : ownerType == "founder" ? MarketingOwnerScope.Founder : MarketingOwnerScope.Agent(agent.Id);
        if (historicalMode is not null)
        {
            db.MarketingDestinationDeliveries.Add(new MarketingDestinationDelivery
            {
                OwnerKey = owner.Key, OwnerType = owner.OwnerType, AgentTrackingProfileId = owner.AgentTrackingProfileId,
                Provider = MarketingDestinationKeys.OpenAi, Channel = "server", CanonicalSource = nameof(MetaSignalEvent),
                AnalyticsEventId = historicalMode == "first_link" ? null : source.Id, CanonicalEventId = "historical-issued-event", CanonicalEventName = "Purchase",
                ProviderEventName = "order_created", PixelId = historicalMode == "changed_pixel" ? "previous-pixel" : "openai-pixel", Status = "pending",
                AdvertiserAccountId = historicalMode == "unverified" ? null : historicalMode == "changed_account" ? "previous-account" : "openai-account",
                ConversionDataSourceId = historicalMode == "unverified" ? null : historicalMode == "changed_datasource" ? "previous-datasource" : "openai-datasource"
            });
            if (historicalMode == "first_link")
                db.MetaSignalEvents.Add(new MetaSignalEvent
                {
                    EventId = "historical-issued-event", EventName = "Purchase", CreatedUtc = source.EventUtc,
                    AgentTrackingProfileId = source.AgentTrackingProfileId,
                    MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { sourceAnalyticsEventId = source.Id })
                });
            await db.SaveChangesAsync();
        }
        var connections = new Mock<IOpenAiAdsAccountConnectionAuthority>(MockBehavior.Strict);
        connections.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "openai-account", "Account", "admin", "approved", "api_key", null, null,
            Array.Empty<string>(), "openai-pixel", "openai-datasource", true, true, DateTime.UtcNow, null, DateTime.UtcNow));
        connections.Setup(x => x.GetSecretsAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSecrets(null, "owner-secret"));
        var conversions = new List<OpenAiConversionEvent>();
        var capi = new Mock<IOpenAiConversionsApiService>(MockBehavior.Strict);
        capi.Setup(x => x.SendAsync("openai-pixel", "owner-secret", It.IsAny<OpenAiConversionEvent>(), false, It.IsAny<CancellationToken>()))
            .Callback<string, string, OpenAiConversionEvent, bool, CancellationToken>((_, _, evt, _, _) => conversions.Add(evt))
            .ReturnsAsync(new OpenAiConversionsApiResult(true, accepted, !accepted, accepted ? 200 : 503,
                accepted ? "sent" : "retryable_failure", ProviderReceiptJson: accepted ? "{\"received\":1}" : "{\"error\":\"unavailable\"}"));
        var services = new ServiceCollection().AddSingleton(db).AddSingleton<IConfiguration>(Config())
            .AddSingleton(connections.Object).AddSingleton(capi.Object).BuildServiceProvider();
        var dispatcher = new OpenAiConversionDispatcherHostedService(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OpenAiConversionDispatcherHostedService>.Instance);
        async Task Dispatch() => await (Task)typeof(OpenAiConversionDispatcherHostedService).GetMethod("DispatchBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dispatcher, new object[] { CancellationToken.None })!;
        await Dispatch();
        var receipt = Assert.Single(await db.MarketingDestinationDeliveries.ToListAsync());
        Assert.Equal(historicalMode is null ? nameof(AnalyticsEvent) : nameof(MetaSignalEvent), receipt.CanonicalSource);
        if (historicalMode is not null)
        {
            // Historical rows are a read-only fence, never adopted/retried by the
            // canonical AnalyticsEvent dispatcher (including uncertain pending receipts).
            Assert.Equal("pending", receipt.Status);
            Assert.Equal("historical-issued-event", receipt.CanonicalEventId);
            Assert.Equal(historicalMode == "first_link" ? (long?)null : source.Id, receipt.AnalyticsEventId);
            Assert.Null(receipt.MetaSignalEventId);
            Assert.Null(receipt.ProviderReceiptJson);
            Assert.Empty(conversions);
            await Dispatch();
            Assert.Empty(conversions);
            Assert.Single(await db.MarketingDestinationDeliveries.ToListAsync());
            Assert.Equal(originalId, Assert.Single(await db.AnalyticsEvents.ToListAsync()).EventId);
            return;
        }
        if (!humanEvidence)
        {
            Assert.Equal("blocked_human_evidence", receipt.Status);
            Assert.Equal(0, receipt.AttemptCount); Assert.Empty(conversions);
            Assert.Null(receipt.ProviderReceiptJson);
            return;
        }
        Assert.Equal(source.Id, receipt.AnalyticsEventId);
        Assert.Equal(owner.Key, receipt.OwnerKey);
        Assert.Equal("openai-account", receipt.AdvertiserAccountId);
        Assert.Equal("openai-datasource", receipt.ConversionDataSourceId);
        Assert.Equal(accepted ? "sent" : "retryable", receipt.Status);
        Assert.NotNull(receipt.ProviderReceiptJson);
        Assert.Equal(originalId, Assert.Single(await db.AnalyticsEvents.ToListAsync()).EventId);
        if (historicalMode == "first_link")
            Assert.Equal(Assert.Single(await db.MetaSignalEvents.ToListAsync()).Id, receipt.MetaSignalEventId);
        else
            Assert.Empty(await db.MetaSignalEvents.ToListAsync());
        Assert.Empty(await db.MarketingConnections.ToListAsync());
        var converted = Assert.Single(conversions);
        Assert.Equal(historicalMode is null ? "original-order-event" : "historical-issued-event", converted.Id);
        Assert.Equal("oppref-first-touch", converted.Oppref);
        Assert.Equal(8900, converted.Data.Amount);
        Assert.Equal("sku-1", Assert.Single(converted.Data.Contents!).Id);
        Assert.Equal(new DateTimeOffset(source.EventUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), converted.TimestampMs);
        if (!accepted) { receipt.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); }
        await Dispatch();
        Assert.Equal(accepted ? 1 : 2, conversions.Count);
        Assert.All(conversions, x => Assert.Equal(converted.Id, x.Id));
        Assert.Single(await db.MarketingDestinationDeliveries.ToListAsync());
        if (!accepted && historicalMode != "first_link")
        {
            // Reciprocal independence: OpenAI has failed twice; the same accepted source still reaches Meta.
            Assert.True(await MetaSignalAnalyticsBridge.PersistAsync(db, source));
            var metaCapi = new Mock<IMetaConversionsApiService>(MockBehavior.Strict);
            metaCapi.Setup(x => x.SendEventAsync(It.Is<MetaConversionsApiEventRequest>(request =>
                request.EventName == "Purchase" && request.PixelId == "meta-pixel"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetaConversionsApiResult { Attempted = true, Sent = true, Status = "sent", EventsReceived = 1 });
            var pixels = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);
            pixels.Setup(x => x.ResolveForOwnerAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new ResolvedMetaPixelContext
            {
                PixelId = "meta-pixel", AccessToken = "meta-secret",
                PixelOwnerType = ownerType == "business" ? MetaPixelOwnerTypes.Business : ownerType == "founder" ? MetaPixelOwnerTypes.Agency : MetaPixelOwnerTypes.Agent
            });
            var metaServices = new ServiceCollection().AddSingleton(db).AddSingleton<IConfiguration>(Config())
                .AddSingleton(metaCapi.Object).AddSingleton(pixels.Object).BuildServiceProvider();
            var metaDispatcher = new MetaSignalOutcomeDispatcherHostedService(metaServices.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new MetaSignalIntelligenceOptions { Enabled = true, SendServerEvents = true }), NullLogger<MetaSignalOutcomeDispatcherHostedService>.Instance);
            await (Task)typeof(MetaSignalOutcomeDispatcherHostedService).GetMethod("DispatchBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(metaDispatcher, new object[] { CancellationToken.None })!;
            Assert.True(Assert.Single(await db.MetaSignalEvents.ToListAsync()).MetaServerSent);
            Assert.Equal("retryable", receipt.Status);
            Assert.Single(await db.AnalyticsEvents.ToListAsync());
            metaCapi.VerifyAll();
            pixels.VerifyAll();
        }
    }

    [Fact]
    public void BrowserSpoofCannotAcquireServerMeasurementEligibility()
    {
        var source = new AnalyticsEvent { Id = 1, EventId = Guid.NewGuid(), EventType = "Purchase", MetadataJson = "{\"isBrowserSignal\":true,\"isServerAuthority\":true,\"measurementServerAuthorityEligible\":true}" };
        Assert.False(CanonicalAdvertisingEventProjection.CanProjectServer(source));
        source.MetadataJson = "{\"payload\":{\"isServerAuthority\":true,\"measurementServerAuthorityEligible\":true},\"isServerAuthority\":false}";
        Assert.False(CanonicalAdvertisingEventProjection.CanProjectServer(source));
    }

    [Fact]
    public void NeutralEligibilityIsIndependentOfMetaAndInternalTrafficIsRejected()
    {
        var source = new AnalyticsEvent
        {
            Id = 1, EventId = Guid.NewGuid(), EventType = "Lead",
            MetadataJson = "{\"isServerAuthority\":true,\"isBrowserSignal\":false,\"measurementServerAuthorityEligible\":true,\"metaServerAuthorityEligible\":false}"
        };
        Assert.True(CanonicalAdvertisingEventProjection.CanProjectServer(source));
        source.IsInternal = true;
        Assert.False(CanonicalAdvertisingEventProjection.CanProjectServer(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedLeadIdentityIsStableAndHistoricalDestinationIdIsPreserved(bool business)
    {
        var lead = new WebsiteLead { LeadId = Guid.NewGuid(), CommerceBusinessId = business ? Guid.NewGuid() : null };
        var identity = CanonicalLeadEventIdentity.Resolve(lead);
        Assert.Equal(identity, CanonicalLeadEventIdentity.Resolve(lead));
        Assert.Equal(identity, CanonicalAdvertisingEventProjection.ScopeEventId(lead.CommerceBusinessId, identity));
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventId = identity, EventName = "website_lead_submitted", IsServerAuthority = true,
            IsBrowserSignal = false, MetaServerAuthorityEligible = true, CommerceBusinessId = lead.CommerceBusinessId,
            Metadata = new { LeadId = lead.LeadId }
        });
        Assert.Equal(identity, CanonicalAdvertisingEventProjection.ResolveEventId(source));
        lead.MetadataJson = MetaLeadTrackingJson.Upsert(null, state => state.EventId = "already-issued-browser-event");
        Assert.Equal("already-issued-browser-event", CanonicalLeadEventIdentity.Resolve(lead));
    }

    [Fact]
    public async Task HealthDoesNotReportReadyWithoutAnOwnerDataSource()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = MarketingOwnerScope.Founder;
        var connections = new Mock<IOpenAiAdsAccountConnectionAuthority>();
        connections.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "account", "Account", "admin", "approved", "api_key", null, null,
            Array.Empty<string>(), "pixel", null, false, true, DateTime.UtcNow, null, DateTime.UtcNow));
        connections.Setup(x => x.GetSecretsAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSecrets());
        using var http = new System.Net.Http.HttpClient();
        Assert.Equal("data_source_not_configured", (await new OpenAiMeasurementHealthService(db, connections.Object, http).GetAsync(owner)).Status);
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Founder:Upn"] = "founder@example.com" }).Build();
    private static AgentTrackingProfile Profile(string upn) => new() { Id = Guid.NewGuid(), AgentUpn = upn, AgentUserId = Guid.NewGuid().ToString(), Slug = Guid.NewGuid().ToString("N"), Status = "Active", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
}

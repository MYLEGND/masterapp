using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Businesses;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ParfaitApp.Services;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ScopedMarketingMeasurementTests
{
    [Theory]
    [InlineData("founder-protect")]
    [InlineData("founder-legend")]
    [InlineData("agent-a")]
    [InlineData("agent-b")]
    [InlineData("business-a")]
    [InlineData("business-b")]
    [InlineData("parfait")]
    public async Task RealOwnerAndConnectionAuthoritiesSelectBothPixelsForExactlyOneWebsite(string site)
    {
        using var f = new Fixture();
        await f.SeedAsync();
        var owner = await f.ResolveAsync(site);
        Assert.Equal(f.Expected(site), owner);
        var config = await f.Browser.GetAsync(owner);
        Assert.Equal("meta-" + owner!.Key, config.MetaPixelId);
        Assert.Equal("openai-" + owner.Key, config.OpenAiPixelId);
        Assert.Equal(owner.Key, config.MarketingOwnerKey);
        Assert.True(config.MetaTestMode);
        var publicJson = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("secret", publicJson);
        Assert.DoesNotContain("private-test-code", publicJson);
        Assert.DoesNotContain("AccessToken", publicJson);
        foreach (var foreign in f.Owners.Where(o => o != owner))
        {
            Assert.DoesNotContain("meta-" + foreign.Key, publicJson);
            Assert.DoesNotContain("openai-" + foreign.Key, publicJson);
        }
        await f.Meta.DisconnectAsync(owner);
        var openAi = await f.OpenAi.GetAsync(owner);
        await f.OpenAi.DisconnectAsync(owner, openAi.Revision);
        var disconnected = await f.Browser.GetAsync(owner);
        Assert.Null(disconnected.MetaPixelId);
        Assert.Null(disconnected.OpenAiPixelId);
        foreach (var foreign in f.Owners.Where(o => o != owner))
        {
            Assert.NotNull((await f.Browser.GetAsync(foreign)).MetaPixelId);
            Assert.NotNull((await f.Browser.GetAsync(foreign)).OpenAiPixelId);
        }
    }

    [Fact]
    public async Task UnknownSlugHostAndStaleBusinessNeverReceiveFounderOrForeignConfiguration()
    {
        using var f = new Fixture();
        await f.SeedAsync();
        var request = Http("protect.mylegnd.com", "/a/missing");
        Assert.Null(await ProtectWebsiteOwnerResolver.ResolveAsync(request, f.Profiles, "founder@example.com"));
        request = Http("unknown.example", "/");
        Assert.Null(await f.PublicScopes.ResolveAsync(request, WebsiteEditorSiteKeys.Business));
        Assert.Null(await f.Stores.ResolvePublicAsync(request, null));
        Assert.Null(await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(f.Db, f.Configuration,
            new PublicWebsiteRuntimeScope(WebsiteEditorSiteKeys.Business, WebsiteEditorSiteKeys.BusinessOwnerKey(Guid.NewGuid()), Guid.NewGuid(), null, "unknown.example")));
        var closed = await f.Browser.GetAsync(null);
        Assert.Null(closed.MetaPixelId); Assert.Null(closed.OpenAiPixelId); Assert.Null(closed.MarketingOwnerKey);
        var business = await f.PublicScopes.ResolveAsync(Http("business-a.example", "/unpublished"), WebsiteEditorSiteKeys.Business);
        Assert.NotNull(business);
        Assert.False(PublicWebsiteRuntimeScopeResolver.IsPublishedPath(business!, "/unpublished"));
    }

    [Fact]
    public async Task ConfigurationIsNotDeliveryAndForeignReceiptsCannotMakeHealthGreen()
    {
        using var f = new Fixture(); await f.SeedAsync();
        var owner = f.Expected("business-a"); var foreign = f.Expected("business-b");
        var service = new MarketingMeasurementEvidenceService(f.Db, f.Configuration, f.Meta, f.OpenAi);
        var empty = await service.GetAsync(owner);
        Assert.False(empty.ReceivingEvents); Assert.Equal(0, empty.Meta.Accepted); Assert.Equal(0, empty.OpenAi.Accepted);
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", CommerceBusinessId = foreign.CommerceBusinessId,
            IsServerAuthority = true, EventUtc = DateTime.UtcNow, Host = "business-b.example" });
        UnifiedAnalyticsWriter.Write(f.Db, source); await f.Db.SaveChangesAsync();
        f.Db.MarketingDestinationDeliveries.Add(new() {
            OwnerKey = owner.Key, OwnerType = owner.OwnerType, CommerceBusinessId = owner.CommerceBusinessId,
            AnalyticsEventId = source.Id, Provider = "openai", Channel = "server", CanonicalSource = nameof(AnalyticsEvent),
            Status = "sent", LastHttpStatusCode = 200, AttemptCount = 1, SentUtc = DateTime.UtcNow,
            PixelId = "openai-" + owner.Key, AdvertiserAccountId = "account-" + owner.Key, ConversionDataSourceId = "data-" + owner.Key });
        f.Db.MetaSignalEvents.Add(new() { EventId = "foreign-link", EventName = "Purchase", CommerceBusinessId = owner.CommerceBusinessId,
            CreatedUtc = DateTime.UtcNow, MetaServerSent = true, MetadataJson = JsonSerializer.Serialize(new {
                sourceAnalyticsEventId = source.Id, metaServerAttempted = true, metaServerEventsReceived = 1, metaServerPixelId = "meta-" + owner.Key }) });
        await f.Db.SaveChangesAsync();
        var result = await service.GetAsync(owner);
        Assert.False(result.ReceivingEvents); Assert.Equal(0, result.Meta.Accepted); Assert.Equal(0, result.OpenAi.Accepted);
        Assert.False(result.Meta.AttributionObserved); Assert.False(result.OpenAi.AttributionObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedDeliveryDoesNotInventPaidAttribution(bool paid)
    {
        using var f = new Fixture(); await f.SeedAsync(); var owner = f.Expected("agent-a");
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", AgentTrackingProfileId = owner.AgentTrackingProfileId, IsServerAuthority = true,
            Fbclid = paid ? "valid-meta-click" : null, Oppref = paid ? "valid-openai-click" : null,
            EventUtc = DateTime.UtcNow, Host = "protect.mylegnd.com" });
        UnifiedAnalyticsWriter.Write(f.Db, source); await f.Db.SaveChangesAsync();
        f.Db.MarketingDestinationDeliveries.Add(new() { OwnerKey = owner.Key, OwnerType = owner.OwnerType,
            AgentTrackingProfileId = owner.AgentTrackingProfileId, AnalyticsEventId = source.Id,
            Provider = "openai", Channel = "server", CanonicalSource = nameof(AnalyticsEvent),
            Status = "sent", LastHttpStatusCode = 200, AttemptCount = 1, SentUtc = DateTime.UtcNow,
            PixelId = "openai-" + owner.Key, AdvertiserAccountId = "account-" + owner.Key, ConversionDataSourceId = "data-" + owner.Key });
        f.Db.MetaSignalEvents.Add(new() { EventId = "own", EventName = "Purchase", AgentTrackingProfileId = owner.AgentTrackingProfileId,
            CreatedUtc = DateTime.UtcNow, MetaServerSent = true, MetadataJson = JsonSerializer.Serialize(new {
                sourceAnalyticsEventId = source.Id, metaServerAttempted = true, metaServerEventsReceived = 1, metaServerPixelId = "meta-" + owner.Key,
                metaServerDispatchedUtc = DateTime.UtcNow }) });
        await f.Db.SaveChangesAsync();
        var result = await new MarketingMeasurementEvidenceService(f.Db, f.Configuration, f.Meta, f.OpenAi).GetAsync(owner);
        Assert.True(result.ReceivingEvents); Assert.Equal(1, result.Meta.Accepted); Assert.Equal(1, result.OpenAi.Accepted);
        Assert.Equal(paid, result.Meta.AttributionObserved); Assert.Equal(paid, result.OpenAi.AttributionObserved);
        Assert.Equal("events_received", result.Meta.AcceptanceEvidence);
        Assert.Equal("http_accepted", result.OpenAi.AcceptanceEvidence);
    }

    [Fact]
    public async Task ReconnectionCannotBorrowOldPixelReceiptsOrOldFailures()
    {
        using var f = new Fixture(); await f.SeedAsync(); var owner = f.Expected("agent-a");
        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", AgentTrackingProfileId = owner.AgentTrackingProfileId, IsServerAuthority = true,
            EventUtc = DateTime.UtcNow, Host = "protect.mylegnd.com" });
        UnifiedAnalyticsWriter.Write(f.Db, source); await f.Db.SaveChangesAsync();
        f.Db.MarketingDestinationDeliveries.Add(new() { OwnerKey = owner.Key, OwnerType = owner.OwnerType,
            AgentTrackingProfileId = owner.AgentTrackingProfileId, AnalyticsEventId = source.Id,
            Provider = "openai", Channel = "server", CanonicalSource = nameof(AnalyticsEvent),
            PixelId = "openai-" + owner.Key, AdvertiserAccountId = "account-" + owner.Key, ConversionDataSourceId = "data-" + owner.Key,
            Status = "sent", LastHttpStatusCode = 200, AttemptCount = 1, SentUtc = DateTime.UtcNow });
        f.Db.MetaSignalEvents.Add(new() { EventId = "previous-pixel", EventName = "Purchase", AgentTrackingProfileId = owner.AgentTrackingProfileId,
            CreatedUtc = DateTime.UtcNow, MetaServerSent = true, MetadataJson = JsonSerializer.Serialize(new {
                sourceAnalyticsEventId = source.Id, metaServerAttempted = true, metaServerEventsReceived = 1,
                metaServerPixelId = "meta-" + owner.Key }) });
        await f.Db.SaveChangesAsync();
        var evidence = new MarketingMeasurementEvidenceService(f.Db, f.Configuration, f.Meta, f.OpenAi);
        Assert.Equal(1, (await evidence.GetAsync(owner)).OpenAi.Accepted);
        Assert.Equal(1, (await evidence.GetAsync(owner)).Meta.Accepted);
        await f.Meta.SaveSettingsAsync(owner, "987654", null, null, (await f.Meta.GetStatusAsync(owner))!.Revision);
        var old = await f.OpenAi.GetAsync(owner);
        await f.OpenAi.BindVerifiedAsync(owner, new("new-account", "New account", "admin", "approved", "api_key",
            PixelId: "new-pixel", ConversionDataSourceId: "new-source"), new("management-secret", "conversion-secret"), old.Revision);
        f.Db.MarketingDestinationDeliveries.Add(new() { OwnerKey = owner.Key, Provider = "openai", Channel = "server",
            PixelId = "new-pixel", AdvertiserAccountId = "new-account", ConversionDataSourceId = "new-source",
            Status = "permanent_failure", CreatedUtc = DateTime.UtcNow.AddDays(-60) });
        await f.Db.SaveChangesAsync();
        var current = await evidence.GetAsync(owner);
        Assert.True(current.ReceivingEvents); Assert.Equal(0, current.Meta.Accepted); Assert.Equal(0, current.OpenAi.Accepted);
        using var client = new HttpClient(new UnavailableHandler());
        var health = await new OpenAiMeasurementHealthService(f.Db, f.OpenAi, client).GetAsync(owner);
        Assert.Equal("configured_no_delivery_evidence", health.Status);
        Assert.Equal(0, health.SentDeliveries); Assert.Equal(0, health.FailedDeliveries);
        Assert.False(health.ProviderMonitoringAvailable);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task ExpiredOAuthCannotReportConnectedOrSupplyCapiFallback()
    {
        using var f = new Fixture(); var owner = MarketingOwnerScope.Founder;
        await f.Meta.SaveAdsAsync(owner, new() { AccessToken = "expired-secret", AccessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(-1) });
        Assert.Null(await f.Meta.GetAdsAsync(owner)); Assert.Null(await f.Meta.GetCapiTokenAsync(owner));
        var revision = (await f.Meta.GetStatusAsync(owner))!.Revision;
        await f.Meta.SaveSettingsAsync(owner, "123", null, "independent-capi-secret", revision);
        Assert.Null(await f.Meta.GetAdsAsync(owner));
        Assert.Equal("independent-capi-secret", await f.Meta.GetCapiTokenAsync(owner));
    }

    private static DefaultHttpContext Http(string host, string path)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https";
        context.Request.Host = new HostString(host); context.Request.Path = path; context.Request.Method = "GET";
        context.Request.Headers.Origin = "https://" + host; return context;
    }
    private sealed class Fixture : IDisposable
    {
        public MasterAppDbContext Db { get; } = ControllerTestHelpers.BuildDb();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Founder:Upn"] = "founder@example.com" }).Build();
        public AgentTrackingResolver Profiles { get; }
        public MarketingConnectionStore Meta { get; }
        public OpenAiAdsAccountConnectionAuthority OpenAi { get; }
        public MarketingBrowserConfigurationService Browser { get; }
        public PublicWebsiteRuntimeScopeResolver PublicScopes { get; }
        public CommerceStoreContextService Stores { get; }
        private readonly MarketingCredentialProtector protector;
        private readonly Dictionary<string,AgentTrackingProfile> agents = new();
        private readonly Dictionary<string,CommerceBusiness> businesses = new();
        public IEnumerable<MarketingOwnerScope> Owners => new[] { MarketingOwnerScope.Founder,
            MarketingOwnerScope.Agent(agents["agent-a"].Id), MarketingOwnerScope.Agent(agents["agent-b"].Id) }
            .Concat(businesses.Values.Select(b => MarketingOwnerScope.Business(b.Id)));
        public Fixture()
        {
            var protection = new EphemeralDataProtectionProvider(); protector = new(protection);
            Meta = new(Db, protector); OpenAi = new(Db, protector);
            Profiles = new(Db, NullLogger<AgentTrackingResolver>.Instance);
            var pixels = new MetaPixelResolutionService(Configuration, Db, Profiles,
                new AgentMarketingProfileService(Db, Meta, protection), Meta, NullLogger<MetaPixelResolutionService>.Instance);
            Browser = new(pixels, OpenAi, NullLogger<MarketingBrowserConfigurationService>.Instance);
            var domains = new WebsiteDomainService(Db, Mock.Of<IHttpClientFactory>(), Configuration);
            PublicScopes = new(Db, domains, Configuration);
            Stores = new(Db, new CommerceBusinessScopeResolver(Db), new ParfaitBusinessScopeService(Db), domains, Configuration);
        }
        public async Task SeedAsync()
        {
            foreach (var key in new[] { "founder", "agent-a", "agent-b" }) {
                var profile = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = key, AgentUpn = key + "@example.com", Slug = key, Status = "Active" };
                agents[key] = profile; Db.Add(profile);
            }
            foreach (var key in new[] { "business-a", "business-b", "parfait" }) {
                var b = new CommerceBusiness { Id = Guid.NewGuid(), Key = key, DisplayName = key, IsActive = true, Status = "Active" };
                businesses[key] = b; Db.Add(b);
                Db.Add(new CommerceBusinessStorefrontSettings { CommerceBusinessId = b.Id });
                Db.Add(new WebsiteDomainBinding { CommerceBusinessId = b.Id, Hostname = key + ".example", Status = "active", CertificateStatus = "active", LastCheckedUtc = DateTime.UtcNow });
                var state = new WebsiteContentState { SiteKey = WebsiteEditorSiteKeys.Business, OwnerKey = WebsiteEditorSiteKeys.BusinessOwnerKey(b.Id), CommerceBusinessId = b.Id, PublishedVersionId = Guid.NewGuid() };
                Db.Add(state); Db.Add(new WebsiteContentVersion { Id = state.PublishedVersionId.Value, StateId = state.Id,
                    DocumentJson = "{}", CompiledPagesJson = "{\"pages\":{\"/\":{\"html\":\"published\"}}}" });
            }
            await Db.SaveChangesAsync();
            foreach (var owner in Owners) {
                await Meta.ImportProfileAsync(owner, "meta-" + owner.Key, "meta-secret-" + owner.Key, "private-test-code");
                await OpenAi.BindVerifiedAsync(owner, new("account-" + owner.Key, "Account", "admin", "approved", "api_key",
                    PixelId: "openai-" + owner.Key, ConversionDataSourceId: "data-" + owner.Key), new("management-secret", "conversion-secret"));
            }
        }
        public MarketingOwnerScope Expected(string site) => site.StartsWith("founder", StringComparison.Ordinal) ? MarketingOwnerScope.Founder
            : site.StartsWith("agent", StringComparison.Ordinal) ? MarketingOwnerScope.Agent(agents[site].Id) : MarketingOwnerScope.Business(businesses[site].Id);
        public async Task<MarketingOwnerScope?> ResolveAsync(string site)
        {
            if (site == "founder-protect" || site.StartsWith("agent", StringComparison.Ordinal)) {
                var scoped = await ProtectWebsiteOwnerResolver.ResolveAsync(Http("protect.mylegnd.com", site == "founder-protect" ? "/" : "/a/" + site), Profiles, "founder@example.com");
                return scoped is null ? null : await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(Db, Configuration, scoped.Profile);
            }
            if (site == "parfait") {
                var context = await Stores.ResolvePublicAsync(Http("shopparfait.com", "/store"), null);
                return context is null ? null : await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(Db, Configuration, context, "shopparfait.com");
            }
            var scope = await PublicScopes.ResolveAsync(Http(site == "founder-legend" ? "mylegnd.com" : site + ".example", "/"), site == "founder-legend" ? WebsiteEditorSiteKeys.Legend : WebsiteEditorSiteKeys.Business);
            return scope is null ? null : await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(Db, Configuration, scope);
        }
        public void Dispose() { protector.Dispose(); Db.Dispose(); }
    }
}

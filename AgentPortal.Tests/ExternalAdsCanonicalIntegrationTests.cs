using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ExternalAdsCanonicalIntegrationTests
{
    [Fact]
    public void ExternalAdsReportingUsesProviderAccountCalendarInsteadOfUtcCalendar()
    {
        var range = TimeRangeRequest.FromPreset(
            "custom",
            new DateTime(2026, 10, 6, 0, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 6, 8, 30, 0, DateTimeKind.Utc));

        var (from, to) = MarketingExternalAdsReportingService.ProviderDateWindow(range, "America/Phoenix");

        Assert.Equal(new DateTime(2026, 10, 5), from);
        Assert.Equal(new DateTime(2026, 10, 6), to);
    }


    [Fact]
    public void GoogleAndTikTokClickIdsUseCanonicalEventMetadataAndChannelResolution()
    {
        var google = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "Purchase",
            EventUtc = DateTime.UtcNow,
            IsServerAuthority = true,
            Gclid = "google-click-1",
            Metadata = new { valueCents = 12500, clientUserId = "customer-google" }
        });
        var tiktok = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "Purchase",
            EventUtc = DateTime.UtcNow,
            IsServerAuthority = true,
            Ttclid = "tiktok-click-1",
            Metadata = new { valueCents = 9900, clientUserId = "customer-tiktok" }
        });

        Assert.Equal("google-click-1", CanonicalAdvertisingEventProjection.ReadString(google.MetadataJson, "gclid"));
        Assert.Equal("tiktok-click-1", CanonicalAdvertisingEventProjection.ReadString(tiktok.MetadataJson, "ttclid"));
        Assert.Equal(MarketingChannels.GoogleAds, CanonicalMarketingOutcomeProjection.ChannelFor(google));
        Assert.Equal(MarketingChannels.TikTokAds, CanonicalMarketingOutcomeProjection.ChannelFor(tiktok));
        Assert.Equal(TrafficType.PaidAds, TrafficAttribution.Classify(null, null, null, null, gclid: "google-click-1"));
        Assert.Equal(TrafficType.PaidAds, TrafficAttribution.Classify(null, null, null, null, ttclid: "tiktok-click-1"));
    }

    [Fact]
    public async Task ExternalAdsOAuthPersistsEncryptedScopedCredentialsAndRequiresAccountSelectionWhenAmbiguous()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
        {
            ["GoogleAds:ClientId"] = "google-client",
            ["GoogleAds:ClientSecret"] = "google-secret",
            ["GoogleAds:DeveloperToken"] = "developer-token",
            ["GoogleAds:RedirectUri"] = "https://portal.example.test/business/external-ads/callback",
            ["TikTokAds:AppId"] = "tiktok-app",
            ["TikTokAds:AppSecret"] = "tiktok-secret",
            ["TikTokAds:AdvertiserAuthorizationUrl"] = "https://ads.tiktok.com/marketing_api/auth?app_id=tiktok-app",
            ["TikTokAds:BusinessAdvertiserAuthorizationUrl"] = "https://ads.tiktok.com/marketing_api/auth?app_id=tiktok-app&business=1",
            ["TikTokAds:RedirectUri"] = "https://portal.example.test/business/external-ads/callback"
        }).Build();

        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.Host == "oauth2.googleapis.com")
                return Json("""{"access_token":"google-access","refresh_token":"google-refresh","expires_in":3600,"scope":"https://www.googleapis.com/auth/adwords"}""");
            if (uri.Host == "googleads.googleapis.com")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("google-access", request.Headers.Authorization?.Parameter);
                Assert.Contains("developer-token", request.Headers.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
                return Json("""{"resourceNames":["customers/1111111111","customers/2222222222"]}""");
            }
            if (uri.Host == "business-api.tiktok.com")
                return Json("""{"code":0,"data":{"access_token":"tiktok-access","advertiser_ids":["3333333333","4444444444"]}}""");
            throw new InvalidOperationException("Unexpected provider request: " + uri);
        });

        var service = new MarketingExternalAdsOAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            configuration,
            new EphemeralDataProtectionProvider(),
            store,
            NullLogger<MarketingExternalAdsOAuthService>.Instance);

        var googleConnect = service.BuildConnectUrl(
            owner,
            MarketingDestinationKeys.Google,
            "/business/example/analytics",
            "https://portal.example.test/business/external-ads/callback");
        var googleState = QueryHelpers.ParseQuery(new Uri(googleConnect).Query)["state"].ToString();
        var google = await service.CompleteCallbackAsync(
            MarketingDestinationKeys.Google, "google-code", googleState);
        Assert.True(google.Connection.Connected);
        Assert.True(google.Connection.RequiresAccountSelection);
        Assert.False(google.Connection.Ready);
        Assert.Equal(2, google.Accounts.Count);
        Assert.Equal("google-refresh",
            (await store.GetProviderCredentialAsync(owner, MarketingDestinationKeys.Google))!.PrimarySecret);

        var tikTokConnect = service.BuildConnectUrl(
            owner,
            MarketingDestinationKeys.TikTok,
            "/business/example/analytics",
            "https://portal.example.test/business/external-ads/callback");
        var tikTokState = QueryHelpers.ParseQuery(new Uri(tikTokConnect).Query)["state"].ToString();
        var tiktok = await service.CompleteCallbackAsync(
            MarketingDestinationKeys.TikTok, "tiktok-code", tikTokState);
        Assert.True(tiktok.Connection.Connected);
        Assert.True(tiktok.Connection.RequiresAccountSelection);
        Assert.False(tiktok.Connection.Ready);
        Assert.Equal(2, tiktok.Accounts.Count);
        Assert.Equal("tiktok-access",
            (await store.GetProviderCredentialAsync(owner, MarketingDestinationKeys.TikTok))!.PrimarySecret);

        var rows = await db.MarketingConnections.AsNoTracking().ToListAsync();
        Assert.DoesNotContain("google-refresh", rows.Single(x => x.Provider == MarketingDestinationKeys.Google).AdsAccessTokenCiphertext!);
        Assert.DoesNotContain("tiktok-access", rows.Single(x => x.Provider == MarketingDestinationKeys.TikTok).AdsAccessTokenCiphertext!);
    }

    [Fact]
    public async Task ExternalMeasurementMappingIsAccountBoundAndWrongGoogleCustomerIsRejected()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());

        await store.SaveProviderCredentialAsync(
            owner,
            MarketingDestinationKeys.Google,
            "google-refresh",
            MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
            "{}",
            null,
            "1111111111",
            "Google Ads 1111111111");

        var connection = await store.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google);
        var saved = await store.SaveProviderMeasurementConfigurationAsync(
            owner,
            new MarketingProviderMeasurementUpdate(
                MarketingDestinationKeys.Google,
                null,
                "click",
                [
                    new MarketingProviderEventMapping(
                        "QualifiedLead",
                        "QualifiedLead",
                        "customers/1111111111/conversionActions/77")
                ],
                connection.Revision));

        Assert.True(saved.MappingReady);
        Assert.Single(saved.Mappings);
        Assert.Equal("customers/1111111111/conversionActions/77", saved.Mappings[0].DestinationId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveProviderMeasurementConfigurationAsync(
                owner,
                new MarketingProviderMeasurementUpdate(
                    MarketingDestinationKeys.Google,
                    null,
                    "click",
                    [
                        new MarketingProviderEventMapping(
                            "PolicyPaid",
                            "PolicyPaid",
                            "customers/2222222222/conversionActions/88")
                    ],
                    saved.Revision)));

        await store.SelectProviderAccountAsync(
            owner,
            MarketingDestinationKeys.Google,
            "2222222222",
            "Google Ads 2222222222");

        var invalidated = await store.GetProviderMeasurementConfigurationAsync(
            owner, MarketingDestinationKeys.Google);
        Assert.False(invalidated.MappingReady);
        Assert.Equal("measurement_mapping_required", invalidated.Status);
    }

    [Fact]
    public async Task GoogleConversionTransportUsesCanonicalGclidValueTimeAndStableOrderId()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        await store.SaveProviderCredentialAsync(
            owner,
            MarketingDestinationKeys.Google,
            "google-refresh",
            MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken,
            "{}",
            null,
            "1111111111",
            "Google Ads 1111111111");
        var connection = await store.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google);
        var measurement = await store.SaveProviderMeasurementConfigurationAsync(
            owner,
            new MarketingProviderMeasurementUpdate(
                MarketingDestinationKeys.Google,
                null,
                "click",
                [new("QualifiedLead", "QualifiedLead", "customers/1111111111/conversionActions/77")],
                connection.Revision));

        string? uploadBody = null;
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.Host == "oauth2.googleapis.com")
                return Json("""{"access_token":"google-access","expires_in":3600}""");
            if (uri.Host == "googleads.googleapis.com" && uri.AbsolutePath.EndsWith(":uploadClickConversions", StringComparison.Ordinal))
            {
                uploadBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"results":[{"gclid":"gclid-canonical-1"}]}""");
            }
            throw new InvalidOperationException("Unexpected provider request: " + uri);
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["GoogleAds:ClientId"] = "google-client",
                ["GoogleAds:ClientSecret"] = "google-secret",
                ["GoogleAds:DeveloperToken"] = "developer-token",
                ["GoogleAds:ApiVersion"] = "v25"
            }).Build();
        var oauth = new MarketingExternalAdsOAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            configuration,
            new EphemeralDataProtectionProvider(),
            store,
            NullLogger<MarketingExternalAdsOAuthService>.Instance);
        var transport = new ExternalAdsConversionApiService(
            new HttpClient(handler), oauth, store, configuration);

        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "QualifiedLead",
            EventUtc = new DateTime(2026, 10, 6, 4, 15, 0, DateTimeKind.Utc),
            AgentTrackingProfileId = owner.AgentTrackingProfileId,
            IsServerAuthority = true,
            Gclid = "gclid-canonical-1",
            Metadata = new { valueCents = 25000, currency = "USD" }
        });
        var mapping = Assert.Single(measurement.Mappings);
        var result = await transport.SendAsync(
            owner, connection, measurement, mapping, source, "gclid-canonical-1");

        Assert.True(result.Sent);
        Assert.NotNull(uploadBody);
        using var payload = JsonDocument.Parse(uploadBody!);
        var conversion = payload.RootElement.GetProperty("conversions")[0];
        Assert.Equal("customers/1111111111/conversionActions/77",
            conversion.GetProperty("conversionAction").GetString());
        Assert.Equal("gclid-canonical-1", conversion.GetProperty("gclid").GetString());
        Assert.Equal(250m, conversion.GetProperty("conversionValue").GetDecimal());
        Assert.Equal("USD", conversion.GetProperty("currencyCode").GetString());
        Assert.Equal(CanonicalAdvertisingEventProjection.ResolveEventId(source),
            conversion.GetProperty("orderId").GetString());
        Assert.Contains("+00:00", conversion.GetProperty("conversionDateTime").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TikTokConversionTransportUsesCanonicalTtclidEventSourceAndStableEventId()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        await store.SaveProviderCredentialAsync(
            owner,
            MarketingDestinationKeys.TikTok,
            "tiktok-access",
            MarketingProviderAuthorizationMethods.TikTokOAuthAccessToken,
            """{"authorizedAccountIds":["3333333333"]}""",
            null,
            "3333333333",
            "TikTok Ads 3333333333");
        var connection = await store.GetProviderConnectionAsync(owner, MarketingDestinationKeys.TikTok);
        var measurement = await store.SaveProviderMeasurementConfigurationAsync(
            owner,
            new MarketingProviderMeasurementUpdate(
                MarketingDestinationKeys.TikTok,
                "crm-event-source-1",
                "crm",
                [new("QualifiedLead", "QualifiedLeadReady")],
                connection.Revision,
                "tiktok-events-token"));

        string? eventBody = null;
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.Host == "business-api.tiktok.com" && uri.AbsolutePath.EndsWith("/event/track/", StringComparison.Ordinal))
            {
                eventBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.True(request.Headers.TryGetValues("Access-Token", out var values));
                Assert.Contains("tiktok-events-token", values);
                return Json("""{"code":0,"message":"OK","data":{}}""");
            }
            throw new InvalidOperationException("Unexpected provider request: " + uri);
        });
        var configuration = new ConfigurationBuilder().Build();
        var oauth = new MarketingExternalAdsOAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            configuration,
            new EphemeralDataProtectionProvider(),
            store,
            NullLogger<MarketingExternalAdsOAuthService>.Instance);
        var transport = new ExternalAdsConversionApiService(
            new HttpClient(handler), oauth, store, configuration);

        var source = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext
        {
            EventName = "QualifiedLead",
            EventUtc = new DateTime(2026, 10, 6, 4, 20, 0, DateTimeKind.Utc),
            CommerceBusinessId = owner.CommerceBusinessId,
            IsServerAuthority = true,
            Ttclid = "ttclid-canonical-1"
        });
        Assert.True(measurement.HasMeasurementCredential);
        var stored = await db.MarketingConnections.AsNoTracking()
            .SingleAsync(row => row.OwnerKey == owner.Key && row.Provider == MarketingDestinationKeys.TikTok);
        Assert.False(string.IsNullOrWhiteSpace(stored.CapiAccessTokenCiphertext));
        Assert.DoesNotContain("tiktok-events-token", stored.CapiAccessTokenCiphertext!, StringComparison.Ordinal);

        var mapping = Assert.Single(measurement.Mappings);
        var result = await transport.SendAsync(
            owner, connection, measurement, mapping, source, "ttclid-canonical-1");

        Assert.True(result.Sent);
        Assert.NotNull(eventBody);
        using var payload = JsonDocument.Parse(eventBody!);
        Assert.Equal("crm", payload.RootElement.GetProperty("event_source").GetString());
        Assert.Equal("crm-event-source-1", payload.RootElement.GetProperty("event_source_id").GetString());
        var evt = payload.RootElement.GetProperty("data")[0];
        Assert.Equal("QualifiedLeadReady", evt.GetProperty("event").GetString());
        Assert.Equal(CanonicalAdvertisingEventProjection.ResolveEventId(source),
            evt.GetProperty("event_id").GetString());
        Assert.Equal("ttclid-canonical-1",
            evt.GetProperty("user").GetProperty("ttclid").GetString());
    }

    [Fact]
    public void ExternalAdsStayOnTheCanonicalConnectionAnalyticsAndReportingAuthorities()
    {
        var root = Root();
        var store = Read(root, "Infrastructure", "Analytics", "MarketingConnectionStore.cs");
        var oauth = Read(root, "Infrastructure", "Analytics", "MarketingExternalAdsOAuthService.cs");
        var reporting = Read(root, "Infrastructure", "Analytics", "MarketingExternalAdsReportingService.cs");
        var projection = Read(root, "Infrastructure", "Analytics", "MarketingProviderSetupProjection.cs");
        var controller = Read(root, "AgentPortal", "Controllers", "WebsiteAnalyticsController.cs");
        var business = Read(root, "Infrastructure", "Businesses", "BusinessWorkspaceControllerBase.cs");
        var view = Read(root, "AgentPortal", "Views", "WebsiteAnalytics", "Index.cshtml");
        var js = Read(root, "AgentPortal", "wwwroot", "js", "website-analytics.js");

        Assert.Contains("MarketingDestinationKeys.Google", store, StringComparison.Ordinal);
        Assert.Contains("MarketingDestinationKeys.TikTok", store, StringComparison.Ordinal);
        Assert.Contains("protector.Protect(owner, key, primarySecret)", store, StringComparison.Ordinal);
        Assert.Contains("Marketing.ExternalAds.OAuthState.v1", oauth, StringComparison.Ordinal);
        Assert.Contains("customers:listAccessibleCustomers", oauth, StringComparison.Ordinal);
        Assert.Contains("oauth2/access_token/", oauth, StringComparison.Ordinal);
        Assert.Contains("report/integrated/get/", reporting, StringComparison.Ordinal);
        Assert.Contains("MarketingChannels.GoogleAds", reporting, StringComparison.Ordinal);
        Assert.Contains("MarketingChannels.TikTokAds", reporting, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderConnectionSnapshot Google", projection, StringComparison.Ordinal);
        Assert.Contains("MarketingProviderConnectionSnapshot TikTok", projection, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"external-ads/connect\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"analytics/external-ads/connect\")]", business, StringComparison.Ordinal);
        Assert.Contains("marketing-setup-google-connect", view, StringComparison.Ordinal);
        Assert.Contains("marketing-setup-tiktok-connect", view, StringComparison.Ordinal);
        Assert.Contains("externalAdsConnect: analyticsEndpoint('/external-ads/connect')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<Google", store, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<TikTok", store, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static string Root([CallerFilePath] string currentFile = "")
    {
        var directory = Path.GetDirectoryName(currentFile)
            ?? throw new DirectoryNotFoundException("Could not resolve test file path.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }
}

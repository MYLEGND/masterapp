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
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalAdsOAuthPersistsEncryptedScopedCredentialsAndRequiresAccountSelectionWhenAmbiguous(
        bool hasRetainedDeveloperToken)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var settings = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["GoogleAds:ClientId"] = "google-client",
            ["GoogleAds:ClientSecret"] = "google-secret",
            ["GoogleAds:RedirectUri"] = "https://portal.example.test/business/external-ads/callback",
            ["TikTokAds:AppId"] = "tiktok-app",
            ["TikTokAds:AppSecret"] = "tiktok-secret",
            ["TikTokAds:AdvertiserAuthorizationUrl"] = "https://ads.tiktok.com/marketing_api/auth?app_id=tiktok-app",
            ["TikTokAds:BusinessAdvertiserAuthorizationUrl"] = "https://ads.tiktok.com/marketing_api/auth?app_id=tiktok-app&business=1",
            ["TikTokAds:RedirectUri"] = "https://portal.example.test/business/external-ads/callback"
        };
        if (hasRetainedDeveloperToken)
            settings["GoogleAds:DeveloperToken"] = "retired-developer-token";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.Host == "oauth2.googleapis.com")
                return Json("""{"access_token":"google-access","refresh_token":"google-refresh","expires_in":3600,"scope":"https://www.googleapis.com/auth/adwords"}""");
            if (uri.Host == "googleads.googleapis.com")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("google-access", request.Headers.Authorization?.Parameter);
                Assert.False(request.Headers.Contains("developer-token"));
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
        var googleQuery = QueryHelpers.ParseQuery(new Uri(googleConnect).Query);
        Assert.Equal("https://www.googleapis.com/auth/adwords", googleQuery["scope"].ToString());
        Assert.Equal("offline", googleQuery["access_type"].ToString());
        Assert.Equal("consent", googleQuery["prompt"].ToString());
        var googleState = googleQuery["state"].ToString();
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

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task GoogleOAuthDiscoveryDenialDoesNotPersistAConnectionWithoutDeveloperToken(int status)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Founder;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["GoogleAds:ClientId"] = "google-client",
                ["GoogleAds:ClientSecret"] = "google-secret"
            }).Build();
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.Host == "oauth2.googleapis.com")
                return Json("""{"access_token":"google-access","refresh_token":"google-refresh","expires_in":3600}""");
            Assert.Equal("googleads.googleapis.com", uri.Host);
            Assert.Equal("/v25/customers:listAccessibleCustomers", uri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("google-access", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("developer-token"));
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"status":"PERMISSION_DENIED","message":"Synthetic API access denial"}}""")
            };
        });
        using var http = new HttpClient(handler);
        var service = new MarketingExternalAdsOAuthService(
            new SingleClientFactory(http), configuration,
            new EphemeralDataProtectionProvider(), store,
            NullLogger<MarketingExternalAdsOAuthService>.Instance);

        Assert.True(service.GetConnectionReadiness(owner, MarketingDestinationKeys.Google).CanConnect);
        var connect = service.BuildConnectUrl(owner, MarketingDestinationKeys.Google,
            "/WebsiteAnalytics/Index", "https://portal.example.test/WebsiteAnalytics/external-ads/callback");
        var state = QueryHelpers.ParseQuery(new Uri(connect).Query)["state"].ToString();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCallbackAsync(MarketingDestinationKeys.Google, "google-code", state));

        Assert.Equal(2, requests);
        Assert.False((await store.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google)).Connected);
        Assert.Empty(await db.MarketingConnections.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoogleReportingUsesOAuthWithoutDeveloperTokenAndPreservesSelectedAccount(
        bool hasRetainedDeveloperToken)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        await store.SaveProviderCredentialAsync(owner, MarketingDestinationKeys.Google, "google-refresh",
            MarketingProviderAuthorizationMethods.GoogleOAuthRefreshToken, "{}", null,
            "1111111111", "Scoped account");
        var initial = await store.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google);
        var settings = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["GoogleAds:ClientId"] = "google-client",
            ["GoogleAds:ClientSecret"] = "google-secret",
            ["GoogleAds:LoginCustomerId"] = "999-999-9999"
        };
        if (hasRetainedDeveloperToken)
            settings["GoogleAds:DeveloperToken"] = "retired-developer-token";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var queries = new System.Collections.Generic.List<string>();
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.Host == "oauth2.googleapis.com")
            {
                var form = QueryHelpers.ParseQuery("?" +
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("refresh_token", form["grant_type"].ToString());
                Assert.Equal("google-refresh", form["refresh_token"].ToString());
                return Json("""{"access_token":"google-access","expires_in":3600}""");
            }
            Assert.Equal("googleads.googleapis.com", uri.Host);
            Assert.Equal("/v25/customers/1111111111/googleAds:searchStream", uri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("google-access", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("developer-token"));
            Assert.Equal("9999999999", Assert.Single(request.Headers.GetValues("login-customer-id")));
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var query = body.RootElement.GetProperty("query").GetString()!;
            queries.Add(query);
            if (query == "SELECT customer.time_zone FROM customer LIMIT 1")
                return Json("""[{"results":[{"customer":{"timeZone":"America/Phoenix"}}]}]""");
            Assert.Contains("FROM campaign", query, StringComparison.Ordinal);
            Assert.Contains("BETWEEN '2026-10-05' AND '2026-10-06'", query, StringComparison.Ordinal);
            return Json("""[{"results":[{"campaign":{"id":"42","name":"Scoped campaign","status":"ENABLED"},"metrics":{"costMicros":"12500000","impressions":"30","clicks":"4","conversions":"2"}}]}]""");
        });
        using var http = new HttpClient(handler);
        var factory = new SingleClientFactory(http);
        var oauth = new MarketingExternalAdsOAuthService(factory, configuration,
            new EphemeralDataProtectionProvider(), store,
            NullLogger<MarketingExternalAdsOAuthService>.Instance);
        var reporting = new MarketingExternalAdsReportingService(store, oauth, factory, configuration);
        var range = TimeRangeRequest.FromPreset("custom",
            new DateTime(2026, 10, 6, 0, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 6, 8, 30, 0, DateTimeKind.Utc));

        var report = await reporting.GetCampaignsAsync(owner, MarketingDestinationKeys.Google, range);

        Assert.Single(report.Rows);
        Assert.Equal("America/Phoenix", report.AccountTimeZone);
        Assert.Equal(new DateTime(2026, 10, 5), report.ProviderFromDate);
        Assert.Equal(new DateTime(2026, 10, 6), report.ProviderToDate);
        Assert.Equal(2, queries.Count);
        Assert.Equal(3, requests);
        Assert.Equal(initial.Revision,
            (await store.GetProviderConnectionAsync(owner, MarketingDestinationKeys.Google)).Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reporting.GetCampaignsAsync(
            MarketingOwnerScope.Business(Guid.NewGuid()), MarketingDestinationKeys.Google, range));
        Assert.Equal(3, requests);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoogleConversionTransportUsesCanonicalGclidValueTimeAndStableOrderId(
        bool hasRetainedDeveloperToken)
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
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("google-access", request.Headers.Authorization?.Parameter);
                Assert.False(request.Headers.Contains("developer-token"));
                uploadBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"results":[{"gclid":"gclid-canonical-1","conversionAction":"customers/1111111111/conversionActions/77","conversionDateTime":"2026-10-06 04:15:00+00:00"}]}""");
            }
            throw new InvalidOperationException("Unexpected provider request: " + uri);
        });
        var settings = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["GoogleAds:ClientId"] = "google-client",
            ["GoogleAds:ClientSecret"] = "google-secret",
            ["GoogleAds:ApiVersion"] = "v25"
        };
        if (hasRetainedDeveloperToken)
            settings["GoogleAds:DeveloperToken"] = "retired-developer-token";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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


    [Theory]
    [InlineData("google", "GoogleAds:ClientId", "OAuth client ID")]
    [InlineData("google", "GoogleAds:ClientSecret", "OAuth client secret")]
    [InlineData("tiktok", "TikTokAds:AppId", "app ID")]
    [InlineData("tiktok", "TikTokAds:AppSecret", "app secret")]
    public async Task ExternalAdsReadinessRejectsEveryMissingProviderPrerequisiteBeforeConsent(
        string provider, string setting, string requirement)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var settings = ExternalAdsReadySettings();
        settings.Remove(setting);
        var service = ExternalAdsOAuth(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), store);

        var readiness = service.GetConnectionReadiness(MarketingOwnerScope.Founder, provider);

        Assert.False(readiness.CanConnect);
        Assert.Equal("configuration_required", readiness.Status);
        Assert.Contains(requirement, readiness.MissingRequirements);
        Assert.DoesNotContain("fixture-secret", readiness.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => service.BuildConnectUrl(
            MarketingOwnerScope.Founder, provider, "/WebsiteAnalytics/Index",
            "https://portal.example.test/WebsiteAnalytics/external-ads/callback"));
        Assert.Empty(await db.MarketingConnections.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("google")]
    [InlineData("tiktok")]
    public async Task ExternalAdsConfiguredReadinessPreservesOwnerAndDoesNotClaimAConnection(string provider)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var service = ExternalAdsOAuth(new ConfigurationBuilder().AddInMemoryCollection(ExternalAdsReadySettings()).Build(), store);
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var target = "/WebsiteAnalytics/Index?siteKey=protect&agentProfileId=" + owner.AgentTrackingProfileId;
        var readiness = service.GetConnectionReadiness(owner, provider);

        Assert.True(readiness.CanConnect);
        Assert.Empty(readiness.MissingRequirements);
        var connectUrl = service.BuildConnectUrl(owner, provider, target,
            "https://portal.example.test/WebsiteAnalytics/external-ads/callback");
        var uri = new Uri(connectUrl);
        var query = QueryHelpers.ParseQuery(uri.Query);
        var inspection = service.InspectState(query["state"].ToString());
        Assert.Equal(owner, inspection.Owner);
        Assert.Equal(provider, inspection.Provider);
        Assert.Equal(target, inspection.ReturnUrl);
        Assert.Throws<InvalidOperationException>(() => service.InspectOwner(
            query["state"].ToString(), provider == "google" ? "tiktok" : "google"));
        Assert.False((await store.GetProviderConnectionAsync(owner, provider)).Connected);
        Assert.Empty(await db.MarketingConnections.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void TikTokBusinessReadinessRequiresItsOwnConfiguredAuthorizationUrl()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var store = new MarketingConnectionStore(db, protector);
        var settings = ExternalAdsReadySettings();
        settings.Remove("TikTokAds:BusinessAdvertiserAuthorizationUrl");
        var service = ExternalAdsOAuth(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), store);

        Assert.True(service.GetConnectionReadiness(MarketingOwnerScope.Founder, "tiktok").CanConnect);
        var business = MarketingOwnerScope.Business(Guid.NewGuid());
        var readiness = service.GetConnectionReadiness(business, "tiktok");
        Assert.False(readiness.CanConnect);
        Assert.Contains("HTTPS advertiser authorization URL from TikTok My Apps", readiness.MissingRequirements);
        Assert.Throws<InvalidOperationException>(() => service.BuildConnectUrl(
            business, "tiktok", "/business/" + business.CommerceBusinessId + "/analytics",
            "https://portal.example.test/business/external-ads/callback"));
    }

    [Theory]
    [InlineData("TikTokAds:AdvertiserAuthorizationUrl", "http://ads.tiktok.com/marketing_api/auth")]
    [InlineData("TikTokAds:AdvertiserAuthorizationUrl", "https://fixture-secret@ads.tiktok.com/marketing_api/auth")]
    [InlineData("TikTokAds:TokenEndpoint", "http://business-api.tiktok.com/token")]
    public void TikTokReadinessRejectsInvalidConfigurationWithoutReflectingItsValue(string setting, string value)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var settings = ExternalAdsReadySettings();
        settings[setting] = value;
        var service = ExternalAdsOAuth(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new MarketingConnectionStore(db, protector));
        var readiness = service.GetConnectionReadiness(MarketingOwnerScope.Founder, "tiktok");

        Assert.False(readiness.CanConnect);
        Assert.DoesNotContain(value, readiness.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", readiness.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAdsReturnUrlPreservesScopeAndFragmentAndReplacesTransportParameters()
    {
        var ownerId = Guid.NewGuid().ToString();
        var target = "/WebsiteAnalytics/Index?agentProfileId=" + ownerId +
            "&siteKey=legend&preset=today&provider=google&status=error&message=private-error" +
            "&code=private-code&auth_code=private-auth-code&state=private-state#provider-setup";
        var redirect = MarketingExternalAdsOAuthService.BuildReturnUrl(
            target, "tiktok", "error", "authorization_declined");
        var uri = new Uri("https://portal.example.test" + redirect);
        var query = QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("/WebsiteAnalytics/Index", uri.AbsolutePath);
        Assert.Equal("#provider-setup", uri.Fragment);
        Assert.Equal(ownerId, query["agentProfileId"].ToString());
        Assert.Equal("legend", query["siteKey"].ToString());
        Assert.Equal("today", query["preset"].ToString());
        Assert.Single(query["provider"]);
        Assert.Equal("tiktok", query["provider"].ToString());
        Assert.Equal("error", query["status"].ToString());
        Assert.Equal("authorization_declined", query["reason"].ToString());
        foreach (var key in new[] { "message", "code", "auth_code", "state" })
            Assert.False(query.ContainsKey(key));
        Assert.DoesNotContain("private-", redirect, StringComparison.Ordinal);

        var connected = MarketingExternalAdsOAuthService.BuildReturnUrl(redirect, "google", "connected");
        var connectedQuery = QueryHelpers.ParseQuery(new Uri("https://portal.example.test" + connected).Query);
        Assert.Equal("connected", connectedQuery["status"].ToString());
        Assert.False(connectedQuery.ContainsKey("reason"));
        Assert.Equal(ownerId, connectedQuery["agentProfileId"].ToString());
    }

    [Theory]
    [InlineData("https://outside.example.test/")]
    [InlineData("//outside.example.test/")]
    [InlineData("/\\outside.example.test/")]
    [InlineData("/WebsiteAnalytics/Index\r\nLocation: https://outside.example.test/")]
    public void ExternalAdsReturnUrlRejectsNonLocalOrControlCharacterTargets(string target)
    {
        var redirect = MarketingExternalAdsOAuthService.BuildReturnUrl(
            target, "untrusted-provider", "untrusted-status", "private-provider-message");
        var uri = new Uri("https://portal.example.test" + redirect);
        var query = QueryHelpers.ParseQuery(uri.Query);

        Assert.Equal("/WebsiteAnalytics/Index", uri.AbsolutePath);
        Assert.Equal("external", query["provider"].ToString());
        Assert.Equal("error", query["status"].ToString());
        Assert.Equal("connection_failed", query["reason"].ToString());
        Assert.DoesNotContain("untrusted", redirect, StringComparison.Ordinal);
        Assert.DoesNotContain("private-provider-message", redirect, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderSetupBoundsOptionalInspectionAndPreservesStoredConnectionsAndOtherReadiness()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var direct = new Mock<IOpenAiAdsDirectConnectionService>(MockBehavior.Strict);
        CancellationToken inspectionToken = default;
        direct.Setup(service => service.InspectAsync(owner, It.IsAny<CancellationToken>()))
            .Returns(async (MarketingOwnerScope _, CancellationToken token) =>
            {
                inspectionToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return (OpenAiAdsProviderAccountSnapshot?)null;
            });
        var (projection, authority, stored) = await ProviderSetupFixture(db, protector, owner, direct.Object);

        var result = await projection.ReadAsync(owner).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(inspectionToken.IsCancellationRequested);
        Assert.Equal(stored.Revision, result.Connection.Revision);
        Assert.True(result.Connection.Connected);
        Assert.Null(result.Account);
        Assert.Null(result.Capability);
        Assert.Equal("Provider status unavailable; stored connection shown.", result.OpenAiError);
        Assert.False(result.GoogleConnectionSetup.CanConnect);
        Assert.Contains("OAuth client ID", result.GoogleConnectionSetup.MissingRequirements);
        Assert.True(result.TikTokConnectionSetup.CanConnect);
        Assert.False(result.TikTok.Connected);
        Assert.Equal(stored.Revision, (await authority.GetAsync(owner)).Revision);
        Assert.Equal("fixture-secret-openai", (await authority.GetSecretsAsync(owner)).ManagementApiKey);
        direct.Verify(service => service.InspectMeasurementAsync(It.IsAny<MarketingOwnerScope>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProviderSetupPropagatesRequestCancellationWithoutChangingStoredConnection()
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var protector = new MarketingCredentialProtector(new EphemeralDataProtectionProvider());
        using var requestCancellation = new CancellationTokenSource();
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var direct = new Mock<IOpenAiAdsDirectConnectionService>(MockBehavior.Strict);
        direct.Setup(service => service.InspectAsync(owner, It.IsAny<CancellationToken>()))
            .Returns(async (MarketingOwnerScope _, CancellationToken token) =>
            {
                requestCancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return (OpenAiAdsProviderAccountSnapshot?)null;
            });
        var (projection, authority, stored) = await ProviderSetupFixture(db, protector, owner, direct.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            projection.ReadAsync(owner, requestCancellation.Token));

        Assert.Equal(stored.Revision, (await authority.GetAsync(owner)).Revision);
        Assert.Equal("fixture-secret-openai", (await authority.GetSecretsAsync(owner)).ManagementApiKey);
    }

    private static async Task<(MarketingProviderSetupProjection Projection,
        OpenAiAdsAccountConnectionAuthority Authority, OpenAiAdsConnectionSnapshot Stored)> ProviderSetupFixture(
        MasterAppDbContext db, MarketingCredentialProtector protector, MarketingOwnerScope owner,
        IOpenAiAdsDirectConnectionService direct)
    {
        var settings = ExternalAdsReadySettings();
        settings.Remove("GoogleAds:ClientId");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var store = new MarketingConnectionStore(db, protector);
        var authority = new OpenAiAdsAccountConnectionAuthority(db, protector);
        var stored = await authority.BindVerifiedAsync(owner,
            new VerifiedOpenAiAdsAccount("fixture-account", "Fixture account", null, "approved", "api_key",
                Permissions: ["ad_account.read"]),
            new OpenAiAdsConnectionSecrets(ManagementApiKey: "fixture-secret-openai"));
        var pixels = new Mock<IMetaPixelResolutionService>(MockBehavior.Strict);
        pixels.Setup(service => service.ResolveForOwnerAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedMetaPixelContext());
        var health = new OpenAiMeasurementHealthService(db, authority,
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No remote health call expected."))));
        var evidence = new MarketingMeasurementEvidenceService(db, configuration, store, authority);
        var projection = new MarketingProviderSetupProjection(store, pixels.Object, authority, direct,
            health, evidence, ExternalAdsOAuth(configuration, store));
        return (projection, authority, stored);
    }

    private static System.Collections.Generic.Dictionary<string, string?> ExternalAdsReadySettings() => new()
    {
        ["GoogleAds:ClientId"] = "fixture-google-client",
        ["GoogleAds:ClientSecret"] = "fixture-secret-google",
        ["TikTokAds:AppId"] = "fixture-tiktok-app",
        ["TikTokAds:AppSecret"] = "fixture-secret-tiktok",
        ["TikTokAds:AdvertiserAuthorizationUrl"] = "https://ads.tiktok.com/marketing_api/auth?app_id=fixture-tiktok-app",
        ["TikTokAds:BusinessAdvertiserAuthorizationUrl"] = "https://ads.tiktok.com/marketing_api/auth?app_id=fixture-tiktok-app&business=1"
    };

    private static MarketingExternalAdsOAuthService ExternalAdsOAuth(
        IConfiguration configuration, MarketingConnectionStore store) =>
        new(new SingleClientFactory(new HttpClient(new StubHandler(_ =>
                throw new InvalidOperationException("Readiness must not make a provider request.")))),
            configuration, new EphemeralDataProtectionProvider(), store,
            NullLogger<MarketingExternalAdsOAuthService>.Instance);

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

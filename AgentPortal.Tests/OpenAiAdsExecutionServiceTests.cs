using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiAdsExecutionServiceTests
{
    [Theory]
    [InlineData("founder")]
    [InlineData("agent")]
    [InlineData("business")]
    public async Task ListCampaigns_UsesOnlyTheScopedAuthorityCredential(string scopeKind)
    {
        var owner = Owner(scopeKind);
        var authority = new FakeAuthority(owner, "scope-secret");
        var handler = new RecordingHandler((request, _) =>
            Json(HttpStatusCode.OK, """{"object":"list","data":[],"has_more":false}"""));
        var service = new OpenAiAdsExecutionService(new HttpClient(handler), authority);

        await service.ListCampaignsAsync(owner);

        Assert.Equal(owner.Key, authority.LastOwnerKey);
        Assert.Single(handler.Requests);
        Assert.Equal("https://api.ads.openai.com/v1/campaigns", handler.Requests[0].Url);
        Assert.Equal("Bearer scope-secret", handler.Requests[0].Authorization);
        Assert.DoesNotContain(owner.Key, handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectedScope_FailsClosedBeforeProviderCall()
    {
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var authority = new FakeAuthority(owner, "secret") { Connected = false };
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var service = new OpenAiAdsExecutionService(new HttpClient(handler), authority);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListCampaignsAsync(owner));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateCampaign_SendsBudgetTargetingAndStableIdempotencyKey()
    {
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var handler = new RecordingHandler((request, _) =>
            Json(HttpStatusCode.OK, """{"id":"cmpn_1","name":"Roofing Growth"}"""));
        var service = Service(owner, handler);

        await service.CreateCampaignAsync(owner, new(
            Name: "Roofing Growth",
            Status: "paused",
            Budget: new(DailySpendLimitMicros: 25_000_000),
            BiddingType: "clicks",
            Targeting: new(Countries: ["US"], Platforms: ["web"]),
            IdempotencyKey: "legend-business-roofing-v1"));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://api.ads.openai.com/v1/campaigns", sent.Url);
        Assert.Equal("legend-business-roofing-v1", sent.IdempotencyKey);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("Roofing Growth", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(25_000_000, body.RootElement.GetProperty("budget").GetProperty("daily_spend_limit_micros").GetInt64());
        Assert.Equal("clicks", body.RootElement.GetProperty("bidding_type").GetString());
        Assert.Equal("US", body.RootElement.GetProperty("targeting").GetProperty("locations").GetProperty("countries")[0].GetString());
    }

    [Fact]
    public async Task ConversionCampaign_RequiresProviderConfirmedStandardConversionSetting()
    {
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Url.Contains("/conversions/event_settings", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"object":"list","data":[{"id":"ces_1","event_type":"standard","status":"active"}],"has_more":false}""");
            return Json(HttpStatusCode.OK, """{"id":"cmpn_1"}""");
        });
        var service = Service(owner, handler);

        await service.CreateCampaignAsync(owner, new(
            "Qualified Roofing Leads", "paused", new(DailySpendLimitMicros: 40_000_000),
            "conversions", ConversionEventSettingId: "ces_1", IdempotencyKey: "conversion-create-1"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/conversions/event_settings", handler.Requests[0].Url, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("ces_1", body.RootElement.GetProperty("conversion_event_setting_ids")[0].GetString());
    }

    [Fact]
    public async Task ConversionCampaign_RejectsCustomMeasurementEventAsOptimizationGoal()
    {
        var owner = MarketingOwnerScope.Founder;
        var handler = new RecordingHandler((request, _) =>
            Json(HttpStatusCode.OK, """{"object":"list","data":[{"id":"custom_1","event_type":"custom","status":"active"}],"has_more":false}"""));
        var service = Service(owner, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateCampaignAsync(owner, new(
                "Custom Goal", "paused", new(DailySpendLimitMicros: 20_000_000),
                "conversions", ConversionEventSettingId: "custom_1")));

        Assert.Contains("measurement-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ActiveCreate_RequiresLiveActiveApprovedAccount()
    {
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Contains("/ad_account", request.Url, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, """{"id":"acct","status":"active","review":{"status":"in_review"}}""");
        });
        var service = Service(owner, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateCampaignAsync(owner, new(
                "Do Not Launch", "active", new(DailySpendLimitMicros: 10_000_000), "clicks")));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateAd_ResolvesParentModeAndRejectsAttributionStuffedDestination()
    {
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Url.Contains("/ad_groups/ag_1", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"id":"ag_1","campaign_id":"cmpn_1"}""");
            if (request.Url.Contains("/campaigns/cmpn_1", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"id":"cmpn_1","bidding_type":"clicks"}""");
            return Json(HttpStatusCode.OK, """{"id":"ad_1"}""");
        });
        var service = Service(owner, handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAdAsync(owner, new(
                "ag_1",
                "Roofing Ad",
                new("chat_card", "Need a roofer?", "Get a local quote", "https://example.com/roofing?oppref=forged", "file_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                "paused")));

        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, x => x.Method == "POST" && x.Url.EndsWith("/ads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAdGroup_PreservesContextHintsAndValidatesParentBilling()
    {
        var owner = MarketingOwnerScope.Agent(Guid.NewGuid());
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == "GET")
                return Json(HttpStatusCode.OK, """{"id":"cmpn_1","bidding_type":"clicks"}""");
            return Json(HttpStatusCode.OK, """{"id":"ag_1"}""");
        });
        var service = Service(owner, handler);

        await service.CreateAdGroupAsync(owner, new(
            "cmpn_1",
            "Roofing Prospects",
            "paused",
            new("maximize_clicks"),
            ContextHints: ["roof repair", "storm damage"],
            IdempotencyKey: "ag-roofing-1"));

        using var body = JsonDocument.Parse(handler.Requests.Last().Body!);
        Assert.Equal("maximize_clicks", body.RootElement.GetProperty("bidding_config").GetProperty("strategy").GetString());
        Assert.Equal("roof repair", body.RootElement.GetProperty("context_hints")[0].GetString());
        Assert.Equal("ag-roofing-1", handler.Requests.Last().IdempotencyKey);
    }

    [Fact]
    public async Task Insights_ReadsUseOfficialScopedEndpointAndUnixRange()
    {
        var owner = MarketingOwnerScope.Founder;
        var handler = new RecordingHandler((_, _) =>
            Json(HttpStatusCode.OK, """{"object":"list","data":[{"campaign_id":"cmpn_1","clicks":4,"spend":12.50}],"has_more":false}"""));
        var service = Service(owner, handler);

        var from = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
        await service.GetAccountInsightsAsync(owner, "campaign", new(from, to, "none", ["campaign.id", "campaign.clicks", "campaign.spend"]));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("/ad_account/insights?", request.Url, StringComparison.Ordinal);
        Assert.Contains("aggregation_level=campaign", request.Url, StringComparison.Ordinal);
        Assert.Contains("time_ranges%5B%5D=", request.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fields%5B%5D=campaign.id", request.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderFailuresAreClassifiedWithoutEchoingSecrets()
    {
        var owner = MarketingOwnerScope.Founder;
        var handler = new RecordingHandler((_, _) =>
            Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limited"}}"""));
        var service = Service(owner, handler, "never-echo-this-secret");

        var ex = await Assert.ThrowsAsync<OpenAiAdsExecutionException>(() => service.ListCampaignsAsync(owner));

        Assert.True(ex.Retryable);
        Assert.Equal(429, ex.HttpStatusCode);
        Assert.DoesNotContain("never-echo-this-secret", ex.Message, StringComparison.Ordinal);
    }

    private static OpenAiAdsExecutionService Service(
        MarketingOwnerScope owner,
        RecordingHandler handler,
        string key = "secret") =>
        new(new HttpClient(handler), new FakeAuthority(owner, key));

    private static MarketingOwnerScope Owner(string kind) => kind switch
    {
        "founder" => MarketingOwnerScope.Founder,
        "agent" => MarketingOwnerScope.Agent(Guid.NewGuid()),
        "business" => MarketingOwnerScope.Business(Guid.NewGuid()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class RecordingHandler(Func<RequestRecord, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var record = new RequestRecord(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization is null ? null : $"{request.Headers.Authorization.Scheme} {request.Headers.Authorization.Parameter}",
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.SingleOrDefault() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(record);
            return response(record, Requests.Count);
        }
    }

    private sealed record RequestRecord(
        string Method,
        string Url,
        string? Authorization,
        string? IdempotencyKey,
        string? Body);

    private sealed class FakeAuthority(MarketingOwnerScope owner, string key) : IOpenAiAdsAccountConnectionAuthority
    {
        public bool Connected { get; set; } = true;
        public string? LastOwnerKey { get; private set; }

        public Task<OpenAiAdsConnectionSnapshot> GetAsync(MarketingOwnerScope requested, CancellationToken cancellationToken = default)
        {
            LastOwnerKey = requested.Key;
            if (requested.Key != owner.Key) throw new InvalidOperationException("owner mismatch");
            return Task.FromResult(new OpenAiAdsConnectionSnapshot(
                requested, true, Connected, Guid.NewGuid(), "acct", "Scoped Account", "admin", "approved", "api_key",
                null, null, ["ad_account.read"], "pixel", "source", true, true, DateTime.UtcNow, null, DateTime.UtcNow));
        }

        public Task<OpenAiAdsConnectionSecrets> GetSecretsAsync(MarketingOwnerScope requested, CancellationToken cancellationToken = default)
        {
            LastOwnerKey = requested.Key;
            if (requested.Key != owner.Key) throw new InvalidOperationException("owner mismatch");
            return Task.FromResult(new OpenAiAdsConnectionSecrets(key, "capi"));
        }

        public Task<OpenAiAdsConnectionSnapshot> BindVerifiedAsync(
            MarketingOwnerScope requested,
            VerifiedOpenAiAdsAccount verifiedAccount,
            OpenAiAdsConnectionSecrets secrets,
            Guid? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OpenAiAdsConnectionSnapshot> DisconnectAsync(
            MarketingOwnerScope requested,
            Guid expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiAdsDirectConnectionServiceTests
{
    [Fact]
    public async Task Connect_VerifiesAccountAndProvisionsMissingPixelAndCapi()
    {
        var owner = MarketingOwnerScope.Founder;
        var authority = new FakeAuthority(owner);
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"id":"adacct_1","name":"LEGEND","url":"https://mylegnd.com","status":"active","timezone":"America/Phoenix","currency_code":"USD","review":{"status":"in_review"}}"""),
            Json(HttpStatusCode.OK, """{"object":"list","data":[]}"""),
            Json(HttpStatusCode.OK, """{"id":"clidsrc_1","client_type":"web","name":"LEGEND website","pixel_id":"pixel_1"}"""),
            Json(HttpStatusCode.OK, """{"name":"LEGEND production conversions","api_key":"capi_secret"}"""));
        var service = new OpenAiAdsDirectConnectionService(new HttpClient(handler), authority);

        var result = await service.ConnectAsync(owner, "ads_secret");

        Assert.True(result.Connected);
        Assert.Equal("adacct_1", authority.Verified!.AccountId);
        Assert.Equal("pixel_1", authority.Verified.PixelId);
        Assert.Equal("clidsrc_1", authority.Verified.ConversionDataSourceId);
        Assert.Equal("ads_secret", authority.Secrets!.ManagementApiKey);
        Assert.Equal("capi_secret", authority.Secrets.ConversionsApiKey);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("/v1/ad_account", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/v1/conversions/pixels", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("/v1/conversions/api_keys", handler.Requests[3].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Refresh_UsesStoredAdvertiserKeyAndReturnsLiveProviderReadiness()
    {
        var owner = MarketingOwnerScope.Founder;
        var authority = new FakeAuthority(owner)
        {
            Current = Snapshot(owner, pixelId: "pixel_existing", hasCapi: true),
            StoredSecrets = new OpenAiAdsConnectionSecrets("stored_ads_key", "stored_capi_key")
        };
        var accountResponse = """{"id":"adacct_1","name":"LEGEND","url":"https://mylegnd.com","preview_url":"https://example.test/icon.png","status":"active","timezone":"America/Phoenix","currency_code":"USD","review":{"status":"approved"}}""";
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, accountResponse),
            Json(HttpStatusCode.OK, accountResponse));
        var service = new OpenAiAdsDirectConnectionService(new HttpClient(handler), authority);

        await service.RefreshAsync(owner, authority.Current.Revision);
        var provider = await service.InspectAsync(owner);

        Assert.Equal("stored_ads_key", authority.Secrets!.ManagementApiKey);
        Assert.Equal("stored_capi_key", authority.Secrets.ConversionsApiKey);
        Assert.NotNull(provider);
        Assert.Equal("active", provider!.Status);
        Assert.Equal("approved", provider.ReviewStatus);
        Assert.Equal("America/Phoenix", provider.Timezone);
        Assert.Equal("USD", provider.CurrencyCode);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static OpenAiAdsConnectionSnapshot Snapshot(MarketingOwnerScope owner, string? pixelId, bool hasCapi) =>
        new(owner, true, true, Guid.NewGuid(), "adacct_1", "LEGEND", null, "approved", "api_key", null, null,
            ["ad_account.read"], pixelId, "clidsrc_1", true, hasCapi, DateTime.UtcNow, null, DateTime.UtcNow);

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request.");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeAuthority(MarketingOwnerScope owner) : IOpenAiAdsAccountConnectionAuthority
    {
        public OpenAiAdsConnectionSnapshot Current { get; set; } =
            new(owner, false, false, Guid.Empty, null, null, null, null, null, null, null, [], null, null,
                false, false, null, null, null);
        public OpenAiAdsConnectionSecrets StoredSecrets { get; set; } = new();
        public VerifiedOpenAiAdsAccount? Verified { get; private set; }
        public OpenAiAdsConnectionSecrets? Secrets { get; private set; }

        public Task<OpenAiAdsConnectionSnapshot> GetAsync(MarketingOwnerScope requestedOwner, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<OpenAiAdsConnectionSecrets> GetSecretsAsync(MarketingOwnerScope requestedOwner, CancellationToken cancellationToken = default) =>
            Task.FromResult(StoredSecrets);

        public Task<OpenAiAdsConnectionSnapshot> BindVerifiedAsync(
            MarketingOwnerScope requestedOwner,
            VerifiedOpenAiAdsAccount verifiedAccount,
            OpenAiAdsConnectionSecrets secrets,
            Guid? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            Verified = verifiedAccount;
            Secrets = secrets;
            StoredSecrets = secrets;
            Current = new(
                requestedOwner, true, true, Guid.NewGuid(), verifiedAccount.AccountId, verifiedAccount.AccountName,
                verifiedAccount.Role, verifiedAccount.ReviewStatus, verifiedAccount.AuthorizationMethod,
                verifiedAccount.ProviderUserId, verifiedAccount.ProviderUserEmail,
                verifiedAccount.Permissions?.ToArray() ?? [], verifiedAccount.PixelId, verifiedAccount.ConversionDataSourceId,
                !string.IsNullOrWhiteSpace(secrets.ManagementApiKey), !string.IsNullOrWhiteSpace(secrets.ConversionsApiKey),
                DateTime.UtcNow, null, verifiedAccount.VerifiedUtc);
            return Task.FromResult(Current);
        }

        public Task<OpenAiAdsConnectionSnapshot> DisconnectAsync(
            MarketingOwnerScope requestedOwner,
            Guid expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

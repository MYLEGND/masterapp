using System.Net;
using Infrastructure.Analytics;

namespace AgentPortal.Tests;

[CollectionDefinition("Meta Graph endpoint authority", DisableParallelization = true)]
public sealed class MetaGraphEndpointAuthorityCollection;

[Collection("Meta Graph endpoint authority")]
public sealed class MetaGraphEndpointAuthorityTests
{
    [Fact]
    public async Task DeprecatedProviderDefault_RetriesRecommendedVersionAndCachesIt()
    {
        MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        try
        {
            var handler = new RecordingHandler((request, attempt) =>
                attempt == 1
                    ? Error2635("v25.0")
                    : Json(HttpStatusCode.OK, """{"data":[{"id":"1"}]}"""));
            using var client = new HttpClient(handler);

            var result = await MetaGraphEndpointAuthority.GetAsync(
                client,
                "act_123/campaigns?fields=id&access_token=test");

            Assert.True(result.IsSuccessStatusCode);
            Assert.True(result.Negotiated);
            Assert.Equal("v25.0", result.EffectiveVersion);
            Assert.Equal("v25.0", MetaGraphEndpointAuthority.NegotiatedVersion);
            Assert.Equal(2, handler.Uris.Count);
            Assert.Equal("/act_123/campaigns", handler.Uris[0].AbsolutePath);
            Assert.Equal("/v25.0/act_123/campaigns", handler.Uris[1].AbsolutePath);

            var second = await MetaGraphEndpointAuthority.GetAsync(
                client,
                "act_123/campaigns?fields=id&access_token=test");
            Assert.True(second.IsSuccessStatusCode);
            Assert.Equal("/v25.0/act_123/campaigns", handler.Uris[2].AbsolutePath);
        }
        finally
        {
            MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        }
    }

    [Fact]
    public async Task CachedVersion_DeprecatedLater_RenegotiatesWithoutManualConfiguration()
    {
        MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        MetaGraphEndpointAuthority.SetNegotiatedVersionForTests("v25.0");
        try
        {
            var handler = new RecordingHandler((_, attempt) =>
                attempt == 1
                    ? Error2635("v26.0")
                    : Json(HttpStatusCode.OK, """{"data":[]}"""));
            using var client = new HttpClient(handler);

            var result = await MetaGraphEndpointAuthority.GetAsync(
                client,
                "act_123/insights?fields=campaign_id&access_token=test");

            Assert.True(result.IsSuccessStatusCode);
            Assert.Equal("v26.0", MetaGraphEndpointAuthority.NegotiatedVersion);
            Assert.Equal("/v25.0/act_123/insights", handler.Uris[0].AbsolutePath);
            Assert.Equal("/v26.0/act_123/insights", handler.Uris[1].AbsolutePath);
        }
        finally
        {
            MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        }
    }

    [Fact]
    public async Task NonDeprecationProviderError_IsNotRetriedAndRemainsDiagnosable()
    {
        MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        try
        {
            var handler = new RecordingHandler((_, _) =>
                Json(HttpStatusCode.BadRequest,
                    """{"error":{"message":"Invalid OAuth access token.","type":"OAuthException","code":190,"error_subcode":463}}"""));
            using var client = new HttpClient(handler);

            var result = await MetaGraphEndpointAuthority.GetAsync(
                client,
                "act_123/campaigns?fields=id&access_token=test");

            Assert.False(result.IsSuccessStatusCode);
            Assert.Single(handler.Uris);
            var message = MetaGraphEndpointAuthority.SafeErrorMessage(result.Body, "fallback");
            Assert.Contains("190/463", message, StringComparison.Ordinal);
            Assert.Contains("Invalid OAuth access token.", message, StringComparison.Ordinal);
        }
        finally
        {
            MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        }
    }

    [Fact]
    public async Task PostForm_RetainsPayloadAcrossProviderDirectedVersionRetry()
    {
        MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        try
        {
            var handler = new RecordingHandler((_, attempt) =>
                attempt == 1
                    ? Error2635("v25.0")
                    : Json(HttpStatusCode.OK, """{"events_received":1}"""),
                captureBody: true);
            using var client = new HttpClient(handler);

            var result = await MetaGraphEndpointAuthority.PostFormAsync(
                client,
                "pixel-123/events",
                new Dictionary<string, string>
                {
                    ["access_token"] = "secret-token",
                    ["data"] = """[{"event_name":"Lead"}]"""
                });

            Assert.True(result.IsSuccessStatusCode);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
            Assert.Contains("access_token=secret-token", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Equal("/pixel-123/events", handler.Uris[0].AbsolutePath);
            Assert.Equal("/v25.0/pixel-123/events", handler.Uris[1].AbsolutePath);
        }
        finally
        {
            MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        }
    }

    [Fact]
    public async Task AbsolutePagingUrl_IsRewrittenToNegotiatedVersion()
    {
        MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        MetaGraphEndpointAuthority.SetNegotiatedVersionForTests("v25.0");
        try
        {
            var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, """{"data":[]}"""));
            using var client = new HttpClient(handler);

            var result = await MetaGraphEndpointAuthority.GetAsync(
                client,
                "https://graph.facebook.com/v21.0/act_123/campaigns?after=cursor&access_token=test");

            Assert.True(result.IsSuccessStatusCode);
            Assert.Equal("/v25.0/act_123/campaigns", Assert.Single(handler.Uris).AbsolutePath);
        }
        finally
        {
            MetaGraphEndpointAuthority.ResetNegotiatedVersionForTests();
        }
    }

    private static HttpResponseMessage Error2635(string version) =>
        Json(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"(#2635) You are calling a deprecated version of the Ads API. Please upgrade to the latest version: VERSION.","type":"OAuthException","code":2635}}"""
                .Replace("VERSION", version, StringComparison.Ordinal));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder,
        bool captureBody = false) : HttpMessageHandler
    {
        private int _attempt;
        public List<Uri> Uris { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _attempt++;
            Uris.Add(request.RequestUri!);
            if (captureBody)
                Bodies.Add(request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request, _attempt);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendCloudflareTransportTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)17, 32).ToArray();
    private static LegendModelTaskRequest TaskRequest(LegendConnectExternalProviderPolicy? policy = null) =>
        new("conversation", "Use evidence honestly.", "Hello", "text", ProviderPolicy: policy,
            RequestingActorId: "founder", CloudflareScope: new("request1", "tenant", "founder", "session",
                "conversation", ["Founder"], "v1"));

    private static LegendConnectModelInferenceTransport Transport(Handler handler, string endpoint = "https://legend.example/v1/legend/respond", bool callbackEnabled = false) =>
        new(handler, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Foundation:HostKind"] = "Cloudflare",
            ["LegendConnect:Foundation:Model"] = "cloudflare:registry",
            ["LegendConnect:Foundation:Enabled"] = "true",
            ["LegendConnect:Foundation:Endpoint"] = endpoint,
            ["LegendConnect:Foundation:Cloudflare:AccountId"] = "account",
            ["LegendConnect:Foundation:Cloudflare:KeyId"] = "key1",
            ["LegendConnect:Foundation:Cloudflare:SigningKey"] = Convert.ToBase64String(Key),
            ["LegendConnect:Foundation:Cloudflare:MaxCostMicrousd"] = "10000",
            ["LegendConnect:Foundation:Cloudflare:ToolCallbackEnabled"] = callbackEnabled.ToString()
        }).Build(), NullLogger<LegendConnectModelInferenceTransport>.Instance);

    [Fact]
    public async Task OptionalToolsRequireExplicitCallbackActivationAndHaveBoundedCloudLoop()
    {
        var task = TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation) with
        {
            AllowTools = true,
            Tools = JsonSerializer.SerializeToElement(new[] { new { type = "function", name = "legend_calculate", parameters = new { type = "object" } } })
        };
        var handler = new Handler();
        Assert.False((await Transport(handler).GenerateAsync("cloudflare:registry", task)).Succeeded);
        Assert.Equal(0, handler.Calls);
        Assert.True((await Transport(handler, callbackEnabled: true).GenerateAsync("cloudflare:registry", task)).Succeeded);
        Assert.Equal(1, handler.Payload.GetProperty("task").GetProperty("tools").GetArrayLength());
        Assert.Equal(3, handler.Payload.GetProperty("limits").GetProperty("maxModelCalls").GetInt32());
        Assert.Equal(4, handler.Payload.GetProperty("limits").GetProperty("maxToolCalls").GetInt32());
        Assert.Equal(10000, handler.Payload.GetProperty("limits").GetProperty("maxCostMicrousd").GetInt32());
    }

    [Fact]
    public async Task ToolExposureDoesNotSatisfyMandatoryEvidenceGate()
    {
        var handler = new Handler();
        var task = TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation) with
        {
            AllowTools = true, RequireToolCall = true,
            Tools = JsonSerializer.SerializeToElement(new[] { new { type = "function", name = "legend_calculate" } })
        };
        var result = await Transport(handler, callbackEnabled: true).GenerateAsync("cloudflare:registry", task);
        Assert.Equal("cloudflare_required_tool_receipt_not_qualified", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExplicitHostedPermissionSignsScopeAndReportsCloudHosting()
    {
        var handler = new Handler();
        var result = await Transport(handler).GenerateAsync("cloudflare:registry", TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation));
        Assert.True(result.Succeeded);
        Assert.Equal("CloudflareHosted", result.Hosting);
        Assert.Equal(50, result.CostMicrounits);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NativeIndependentAbsentAndOrdinaryProviderPermissionCannotInvokeCloud()
    {
        foreach (var policy in new[] { null, LegendConnectExternalProviderPolicy.NativeOnly,
                     LegendConnectExternalProviderPolicy.IndependentAnswering, LegendConnectExternalProviderPolicy.ProviderEnabled })
        {
            var handler = new Handler();
            var result = await Transport(handler).GenerateAsync("cloudflare:registry", TaskRequest(policy));
            Assert.False(result.Succeeded);
            Assert.Equal(0, handler.Calls);
        }
    }

    [Theory]
    [InlineData("http://legend.example/v1/legend/respond")]
    [InlineData("https://127.0.0.1/v1/legend/respond")]
    [InlineData("https://legend.example/v1/legend/respond?override=1")]
    [InlineData("https://legend.example/other")]
    public async Task InvalidEndpointNeverConstructsClient(string endpoint)
    {
        var handler = new Handler();
        Assert.False((await Transport(handler, endpoint).GenerateAsync("cloudflare:registry",
            TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation))).Succeeded);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MissingScopeAndCrossUserContextCannotDispatch()
    {
        var handler = new Handler();
        foreach (var task in new[] { TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation) with { CloudflareScope = null },
            TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation) with { RequestingActorId = "other" } })
            Assert.False((await Transport(handler).GenerateAsync("cloudflare:registry", task)).Succeeded);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task MismatchedReceiptNeverReleasesAnswer()
    {
        var result = await Transport(new Handler { ResponseId = "other" }).GenerateAsync("cloudflare:registry",
            TaskRequest(LegendConnectExternalProviderPolicy.CloudflareFoundation));
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
    }

    private sealed class Handler : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public JsonElement Payload;
        public string ResponseId = "request1";
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("LegendCloudflareFoundation", name);
            return new HttpClient(this);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var signing = string.Join('\n', "legend-service.v1", "POST", "/v1/legend/respond", "key1",
                request.Headers.GetValues("X-Legend-Timestamp").Single(), request.Headers.GetValues("X-Legend-Nonce").Single(),
                Convert.ToHexStringLower(SHA256.HashData(body)));
            Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(signing))),
                request.Headers.GetValues("X-Legend-Signature").Single());
            using var parsed = JsonDocument.Parse(body);
            Payload = parsed.RootElement.Clone();
            Assert.Equal("founder", parsed.RootElement.GetProperty("scope").GetProperty("userId").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                version = "legend-cloudflare.v1", requestId = ResponseId, status = "completed", text = "Fixture answer",
                provider = new { name = "cloudflare-workers-ai", modelId = "@cf/fixture", hosting = "cloudflare" },
                usage = new { costMicrousd = 50, costEvidence = "provider_usage" }
            })) };
        }
    }
}

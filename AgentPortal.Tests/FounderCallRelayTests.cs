using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderCallRelayTests
{
    private const string Resource = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/relay/providers/Microsoft.Compute/virtualMachines/legend-relay";
    [Theory]
    [InlineData("https://attacker.invalid")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/a/providers/Microsoft.Compute/virtualMachines/relay?x=1")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/a/providers/Microsoft.Compute/virtualMachines/../other")]
    public void ArbitraryResourceDestinationsRejected(string value) => Assert.Null(FounderCallRelayService.NormalizeResourceId(value));

    [Fact]
    public async Task MissingSetupNeverConstructsAzureRequest()
    {
        using var handler = new Handler();
        var status = await Create(handler, false).GetAsync(default);
        Assert.True(status.SetupRequired);
        Assert.False(status.CanStart);
        Assert.Equal(0, handler.Count);
    }
    [Fact]
    public async Task UnconfirmedMutationMakesNoAzureRequest()
    {
        using var handler = new Handler();
        var result = await Create(handler).ChangeAsync("start", false, default);
        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.Count);
    }
    [Fact]
    public async Task UnavailableAzureStateCannotAuthorizeStart()
    {
        using var handler = new Handler { Status = HttpStatusCode.Forbidden };
        var result = await Create(handler).ChangeAsync("start", true, default);
        Assert.False(result.Succeeded);
        Assert.Equal(1, handler.Count);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }
    [Fact]
    public async Task InvalidRelayEndpointCannotStartBillableHosting()
    {
        using var handler = new Handler();
        var result = await Create(handler, url: "turn:").ChangeAsync("start", true, default);
        Assert.False(result.Succeeded);
        Assert.Equal(1, handler.Count);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task AcceptedStartIsNotReportedAsCompleted()
    {
        using var handler = new Handler();
        var result = await Create(handler).ChangeAsync("start", true, default);
        Assert.True(result.Succeeded);
        Assert.Equal("Accepted", result.Status);
        Assert.Equal(2, handler.Count);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("management.azure.com", handler.LastUri!.Host);
        Assert.EndsWith("/start", handler.LastUri.AbsolutePath);
    }
    private static FounderCallRelayService Create(Handler handler, bool configured = true, string url = "turn:relay.invalid:3478")
    {
        var values = configured ? new Dictionary<string,string?> {
            ["Calling:Relay:ResourceId"] = Resource,
            ["Calling:Relay:Urls:0"] = url,
            ["Calling:Relay:SharedSecret"] = new string('x', 40)
        } : new Dictionary<string,string?>();
        return new(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new Factory(handler),
            NullLogger<FounderCallRelayService>.Instance, new Credential());
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) => new("test", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(context, cancellationToken));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count; public HttpMethod? LastMethod; public Uri? LastUri;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++; LastMethod = request.Method; LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Accepted : Status) {
                Content = new StringContent("{\"statuses\":[{\"code\":\"PowerState/deallocated\"}]}", Encoding.UTF8, "application/json") });
        }
    }
}

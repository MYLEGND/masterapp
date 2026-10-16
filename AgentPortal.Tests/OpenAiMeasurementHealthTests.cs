using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Analytics;
using Moq;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class OpenAiMeasurementHealthTests
{
    [Theory]
    [InlineData("{\"data\":[]}", true, 0)]
    [InlineData("{\"data\":[{},{}]}", true, 2)]
    [InlineData("{", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("null", false, 0)]
    [InlineData("42", false, 0)]
    [InlineData("\"text\"", false, 0)]
    [InlineData("[]", false, 0)]
    [InlineData("{}", false, 0)]
    [InlineData("{\"data\":null}", false, 0)]
    [InlineData("{\"data\":{}}", false, 0)]
    [InlineData("{\"data\":1}", false, 0)]
    public async Task MonitoringRequiresAValidProviderEventArray(string body, bool available, int count)
    {
        var snapshot = await ReadAsync(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
        Assert.Equal(available, snapshot.ProviderMonitoringAvailable);
        Assert.Equal(count, snapshot.RecentProviderEvents);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task NonSuccessIsUnavailableEvenWithAnEventArray(int status)
    {
        var snapshot = await ReadAsync(new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("{\"data\":[]}") }));
        Assert.False(snapshot.ProviderMonitoringAvailable);
    }

    [Fact]
    public async Task TransportFailureIsUnavailable()
    {
        var snapshot = await ReadAsync(new Handler(_ => throw new HttpRequestException("synthetic failure")));
        Assert.False(snapshot.ProviderMonitoringAvailable);
    }

    [Fact]
    public async Task ProviderTimeoutIsUnavailable()
    {
        var snapshot = await ReadAsync(new Handler(_ => throw new OperationCanceledException()));
        Assert.False(snapshot.ProviderMonitoringAvailable);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(new Handler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }), cancellation.Token));
    }

    private static async Task<OpenAiMeasurementHealthSnapshot> ReadAsync(HttpMessageHandler handler, CancellationToken ct = default)
    {
        using var db = ControllerTestHelpers.BuildDb();
        using var http = new HttpClient(handler);
        var owner = MarketingOwnerScope.Business(Guid.NewGuid());
        var connections = new Mock<IOpenAiAdsAccountConnectionAuthority>(MockBehavior.Strict);
        connections.Setup(x => x.GetAsync(owner, ct)).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "synthetic-account", "Test", null, "approved", "api_key",
            null, null, [], "synthetic-pixel", "synthetic-source", true, true, DateTime.UtcNow, null, DateTime.UtcNow));
        connections.Setup(x => x.GetSecretsAsync(owner, ct)).ReturnsAsync(new OpenAiAdsConnectionSecrets("synthetic-key"));
        return await new OpenAiMeasurementHealthService(db, connections.Object, http).GetAsync(owner, ct);
    }

    private sealed class Handler(Func<CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(ct));
    }
}

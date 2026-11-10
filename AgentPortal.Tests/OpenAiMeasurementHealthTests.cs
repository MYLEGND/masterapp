using System;
using System.Net;
using System.Linq;
using System.Text.Json;
using Domain.Entities;
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

    [Theory]
    [InlineData("founder")]
    [InlineData("agent")]
    [InlineData("business")]
    public async Task DeliveryDiagnosticsPreserveReasonsRetriesAndDestinationIsolation(string kind)
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owner = kind == "founder" ? MarketingOwnerScope.Founder : kind == "agent"
            ? MarketingOwnerScope.Agent(Guid.NewGuid()) : MarketingOwnerScope.Business(Guid.NewGuid());
        var at = DateTime.UtcNow;
        MarketingDestinationDelivery Row(string status, string pixel = "synthetic-pixel") => new()
        {
            OwnerKey = owner.Key, OwnerType = owner.OwnerType, AgentTrackingProfileId = owner.AgentTrackingProfileId,
            CommerceBusinessId = owner.CommerceBusinessId, Provider = "openai", Channel = "server",
            CanonicalSource = nameof(AnalyticsEvent), CanonicalEventId = Guid.NewGuid().ToString("N"),
            AdvertiserAccountId = "synthetic-account", ConversionDataSourceId = "synthetic-source", PixelId = pixel,
            Status = status, CreatedUtc = at, UpdatedUtc = at, ErrorMessage = "private-provider-body", ProviderReceiptJson = "private-receipt"
        };
        var blocked = Row("blocked_human_evidence"); blocked.ErrorCode = "human_evidence_reviewed_needed";
        var failed = Row("permanent_failure"); failed.ErrorCode = "openai_http_400"; failed.LastHttpStatusCode = 400; failed.AttemptCount = 1;
        var retry = Row("retryable"); retry.ErrorCode = "openai_timeout"; retry.AttemptCount = 2;
        retry.LastAttemptUtc = at.AddMinutes(-1); retry.NextAttemptUtc = at.AddMinutes(5);
        var old = Row("retryable", "previous-pixel"); old.ErrorCode = "private-free-text";
        var foreign = Row("permanent_failure"); foreign.OwnerKey = "other-owner";
        var wrongRawOwner = Row("permanent_failure"); wrongRawOwner.CommerceBusinessId = Guid.NewGuid();
        db.AddRange(blocked, failed, retry, old, foreign, wrongRawOwner); await db.SaveChangesAsync();
        var connections = new Mock<IOpenAiAdsAccountConnectionAuthority>();
        connections.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSnapshot(
            owner, true, true, Guid.NewGuid(), "synthetic-account", "Test", null, "approved", "api_key",
            null, null, [], "synthetic-pixel", "synthetic-source", true, true, at, null, at));
        connections.Setup(x => x.GetSecretsAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync(new OpenAiAdsConnectionSecrets());
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var health = await new OpenAiMeasurementHealthService(db, connections.Object, http).GetAsync(owner);
        Assert.Equal(0, health.PendingDeliveries); Assert.Equal(1, health.BlockedDeliveries);
        Assert.Equal(1, health.FailedDeliveries); Assert.Equal(1, health.RetryableDeliveries);
        Assert.Equal(1, health.OtherDestinationUnresolved);
        var details = health.RecentDeliveries!; Assert.Equal(4, details.Count);
        Assert.Equal("openai_http_400", details.Single(x => x.DeliveryId == failed.Id).ErrorCode);
        Assert.Equal("human_evidence_reviewed_needed", details.Single(x => x.DeliveryId == blocked.Id).ErrorCode);
        Assert.Equal(retry.NextAttemptUtc, details.Single(x => x.DeliveryId == retry.Id).NextAttemptUtc);
        Assert.Equal("previous_or_unverified", details.Single(x => x.DeliveryId == old.Id).DestinationBinding);
        Assert.Equal("unrecognized_error_code", details.Single(x => x.DeliveryId == old.Id).ErrorCode);
        var serialized = JsonSerializer.Serialize(health);
        Assert.DoesNotContain("private-", serialized); Assert.DoesNotContain("ClaimToken", serialized);
        for (var n = 0; n < 25; n++) db.Add(Row("pending"));
        await db.SaveChangesAsync();
        Assert.Equal(20, (await new OpenAiMeasurementHealthService(db, connections.Object, http).GetAsync(owner)).RecentDeliveries!.Count);
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

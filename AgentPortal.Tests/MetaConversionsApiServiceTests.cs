using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Infrastructure.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public class MetaConversionsApiServiceTests
{
    [Theory]
    [InlineData("business", null, null)]
    [InlineData("business", "business-pixel", null)]
    [InlineData("business", null, "business-token")]
    [InlineData("agent", null, null)]
    [InlineData("agent", "agent-pixel", null)]
    public async Task TenantEventsNeverUseConfiguredAgencyCredentials(string owner, string? pixel, string? token)
    {
        var handler = new RecordingHttpMessageHandler();
        var authority = new Mock<IMetaSendAuthority>();
        authority.Setup(x => x.TrySendAsync(It.IsAny<MetaSendAuthorityRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetaSendAuthorityDecision(true, "Lead", null,
                MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService, "key", "reservation", "allowed", null));
        var service = new MetaConversionsApiService(new HttpClient(handler),
            Options.Create(new MetaOptions { PixelId = "agency-pixel", AccessToken = "agency-token" }),
            authority.Object, NullLogger<MetaConversionsApiService>.Instance);
        var result = await service.SendEventAsync(new MetaConversionsApiEventRequest
        {
            EventName = "Lead", EventId = "scoped", PixelOwnerType = owner,
            PixelId = pixel, AccessToken = token,
            AuthoritySource = MetaSendAuthoritySources.MetaSignalOutcomeDispatcherHostedService
        });
        Assert.False(result.Attempted);
        Assert.False(result.Sent);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task SendEventAsync_BlocksServerTruthFromNonDispatcherSource()
    {
        var handler = new RecordingHttpMessageHandler();
        var authority = new Mock<IMetaSendAuthority>(MockBehavior.Strict);
        var service = new MetaConversionsApiService(
            new HttpClient(handler, disposeHandler: false),
            Options.Create(new MetaOptions
            {
                PixelId = "pixel-123",
                AccessToken = "token-123"
            }),
            authority.Object,
            NullLogger<MetaConversionsApiService>.Instance);

        var result = await service.SendEventAsync(new MetaConversionsApiEventRequest
        {
            CorrelationId = Guid.NewGuid(),
            EventName = "Lead",
            EventId = "lead-event-1",
            QuoteType = "life",
            PageKey = "quote_life_landing",
            OfferKey = "life",
            EventUtc = DateTime.UtcNow,
            AuthoritySource = MetaSendAuthoritySources.Controllers
        });

        Assert.False(result.Attempted);
        Assert.False(result.Sent);
        Assert.Equal("blocked_invalid_source", result.Status);
        Assert.Equal("single_truth_dispatcher_required", result.Note);
        Assert.Equal(0, handler.SendCount);
        authority.VerifyNoOtherCalls();
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

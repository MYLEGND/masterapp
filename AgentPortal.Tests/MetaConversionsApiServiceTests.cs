using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProtectWebsite.Services.Meta;
using Xunit;

namespace AgentPortal.Tests;

public class MetaConversionsApiServiceTests
{
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

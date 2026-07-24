using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Billing;
using Infrastructure.Billing.Square;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class SquareBillingGatewayTests
{
    [Fact]
    public async Task AttachPaymentMethod_NormalizesAnOversizedIdempotencyKeyBeforeCallingSquare()
    {
        string? requestBody = null;
        using var handler = new DelegatingHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v2/cards", request.RequestUri!.AbsolutePath);

            return JsonResponse(HttpStatusCode.OK, """
            {
              "card": {
                "id": "ccof_123",
                "card_status": "ACTIVE"
              }
            }
            """);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://square.example/") };
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient("MasterAppBilling.Square")).Returns(client);
        var gateway = CreateGateway(httpClientFactory.Object);
        var oversizedKey = $"client-activation-{Guid.NewGuid():N}-1";

        var result = await gateway.AttachPaymentMethodAsync(
            new BillingPaymentMethodAttachmentRequest(
                "cust_123",
                "cnon:card-nonce-safe",
                oversizedKey,
                "Test Client",
                "subscription_123"));

        using var requestJson = JsonDocument.Parse(requestBody!);
        var providerKey = requestJson.RootElement.GetProperty("idempotency_key").GetString();

        Assert.True(result.Success);
        Assert.Equal("ccof_123", result.ProviderPaymentMethodId);
        Assert.NotEqual(oversizedKey, providerKey);
        Assert.NotNull(providerKey);
        Assert.Equal(43, providerKey!.Length);
    }

    [Fact]
    public async Task AttachPaymentMethod_MapsSquareIdempotencyLimitErrorsToAnActionableSafeMessage()
    {
        using var handler = new DelegatingHttpMessageHandler(_ => Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, """
        {
          "errors": [
            {
              "code": "VALUE_TOO_LONG",
              "detail": "Field must not be greater than 45 length",
              "field": "idempotency_key"
            }
          ]
        }
        """)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://square.example/") };
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient("MasterAppBilling.Square")).Returns(client);
        var gateway = CreateGateway(httpClientFactory.Object);

        var result = await gateway.AttachPaymentMethodAsync(
            new BillingPaymentMethodAttachmentRequest(
                "cust_123",
                "cnon:card-nonce-safe",
                $"client-activation-{Guid.NewGuid():N}-1",
                "Test Client",
                "subscription_123"));

        Assert.False(result.Success);
        Assert.Equal("VALUE_TOO_LONG", result.SafeErrorCode);
        Assert.Equal(
            "The payment setup request used an internal identifier that exceeds Square's 45-character limit. No card was charged.",
            result.SanitizedSummary);
    }

    private static SquareBillingGateway CreateGateway(IHttpClientFactory httpClientFactory) =>
        new(
            httpClientFactory,
            new SquareBillingOptions
            {
                AccessToken = "square-access-token",
                LocationId = "location_123"
            },
            NullLogger<SquareBillingGateway>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class DelegatingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _sendAsync;

        public DelegatingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        {
            _sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _sendAsync(request);
    }
}

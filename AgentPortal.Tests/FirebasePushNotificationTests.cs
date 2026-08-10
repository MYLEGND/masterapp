using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FirebasePushNotificationTests
{
    [Fact]
    public async Task Fcm_registration_is_provider_scoped_and_does_not_change_apns_registration()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var engine = CreateEngine(db);
        var actor = new MessagingActor("push-user", MessagingParticipantTypes.Client);

        await engine.RegisterDeviceAsync(actor, "abcdef", "production");
        await engine.RegisterFcmDeviceAsync(actor, "fcm_opaque-registration:abc-123");

        var devices = db.MobilePushDevices.OrderBy(device => device.Provider).ToArray();
        Assert.Equal(2, devices.Length);
        Assert.Equal(MobilePushProviders.Apns, devices[0].Provider);
        Assert.Equal("production", devices[0].Environment);
        Assert.Equal(MobilePushProviders.Fcm, devices[1].Provider);
        Assert.Equal("not-applicable", devices[1].Environment);
        Assert.NotEqual(devices[0].TokenHash, devices[1].TokenHash);
    }

    [Fact]
    public async Task Fcm_gateway_forwards_only_server_presentation_and_route_metadata()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var gateway = CreateGateway(client, new FirebaseAccessTokenResult("server-access-token", null));
        var notificationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var result = await gateway.SendAsync(new FirebasePushDeliveryRequest(
            "fcm_opaque-registration:abc-123",
            "Jordan Lee",
            "Bonjou. I am ready when you are.",
            notificationId,
            3,
            conversationId));

        Assert.Equal(FirebasePushDeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(
            "https://fcm.googleapis.com/v1/projects/legend-fcm/messages:send",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Contains("\"title\":\"Jordan Lee\"", handler.Body);
        Assert.Contains("\"body\":\"Bonjou. I am ready when you are.\"", handler.Body);
        Assert.Contains(notificationId.ToString("D"), handler.Body);
        Assert.Contains(conversationId.ToString("D"), handler.Body);
        Assert.Contains("\"unreadCount\":\"3\"", handler.Body);
        Assert.DoesNotContain("originalBody", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("translation", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fcm_gateway_invalidates_only_explicitly_unregistered_devices()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.NotFound,
            """{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}"""));
        using var client = new HttpClient(handler, disposeHandler: false);
        var gateway = CreateGateway(client, new FirebaseAccessTokenResult("server-access-token", null));

        var result = await gateway.SendAsync(new FirebasePushDeliveryRequest(
            "fcm_opaque-registration:abc-123",
            "Jordan Lee",
            "A message arrived.",
            Guid.NewGuid(),
            1,
            null));

        Assert.Equal(FirebasePushDeliveryOutcome.InvalidDevice, result.Outcome);
        Assert.Contains("UNREGISTERED", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("fcm_opaque", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static NotificationEngine CreateEngine(Infrastructure.Data.MasterAppDbContext db) => new(
        db,
        new MessagingProfileImageResolver(db, NullLogger<MessagingProfileImageResolver>.Instance),
        new NoopRealtimePublisher(),
        new ApplePushDeliverySignal(),
        NullLogger<NotificationEngine>.Instance);

    private static FirebasePushGateway CreateGateway(HttpClient client, FirebaseAccessTokenResult accessToken)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient("FirebasePush")).Returns(client);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Fcm:ProjectId"] = "legend-fcm"
            })
            .Build();
        return new FirebasePushGateway(
            factory.Object,
            configuration,
            new StaticFirebaseAccessTokenProvider(accessToken),
            NullLogger<FirebasePushGateway>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticFirebaseAccessTokenProvider(FirebaseAccessTokenResult result) : IFirebaseAccessTokenProvider
    {
        public Task<FirebaseAccessTokenResult> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class NoopRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}

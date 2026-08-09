using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Mobile;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Infrastructure.Mobile;
using Infrastructure.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ApplePushNotificationTests
{
    [Theory]
    [InlineData("sandbox", "https://api.sandbox.push.apple.com/3/device/abcdef")]
    [InlineData("production", "https://api.push.apple.com/3/device/abcdef")]
    public async Task Gateway_uses_device_environment_topic_and_es256_provider_claims(
        string environment,
        string expectedEndpoint)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var gateway = CreateGateway(client, PrivateKeyPem(signingKey));

        var result = await gateway.SendAsync(Request(environment));

        Assert.Equal(ApplePushDeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(expectedEndpoint, handler.RequestUri?.AbsoluteUri);
        Assert.Equal(HttpVersion.Version20, handler.RequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.RequestVersionPolicy);
        Assert.Equal("com.mylegnd.legend.registered", handler.Headers["apns-topic"]);
        Assert.Equal("alert", handler.Headers["apns-push-type"]);
        Assert.Equal("10", handler.Headers["apns-priority"]);
        Assert.True(long.TryParse(handler.Headers["apns-expiration"], out var expiration));
        Assert.InRange(
            expiration,
            DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddHours(25).ToUnixTimeSeconds());
        Assert.Equal("bearer", handler.AuthorizationScheme);
        Assert.False(string.IsNullOrWhiteSpace(handler.ProviderToken));

        using var header = DecodeJwtSegment(handler.ProviderToken!, 0);
        using var payload = DecodeJwtSegment(handler.ProviderToken!, 1);
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("ABC123DEFG", header.RootElement.GetProperty("kid").GetString());
        Assert.Equal("Z8XL9RU485", payload.RootElement.GetProperty("iss").GetString());
        Assert.True(payload.RootElement.GetProperty("iat").GetInt64() > 0);
        Assert.Contains("\"badge\":3", handler.Body);
        Assert.Contains("\"sound\":\"default\"", handler.Body);
    }

    [Fact]
    public async Task Gateway_parses_bad_topic_without_invalidating_the_device()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var apnsId = Guid.NewGuid();
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.BadRequest,
            """{"reason":"BadTopic"}""",
            apnsId));
        using var client = new HttpClient(handler, disposeHandler: false);
        var gateway = CreateGateway(client, PrivateKeyPem(signingKey));

        var result = await gateway.SendAsync(Request("production"));

        Assert.Equal(ApplePushDeliveryOutcome.Suppressed, result.Outcome);
        Assert.True(ApplePushDiagnosticDetail.TryParse(result.Detail, out var detail));
        Assert.NotNull(detail);
        Assert.Equal(400, detail!.StatusCode);
        Assert.Equal("BadTopic", detail.Reason);
        Assert.Equal(apnsId.ToString("D"), detail.ApnsId);
        Assert.Equal("production", detail.Environment);
        Assert.Equal("com.mylegnd.legend.registered", detail.Topic);
        Assert.DoesNotContain("abcdef", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Gateway_marks_unregistered_token_invalid_and_retries_transient_failures()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var invalidHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Gone,
            """{"reason":"Unregistered","timestamp":1722470400000}""",
            Guid.NewGuid()));
        using var invalidClient = new HttpClient(invalidHandler, disposeHandler: false);
        var invalidGateway = CreateGateway(invalidClient, PrivateKeyPem(signingKey));

        var invalid = await invalidGateway.SendAsync(Request("sandbox"));

        Assert.Equal(ApplePushDeliveryOutcome.InvalidDevice, invalid.Outcome);
        Assert.True(ApplePushDiagnosticDetail.TryParse(invalid.Detail, out var invalidDetail));
        Assert.Equal("Unregistered", invalidDetail!.Reason);
        Assert.Equal(1722470400000, invalidDetail.Timestamp);

        using var retryHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """{"reason":"ServiceUnavailable"}""",
            Guid.NewGuid()));
        using var retryClient = new HttpClient(retryHandler, disposeHandler: false);
        var retryGateway = CreateGateway(retryClient, PrivateKeyPem(signingKey));

        var retry = await retryGateway.SendAsync(Request("production"));

        Assert.Equal(ApplePushDeliveryOutcome.Retry, retry.Outcome);
        Assert.True(ApplePushDiagnosticDetail.TryParse(retry.Detail, out var retryDetail));
        Assert.Equal(503, retryDetail!.StatusCode);
        Assert.Equal("ServiceUnavailable", retryDetail.Reason);
    }

    [Fact]
    public async Task Gateway_preserves_safe_transport_failure_classification_without_device_or_secret_data()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var handler = new ThrowingHandler(new HttpRequestException(
            HttpRequestError.ConnectionError,
            "sensitive transport detail that must not be persisted",
            new SocketException((int)SocketError.ConnectionReset)));
        using var client = new HttpClient(handler, disposeHandler: false);
        var gateway = CreateGateway(client, PrivateKeyPem(signingKey));

        var result = await gateway.SendAsync(Request("production"));

        Assert.Equal(ApplePushDeliveryOutcome.Retry, result.Outcome);
        Assert.True(ApplePushDiagnosticDetail.TryParse(result.Detail, out var detail));
        Assert.NotNull(detail);
        Assert.Null(detail!.StatusCode);
        Assert.Equal("APNs transport ConnectionError ConnectionReset", detail.Reason);
        Assert.Equal("production", detail.Environment);
        Assert.Equal("com.mylegnd.legend.registered", detail.Topic);
        Assert.DoesNotContain("abcdef", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_reregistration_corrects_environment_and_rejects_unknown_values()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var engine = CreateEngine(db);
        var actor = new MessagingActor("push-user", MessagingParticipantTypes.Client);

        await engine.RegisterDeviceAsync(actor, "ABCDEF", "sandbox");
        await engine.RegisterDeviceAsync(actor, "abcdef", "production");

        var device = Assert.Single(db.MobilePushDevices);
        Assert.Equal("production", device.Environment);
        Assert.True(device.IsActive);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.RegisterDeviceAsync(actor, "abcdef", "staging"));
    }

    [Fact]
    public async Task Message_notification_uses_the_sender_canonical_full_name()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.ClientProfiles.Add(new ClientProfile
        {
            ClientUserId = "sender-user",
            FirstName = "Avery",
            LastName = "Stone",
            Email = "avery.stone@example.test"
        });
        await db.SaveChangesAsync();

        var engine = CreateEngine(db);
        await engine.StageMessageForRecipientsAsync(
            new MessagingActor("sender-user", MessagingParticipantTypes.Client),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Hello there",
            DateTime.UtcNow,
            [new MessagingActor("recipient-user", MessagingParticipantTypes.Client)]);
        await db.SaveChangesAsync();

        var notification = Assert.Single(db.MobileActivityNotifications);
        Assert.Equal("Avery Stone", notification.Title);
    }

    [Fact]
    public async Task Message_notification_uses_the_agent_sender_full_name()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentProfiles.Add(new AgentProfile
        {
            AgentUserId = "agent-user",
            AgentUpn = "jordan.lee@example.test",
            FullName = "Jordan Lee"
        });
        await db.SaveChangesAsync();

        var engine = CreateEngine(db);
        await engine.StageMessageForRecipientsAsync(
            new MessagingActor("agent-user", MessagingParticipantTypes.Agent),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Hello there",
            DateTime.UtcNow,
            [new MessagingActor("recipient-user", MessagingParticipantTypes.Client)]);
        await db.SaveChangesAsync();

        var notification = Assert.Single(db.MobileActivityNotifications);
        Assert.Equal("Jordan Lee", notification.Title);
    }

    [Fact]
    public void Delivery_worker_abandons_permanent_device_failures_and_retries_transient_ones()
    {
        var now = DateTime.UtcNow;
        var invalid = new MobilePushDelivery();

        ApplePushDeliveryHostedService.ApplyResult(
            invalid,
            new ApplePushDeliveryResult(ApplePushDeliveryOutcome.InvalidDevice, "safe failure"),
            now);

        Assert.Equal(1, invalid.AttemptCount);
        Assert.Equal(now, invalid.AbandonedUtc);
        Assert.Null(invalid.SentUtc);

        var transient = new MobilePushDelivery();
        ApplePushDeliveryHostedService.ApplyResult(
            transient,
            new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Retry, "safe failure"),
            now);

        Assert.Equal(1, transient.AttemptCount);
        Assert.Null(transient.AbandonedUtc);
        Assert.Equal(now.AddSeconds(2), transient.NextAttemptUtc);
    }

    [Fact]
    public async Task Committed_notification_wakes_the_local_apns_outbox_immediately()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var signal = new ApplePushDeliverySignal();
        var engine = CreateEngine(db, signal);
        var recipient = new MessagingActor("push-recipient", MessagingParticipantTypes.Client);

        await engine.StageAsync(new MobileActivityNotification
        {
            RecipientUserId = recipient.UserId,
            RecipientParticipantType = recipient.ParticipantType,
            Kind = "Message",
            Title = "Jordan Lee",
            Detail = "Ready when you are.",
            OccurredUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await engine.ReconcileAndPublishAsync([recipient]);

        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Push_diagnostic_is_actor_scoped_and_contains_no_token_material()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var engine = CreateEngine(db);
        var actorA = new MessagingActor("push-user-a", MessagingParticipantTypes.Client);
        var actorB = new MessagingActor("push-user-b", MessagingParticipantTypes.Client);
        await engine.RegisterDeviceAsync(actorA, "abcdef", "production");
        var device = Assert.Single(db.MobilePushDevices);
        var notification = new MobileActivityNotification
        {
            RecipientUserId = actorA.UserId,
            RecipientParticipantType = actorA.ParticipantType,
            Kind = "message",
            Title = "A private message",
            Detail = "A message arrived."
        };
        db.MobileActivityNotifications.Add(notification);
        db.MobilePushDeliveries.Add(new MobilePushDelivery
        {
            NotificationId = notification.Id,
            MobilePushDeviceId = device.Id,
            AttemptCount = 1,
            AbandonedUtc = DateTime.UtcNow,
            LastError = ApplePushDiagnosticDetail.Create(
                403,
                "InvalidProviderToken",
                Guid.NewGuid().ToString("D"),
                null,
                "production",
                "com.mylegnd.legend.registered")
        });
        await db.SaveChangesAsync();

        var resolver = new Mock<IMobileActorResolver>();
        var selectedActor = new MobileResolvedActor(actorB, Guid.NewGuid(), "Other user");
        resolver.Setup(value => value.ResolveAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(
                true,
                null,
                null,
                [selectedActor],
                selectedActor,
                false));
        var controller = new MobileNotificationsController(resolver.Object, engine)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var action = await controller.ApnsStatus(CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(action);
        var status = Assert.IsType<MobilePushDiagnosticDto>(response.Value);
        Assert.Equal("missing", status.RegistrationState);
        Assert.Null(status.Environment);
        var serialized = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("DeviceToken", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static NotificationEngine CreateEngine(
        Infrastructure.Data.MasterAppDbContext db,
        IApplePushDeliverySignal? deliverySignal = null) =>
        new(
            db,
            new MessagingProfileImageResolver(
                db,
                NullLogger<MessagingProfileImageResolver>.Instance),
            new NoopRealtimePublisher(),
            deliverySignal ?? new ApplePushDeliverySignal(),
            NullLogger<NotificationEngine>.Instance);

    private static ApplePushGateway CreateGateway(HttpClient client, string privateKey)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient("ApplePush")).Returns(client);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:ApplePush:KeyId"] = "ABC123DEFG",
                ["Notifications:ApplePush:TeamId"] = "Z8XL9RU485",
                ["Notifications:ApplePush:BundleId"] = "com.mylegnd.legend.registered",
                ["Notifications:ApplePush:PrivateKey"] = privateKey
            })
            .Build();
        return new ApplePushGateway(
            factory.Object,
            configuration,
            NullLogger<ApplePushGateway>.Instance);
    }

    private static ApplePushDeliveryRequest Request(string environment) => new(
        "abcdef",
        environment,
        "Message",
        "A message arrived.",
        Guid.NewGuid(),
        3,
        null);

    private static string PrivateKeyPem(ECDsa key) =>
        new(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey()));

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string json,
        Guid? apnsId = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (apnsId is not null)
            response.Headers.TryAddWithoutValidation("apns-id", apnsId.Value.ToString("D"));
        return response;
    }

    private static JsonDocument DecodeJwtSegment(string token, int index)
    {
        var segment = token.Split('.')[index]
            .Replace('-', '+')
            .Replace('_', '/');
        segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(segment));
    }

    private sealed class NoopRealtimePublisher : INotificationRealtimePublisher
    {
        public Task PublishAsync(
            MessagingActor recipient,
            NotificationRealtimeEvent notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        public Uri? RequestUri { get; private set; }
        public Version? RequestVersion { get; private set; }
        public HttpVersionPolicy RequestVersionPolicy { get; private set; }
        public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();
        public string? AuthorizationScheme { get; private set; }
        public string? ProviderToken { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestVersion = request.Version;
            RequestVersionPolicy = request.VersionPolicy;
            Headers = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            ProviderToken = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response(request);
        }
    }
}

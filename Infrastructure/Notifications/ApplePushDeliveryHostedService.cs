using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Notifications;

internal enum ApplePushDeliveryOutcome
{
    Sent,
    Retry,
    InvalidDevice,
    Suppressed
}

internal sealed record ApplePushDeliveryRequest(
    string DeviceToken,
    string Environment,
    string Title,
    string Body,
    Guid NotificationId,
    int BadgeCount,
    Guid? ConversationId);

internal sealed record ApplePushDeliveryResult(
    ApplePushDeliveryOutcome Outcome,
    string? Detail = null);

internal interface IApplePushGateway
{
    Task<ApplePushDeliveryResult> SendAsync(
        ApplePushDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Token-based APNs sender. It is deliberately configuration-gated: absent
/// production credentials suppress delivery safely while the database ledger
/// and foreground WebSocket synchronization remain fully authoritative.
/// </summary>
internal sealed class ApplePushGateway : IApplePushGateway
{
    private readonly IHttpClientFactory _httpClients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApplePushGateway> _logger;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedProviderToken;
    private DateTime _cachedProviderTokenIssuedUtc;

    public ApplePushGateway(
        IHttpClientFactory httpClients,
        IConfiguration configuration,
        ILogger<ApplePushGateway> logger)
    {
        _httpClients = httpClients;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ApplePushDeliveryResult> SendAsync(
        ApplePushDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var configuration = ReadConfiguration();
        if (configuration is null)
            return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Suppressed, "APNs is not configured.");

        try
        {
            var providerToken = await ProviderTokenAsync(configuration, cancellationToken);
            var host = string.Equals(request.Environment, "sandbox", StringComparison.OrdinalIgnoreCase)
                ? "https://api.sandbox.push.apple.com"
                : "https://api.push.apple.com";
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"{host}/3/device/{request.DeviceToken}");
            message.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerToken);
            message.Headers.TryAddWithoutValidation("apns-topic", configuration.BundleId);
            message.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            message.Headers.TryAddWithoutValidation("apns-priority", "10");
            message.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    aps = new Dictionary<string, object?>
                    {
                        ["alert"] = new { title = request.Title, body = request.Body },
                        ["badge"] = Math.Max(0, request.BadgeCount),
                        ["sound"] = "default",
                        ["mutable-content"] = 1
                    },
                    notificationId = request.NotificationId,
                    conversationId = request.ConversationId,
                    unreadCount = Math.Max(0, request.BadgeCount)
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClients.CreateClient("ApplePush")
                .SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Sent);

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Gone)
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.InvalidDevice, Clip(detail));
            if ((int)response.StatusCode is 429 or >= 500)
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Retry, Clip(detail));
            return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Suppressed, Clip(detail));
        }
        catch (HttpRequestException exception)
        {
            return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Retry, exception.Message);
        }
        catch (CryptographicException exception)
        {
            _logger.LogError(exception, "APNs provider token could not be generated.");
            return new ApplePushDeliveryResult(
                ApplePushDeliveryOutcome.Suppressed,
                Clip($"APNs cryptographic failure: {exception.GetType().Name}: {exception.Message}"));
        }
    }

    private async Task<string> ProviderTokenAsync(
        ApplePushConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedProviderToken) &&
            DateTime.UtcNow - _cachedProviderTokenIssuedUtc < TimeSpan.FromMinutes(50))
        {
            return _cachedProviderToken;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedProviderToken) &&
                DateTime.UtcNow - _cachedProviderTokenIssuedUtc < TimeSpan.FromMinutes(50))
            {
                return _cachedProviderToken;
            }

            var issuedUtc = DateTime.UtcNow;
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "ES256",
                kid = configuration.KeyId
            }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = configuration.TeamId,
                iat = new DateTimeOffset(issuedUtc).ToUnixTimeSeconds()
            }));
            var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");

            var pem = configuration.PrivateKey.AsSpan();
            if (!PemEncoding.TryFind(pem, out var pemFields) ||
                !pem[pemFields.Label].SequenceEqual("PRIVATE KEY"))
            {
                throw new CryptographicException("APNs private key is not a PKCS#8 PRIVATE KEY PEM.");
            }

            var keyBytes = Convert.FromBase64String(pem[pemFields.Base64Data].ToString());

            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out var bytesRead);

            if (bytesRead != keyBytes.Length)
                throw new CryptographicException("APNs PKCS#8 private key was not fully consumed.");

            var signature = key.SignData(
                signingInput,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            _cachedProviderToken = $"{header}.{payload}.{Base64Url(signature)}";
            _cachedProviderTokenIssuedUtc = issuedUtc;
            return _cachedProviderToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private ApplePushConfiguration? ReadConfiguration()
    {
        var keyId = _configuration["Notifications:ApplePush:KeyId"]?.Trim();
        var teamId = _configuration["Notifications:ApplePush:TeamId"]?.Trim();
        var bundleId = _configuration["Notifications:ApplePush:BundleId"]?.Trim();
        var privateKey = _configuration["Notifications:ApplePush:PrivateKey"]?
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Trim();
        return string.IsNullOrWhiteSpace(keyId) ||
               string.IsNullOrWhiteSpace(teamId) ||
               string.IsNullOrWhiteSpace(bundleId) ||
               string.IsNullOrWhiteSpace(privateKey)
            ? null
            : new ApplePushConfiguration(keyId, teamId, bundleId, privateKey);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string Clip(string value) => value.Length <= 1_000 ? value : value[..1_000];

    private sealed record ApplePushConfiguration(
        string KeyId,
        string TeamId,
        string BundleId,
        string PrivateKey);
}

internal sealed class ApplePushDeliveryHostedService : BackgroundService
{
    private const int MaximumAttempts = 6;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplePushGateway _gateway;
    private readonly ILogger<ApplePushDeliveryHostedService> _logger;

    public ApplePushDeliveryHostedService(
        IServiceScopeFactory scopeFactory,
        IApplePushGateway gateway,
        ILogger<ApplePushDeliveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _gateway = gateway;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverDueNotificationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "APNs notification delivery pass failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task DeliverDueNotificationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<INotificationEngine>();
        var now = DateTime.UtcNow;
        var candidates = await (
                from delivery in db.MobilePushDeliveries
                join notification in db.MobileActivityNotifications on delivery.NotificationId equals notification.Id
                join device in db.MobilePushDevices on delivery.MobilePushDeviceId equals device.Id
                where delivery.SentUtc == null &&
                      delivery.AbandonedUtc == null &&
                      delivery.NextAttemptUtc <= now &&
                      delivery.AttemptCount < MaximumAttempts &&
                      device.IsActive
                orderby delivery.NextAttemptUtc, delivery.Id
                select new DeliveryCandidate(
                    delivery.Id,
                    notification.Id,
                    notification.RecipientUserId,
                    notification.RecipientParticipantType,
                    notification.Title,
                    notification.Detail,
                    notification.ConversationId,
                    notification.IsRead,
                    notification.IsCleared,
                    device.Id,
                    device.DeviceToken,
                    device.Environment))
            .Take(50)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var delivery = await db.MobilePushDeliveries.FindAsync([candidate.DeliveryId], cancellationToken);
            if (delivery is null || delivery.SentUtc is not null || delivery.AbandonedUtc is not null)
                continue;

            if (candidate.IsRead || candidate.IsCleared)
            {
                delivery.AbandonedUtc = now;
                delivery.LastError = "Notification no longer unread.";
                continue;
            }

            var snapshot = await engine.GetSnapshotAsync(
                new MessagingActor(candidate.RecipientUserId, candidate.RecipientParticipantType),
                take: 1,
                cancellationToken);
            var result = await _gateway.SendAsync(
                new ApplePushDeliveryRequest(
                    candidate.DeviceToken,
                    candidate.Environment,
                    candidate.Title,
                    candidate.Detail,
                    candidate.NotificationId,
                    snapshot.Badge.UnreadCount,
                    candidate.ConversationId),
                cancellationToken);
            ApplyResult(delivery, result, now);

            if (result.Outcome == ApplePushDeliveryOutcome.InvalidDevice)
            {
                var device = await db.MobilePushDevices.FindAsync([candidate.DeviceId], cancellationToken);
                if (device is not null)
                {
                    device.IsActive = false;
                    device.InvalidatedUtc = now;
                    device.UpdatedUtc = now;
                }
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyResult(
        MobilePushDelivery delivery,
        ApplePushDeliveryResult result,
        DateTime now)
    {
        delivery.AttemptCount++;
        delivery.LastError = result.Detail;
        switch (result.Outcome)
        {
            case ApplePushDeliveryOutcome.Sent:
                delivery.SentUtc = now;
                break;
            case ApplePushDeliveryOutcome.InvalidDevice:
            case ApplePushDeliveryOutcome.Suppressed:
                delivery.AbandonedUtc = now;
                break;
            case ApplePushDeliveryOutcome.Retry:
                delivery.NextAttemptUtc = now.AddSeconds(Math.Min(
                    300,
                    Math.Pow(2, delivery.AttemptCount)));
                break;
        }
    }

    private sealed record DeliveryCandidate(
        Guid DeliveryId,
        Guid NotificationId,
        string RecipientUserId,
        string RecipientParticipantType,
        string Title,
        string Detail,
        Guid? ConversationId,
        bool IsRead,
        bool IsCleared,
        Guid DeviceId,
        string DeviceToken,
        string Environment);
}

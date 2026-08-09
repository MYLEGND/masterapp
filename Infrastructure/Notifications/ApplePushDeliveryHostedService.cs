using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

/// <summary>
/// The safe, durable subset of an APNs failure response.  Delivery records are
/// intentionally useful to the authenticated mobile diagnostic without ever
/// retaining an opaque device token, provider JWT, or response body.
/// </summary>
internal sealed record ApplePushDiagnosticDetail(
    int? StatusCode,
    string? Reason,
    string? ApnsId,
    long? Timestamp,
    string Environment,
    string? Topic)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Create(
        int? statusCode,
        string? reason,
        string? apnsId,
        long? timestamp,
        string environment,
        string? topic) =>
        JsonSerializer.Serialize(
            new ApplePushDiagnosticDetail(
                statusCode,
                SanitizeReason(reason),
                Guid.TryParse(apnsId, out var parsedApnsId)
                    ? parsedApnsId.ToString("D")
                    : null,
                timestamp,
                string.Equals(environment, "sandbox", StringComparison.OrdinalIgnoreCase)
                    ? "sandbox"
                    : "production",
                SanitizeTopic(topic)),
            SerializerOptions);

    public static bool TryParse(string? value, out ApplePushDiagnosticDetail? detail)
    {
        detail = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            detail = JsonSerializer.Deserialize<ApplePushDiagnosticDetail>(value, SerializerOptions);
            return detail is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? SanitizeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 160 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is ' ' or '.' or '_' or '-')
            ? normalized
            : "APNs request failed.";
    }

    private static string? SanitizeTopic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 255 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-')
            ? normalized
            : null;
    }
}

internal interface IApplePushGateway
{
    Task<ApplePushDeliveryResult> SendAsync(
        ApplePushDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Wakes the local APNs outbox worker immediately after a notification has been
/// committed. The database remains the durable cross-instance authority; the
/// one-second polling fallback handles deployments with more than one worker
/// or a process restart without delaying the normal, in-process path.
/// </summary>
internal interface IApplePushDeliverySignal
{
    void Notify();

    Task<bool> WaitAsync(
        TimeSpan fallbackInterval,
        CancellationToken cancellationToken = default);
}

internal sealed class ApplePushDeliverySignal : IApplePushDeliverySignal
{
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Notify() => _signals.Writer.TryWrite(0);

    public async Task<bool> WaitAsync(
        TimeSpan fallbackInterval,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(fallbackInterval);
        try
        {
            await _signals.Reader.ReadAsync(timeout.Token);
            while (_signals.Reader.TryRead(out _))
            {
                // Coalesce a burst of committed notification entries into one
                // outbox pass; every entry is still read from the database.
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
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
        var configuration = ReadConfiguration(out var configurationFailure);
        if (configuration is null)
        {
            return Suppressed(
                request,
                topic: null,
                reason: configurationFailure);
        }

        try
        {
            var providerToken = await ProviderTokenAsync(configuration, cancellationToken);
            var host = string.Equals(request.Environment, "sandbox", StringComparison.OrdinalIgnoreCase)
                ? "https://api.sandbox.push.apple.com"
                : "https://api.push.apple.com";
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"{host}/3/device/{request.DeviceToken}")
            {
                // HttpRequestMessage starts at HTTP/1.1 and RequestVersionOrLower.
                // APNs requires HTTP/2, so the request itself—not only the named
                // client's defaults—must carry the exact version requirement.
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerToken);
            message.Headers.TryAddWithoutValidation("apns-topic", configuration.BundleId);
            message.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            message.Headers.TryAddWithoutValidation("apns-priority", "10");
            // APNs defaults this to immediate expiry. Keep an authenticated alert
            // available for a day when the phone is briefly offline, while still
            // using priority 10 for immediate delivery whenever it is reachable.
            message.Headers.TryAddWithoutValidation(
                "apns-expiration",
                DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds().ToString());
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

            using var response = await _httpClients.CreateClient("ApplePush")
                .SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Sent);

            var failure = await ReadFailureAsync(response, request, configuration.BundleId, cancellationToken);
            if (IsPermanentlyInvalidDevice(response.StatusCode, failure.Reason))
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.InvalidDevice, failure.Detail);
            if (string.Equals(failure.Reason, "ExpiredProviderToken", StringComparison.Ordinal))
                _cachedProviderToken = null;
            if ((int)response.StatusCode is 429 or >= 500)
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Retry, failure.Detail);
            if (string.Equals(failure.Reason, "ExpiredProviderToken", StringComparison.Ordinal))
                return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Retry, failure.Detail);
            return new ApplePushDeliveryResult(ApplePushDeliveryOutcome.Suppressed, failure.Detail);
        }
        catch (HttpRequestException exception)
        {
            var transportReason = TransportFailureReason(exception);
            _logger.LogError(
                "APNs HTTP request failed. HttpRequestError={HttpRequestError} InnerException={InnerException} SocketError={SocketError}",
                exception.HttpRequestError,
                exception.InnerException?.GetType().Name ?? "None",
                FindSocketException(exception)?.SocketErrorCode.ToString() ?? "None");
            return Retry(request, configuration.BundleId, transportReason);
        }
        catch (CryptographicException)
        {
            _logger.LogError("APNs provider token could not be generated.");
            return Suppressed(request, configuration.BundleId, "APNs credential configuration is invalid.");
        }
    }

    private static string TransportFailureReason(HttpRequestException exception)
    {
        var parts = new List<string>
        {
            "APNs transport",
            exception.HttpRequestError.ToString()
        };

        var socketException = FindSocketException(exception);
        if (socketException is not null)
            parts.Add(socketException.SocketErrorCode.ToString());
        else if (exception.InnerException is not null)
            parts.Add(exception.InnerException.GetType().Name);

        return string.Join(' ', parts);
    }

    private static SocketException? FindSocketException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
                return socketException;
        }

        return null;
    }

    private static bool IsPermanentlyInvalidDevice(HttpStatusCode statusCode, string? reason) =>
        statusCode == HttpStatusCode.Gone &&
        string.Equals(reason, "Unregistered", StringComparison.Ordinal) ||
        statusCode == HttpStatusCode.BadRequest &&
        (string.Equals(reason, "BadDeviceToken", StringComparison.Ordinal) ||
         string.Equals(reason, "DeviceTokenNotForTopic", StringComparison.Ordinal));

    private static async Task<(string? Reason, string Detail)> ReadFailureAsync(
        HttpResponseMessage response,
        ApplePushDeliveryRequest request,
        string topic,
        CancellationToken cancellationToken)
    {
        string? reason = null;
        long? timestamp = null;
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("reason", out var reasonProperty) &&
                reasonProperty.ValueKind == JsonValueKind.String)
            {
                reason = reasonProperty.GetString();
            }
            if (document.RootElement.TryGetProperty("timestamp", out var timestampProperty) &&
                timestampProperty.ValueKind == JsonValueKind.Number &&
                timestampProperty.TryGetInt64(out var value))
            {
                timestamp = value;
            }
        }
        catch (JsonException)
        {
            reason = "APNs request failed.";
        }

        var apnsId = response.Headers.TryGetValues("apns-id", out var values)
            ? values.FirstOrDefault()
            : null;
        var detail = ApplePushDiagnosticDetail.Create(
            (int)response.StatusCode,
            reason,
            apnsId,
            timestamp,
            request.Environment,
            topic);
        return (reason, detail);
    }

    private static ApplePushDeliveryResult Retry(
        ApplePushDeliveryRequest request,
        string? topic,
        string reason) =>
        new(
            ApplePushDeliveryOutcome.Retry,
            ApplePushDiagnosticDetail.Create(
                statusCode: null,
                reason,
                apnsId: null,
                timestamp: null,
                request.Environment,
                topic));

    private static ApplePushDeliveryResult Suppressed(
        ApplePushDeliveryRequest request,
        string? topic,
        string reason) =>
        new(
            ApplePushDeliveryOutcome.Suppressed,
            ApplePushDiagnosticDetail.Create(
                statusCode: null,
                reason,
                apnsId: null,
                timestamp: null,
                request.Environment,
                topic));

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

    private ApplePushConfiguration? ReadConfiguration(out string failure)
    {
        var keyId = _configuration["Notifications:ApplePush:KeyId"]?.Trim();
        var teamId = _configuration["Notifications:ApplePush:TeamId"]?.Trim();
        var bundleId = _configuration["Notifications:ApplePush:BundleId"]?.Trim();
        var privateKey = _configuration["Notifications:ApplePush:PrivateKey"]?
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrWhiteSpace(keyId) ||
            string.IsNullOrWhiteSpace(teamId) ||
            string.IsNullOrWhiteSpace(bundleId) ||
            string.IsNullOrWhiteSpace(privateKey))
        {
            failure = "APNs is not configured.";
            return null;
        }

        if (!IsAppleIdentifier(keyId) ||
            !IsAppleIdentifier(teamId) ||
            !IsBundleIdentifier(bundleId))
        {
            failure = "APNs credential configuration is invalid.";
            return null;
        }

        failure = string.Empty;
        return new ApplePushConfiguration(keyId, teamId, bundleId, privateKey);
    }

    private static bool IsAppleIdentifier(string value) =>
        value.Length == 10 && value.All(char.IsLetterOrDigit);

    private static bool IsBundleIdentifier(string value) =>
        value.Length is > 0 and <= 255 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-');

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private sealed record ApplePushConfiguration(
        string KeyId,
        string TeamId,
        string BundleId,
        string PrivateKey);
}

internal sealed class ApplePushDeliveryHostedService : BackgroundService
{
    private const int MaximumAttempts = 6;
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplePushGateway _gateway;
    private readonly IApplePushDeliverySignal _signal;
    private readonly ILogger<ApplePushDeliveryHostedService> _logger;

    public ApplePushDeliveryHostedService(
        IServiceScopeFactory scopeFactory,
        IApplePushGateway gateway,
        IApplePushDeliverySignal signal,
        ILogger<ApplePushDeliveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _gateway = gateway;
        _signal = signal;
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

            await _signal.WaitAsync(FallbackPollInterval, stoppingToken);
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

    internal static void ApplyResult(
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

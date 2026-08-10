using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Domain.Messaging;
using Google.Apis.Auth.OAuth2;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Notifications;

internal enum FirebasePushDeliveryOutcome
{
    Sent,
    Retry,
    InvalidDevice,
    Suppressed
}

internal sealed record FirebasePushDeliveryRequest(
    string DeviceToken,
    string Title,
    string Body,
    Guid NotificationId,
    int BadgeCount,
    Guid? ConversationId);

internal sealed record FirebasePushDeliveryResult(
    FirebasePushDeliveryOutcome Outcome,
    string? Detail = null);

internal interface IFirebasePushGateway
{
    Task<FirebasePushDeliveryResult> SendAsync(
        FirebasePushDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record FirebaseAccessTokenResult(string? AccessToken, string? Failure)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>
/// Holds FCM transport credentials only in process memory. The service-account
/// JSON comes from the deployment secret store; it is never written, logged,
/// returned by an endpoint, or included in a notification payload.
/// </summary>
internal interface IFirebaseAccessTokenProvider
{
    Task<FirebaseAccessTokenResult> GetAsync(CancellationToken cancellationToken = default);
}

internal sealed class FirebaseAccessTokenProvider : IFirebaseAccessTokenProvider
{
    private const string FirebaseMessagingScope = "https://www.googleapis.com/auth/firebase.messaging";
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleCredential? _credential;
    private string? _configurationFingerprint;

    public FirebaseAccessTokenProvider(IConfiguration configuration) => _configuration = configuration;

    public async Task<FirebaseAccessTokenResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var serviceAccountJson = _configuration["Notifications:Fcm:ServiceAccountJson"]?.Trim();
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
            return new FirebaseAccessTokenResult(null, "FCM is not configured.");

        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serviceAccountJson)));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_credential is null || !string.Equals(_configurationFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _credential = CredentialFactory
                        .FromJson<ServiceAccountCredential>(serviceAccountJson)
                        .ToGoogleCredential()
                        .CreateScoped(FirebaseMessagingScope);
                    _configurationFingerprint = fingerprint;
                }

                var token = await _credential.UnderlyingCredential.GetAccessTokenForRequestAsync(
                    "https://fcm.googleapis.com/",
                    cancellationToken);
                return string.IsNullOrWhiteSpace(token)
                    ? new FirebaseAccessTokenResult(null, "FCM authorization is unavailable.")
                    : new FirebaseAccessTokenResult(token, null);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) // Provider exceptions must remain secret-free at this boundary.
        {
            return new FirebaseAccessTokenResult(null, "FCM authorization is unavailable.");
        }
    }
}

/// <summary>
/// FCM is a last-mile transport only. It consumes the existing notification
/// ledger/outbox and forwards the server-produced recipient-localized title,
/// detail, badge, and route metadata without generating notification meaning.
/// </summary>
internal sealed class FirebasePushGateway : IFirebasePushGateway
{
    private const string ChannelId = "legend_activity";
    private readonly IHttpClientFactory _httpClients;
    private readonly IConfiguration _configuration;
    private readonly IFirebaseAccessTokenProvider _tokens;
    private readonly ILogger<FirebasePushGateway> _logger;

    public FirebasePushGateway(
        IHttpClientFactory httpClients,
        IConfiguration configuration,
        IFirebaseAccessTokenProvider tokens,
        ILogger<FirebasePushGateway> logger)
    {
        _httpClients = httpClients;
        _configuration = configuration;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<FirebasePushDeliveryResult> SendAsync(
        FirebasePushDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = _configuration["Notifications:Fcm:ProjectId"]?.Trim();
        if (!IsProjectId(projectId))
            return new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.Suppressed, "FCM is not configured.");

        var accessToken = await _tokens.GetAsync(cancellationToken);
        if (!accessToken.IsAvailable)
            return new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.Suppressed, accessToken.Failure);

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.AccessToken);
            message.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    message = new
                    {
                        token = request.DeviceToken,
                        // Both Android foreground handling and background system-tray delivery use
                        // this server-authoritative localized presentation.
                        notification = new { title = request.Title, body = request.Body },
                        data = new Dictionary<string, string>
                        {
                            ["notificationId"] = request.NotificationId.ToString("D"),
                            ["conversationId"] = request.ConversationId?.ToString("D") ?? string.Empty,
                            ["unreadCount"] = Math.Max(0, request.BadgeCount).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        },
                        android = new
                        {
                            priority = "HIGH",
                            notification = new
                            {
                                channel_id = ChannelId,
                                sound = "default",
                                notification_priority = "PRIORITY_DEFAULT"
                            }
                        }
                    }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClients.CreateClient("FirebasePush")
                .SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.Sent);

            var detail = await ReadFailureAsync(response, cancellationToken);
            if (detail.ErrorCode == "UNREGISTERED")
                return new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.InvalidDevice, detail.Serialized);
            if ((int)response.StatusCode is 429 or >= 500)
                return new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.Retry, detail.Serialized);
            return new FirebasePushDeliveryResult(FirebasePushDeliveryOutcome.Suppressed, detail.Serialized);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(
                "FCM HTTP request failed. HttpRequestError={HttpRequestError} InnerException={InnerException}",
                exception.HttpRequestError,
                exception.InnerException?.GetType().Name ?? "None");
            return new FirebasePushDeliveryResult(
                FirebasePushDeliveryOutcome.Retry,
                FirebasePushDiagnosticDetail.Create(null, "FCM transport unavailable."));
        }
    }

    private static bool IsProjectId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is <= 100 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static async Task<FirebaseFailure> ReadFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        string? fcmErrorCode = null;
        string? providerStatus = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("status", out var statusElement))
                    providerStatus = statusElement.GetString();
                if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in details.EnumerateArray())
                    {
                        if (item.TryGetProperty("errorCode", out var code))
                        {
                            fcmErrorCode = code.GetString();
                            break;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Provider bodies are untrusted and are never stored verbatim.
        }

        var safeCode = FirebasePushDiagnosticDetail.SanitizeCode(fcmErrorCode ?? providerStatus);
        return new FirebaseFailure(
            safeCode,
            FirebasePushDiagnosticDetail.Create((int)status, safeCode));
    }

    private sealed record FirebaseFailure(string? ErrorCode, string Serialized);
}

/// <summary>Safe, token-free FCM delivery detail retained on the durable outbox.</summary>
internal sealed record FirebasePushDiagnosticDetail(int? StatusCode, string? Code)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Create(int? statusCode, string? code) =>
        JsonSerializer.Serialize(
            new FirebasePushDiagnosticDetail(statusCode, SanitizeCode(code)),
            SerializerOptions);

    public static string? SanitizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length <= 80 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is ' ' or '.' or '_' or '-')
            ? normalized
            : "FCM request failed.";
    }
}

internal sealed class FirebasePushDeliveryHostedService : BackgroundService
{
    private const int MaximumAttempts = 6;
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFirebasePushGateway _gateway;
    private readonly ILogger<FirebasePushDeliveryHostedService> _logger;

    public FirebasePushDeliveryHostedService(
        IServiceScopeFactory scopeFactory,
        IFirebasePushGateway gateway,
        ILogger<FirebasePushDeliveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _gateway = gateway;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FallbackPollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
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
                _logger.LogError(exception, "FCM notification delivery pass failed.");
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
                      device.IsActive &&
                      device.Provider == MobilePushProviders.Fcm
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
                    device.DeviceToken))
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
                new FirebasePushDeliveryRequest(
                    candidate.DeviceToken,
                    candidate.Title,
                    candidate.Detail,
                    candidate.NotificationId,
                    snapshot.Badge.UnreadCount,
                    candidate.ConversationId),
                cancellationToken);
            ApplyResult(delivery, result, now);

            if (result.Outcome == FirebasePushDeliveryOutcome.InvalidDevice)
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
        FirebasePushDeliveryResult result,
        DateTime now)
    {
        delivery.AttemptCount++;
        delivery.LastError = result.Detail;
        switch (result.Outcome)
        {
            case FirebasePushDeliveryOutcome.Sent:
                delivery.SentUtc = now;
                break;
            case FirebasePushDeliveryOutcome.InvalidDevice:
            case FirebasePushDeliveryOutcome.Suppressed:
                delivery.AbandonedUtc = now;
                break;
            case FirebasePushDeliveryOutcome.Retry:
                delivery.NextAttemptUtc = now.AddSeconds(Math.Min(300, Math.Pow(2, delivery.AttemptCount)));
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
        string DeviceToken);
}

using System.Security.Cryptography;
using System.Text;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Notifications;

public sealed record NotificationLedgerItem(
    Guid Id,
    string Kind,
    string Title,
    string Detail,
    Guid? ConversationId,
    DateTime OccurredUtc,
    bool IsRead,
    bool IsCleared);

public sealed record NotificationBadgeSnapshot(
    int UnreadCount,
    long Revision,
    DateTime UpdatedUtc);

public sealed record NotificationSnapshot(
    NotificationBadgeSnapshot Badge,
    IReadOnlyList<NotificationLedgerItem> Notifications);

/// <summary>
/// Safe APNs state for the authenticated actor's existing device registration.
/// This projection deliberately excludes the opaque token and its hash.
/// </summary>
public sealed record MobilePushDiagnosticSnapshot(
    string RegistrationState,
    string? Environment,
    DateTime? LastRegistrationUtc,
    string LastRegistrationResult,
    DateTime? LastDeliveryUtc,
    string DeliveryState,
    int? LastApnsStatus,
    string? LastApnsReason,
    int? DeliveryAttemptCount);

public sealed record NotificationRealtimeEvent(
    Guid? NotificationId,
    int UnreadCount,
    long Revision,
    DateTime OccurredUtc);

public interface INotificationRealtimePublisher
{
    Task PublishAsync(
        MessagingActor recipient,
        NotificationRealtimeEvent notification,
        CancellationToken cancellationToken = default);
}

public interface INotificationEngine
{
    /// <summary>Stages recipient entries in the caller's current database unit of work.</summary>
    Task<IReadOnlyList<MessagingActor>> StageMessageAsync(
        MessagingActor sender,
        Guid conversationId,
        Guid messageId,
        string body,
        DateTime occurredUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a first message while its conversation participants are still in
    /// the caller's unit of work and therefore not queryable from the database.
    /// </summary>
    Task<IReadOnlyList<MessagingActor>> StageMessageForRecipientsAsync(
        MessagingActor sender,
        Guid conversationId,
        Guid messageId,
        string body,
        DateTime occurredUtc,
        IEnumerable<MessagingActor> recipients,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a non-message event in the caller's current database unit of work.</summary>
    Task StageAsync(
        MobileActivityNotification notification,
        CancellationToken cancellationToken = default);

    /// <summary>Stages read state; the caller saves it with its primary domain mutation.</summary>
    Task StageConversationReadAsync(
        MessagingActor actor,
        Guid conversationId,
        DateTime readUtc,
        CancellationToken cancellationToken = default);

    Task<NotificationSnapshot> GetSnapshotAsync(
        MessagingActor actor,
        int take,
        CancellationToken cancellationToken = default);

    Task<NotificationBadgeSnapshot> MarkReadAndPublishAsync(
        MessagingActor actor,
        Guid notificationId,
        CancellationToken cancellationToken = default);

    Task<NotificationBadgeSnapshot> ClearBadgeAndPublishAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);

    Task<NotificationBadgeSnapshot> ReconcileAndPublishAsync(
        IEnumerable<MessagingActor> actors,
        CancellationToken cancellationToken = default);

    Task RegisterDeviceAsync(
        MessagingActor actor,
        string deviceToken,
        string environment,
        CancellationToken cancellationToken = default);

    Task DeactivateDeviceAsync(
        MessagingActor actor,
        string deviceToken,
        CancellationToken cancellationToken = default);

    Task<MobilePushDiagnosticSnapshot> GetPushDiagnosticAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only notification state coordinator. It stages every recipient event in
/// the durable ledger, reconciles the database badge projection from that
/// ledger, and then emits a server-authoritative live update. No app surface is
/// allowed to derive an icon badge by summing its own feature data.
/// </summary>
internal sealed class NotificationEngine : INotificationEngine
{
    private const int MaximumTitleLength = 240;
    private const int MaximumDetailLength = 1_000;
    private const int MaximumDeviceTokenLength = 512;
    private readonly MasterAppDbContext _db;
    private readonly INotificationRealtimePublisher _realtime;
    private readonly ILogger<NotificationEngine> _logger;

    public NotificationEngine(
        MasterAppDbContext db,
        INotificationRealtimePublisher realtime,
        ILogger<NotificationEngine> logger)
    {
        _db = db;
        _realtime = realtime;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MessagingActor>> StageMessageAsync(
        MessagingActor sender,
        Guid conversationId,
        Guid messageId,
        string body,
        DateTime occurredUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedSender = Normalize(sender);
        var recipients = await _db.MessageConversationParticipants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationId && participant.IsActive)
            .Select(participant => new MessagingActor(participant.UserId, participant.ParticipantType))
            .ToListAsync(cancellationToken);

        return await StageMessageForRecipientsAsync(
            sender,
            conversationId,
            messageId,
            body,
            occurredUtc,
            recipients,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MessagingActor>> StageMessageForRecipientsAsync(
        MessagingActor sender,
        Guid conversationId,
        Guid messageId,
        string body,
        DateTime occurredUtc,
        IEnumerable<MessagingActor> recipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var normalizedSender = Normalize(sender);
        var stagedRecipients = new List<MessagingActor>();
        foreach (var recipient in recipients.Select(Normalize).Distinct())
        {
            if (IsSameActor(recipient, normalizedSender))
                continue;

            var exists = await _db.MobileActivityNotifications.AnyAsync(
                notification => notification.SourceMessageId == messageId &&
                                notification.RecipientUserId == recipient.UserId &&
                                notification.RecipientParticipantType == recipient.ParticipantType,
                cancellationToken);
            if (exists)
                continue;

            var notification = new MobileActivityNotification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = recipient.UserId,
                RecipientParticipantType = recipient.ParticipantType,
                Kind = "Message",
                Title = "New message",
                Detail = Clip(body, MaximumDetailLength),
                ConversationId = conversationId,
                SourceMessageId = messageId,
                OccurredUtc = occurredUtc,
                IsRead = false,
                IsCleared = false
            };
            _db.MobileActivityNotifications.Add(notification);
            await StageDeliveriesAsync(notification, recipient, cancellationToken);
            stagedRecipients.Add(recipient);
        }

        return stagedRecipients;
    }

    public async Task StageAsync(
        MobileActivityNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var recipient = Normalize(new MessagingActor(
            notification.RecipientUserId,
            notification.RecipientParticipantType));
        notification.RecipientUserId = recipient.UserId;
        notification.RecipientParticipantType = recipient.ParticipantType;
        notification.Kind = Clip(notification.Kind, 80);
        notification.Title = Clip(notification.Title, MaximumTitleLength);
        notification.Detail = Clip(notification.Detail, MaximumDetailLength);
        notification.IsRead = false;
        notification.IsCleared = false;
        notification.ReadUtc = null;
        notification.ClearedUtc = null;
        _db.MobileActivityNotifications.Add(notification);
        await StageDeliveriesAsync(notification, recipient, cancellationToken);
    }

    public async Task StageConversationReadAsync(
        MessagingActor actor,
        Guid conversationId,
        DateTime readUtc,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var notifications = await _db.MobileActivityNotifications
            .Where(notification =>
                notification.RecipientUserId == recipient.UserId &&
                notification.RecipientParticipantType == recipient.ParticipantType &&
                notification.ConversationId == conversationId &&
                !notification.IsRead &&
                !notification.IsCleared)
            .ToListAsync(cancellationToken);
        foreach (var notification in notifications)
        {
            notification.IsRead = true;
            notification.ReadUtc = readUtc;
        }
    }

    public async Task<NotificationSnapshot> GetSnapshotAsync(
        MessagingActor actor,
        int take,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var notifications = await _db.MobileActivityNotifications
            .AsNoTracking()
            .Where(notification =>
                notification.RecipientUserId == recipient.UserId &&
                notification.RecipientParticipantType == recipient.ParticipantType &&
                !notification.IsCleared)
            .OrderByDescending(notification => notification.OccurredUtc)
            .ThenByDescending(notification => notification.Id)
            .Take(Math.Clamp(take, 1, 100))
            .Select(notification => new NotificationLedgerItem(
                notification.Id,
                notification.Kind,
                notification.Title,
                notification.Detail,
                notification.ConversationId,
                notification.OccurredUtc,
                notification.IsRead,
                notification.IsCleared))
            .ToListAsync(cancellationToken);

        var badge = await ReconcileBadgeAsync(recipient, cancellationToken);
        return new NotificationSnapshot(badge, notifications);
    }

    public async Task<NotificationBadgeSnapshot> MarkReadAndPublishAsync(
        MessagingActor actor,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var notification = await _db.MobileActivityNotifications
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == notificationId &&
                candidate.RecipientUserId == recipient.UserId &&
                candidate.RecipientParticipantType == recipient.ParticipantType,
                cancellationToken);
        if (notification is not null && !notification.IsRead && !notification.IsCleared)
        {
            notification.IsRead = true;
            notification.ReadUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await ReconcileAndPublishAsync([recipient], cancellationToken);
    }

    public async Task<NotificationBadgeSnapshot> ClearBadgeAndPublishAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var now = DateTime.UtcNow;
        var notifications = await _db.MobileActivityNotifications
            .Where(notification =>
                notification.RecipientUserId == recipient.UserId &&
                notification.RecipientParticipantType == recipient.ParticipantType &&
                !notification.IsRead &&
                !notification.IsCleared)
            .ToListAsync(cancellationToken);
        foreach (var notification in notifications)
        {
            notification.IsCleared = true;
            notification.ClearedUtc = now;
        }
        if (notifications.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return await ReconcileAndPublishAsync([recipient], cancellationToken);
    }

    public async Task<NotificationBadgeSnapshot> ReconcileAndPublishAsync(
        IEnumerable<MessagingActor> actors,
        CancellationToken cancellationToken = default)
    {
        NotificationBadgeSnapshot? first = null;
        foreach (var recipient in actors.Select(Normalize).Distinct())
        {
            var badge = await ReconcileBadgeAsync(recipient, cancellationToken);
            first ??= badge;
            await _realtime.PublishAsync(
                recipient,
                new NotificationRealtimeEvent(null, badge.UnreadCount, badge.Revision, badge.UpdatedUtc),
                cancellationToken);
        }

        return first ?? new NotificationBadgeSnapshot(0, 0, DateTime.UtcNow);
    }

    public async Task RegisterDeviceAsync(
        MessagingActor actor,
        string deviceToken,
        string environment,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var token = NormalizeDeviceToken(deviceToken);
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var now = DateTime.UtcNow;
        var device = await _db.MobilePushDevices.SingleOrDefaultAsync(
            candidate => candidate.TokenHash == hash,
            cancellationToken);
        if (device is null)
        {
            device = new MobilePushDevice
            {
                UserId = recipient.UserId,
                ParticipantType = recipient.ParticipantType,
                DeviceToken = token,
                TokenHash = hash,
                Environment = normalizedEnvironment,
                IsActive = true,
                CreatedUtc = now,
                UpdatedUtc = now,
                LastSeenUtc = now
            };
            _db.MobilePushDevices.Add(device);
        }
        else
        {
            device.UserId = recipient.UserId;
            device.ParticipantType = recipient.ParticipantType;
            device.DeviceToken = token;
            device.Environment = normalizedEnvironment;
            device.IsActive = true;
            device.InvalidatedUtc = null;
            device.UpdatedUtc = now;
            device.LastSeenUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeactivateDeviceAsync(
        MessagingActor actor,
        string deviceToken,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var token = NormalizeDeviceToken(deviceToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var device = await _db.MobilePushDevices.SingleOrDefaultAsync(
            candidate =>
                candidate.TokenHash == hash &&
                candidate.UserId == recipient.UserId &&
                candidate.ParticipantType == recipient.ParticipantType,
            cancellationToken);
        if (device is null || !device.IsActive)
            return;

        var now = DateTime.UtcNow;
        device.IsActive = false;
        device.InvalidatedUtc = now;
        device.UpdatedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MobilePushDiagnosticSnapshot> GetPushDiagnosticAsync(
        MessagingActor actor,
        CancellationToken cancellationToken = default)
    {
        var recipient = Normalize(actor);
        var device = await _db.MobilePushDevices
            .AsNoTracking()
            .Where(candidate =>
                candidate.UserId == recipient.UserId &&
                candidate.ParticipantType == recipient.ParticipantType)
            .OrderByDescending(candidate => candidate.LastSeenUtc ?? candidate.UpdatedUtc)
            .ThenByDescending(candidate => candidate.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            return new MobilePushDiagnosticSnapshot(
                "missing",
                null,
                null,
                "unknown",
                null,
                "unknown",
                null,
                null,
                null);
        }

        var delivery = await (
                from candidate in _db.MobilePushDeliveries.AsNoTracking()
                join notification in _db.MobileActivityNotifications.AsNoTracking()
                    on candidate.NotificationId equals notification.Id
                where candidate.MobilePushDeviceId == device.Id &&
                      notification.RecipientUserId == recipient.UserId &&
                      notification.RecipientParticipantType == recipient.ParticipantType
                orderby candidate.SentUtc ?? candidate.AbandonedUtc ?? candidate.NextAttemptUtc descending
                select new
                {
                    candidate.SentUtc,
                    candidate.AbandonedUtc,
                    candidate.NextAttemptUtc,
                    candidate.AttemptCount,
                    candidate.LastError
                })
            .FirstOrDefaultAsync(cancellationToken);

        var registrationState = device.IsActive ? "registered" : "inactive";
        if (delivery is null)
        {
            return new MobilePushDiagnosticSnapshot(
                registrationState,
                device.Environment,
                device.LastSeenUtc,
                registrationState,
                null,
                "unknown",
                null,
                null,
                null);
        }

        var apnsDetail = ApplePushDiagnosticDetail.TryParse(delivery.LastError, out var parsedDetail)
            ? parsedDetail
            : null;
        var deliveryState = delivery.SentUtc is not null
            ? "delivered"
            : delivery.AbandonedUtc is not null
                ? string.Equals(delivery.LastError, "Notification no longer unread.", StringComparison.Ordinal)
                    ? "suppressed"
                    : "failed"
                : "pending";
        return new MobilePushDiagnosticSnapshot(
            registrationState,
            device.Environment,
            device.LastSeenUtc,
            registrationState,
            delivery.SentUtc ?? delivery.AbandonedUtc,
            deliveryState,
            apnsDetail?.StatusCode,
            apnsDetail?.Reason,
            delivery.AttemptCount);
    }

    private static string NormalizeDeviceToken(string? deviceToken)
    {
        var token = deviceToken?.Trim().ToLowerInvariant() ?? string.Empty;
        if (token.Length is 0 or > MaximumDeviceTokenLength ||
            token.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The APNs device token is invalid.", nameof(deviceToken));
        }

        return token;
    }

    private static string NormalizeEnvironment(string? environment)
    {
        if (string.Equals(environment?.Trim(), "sandbox", StringComparison.OrdinalIgnoreCase))
            return "sandbox";
        if (string.Equals(environment?.Trim(), "production", StringComparison.OrdinalIgnoreCase))
            return "production";

        throw new ArgumentException(
            "The APNs environment must be sandbox or production.",
            nameof(environment));
    }

    private async Task StageDeliveriesAsync(
        MobileActivityNotification notification,
        MessagingActor recipient,
        CancellationToken cancellationToken)
    {
        var devices = await _db.MobilePushDevices
            .AsNoTracking()
            .Where(device =>
                device.UserId == recipient.UserId &&
                device.ParticipantType == recipient.ParticipantType &&
                device.IsActive)
            .Select(device => device.Id)
            .ToListAsync(cancellationToken);
        foreach (var deviceId in devices)
        {
            _db.MobilePushDeliveries.Add(new MobilePushDelivery
            {
                NotificationId = notification.Id,
                MobilePushDeviceId = deviceId,
                NextAttemptUtc = notification.OccurredUtc
            });
        }
    }

    private async Task<NotificationBadgeSnapshot> ReconcileBadgeAsync(
        MessagingActor recipient,
        CancellationToken cancellationToken)
    {
        var unreadCount = await _db.MobileActivityNotifications
            .AsNoTracking()
            .CountAsync(notification =>
                notification.RecipientUserId == recipient.UserId &&
                notification.RecipientParticipantType == recipient.ParticipantType &&
                !notification.IsRead &&
                !notification.IsCleared,
                cancellationToken);
        var now = DateTime.UtcNow;
        var badge = await _db.UserGlobalBadges.SingleOrDefaultAsync(
            candidate =>
                candidate.UserId == recipient.UserId &&
                candidate.ParticipantType == recipient.ParticipantType,
            cancellationToken);
        if (badge is null)
        {
            badge = new UserGlobalBadge
            {
                UserId = recipient.UserId,
                ParticipantType = recipient.ParticipantType,
                UnreadCount = unreadCount,
                Revision = 1,
                UpdatedUtc = now
            };
            _db.UserGlobalBadges.Add(badge);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (badge.UnreadCount != unreadCount)
        {
            badge.UnreadCount = unreadCount;
            badge.Revision++;
            badge.UpdatedUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new NotificationBadgeSnapshot(
            unreadCount,
            badge.Revision,
            badge.UpdatedUtc);
    }

    private static MessagingActor Normalize(MessagingActor actor) => new(
        actor.UserId.Trim().ToLowerInvariant(),
        actor.ParticipantType.Trim());

    private static bool IsSameActor(MessagingActor left, MessagingActor right) =>
        string.Equals(left.UserId, right.UserId, StringComparison.Ordinal) &&
        string.Equals(left.ParticipantType, right.ParticipantType, StringComparison.Ordinal);

    private static string Clip(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}

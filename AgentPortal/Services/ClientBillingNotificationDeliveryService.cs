using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public sealed record ClientBillingNotificationDeliveryResult(int Selected, int Sent, int Failed);

public sealed class ClientBillingNotificationDeliveryService
{
    private readonly MasterAppDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<ClientBillingNotificationDeliveryService> _logger;

    public ClientBillingNotificationDeliveryService(
        MasterAppDbContext db,
        IEmailSender emailSender,
        ILogger<ClientBillingNotificationDeliveryService> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<ClientBillingNotificationDeliveryResult> DeliverDueAsync(int maxItems, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var limit = Math.Clamp(maxItems, 1, 100);
        var notifications = await _db.ClientBillingNotifications
            .Include(notification => notification.ClientProfile)
            .Where(notification =>
                notification.SentUtc == null &&
                notification.NotBeforeUtc <= nowUtc &&
                (notification.NextAttemptUtc == null || notification.NextAttemptUtc <= nowUtc))
            .OrderBy(notification => notification.NotBeforeUtc)
            .ThenBy(notification => notification.CreatedUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var sent = 0;
        var failed = 0;
        foreach (var notification in notifications)
        {
            notification.AttemptCount++;
            notification.LastAttemptUtc = nowUtc;
            notification.UpdatedUtc = nowUtc;

            var recipient = notification.ClientProfile?.Email?.Trim();
            if (string.IsNullOrWhiteSpace(recipient))
            {
                notification.SafeFailureCode = "CLIENT_EMAIL_MISSING";
                notification.NextAttemptUtc = null;
                failed++;
                continue;
            }

            try
            {
                if (await _emailSender.TrySendAsync(recipient, notification.Subject, null, notification.PlainTextBody))
                {
                    notification.SentUtc = nowUtc;
                    notification.SafeFailureCode = null;
                    notification.NextAttemptUtc = null;
                    sent++;
                    continue;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Billing notification delivery failed for notification {NotificationId}.", notification.Id);
            }

            notification.SafeFailureCode = "EMAIL_DELIVERY_FAILED";
            notification.NextAttemptUtc = nowUtc.AddMinutes(ResolveRetryDelayMinutes(notification.AttemptCount));
            failed++;
        }

        if (notifications.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return new ClientBillingNotificationDeliveryResult(notifications.Count, sent, failed);
    }

    private static int ResolveRetryDelayMinutes(int attemptCount) =>
        attemptCount switch
        {
            <= 1 => 15,
            2 => 60,
            3 => 240,
            _ => 1_440
        };
}

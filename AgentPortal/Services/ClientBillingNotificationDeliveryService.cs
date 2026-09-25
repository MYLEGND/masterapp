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
            nowUtc = DateTime.UtcNow;
            // Claim durably before sending. Competing workers can select the same
            // row, but only one can move its due time into the lease window.
            if (_db.Database.IsRelational())
            {
                var claimed = await _db.ClientBillingNotifications
                    .Where(x => x.Id == notification.Id && x.SentUtc == null && x.NotBeforeUtc <= nowUtc &&
                        (x.NextAttemptUtc == null || x.NextAttemptUtc <= nowUtc))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.NextAttemptUtc, nowUtc.AddMinutes(15)), cancellationToken);
                if (claimed != 1) continue;
                await _db.Entry(notification).ReloadAsync(cancellationToken);
            }
            notification.NextAttemptUtc = nowUtc.AddMinutes(15);
            notification.AttemptCount++;
            notification.LastAttemptUtc = nowUtc;
            notification.UpdatedUtc = nowUtc;

            await _db.SaveChangesAsync(cancellationToken);
            var recipient = notification.ClientProfile?.Email?.Trim();
            if (string.IsNullOrWhiteSpace(recipient))
            {
                notification.SafeFailureCode = "CLIENT_EMAIL_MISSING";
                notification.NextAttemptUtc = nowUtc.AddDays(1);
                await _db.SaveChangesAsync(cancellationToken);
                failed++;
                continue;
            }

            var accepted = false;
            try
            {
                accepted = await _emailSender.TrySendAsync(recipient, notification.Subject, null, notification.PlainTextBody);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Billing notification delivery failed for notification {NotificationId}.", notification.Id);
            }
            if (accepted)
            {
                notification.SentUtc = DateTime.UtcNow;
                notification.SafeFailureCode = null;
                notification.NextAttemptUtc = null;
                // A database failure after provider acceptance is not an email rejection.
                await _db.SaveChangesAsync(cancellationToken);
                sent++;
                continue;
            }

            notification.SafeFailureCode = "EMAIL_DELIVERY_FAILED";
            notification.NextAttemptUtc = nowUtc.AddMinutes(ResolveRetryDelayMinutes(notification.AttemptCount));
            await _db.SaveChangesAsync(cancellationToken);
            failed++;
        }

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

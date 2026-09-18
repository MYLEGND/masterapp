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
            .Include(notification => notification.ClientSubscription)
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
                var htmlBody = notification.Kind == Domain.Billing.ClientBillingNotificationKind.SubscriptionTermsUpdated
                    ? BuildSubscriptionTermsUpdatedHtml(notification)
                    : null;

                if (await _emailSender.TrySendAsync(recipient, notification.Subject, htmlBody, notification.PlainTextBody))
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

    private static string BuildSubscriptionTermsUpdatedHtml(ClientBillingNotification notification)
    {
        var safeSubject = System.Net.WebUtility.HtmlEncode(notification.Subject);
        var safeBody = System.Net.WebUtility.HtmlEncode(notification.PlainTextBody);
        var subscription = notification.ClientSubscription;
        var amount = subscription is null
            ? string.Empty
            : (subscription.MonthlyAmountCents / 100m).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var nextBilling = subscription?.NextBillingDateUtc is DateTime next
            ? DateTime.SpecifyKind(next, DateTimeKind.Utc).ToLocalTime().ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        var safeAmount = System.Net.WebUtility.HtmlEncode(amount);
        var safeNextBilling = System.Net.WebUtility.HtmlEncode(nextBilling);

        return $"""
<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin:0;padding:0;background:#ffffff;font-family:Arial,sans-serif;color:#14213a;">
  <tr>
    <td align="center" style="padding:28px 14px;background:#ffffff;">
      <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="max-width:640px;background:#ffffff;border:1px solid #d7e0ee;border-radius:18px;overflow:hidden;">
        <tr>
          <td style="padding:24px 28px;background:#0d2145;color:#ffffff;">
            <div style="font-size:12px;font-weight:800;letter-spacing:1.4px;text-transform:uppercase;color:#cbdcff;">LEGEND® Client Portal</div>
            <div style="margin-top:8px;font-size:26px;line-height:1.18;font-weight:800;">{safeSubject}</div>
          </td>
        </tr>
        <tr>
          <td style="padding:24px 28px 28px 28px;background:#ffffff;">
            <div style="font-size:15px;line-height:1.65;color:#506078;">{safeBody}</div>
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin-top:18px;border:1px solid #dbe5f4;border-radius:12px;background:#f6f8fc;">
              <tr>
                <td style="padding:16px 18px;">
                  <div style="font-size:12px;font-weight:800;letter-spacing:1.1px;text-transform:uppercase;color:#2e5fa9;">Next billing period</div>
                  <div style="margin-top:8px;font-size:30px;line-height:1;font-weight:800;color:#0d2145;">{safeAmount}<span style="font-size:15px;color:#66758c;"> / month</span></div>
                  <div style="margin-top:10px;font-size:14px;color:#506078;"><strong>Effective:</strong> {safeNextBilling}</div>
                </td>
              </tr>
            </table>
            <div style="margin-top:22px;padding-top:16px;border-top:1px solid #dbe5f4;font-size:13px;font-weight:800;color:#0d2145;">LEGEND®</div>
          </td>
        </tr>
      </table>
    </td>
  </tr>
</table>
""";
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

using System.Globalization;
using Domain.Billing;
using Domain.Entities;
using Infrastructure.Data;

namespace Infrastructure.Billing;

internal sealed class ClientBillingNotificationService : IClientBillingNotificationService
{
    private readonly MasterAppDbContext _db;

    public ClientBillingNotificationService(MasterAppDbContext db)
    {
        _db = db;
    }

    public void Queue(ClientBillingNotificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventKey))
            throw new ArgumentException("A billing notification event key is required.", nameof(request));

        var eventKey = request.EventKey.Trim();
        var alreadyQueued = _db.ClientBillingNotifications.Local.Any(notification =>
                                string.Equals(notification.EventKey, eventKey, StringComparison.Ordinal)) ||
                            _db.ClientBillingNotifications.Any(notification => notification.EventKey == eventKey);
        if (alreadyQueued)
            return;

        var message = ClientBillingNotificationTemplates.Create(request);
        var nowUtc = DateTime.UtcNow;
        _db.ClientBillingNotifications.Add(new ClientBillingNotification
        {
            ClientProfileId = request.ClientProfileId,
            ClientSubscriptionId = request.ClientSubscriptionId,
            Kind = request.Kind,
            EventKey = eventKey,
            Subject = message.Subject,
            PlainTextBody = message.Body,
            NotBeforeUtc = request.NotBeforeUtc ?? nowUtc,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        });
    }

    private static class ClientBillingNotificationTemplates
    {
        public static (string Subject, string Body) Create(ClientBillingNotificationRequest request)
        {
            var amount = request.AmountCents.HasValue
                ? (request.AmountCents.Value / 100m).ToString("C", CultureInfo.GetCultureInfo("en-US"))
                : null;
            var graceEnd = request.GracePeriodEndsUtc.HasValue
                ? DateTime.SpecifyKind(request.GracePeriodEndsUtc.Value, DateTimeKind.Utc).ToLocalTime().ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)
                : null;

            return request.Kind switch
            {
                ClientBillingNotificationKind.MembershipActivated =>
                    ("Your Legend membership is active", "Your Legend Client Portal membership is active. You can now sign in and use your membership features."),
                ClientBillingNotificationKind.PaymentReceived =>
                    ("Your Legend membership payment was received", $"Your {amount ?? "membership"} payment was received and your membership remains active."),
                ClientBillingNotificationKind.PaymentFailed =>
                    ("Action needed for your Legend membership", "We could not process your membership renewal. We will automatically try again when it is safe to do so. Please review your payment method in Membership & Billing."),
                ClientBillingNotificationKind.PaymentMethodUpdated =>
                    ("Your Legend payment method was updated", "Your saved payment-method settings were updated. You can review them anytime in Membership & Billing."),
                ClientBillingNotificationKind.BackupPaymentUsed =>
                    ("Your Legend membership renewal was completed", "Your membership renewal was completed using a saved backup payment method. Your regular default payment method has not changed."),
                ClientBillingNotificationKind.GracePeriodStarted =>
                    ("Your Legend membership needs a payment update", $"Your membership remains active while you update your payment method. Please update it by {graceEnd ?? "the end of your grace period"} to avoid an interruption."),
                ClientBillingNotificationKind.GracePeriodReminder =>
                    ("Reminder: update your Legend payment method", $"Your membership is still active, but a payment update is needed by {graceEnd ?? "the end of your grace period"}."),
                ClientBillingNotificationKind.GracePeriodFinalReminder =>
                    ("Final reminder: update your Legend payment method", $"Please update your payment method by {graceEnd ?? "the end of your grace period"} to keep your Legend membership active."),
                ClientBillingNotificationKind.MembershipCancelled =>
                    ("Your Legend membership has ended", "Your Legend membership has ended and portal access is no longer active. Contact your agent if you would like to reactivate your membership."),
                ClientBillingNotificationKind.MembershipReactivated =>
                    ("Your Legend membership is active again", "Your payment was received and your Legend membership is active again."),
                ClientBillingNotificationKind.UpcomingRenewal =>
                    ("Your Legend membership renewal is coming up", "Your membership renewal is coming up soon. You can review your saved payment method anytime in Membership & Billing."),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unsupported billing notification kind.")
            };
        }
    }
}

using Domain.Billing;

namespace Infrastructure.Billing;

internal static class BillingStateMapper
{
    public static SubscriptionPaymentStatus MapPaymentStatus(string? providerStatus)
    {
        var normalized = Normalize(providerStatus);
        return normalized switch
        {
            "COMPLETED" or "PAID" => SubscriptionPaymentStatus.Completed,
            "APPROVED" or "AUTHORIZED" => SubscriptionPaymentStatus.Authorized,
            "FAILED" => SubscriptionPaymentStatus.Failed,
            "CANCELED" or "CANCELLED" => SubscriptionPaymentStatus.Canceled,
            "PARTIALLY_REFUNDED" => SubscriptionPaymentStatus.PartiallyRefunded,
            "REFUNDED" => SubscriptionPaymentStatus.Refunded,
            "DISPUTED" => SubscriptionPaymentStatus.Disputed,
            _ => SubscriptionPaymentStatus.Pending
        };
    }

    public static ClientSubscriptionStatus MapSubscriptionStatus(string? providerStatus)
    {
        var normalized = Normalize(providerStatus);
        return normalized switch
        {
            "ACTIVE" => ClientSubscriptionStatus.Active,
            "PAUSED" => ClientSubscriptionStatus.Paused,
            "CANCELED" or "CANCELLED" => ClientSubscriptionStatus.Canceled,
            "PAST_DUE" or "UNPAID" => ClientSubscriptionStatus.PastDue,
            "DEACTIVATED" or "SUSPENDED" => ClientSubscriptionStatus.Suspended,
            "PENDING" or "INITIATED" => ClientSubscriptionStatus.PendingProviderActivation,
            "REQUIRES_ACTION" => ClientSubscriptionStatus.AwaitingPaymentMethod,
            _ => ClientSubscriptionStatus.PendingProviderActivation
        };
    }

    public static ClientSubscriptionPaymentStanding MapPaymentStanding(string? providerStatus)
    {
        var normalized = Normalize(providerStatus);
        return normalized switch
        {
            "ACTIVE" => ClientSubscriptionPaymentStanding.Current,
            "PAST_DUE" or "UNPAID" => ClientSubscriptionPaymentStanding.PastDue,
            "REQUIRES_ACTION" or "PAUSED" => ClientSubscriptionPaymentStanding.RequiresAction,
            "FAILED" or "CANCELED" or "CANCELLED" => ClientSubscriptionPaymentStanding.Failed,
            "GRACE_PERIOD" => ClientSubscriptionPaymentStanding.GracePeriod,
            _ => ClientSubscriptionPaymentStanding.Unknown
        };
    }

    public static bool IsTerminal(ClientSubscriptionStatus status)
    {
        return status is ClientSubscriptionStatus.Canceled or ClientSubscriptionStatus.ActivationFailed;
    }

    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}

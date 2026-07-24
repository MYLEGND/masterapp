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

    public static bool IsTerminal(ClientSubscriptionStatus status)
    {
        return status is ClientSubscriptionStatus.Canceled or ClientSubscriptionStatus.ActivationFailed;
    }

    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}

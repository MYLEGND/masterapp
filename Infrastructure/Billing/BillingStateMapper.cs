using Domain.Billing;
using Domain.Entities;

namespace Infrastructure.Billing;

internal static class BillingStateMapper
{
    public static BillingEntitlementEvaluationResult EvaluateSubscription(
        ClientSubscription? subscription,
        string entitlementKey,
        DateTime evaluatedUtc)
    {
        if (subscription is null)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.NotGranted,
                null,
                null,
                null,
                "NO_SUBSCRIPTION",
                ClientEntitlementSourceType.Subscription,
                string.Empty,
                $"{entitlementKey}: no subscription found.");
        }

        var sourceId = subscription.Id.ToString();

        if (subscription.CancelAtPeriodEnd &&
            subscription.CurrentPeriodEndUtc.HasValue &&
            subscription.CurrentPeriodEndUtc.Value <= evaluatedUtc)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.NotGranted,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                null,
                "SUBSCRIPTION_ENDED_AT_PERIOD_END",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: scheduled cancellation reached its period boundary.");
        }

        if (subscription.Status == ClientSubscriptionStatus.Active)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Active,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                null,
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is active.");
        }

        if ((subscription.Status is ClientSubscriptionStatus.GracePeriod or ClientSubscriptionStatus.PastDue) &&
            subscription.GracePeriodEndsUtc.HasValue &&
            subscription.GracePeriodEndsUtc.Value > evaluatedUtc)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.GracePeriod,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                "SUBSCRIPTION_GRACE_PERIOD",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is in grace period.");
        }

        if (subscription.Status is ClientSubscriptionStatus.Suspended or ClientSubscriptionStatus.Paused)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Suspended,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                "SUBSCRIPTION_SUSPENDED",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is suspended.");
        }

        if (subscription.Status == ClientSubscriptionStatus.PastDue)
        {
            return new BillingEntitlementEvaluationResult(
                ClientEntitlementStatus.Restricted,
                subscription.ActivatedUtc ?? subscription.CreatedUtc,
                subscription.CurrentPeriodEndUtc,
                subscription.GracePeriodEndsUtc,
                "SUBSCRIPTION_PAST_DUE",
                ClientEntitlementSourceType.Subscription,
                sourceId,
                $"{entitlementKey}: subscription is past due.");
        }

        return new BillingEntitlementEvaluationResult(
            ClientEntitlementStatus.NotGranted,
            subscription.ActivatedUtc ?? subscription.CreatedUtc,
            subscription.CurrentPeriodEndUtc,
            subscription.GracePeriodEndsUtc,
            $"SUBSCRIPTION_{subscription.Status.ToString().ToUpperInvariant()}",
            ClientEntitlementSourceType.Subscription,
            sourceId,
            $"{entitlementKey}: subscription is not eligible for access.");
    }

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

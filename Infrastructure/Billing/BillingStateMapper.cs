using Domain.Billing;
using Domain.Entities;
using System.Linq.Expressions;

namespace Infrastructure.Billing;

internal static class BillingStateMapper
{
    private static readonly Expression<Func<ClientSubscription, DateTime, bool>> UsableExpression = (subscription, now) =>
        !(subscription.CancelAtPeriodEnd && subscription.CurrentPeriodEndUtc.HasValue && subscription.CurrentPeriodEndUtc.Value <= now) &&
        (subscription.Status == ClientSubscriptionStatus.Active ||
         ((subscription.Status == ClientSubscriptionStatus.GracePeriod || subscription.Status == ClientSubscriptionStatus.PastDue) &&
          subscription.GracePeriodEndsUtc.HasValue && subscription.GracePeriodEndsUtc.Value > now));
    private static readonly Func<ClientSubscription, DateTime, bool> IsUsable = UsableExpression.Compile();

    internal static Expression<Func<ClientSubscription, bool>> UsableAt(DateTime now) =>
        Expression.Lambda<Func<ClientSubscription, bool>>(
            new EvaluationTimeVisitor(UsableExpression.Parameters[1], now).Visit(UsableExpression.Body)!, UsableExpression.Parameters[0]);

    private sealed class EvaluationTimeVisitor(ParameterExpression parameter, DateTime now) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? Expression.Constant(now) : base.VisitParameter(node);
    }
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

        if (IsUsable(subscription, evaluatedUtc) && subscription.Status == ClientSubscriptionStatus.Active)
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

        if (IsUsable(subscription, evaluatedUtc))
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

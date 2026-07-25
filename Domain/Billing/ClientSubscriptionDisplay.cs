namespace Domain.Billing;

public static class ClientSubscriptionDisplay
{
    public static string FormatMembershipState(
        ClientSubscriptionStatus status,
        ClientSubscriptionPaymentStanding paymentStanding) => status switch
    {
        ClientSubscriptionStatus.Canceled => "Cancelled",
        ClientSubscriptionStatus.GracePeriod => "Grace Period",
        ClientSubscriptionStatus.PastDue => "Past Due",
        ClientSubscriptionStatus.Paused => "Paused",
        ClientSubscriptionStatus.ActivationFailed or ClientSubscriptionStatus.Suspended => "Payment Failed",
        ClientSubscriptionStatus.ReconciliationRequired => "Reconciliation Required",
        ClientSubscriptionStatus.AwaitingPaymentMethod => "Awaiting Payment Method",
        ClientSubscriptionStatus.PendingProviderActivation or ClientSubscriptionStatus.Draft => "Pending Activation",
        ClientSubscriptionStatus.Active when paymentStanding == ClientSubscriptionPaymentStanding.Failed => "Payment Failed",
        ClientSubscriptionStatus.Active when paymentStanding == ClientSubscriptionPaymentStanding.PastDue => "Past Due",
        ClientSubscriptionStatus.Active when paymentStanding == ClientSubscriptionPaymentStanding.GracePeriod => "Grace Period",
        ClientSubscriptionStatus.Active => "Active",
        _ => "Pending Activation"
    };

    public static string FormatPaymentStanding(ClientSubscriptionPaymentStanding paymentStanding) => paymentStanding switch
    {
        ClientSubscriptionPaymentStanding.Current => "Current",
        ClientSubscriptionPaymentStanding.RequiresAction => "Action Required",
        ClientSubscriptionPaymentStanding.PastDue => "Past Due",
        ClientSubscriptionPaymentStanding.GracePeriod => "Grace Period",
        ClientSubscriptionPaymentStanding.Failed => "Payment Failed",
        _ => "Unknown"
    };
}

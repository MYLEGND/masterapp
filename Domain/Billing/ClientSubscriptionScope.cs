using Domain.Entities;

namespace Domain.Billing;

/// <summary>Product ownership within the canonical recurring billing ledger.</summary>
public static class ClientSubscriptionScope
{
    public const string LegendMembership = "LegendMembership";
    public const string MonthlyTraining = "MonthlyTraining";
    public const string OneTimeProduct = "OneTime";

    public static string Training(Guid businessId) => businessId == Guid.Empty
        ? throw new ArgumentException("A training business is required.", nameof(businessId))
        : $"Training:{businessId:N}";

    public static string TrainingEntitlement(Guid businessId, Guid productId) => productId == Guid.Empty
        ? throw new ArgumentException("A training plan is required.", nameof(productId))
        : $"{Training(businessId)}:{productId:N}";

    public static string EntitlementKey(ClientSubscription subscription)
    {
        if (subscription.SubscriptionScope == LegendMembership &&
            subscription.CommerceBusinessId is null && subscription.CommerceProductId is null)
            return BillingEntitlementKeys.ClientAppFullAccess;
        if (subscription.CommerceBusinessId is Guid business && subscription.CommerceProductId is Guid product &&
            subscription.SubscriptionScope == Training(business))
            return TrainingEntitlement(business, product);
        throw new InvalidOperationException("The subscription has no valid product entitlement mapping.");
    }

    public static bool TryTrainingEntitlement(string key, out Guid businessId, out Guid productId)
    {
        businessId = productId = Guid.Empty;
        var parts = key.Split(':');
        return parts.Length == 3 && parts[0] == "Training" &&
            Guid.TryParseExact(parts[1], "N", out businessId) && businessId != Guid.Empty &&
            Guid.TryParseExact(parts[2], "N", out productId) && productId != Guid.Empty &&
            key == TrainingEntitlement(businessId, productId);
    }
}

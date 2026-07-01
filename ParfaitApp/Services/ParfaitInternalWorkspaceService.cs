using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitInternalWorkspaceService
{
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly IParfaitBusinessProfileService _businessProfile;
    private readonly ParfaitInternalAnalyticsService _analytics;

    public ParfaitInternalWorkspaceService(
        ParfaitProductService products,
        ParfaitOrderService orders,
        IParfaitBusinessProfileService businessProfile,
        ParfaitInternalAnalyticsService analytics)
    {
        _products = products;
        _orders = orders;
        _businessProfile = businessProfile;
        _analytics = analytics;
    }

    public async Task<ParfaitInternalWorkspaceSnapshotViewModel> GetSnapshotAsync(CancellationToken ct = default)
    {
        var products = _products.GetAllProducts().ToList();
        var orders = _orders.GetAllOrders().ToList();
        var profile = await _businessProfile.GetProfileAsync(ct);
        var analytics = await _analytics.GetDashboardAsync("30d", ct);
        var meta = analytics.MetaSettings;
        var actionMap = analytics.ActionBreakdowns.ToDictionary(action => action.Key, StringComparer.OrdinalIgnoreCase);

        var paidOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pendingOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failedOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var openFulfillmentOrders = orders
            .Where(order => order.IsFulfillmentOpen)
            .ToList();

        var customersByEmail = orders
            .Where(order => !string.IsNullOrWhiteSpace(order.Email))
            .GroupBy(order => order.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ParfaitInternalWorkspaceSnapshotViewModel
        {
            StoreName = profile.StoreName,
            BusinessType = profile.BusinessType,
            HasCheckoutUrl = !string.IsNullOrWhiteSpace(profile.GlobalStoreCheckoutUrl),
            HasMetaPixel = !string.IsNullOrWhiteSpace(meta.MetaPixelId),
            HasMetaConnection = meta.HasActiveMetaAdsConnection,
            HasAnalyticsTraffic = analytics.Sessions > 0 || analytics.Purchases > 0 || analytics.HasTrackedEvents,
            MetaConnectionLabel = meta.MetaConnectionLabel,
            MetaCapiStatus = meta.MetaCapiStatus,
            AnalyticsStatus = analytics.HasTrackedEvents ? $"{analytics.RangeLabel} synced" : "Awaiting storefront activity",
            TrustStatus = analytics.Devices.Sessions > 0
                ? $"{analytics.Devices.IdentityProfiles + analytics.Devices.VisitorFallbackProfiles} visitor identities mapped"
                : "Awaiting visitor intelligence",
            ProductCount = products.Count,
            ActiveProductCount = products.Count(product => product.IsActive),
            FeaturedProductCount = products.Count(product => product.IsFeatured),
            ProductImageCount = products.Sum(product => product.Images.Count),
            OrderCount = orders.Count,
            PaidOrderCount = paidOrders.Count(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase)),
            PendingOrderCount = pendingOrders.Count,
            FailedOrderCount = failedOrders.Count,
            OpenFulfillmentCount = openFulfillmentOrders.Count,
            CustomerCount = customersByEmail.Count,
            RepeatCustomerCount = customersByEmail.Count(group => group.Count() > 1),
            RevenueCents = _orders.SumNetRevenueCents(orders),
            AverageOrderValueCents = _orders.CalculateAverageNetOrderValueCents(orders),
            LatestOrderUtc = orders.Count == 0 ? null : orders.Max(order => order.CreatedUtc),
            Visitors = analytics.Visitors,
            Sessions = analytics.Sessions,
            StoreViews = GetActionCount(actionMap, "view-content"),
            ProductViews = GetActionCount(actionMap, "product-viewed"),
            AddToCarts = GetActionCount(actionMap, "add-to-cart"),
            CheckoutStarts = GetActionCount(actionMap, "checkout-started"),
            Purchases = analytics.Purchases,
            CheckoutToPurchaseRate = analytics.ActionBreakdowns
                .FirstOrDefault(action => string.Equals(action.Key, "checkout-started", StringComparison.OrdinalIgnoreCase))
                ?.ConversionRate ?? 0m
        };
    }

    private static int GetActionCount(
        IReadOnlyDictionary<string, ParfaitAnalyticsActionBreakdownViewModel> actionMap,
        string key)
        => actionMap.TryGetValue(key, out var action) ? action.Count : 0;
}

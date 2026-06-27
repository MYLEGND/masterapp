using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitInternalWorkspaceService
{
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly IParfaitBusinessProfileService _businessProfile;
    private readonly ParfaitAnalyticsDashboardService _analytics;

    public ParfaitInternalWorkspaceService(
        ParfaitProductService products,
        ParfaitOrderService orders,
        IParfaitBusinessProfileService businessProfile,
        ParfaitAnalyticsDashboardService analytics)
    {
        _products = products;
        _orders = orders;
        _businessProfile = businessProfile;
        _analytics = analytics;
    }

    public async Task<ParfaitInternalWorkspaceSnapshotViewModel> GetSnapshotAsync(CancellationToken ct = default)
    {
        var profileTask = _businessProfile.GetProfileAsync(ct);
        var analyticsTask = _analytics.GetDashboardAsync(ct);

        var products = _products.GetAllProducts().ToList();
        var orders = _orders.GetAllOrders().ToList();

        var profile = await profileTask;
        var analytics = await analyticsTask;

        var paidOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pendingOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Pending", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failedOrders = orders
            .Where(order => string.Equals(order.PaymentStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var openFulfillmentOrders = orders
            .Where(order => !string.Equals(order.FulfillmentStatus, "Fulfilled", StringComparison.OrdinalIgnoreCase))
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
            HasMetaPixel = !string.IsNullOrWhiteSpace(profile.MetaPixelId),
            HasMetaConnection = profile.HasActiveMetaAdsConnection,
            HasAnalyticsTraffic = analytics.Sessions > 0 || analytics.Purchases > 0 || analytics.HasTrackedEvents,
            MetaConnectionLabel = profile.MetaConnectionLabel,
            MetaCapiStatus = profile.MetaCapiStatus,
            AnalyticsStatus = profile.AnalyticsStatus,
            TrustStatus = profile.TrustStatus,
            ProductCount = products.Count,
            ActiveProductCount = products.Count(product => product.IsActive),
            FeaturedProductCount = products.Count(product => product.IsFeatured),
            ProductImageCount = products.Sum(product => product.Images.Count),
            OrderCount = orders.Count,
            PaidOrderCount = paidOrders.Count,
            PendingOrderCount = pendingOrders.Count,
            FailedOrderCount = failedOrders.Count,
            OpenFulfillmentCount = openFulfillmentOrders.Count,
            CustomerCount = customersByEmail.Count,
            RepeatCustomerCount = customersByEmail.Count(group => group.Count() > 1),
            RevenueCents = paidOrders.Sum(order => order.TotalCents),
            AverageOrderValueCents = paidOrders.Count == 0 ? 0 : (int)Math.Round(paidOrders.Average(order => order.TotalCents)),
            LatestOrderUtc = orders.Count == 0 ? null : orders.Max(order => order.CreatedUtc),
            Visitors = analytics.Visitors,
            Sessions = analytics.Sessions,
            StoreViews = analytics.StoreViews,
            ProductViews = analytics.ProductViews,
            AddToCarts = analytics.AddToCarts,
            CheckoutStarts = analytics.CheckoutStarts,
            Purchases = analytics.Purchases,
            CheckoutToPurchaseRate = analytics.CheckoutToPurchaseRate
        };
    }
}

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using Shared.Analytics;

namespace ParfaitApp.Services;

public sealed class ParfaitInternalWorkspaceService
{
    private readonly ParfaitProductService _products;
    private readonly ParfaitOrderService _orders;
    private readonly IParfaitBusinessProfileService _businessProfile;
    private readonly ParfaitInternalAnalyticsService _analytics;
    private readonly MasterAppDbContext _db;

    public ParfaitInternalWorkspaceService(
        ParfaitProductService products,
        ParfaitOrderService orders,
        IParfaitBusinessProfileService businessProfile,
        ParfaitInternalAnalyticsService analytics,
        MasterAppDbContext db)
    {
        _products = products;
        _orders = orders;
        _businessProfile = businessProfile;
        _analytics = analytics;
        _db = db;
    }

    public Task<ParfaitInternalWorkspaceSnapshotViewModel> GetSnapshotAsync(CancellationToken ct = default) =>
        GetSnapshotAsync(_products.GetDefaultBusinessId(), ct);

    public async Task<ParfaitInternalWorkspaceSnapshotViewModel> GetSnapshotAsync(Guid businessId, CancellationToken ct = default)
    {
        var business = await _db.CommerceBusinesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessId && x.IsActive && x.Status == "Active", ct)
            ?? throw new InvalidOperationException("An active commerce business is required.");
        var products = _products.GetAllProducts(businessId).ToList();
        var orders = _orders.GetAllOrders(businessId).ToList();
        var analytics = await _analytics.GetWorkspaceSummaryAsync(
            businessId,
            "30d",
            null,
            null,
            TrafficQualityMode.RealHumanTraffic,
            TimeZoneInfo.Utc,
            ct);
        var meta = analytics.MetaSettings;

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
        var trackedPurchases = analytics.GetEventCount("Purchase");
        var trackedPurchaseSessions = analytics.GetUniqueSessions("Purchase");
        if (trackedPurchases == 0)
        {
            trackedPurchases = paidOrders.Count;
            trackedPurchaseSessions = paidOrders
                .Where(order => !string.IsNullOrWhiteSpace(order.CheckoutAttemptId))
                .Select(order => order.CheckoutAttemptId!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        var trackedCheckoutSessions = analytics.GetUniqueSessions("CheckoutStarted");

        return new ParfaitInternalWorkspaceSnapshotViewModel
        {
            StoreName = business.DisplayName,
            BusinessType = business.BusinessType,
            HasCheckoutUrl = true,
            HasMetaPixel = !string.IsNullOrWhiteSpace(meta.MetaPixelId),
            HasMetaConnection = meta.HasActiveMetaAdsConnection,
            HasAnalyticsTraffic = analytics.Sessions > 0 || trackedPurchases > 0 || analytics.HasTrackedEvents,
            MetaConnectionLabel = meta.MetaConnectionLabel,
            MetaCapiStatus = meta.MetaCapiStatus,
            AnalyticsStatus = analytics.HasTrackedEvents ? $"{analytics.RangeLabel} synced" : "Awaiting storefront activity",
            TrustStatus = analytics.DevicesSessions > 0
                ? $"{analytics.IdentityProfiles + analytics.VisitorFallbackProfiles} visitor identities mapped"
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
            StoreViews = analytics.GetEventCount("ViewContent"),
            ProductViews = analytics.GetEventCount("ProductViewed"),
            AddToCarts = analytics.GetEventCount("AddToCart"),
            CheckoutStarts = analytics.GetEventCount("CheckoutStarted"),
            Purchases = trackedPurchases,
            CheckoutToPurchaseRate = trackedCheckoutSessions <= 0
                ? 0m
                : Math.Round((decimal)trackedPurchaseSessions / trackedCheckoutSessions * 100m, 1)
        };
    }
}

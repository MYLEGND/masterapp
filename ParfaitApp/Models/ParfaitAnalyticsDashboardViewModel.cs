namespace ParfaitApp.Models;

public sealed class ParfaitAnalyticsDashboardViewModel
{
    public int Visitors { get; set; }
    public int Sessions { get; set; }
    public int StoreViews { get; set; }
    public int ProductViews { get; set; }
    public int AddToCarts { get; set; }
    public int CheckoutStarts { get; set; }
    public int Purchases { get; set; }
    public int RevenueCents { get; set; }

    public decimal ProductViewToCartRate { get; set; }
    public decimal CartToCheckoutRate { get; set; }
    public decimal CheckoutToPurchaseRate { get; set; }

    public List<ParfaitAnalyticsProductMetricViewModel> TopProducts { get; set; } = new();
    public List<ParfaitAnalyticsRecentEventViewModel> RecentEvents { get; set; } = new();
}

public sealed class ParfaitAnalyticsProductMetricViewModel
{
    public string ProductName { get; set; } = "";
    public int ProductViews { get; set; }
    public int AddToCarts { get; set; }
    public int CheckoutStarts { get; set; }
    public int Purchases { get; set; }
    public int RevenueCents { get; set; }
}

public sealed class ParfaitAnalyticsRecentEventViewModel
{
    public DateTime EventUtc { get; set; }
    public string EventType { get; set; } = "";
    public string? ProductName { get; set; }
    public string? Size { get; set; }
    public int? Quantity { get; set; }
    public int? ValueCents { get; set; }
    public string? SessionId { get; set; }
}

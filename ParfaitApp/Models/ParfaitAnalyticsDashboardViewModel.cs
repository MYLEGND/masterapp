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

    public List<ParfaitAnalyticsActionBreakdownViewModel> ActionBreakdowns { get; set; } = new();

    public bool HasTrackedEvents => ActionBreakdowns.Exists(action => action.Count > 0);
}

public sealed class ParfaitAnalyticsActionBreakdownViewModel
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Tone { get; set; } = "";
    public string TileNote { get; set; } = "";
    public string ConversionLabel { get; set; } = "";
    public int Count { get; set; }
    public int UniqueSessions { get; set; }
    public int UniqueVisitors { get; set; }
    public int TotalQuantity { get; set; }
    public int RevenueCents { get; set; }
    public decimal ConversionRate { get; set; }
    public List<ParfaitAnalyticsActionProductViewModel> ProductRows { get; set; } = new();
    public List<ParfaitAnalyticsRecentEventViewModel> Events { get; set; } = new();
}

public sealed class ParfaitAnalyticsActionProductViewModel
{
    public string ProductName { get; set; } = "";
    public int EventCount { get; set; }
    public int SessionCount { get; set; }
    public int TotalQuantity { get; set; }
    public int RevenueCents { get; set; }
}

public sealed class ParfaitAnalyticsRecentEventViewModel
{
    public DateTime EventUtc { get; set; }
    public string? ProductName { get; set; }
    public string? Size { get; set; }
    public int? Quantity { get; set; }
    public int? ValueCents { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
}

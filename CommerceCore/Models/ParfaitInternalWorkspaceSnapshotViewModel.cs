namespace ParfaitApp.Models;

public sealed class ParfaitInternalWorkspaceSnapshotViewModel
{
    public string StoreName { get; set; } = "Parfait";
    public string BusinessType { get; set; } = "Apparel / Ecommerce";

    public bool HasCheckoutUrl { get; set; }
    public bool HasMetaPixel { get; set; }
    public bool HasMetaConnection { get; set; }
    public bool HasAnalyticsTraffic { get; set; }

    public string MetaConnectionLabel { get; set; } = "Meta Ads not connected for Parfait.";
    public string MetaCapiStatus { get; set; } = "Pending secure Meta OAuth connection";
    public string AnalyticsStatus { get; set; } = "Pending shared analytics integration";
    public string TrustStatus { get; set; } = "Pending visitor intelligence integration";

    public int ProductCount { get; set; }
    public int ActiveProductCount { get; set; }
    public int FeaturedProductCount { get; set; }
    public int ProductImageCount { get; set; }

    public int OrderCount { get; set; }
    public int PaidOrderCount { get; set; }
    public int PendingOrderCount { get; set; }
    public int FailedOrderCount { get; set; }
    public int OpenFulfillmentCount { get; set; }
    public int CustomerCount { get; set; }
    public int RepeatCustomerCount { get; set; }
    public int RevenueCents { get; set; }
    public int AverageOrderValueCents { get; set; }
    public DateTime? LatestOrderUtc { get; set; }

    public int Visitors { get; set; }
    public int Sessions { get; set; }
    public int StoreViews { get; set; }
    public int ProductViews { get; set; }
    public int AddToCarts { get; set; }
    public int CheckoutStarts { get; set; }
    public int Purchases { get; set; }
    public decimal CheckoutToPurchaseRate { get; set; }
}

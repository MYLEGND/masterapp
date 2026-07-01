using AgentPortal.Models.Analytics;

namespace ParfaitApp.Models;

public sealed class ParfaitInternalAnalyticsViewModel
{
    public string SelectedPreset { get; set; } = "30d";
    public string RangeLabel { get; set; } = "";

    public SummaryKpiDto Summary { get; set; } = new();
    public TrafficOverviewDto Traffic { get; set; } = new();
    public PagePerformanceDto PagePerformance { get; set; } = new();
    public CtaPerformanceDto CtaPerformance { get; set; } = new();
    public EngagementSummaryDto Engagement { get; set; } = new();
    public JourneyAnalysisDto Journey { get; set; } = new();
    public SourcePerformanceDto Sources { get; set; } = new();
    public TimeOnPageDto TimeOnPage { get; set; } = new();
    public ExitAnalysisDto ExitAnalysis { get; set; } = new();
    public ScrollAnalysisDto ScrollAnalysis { get; set; } = new();
    public LandingPagePerformanceDto LandingPages { get; set; } = new();
    public DeviceIntelligenceDto Devices { get; set; } = new();
    public MetaSignalDashboardDto MetaSignal { get; set; } = new();
    public MetaSignalHealthDashboardDto MetaHealth { get; set; } = new();
    public ParfaitMetaAnalyticsSettingsViewModel MetaSettings { get; set; } = new();

    public int Visitors { get; set; }
    public int Sessions { get; set; }
    public int Orders { get; set; }
    public int Purchases { get; set; }
    public int RevenueCents { get; set; }
    public int AverageOrderValueCents { get; set; }
    public decimal RevenuePerVisitor { get; set; }
    public decimal RevenuePerSession { get; set; }
    public int ReturningCustomers { get; set; }
    public int NewCustomers { get; set; }
    public int CartAbandonmentSessions { get; set; }

    public List<ParfaitAnalyticsActionBreakdownViewModel> ActionBreakdowns { get; set; } = new();
    public List<ParfaitAnalyticsTopProductViewModel> TopProducts { get; set; } = new();
    public List<ParfaitAnalyticsOrderInspectorViewModel> RecentOrders { get; set; } = new();
    public List<ParfaitAnalyticsInspectorEventViewModel> RecentEvents { get; set; } = new();

    public bool HasTrackedEvents => ActionBreakdowns.Any(action => action.Count > 0);
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
    public int AverageValueCents { get; set; }
    public decimal ConversionRate { get; set; }
    public List<ParfaitAnalyticsActionProductViewModel> ProductRows { get; set; } = new();
    public List<ParfaitAnalyticsInspectorEventViewModel> Events { get; set; } = new();
}

public sealed class ParfaitAnalyticsActionProductViewModel
{
    public string ProductName { get; set; } = "";
    public string? ProductSlug { get; set; }
    public int EventCount { get; set; }
    public int SessionCount { get; set; }
    public int TotalQuantity { get; set; }
    public int RevenueCents { get; set; }
}

public sealed class ParfaitAnalyticsTopProductViewModel
{
    public string ProductName { get; set; } = "";
    public string? ProductSlug { get; set; }
    public int PurchaseCount { get; set; }
    public int UnitsSold { get; set; }
    public int RevenueCents { get; set; }
    public int ProductViews { get; set; }
    public int AddToCarts { get; set; }
    public int CheckoutStarts { get; set; }
}

public sealed class ParfaitAnalyticsOrderInspectorViewModel
{
    public string OrderNumber { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string CustomerName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string FulfillmentStatus { get; set; } = "";
    public string ReturnStatus { get; set; } = "";
    public int TotalCents { get; set; }
    public int NetRevenueCents { get; set; }
    public string Source { get; set; } = "";
    public string ItemSummary { get; set; } = "";
    public string ReceiptUrl { get; set; } = "";
}

public sealed class ParfaitAnalyticsInspectorEventViewModel
{
    public DateTime EventUtc { get; set; }
    public string EventType { get; set; } = "";
    public string? ProductName { get; set; }
    public string? ProductSlug { get; set; }
    public string? Size { get; set; }
    public int? Quantity { get; set; }
    public int? ValueCents { get; set; }
    public string? OrderNumber { get; set; }
    public string? SessionId { get; set; }
    public string? VisitorId { get; set; }
    public string? Source { get; set; }
    public string? Campaign { get; set; }
    public string? MetadataJson { get; set; }
}

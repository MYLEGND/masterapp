using System.Text.Json;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitInternalAnalyticsService
{
    private const string SiteKey = "ParfaitApp";
    private static readonly string[] FunnelEventTypes =
    [
        "ViewContent",
        "ProductViewed",
        "AddToCart",
        "CheckoutStarted",
        "Purchase"
    ];

    private readonly IAnalyticsQueryService _analytics;
    private readonly IMetaSignalAnalyticsService _metaSignal;
    private readonly IParfaitBusinessProfileService _businessProfile;
    private readonly ParfaitOrderService _orders;

    public ParfaitInternalAnalyticsService(
        IAnalyticsQueryService analytics,
        IMetaSignalAnalyticsService metaSignal,
        IParfaitBusinessProfileService businessProfile,
        ParfaitOrderService orders)
    {
        _analytics = analytics;
        _metaSignal = metaSignal;
        _businessProfile = businessProfile;
        _orders = orders;
    }

    public async Task<ParfaitInternalAnalyticsViewModel> GetDashboardAsync(string? preset = "30d", CancellationToken ct = default)
    {
        var range = TimeRangeRequest.FromPreset(string.IsNullOrWhiteSpace(preset) ? "30d" : preset, viewerTz: TimeZoneInfo.Utc);
        var scope = ScopeContext.ForSite(SiteKey, SiteKey);

        var summary = await _analytics.GetSummaryAsync(range, scope, TrafficType.All);
        var traffic = await _analytics.GetTrafficAsync(range, scope, TrafficType.All);
        var pagePerformance = await _analytics.GetPagePerformanceAsync(range, scope, TrafficType.All);
        var ctaPerformance = await _analytics.GetCtaPerformanceAsync(range, scope, TrafficType.All);
        var engagement = await _analytics.GetEngagementSummaryAsync(range, scope, TrafficType.All);
        var journey = await _analytics.GetJourneyAnalysisAsync(range, scope, TrafficType.All);
        var sources = await _analytics.GetSourcePerformanceAsync(range, scope, TrafficType.All);
        var timeOnPage = await _analytics.GetTimeOnPageAsync(range, scope, TrafficType.All);
        var exitAnalysis = await _analytics.GetExitAnalysisAsync(range, scope, TrafficType.All);
        var scrollAnalysis = await _analytics.GetScrollAnalysisAsync(range, scope);
        var landingPages = await _analytics.GetLandingPagePerformanceAsync(range, scope);
        var devices = await _analytics.GetDeviceIntelligenceAsync(range, scope, TrafficType.All);
        var metaSignal = await _metaSignal.GetDashboardAsync(range, scope, TrafficType.All, ct: ct);
        var metaHealth = await _metaSignal.GetHealthDashboardAsync(range, scope, ct);
        var metaSettings = await _businessProfile.GetMetaSettingsAsync(ct);

        var rawEvents = await _analytics
            .ScopedEvents(range, scope)
            .AsNoTracking()
            .Where(x => FunnelEventTypes.Contains(x.EventType))
            .OrderByDescending(x => x.EventUtc)
            .Take(2500)
            .Select(x => new AnalyticsEventSnapshot
            {
                EventUtc = x.EventUtc,
                EventType = x.EventType,
                PageKey = x.PageKey,
                Path = x.Path,
                SessionId = x.SessionId,
                VisitorId = x.VisitorId,
                UtmSource = x.UtmSource,
                UtmCampaign = x.UtmCampaign,
                MetadataJson = x.MetadataJson
            })
            .ToListAsync(ct);

        var parsedEvents = rawEvents
            .Select(ParseEvent)
            .OrderByDescending(x => x.EventUtc)
            .ToList();

        var allOrders = _orders.GetAllOrders().ToList();
        var ordersInRange = allOrders
            .Where(order => order.CreatedUtc >= range.FromUtc && order.CreatedUtc <= range.ToUtc)
            .OrderByDescending(order => order.CreatedUtc)
            .ToList();
        var paidOrdersInRange = ordersInRange
            .Where(order => IsPaidStatus(order.PaymentStatus))
            .ToList();
        var purchaseEvents = ResolvePurchaseEvents(parsedEvents, paidOrdersInRange);
        var storeViewEvents = EventsFor(parsedEvents, "ViewContent");
        var productViewedEvents = EventsFor(parsedEvents, "ProductViewed");
        var addToCartEvents = EventsFor(parsedEvents, "AddToCart");
        var checkoutEvents = EventsFor(parsedEvents, "CheckoutStarted");

        var storeViewSessions = DistinctSessions(storeViewEvents);
        var productViewSessions = DistinctSessions(productViewedEvents);
        var addToCartSessions = DistinctSessions(addToCartEvents);
        var checkoutSessions = DistinctSessions(checkoutEvents);
        var purchaseSessions = DistinctSessions(purchaseEvents);

        var revenueCents = _orders.SumNetRevenueCents(paidOrdersInRange);
        var averageOrderValueCents = _orders.CalculateAverageNetOrderValueCents(paidOrdersInRange);
        var visitors = DistinctVisitors(parsedEvents);
        var sessions = DistinctSessions(parsedEvents).Count;
        var cartAbandonmentSessions = addToCartSessions.Except(purchaseSessions, StringComparer.OrdinalIgnoreCase).Count();

        var actionBreakdowns = new List<ParfaitAnalyticsActionBreakdownViewModel>
        {
            BuildActionBreakdown(
                key: "view-content",
                label: "View Content",
                tone: storeViewEvents.Count > 0 ? "info" : "warning",
                tileNote: storeViewEvents.Count > 0
                    ? $"{Rate(productViewSessions.Count, storeViewSessions.Count):0.0}% reached product detail"
                    : "No storefront activity tracked yet.",
                conversionRate: Rate(productViewSessions.Count, storeViewSessions.Count),
                conversionLabel: "store-view sessions reached product detail",
                events: storeViewEvents),
            BuildActionBreakdown(
                key: "product-viewed",
                label: "Product Viewed",
                tone: productViewedEvents.Count > 0 ? "info" : "warning",
                tileNote: productViewedEvents.Count > 0
                    ? $"{Rate(addToCartSessions.Count, productViewSessions.Count):0.0}% moved into cart"
                    : "No product detail views tracked yet.",
                conversionRate: Rate(addToCartSessions.Count, productViewSessions.Count),
                conversionLabel: "product-view sessions moved into cart",
                events: productViewedEvents),
            BuildActionBreakdown(
                key: "add-to-cart",
                label: "Add To Cart",
                tone: addToCartEvents.Count > 0 ? "warning" : "danger",
                tileNote: addToCartEvents.Count > 0
                    ? $"{Rate(checkoutSessions.Count, addToCartSessions.Count):0.0}% reached checkout"
                    : "No cart activity tracked yet.",
                conversionRate: Rate(checkoutSessions.Count, addToCartSessions.Count),
                conversionLabel: "cart sessions reached checkout",
                events: addToCartEvents),
            BuildActionBreakdown(
                key: "checkout-started",
                label: "Checkout",
                tone: checkoutEvents.Count > 0 ? "info" : "warning",
                tileNote: checkoutEvents.Count > 0
                    ? $"{Rate(purchaseSessions.Count, checkoutSessions.Count):0.0}% converted to purchase"
                    : "No checkout activity tracked yet.",
                conversionRate: Rate(purchaseSessions.Count, checkoutSessions.Count),
                conversionLabel: "checkout sessions converted to purchase",
                events: checkoutEvents),
            BuildActionBreakdown(
                key: "purchase",
                label: "Purchase",
                tone: purchaseEvents.Count > 0 ? "success" : "danger",
                tileNote: purchaseEvents.Count > 0
                    ? $"{Money(revenueCents)} tracked revenue"
                    : "No purchases tracked yet.",
                conversionRate: Rate(purchaseSessions.Count, checkoutSessions.Count),
                conversionLabel: "checkout sessions converted to purchase",
                events: purchaseEvents,
                revenueOverrideCents: revenueCents)
        };

        var topProducts = BuildTopProducts(paidOrdersInRange, parsedEvents);
        var recentOrders = ordersInRange
            .Take(8)
            .Select(order => new ParfaitAnalyticsOrderInspectorViewModel
            {
                OrderNumber = order.OrderNumber,
                CreatedUtc = order.CreatedUtc,
                CustomerName = order.CustomerName,
                Email = order.Email,
                PaymentStatus = order.PaymentStatus,
                FulfillmentStatus = order.FulfillmentStatus,
                ReturnStatus = order.ReturnStatus,
                TotalCents = order.TotalCents,
                NetRevenueCents = order.NetRevenueCents,
                Source = string.IsNullOrWhiteSpace(order.Source) ? "Store" : order.Source,
                ItemSummary = BuildItemSummary(order),
                ReceiptUrl = order.ReceiptUrl
            })
            .ToList();

        var recentEvents = parsedEvents
            .OrderByDescending(x => x.EventUtc)
            .Take(20)
            .Select(MapInspectorEvent)
            .ToList();

        var customersByEmail = allOrders
            .Where(order => !string.IsNullOrWhiteSpace(order.Email))
            .GroupBy(order => order.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var returningCustomers = customersByEmail.Count(group =>
            group.Any(order => order.CreatedUtc >= range.FromUtc && order.CreatedUtc <= range.ToUtc) &&
            group.Count() > 1);
        var newCustomers = customersByEmail.Count(group =>
        {
            var firstOrder = group.MinBy(order => order.CreatedUtc);
            return firstOrder is not null &&
                   firstOrder.CreatedUtc >= range.FromUtc &&
                   firstOrder.CreatedUtc <= range.ToUtc;
        });

        return new ParfaitInternalAnalyticsViewModel
        {
            SelectedPreset = range.Preset,
            RangeLabel = range.Label,
            Summary = summary,
            Traffic = traffic,
            PagePerformance = pagePerformance,
            CtaPerformance = ctaPerformance,
            Engagement = engagement,
            Journey = journey,
            Sources = sources,
            TimeOnPage = timeOnPage,
            ExitAnalysis = exitAnalysis,
            ScrollAnalysis = scrollAnalysis,
            LandingPages = landingPages,
            Devices = devices,
            MetaSignal = metaSignal,
            MetaHealth = metaHealth,
            MetaSettings = metaSettings,
            Visitors = visitors,
            Sessions = sessions,
            Orders = ordersInRange.Count,
            Purchases = purchaseEvents.Count,
            RevenueCents = revenueCents,
            AverageOrderValueCents = averageOrderValueCents,
            RevenuePerVisitor = visitors <= 0 ? 0 : Math.Round(revenueCents / 100m / visitors, 2),
            RevenuePerSession = sessions <= 0 ? 0 : Math.Round(revenueCents / 100m / sessions, 2),
            ReturningCustomers = returningCustomers,
            NewCustomers = newCustomers,
            CartAbandonmentSessions = cartAbandonmentSessions,
            ActionBreakdowns = actionBreakdowns,
            TopProducts = topProducts,
            RecentOrders = recentOrders,
            RecentEvents = recentEvents
        };
    }

    private static List<AnalyticsEventSnapshot> ResolvePurchaseEvents(
        IReadOnlyList<AnalyticsEventSnapshot> parsedEvents,
        IReadOnlyList<ParfaitOrderRecord> paidOrdersInRange)
    {
        var purchaseEvents = EventsFor(parsedEvents, "Purchase");
        if (purchaseEvents.Count > 0)
            return purchaseEvents;

        return paidOrdersInRange
            .Select(order =>
            {
                var primaryItem = order.Items.FirstOrDefault();
                return new AnalyticsEventSnapshot
                {
                    EventUtc = order.CreatedUtc,
                    EventType = "Purchase",
                    SessionId = order.CheckoutAttemptId,
                    VisitorId = order.Email,
                    ProductName = primaryItem?.Name,
                    ProductSlug = primaryItem?.Slug,
                    Size = primaryItem?.Size,
                    Quantity = order.Items.Sum(item => item.Quantity),
                    ValueCents = order.NetRevenueCents,
                    OrderNumber = order.OrderNumber,
                    Source = order.Source
                };
            })
            .ToList();
    }

    private static List<ParfaitAnalyticsTopProductViewModel> BuildTopProducts(
        IReadOnlyList<ParfaitOrderRecord> paidOrdersInRange,
        IReadOnlyList<AnalyticsEventSnapshot> parsedEvents)
    {
        var purchasedProducts = paidOrdersInRange
            .SelectMany(order => order.Items.Select(item => new
            {
                Key = NormalizeKey(item.Slug, item.Name),
                item.Name,
                item.Slug,
                item.Quantity,
                RevenueCents = item.LineTotalCents
            }))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var slug = group.Select(item => item.Slug).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var productViewCount = parsedEvents.Count(item =>
                    item.EventType == "ProductViewed" &&
                    KeysMatch(item.ProductSlug, item.ProductName, group.Key));
                var addToCartCount = parsedEvents.Count(item =>
                    item.EventType == "AddToCart" &&
                    KeysMatch(item.ProductSlug, item.ProductName, group.Key));
                var checkoutCount = parsedEvents.Count(item =>
                    item.EventType == "CheckoutStarted" &&
                    KeysMatch(item.ProductSlug, item.ProductName, group.Key));

                return new ParfaitAnalyticsTopProductViewModel
                {
                    ProductName = group.Select(item => item.Name).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Parfait Product",
                    ProductSlug = slug,
                    PurchaseCount = group.Count(),
                    UnitsSold = group.Sum(item => item.Quantity),
                    RevenueCents = group.Sum(item => item.RevenueCents),
                    ProductViews = productViewCount,
                    AddToCarts = addToCartCount,
                    CheckoutStarts = checkoutCount
                };
            })
            .OrderByDescending(item => item.RevenueCents)
            .ThenByDescending(item => item.UnitsSold)
            .Take(8)
            .ToList();

        if (purchasedProducts.Count > 0)
            return purchasedProducts;

        return parsedEvents
            .Where(item => item.EventType == "ProductViewed" && !string.IsNullOrWhiteSpace(item.ProductName))
            .GroupBy(item => NormalizeKey(item.ProductSlug, item.ProductName), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ParfaitAnalyticsTopProductViewModel
            {
                ProductName = group.Select(item => item.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Parfait Product",
                ProductSlug = group.Select(item => item.ProductSlug).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                PurchaseCount = 0,
                UnitsSold = 0,
                RevenueCents = 0,
                ProductViews = group.Count(),
                AddToCarts = parsedEvents.Count(item =>
                    item.EventType == "AddToCart" &&
                    KeysMatch(item.ProductSlug, item.ProductName, group.Key)),
                CheckoutStarts = parsedEvents.Count(item =>
                    item.EventType == "CheckoutStarted" &&
                    KeysMatch(item.ProductSlug, item.ProductName, group.Key))
            })
            .OrderByDescending(item => item.ProductViews)
            .Take(8)
            .ToList();
    }

    private static ParfaitAnalyticsActionBreakdownViewModel BuildActionBreakdown(
        string key,
        string label,
        string tone,
        string tileNote,
        decimal conversionRate,
        string conversionLabel,
        IReadOnlyList<AnalyticsEventSnapshot> events,
        int? revenueOverrideCents = null)
    {
        var revenueCents = revenueOverrideCents ?? events.Sum(item => item.ValueCents ?? 0);
        var totalQuantity = events.Sum(item => item.Quantity ?? 0);

        return new ParfaitAnalyticsActionBreakdownViewModel
        {
            Key = key,
            Label = label,
            Tone = tone,
            TileNote = tileNote,
            ConversionRate = conversionRate,
            ConversionLabel = conversionLabel,
            Count = events.Count,
            UniqueSessions = DistinctSessions(events).Count,
            UniqueVisitors = DistinctVisitors(events),
            TotalQuantity = totalQuantity,
            RevenueCents = revenueCents,
            AverageValueCents = events.Count == 0 ? 0 : (int)Math.Round(revenueCents / (decimal)events.Count),
            ProductRows = events
                .Where(item => !string.IsNullOrWhiteSpace(item.ProductName))
                .GroupBy(item => NormalizeKey(item.ProductSlug, item.ProductName), StringComparer.OrdinalIgnoreCase)
                .Select(group => new ParfaitAnalyticsActionProductViewModel
                {
                    ProductName = group.Select(item => item.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Parfait Product",
                    ProductSlug = group.Select(item => item.ProductSlug).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    EventCount = group.Count(),
                    SessionCount = DistinctSessions(group).Count,
                    TotalQuantity = group.Sum(item => item.Quantity ?? 0),
                    RevenueCents = group.Sum(item => item.ValueCents ?? 0)
                })
                .OrderByDescending(item => item.EventCount)
                .ThenByDescending(item => item.RevenueCents)
                .Take(8)
                .ToList(),
            Events = events
                .OrderByDescending(item => item.EventUtc)
                .Select(MapInspectorEvent)
                .ToList()
        };
    }

    private static ParfaitAnalyticsInspectorEventViewModel MapInspectorEvent(AnalyticsEventSnapshot item)
    {
        return new ParfaitAnalyticsInspectorEventViewModel
        {
            EventUtc = item.EventUtc,
            EventType = item.EventType,
            PageKey = item.PageKey,
            Path = item.Path,
            ProductName = item.ProductName,
            ProductSlug = item.ProductSlug,
            Size = item.Size,
            Quantity = item.Quantity,
            ValueCents = item.ValueCents,
            OrderNumber = item.OrderNumber,
            SessionId = item.SessionId,
            VisitorId = item.VisitorId,
            Source = item.Source,
            Campaign = item.Campaign,
            UtmSource = item.UtmSource,
            UtmCampaign = item.UtmCampaign,
            TrafficClassification = item.TrafficClassification,
            Fbclid = item.Fbclid,
            Fbc = item.Fbc,
            Fbp = item.Fbp,
            Device = item.Device,
            Browser = item.Browser,
            OperatingSystem = item.OperatingSystem,
            Viewport = item.Viewport,
            MetadataJson = item.MetadataJson
        };
    }

    private static AnalyticsEventSnapshot ParseEvent(AnalyticsEventSnapshot raw)
    {
        if (string.IsNullOrWhiteSpace(raw.MetadataJson))
            return raw;

        return raw with
        {
            ProductName = raw.ProductName ?? ReadString(raw.MetadataJson, "productName") ?? ReadString(raw.MetadataJson, "name"),
            ProductSlug = raw.ProductSlug ?? ReadString(raw.MetadataJson, "productSlug") ?? ReadString(raw.MetadataJson, "slug"),
            Size = raw.Size ?? ReadString(raw.MetadataJson, "size"),
            Quantity = raw.Quantity ?? ReadInt(raw.MetadataJson, "quantity"),
            ValueCents = raw.ValueCents ?? ReadInt(raw.MetadataJson, "valueCents"),
            OrderNumber = raw.OrderNumber ?? ReadString(raw.MetadataJson, "orderNumber"),
            Source = raw.Source ?? raw.UtmSource ?? ReadString(raw.MetadataJson, "source"),
            Campaign = raw.Campaign ?? raw.UtmCampaign ?? ReadString(raw.MetadataJson, "campaign"),
            UtmSource = raw.UtmSource ?? ReadString(raw.MetadataJson, "utmSource"),
            UtmCampaign = raw.UtmCampaign ?? ReadString(raw.MetadataJson, "utmCampaign"),
            TrafficClassification = raw.TrafficClassification
                ?? ReadString(raw.MetadataJson, "trafficClassification")
                ?? ReadString(raw.MetadataJson, "trafficClass"),
            Fbclid = raw.Fbclid ?? ReadString(raw.MetadataJson, "fbclid"),
            Fbc = raw.Fbc ?? ReadString(raw.MetadataJson, "fbc"),
            Fbp = raw.Fbp ?? ReadString(raw.MetadataJson, "fbp"),
            Device = raw.Device ?? ReadString(raw.MetadataJson, "device"),
            Browser = raw.Browser ?? ReadString(raw.MetadataJson, "browser"),
            OperatingSystem = raw.OperatingSystem
                ?? ReadString(raw.MetadataJson, "operatingSystem")
                ?? ReadString(raw.MetadataJson, "os"),
            Viewport = raw.Viewport ?? ReadViewport(raw.MetadataJson)
        };
    }

    private static List<AnalyticsEventSnapshot> EventsFor(IEnumerable<AnalyticsEventSnapshot> events, string eventType)
    {
        return events
            .Where(item => string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static HashSet<string> DistinctSessions(IEnumerable<AnalyticsEventSnapshot> events)
    {
        return events
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId))
            .Select(item => item.SessionId!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int DistinctVisitors(IEnumerable<AnalyticsEventSnapshot> events)
    {
        return events
            .Where(item => !string.IsNullOrWhiteSpace(item.VisitorId))
            .Select(item => item.VisitorId!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static bool IsPaidStatus(string? status)
    {
        return string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Refunded", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal Rate(int numerator, int denominator)
    {
        return denominator <= 0
            ? 0
            : Math.Round((decimal)numerator / denominator * 100m, 1);
    }

    private static string Money(int cents)
    {
        return "$" + (cents / 100m).ToString("0.00");
    }

    private static string BuildItemSummary(ParfaitOrderRecord order)
    {
        if (order.Items.Count == 0)
            return "No items";

        return string.Join(" · ", order.Items.Select(item => $"{item.Name} x{item.Quantity}"));
    }

    private static string NormalizeKey(string? slug, string? name)
    {
        var preferred = string.IsNullOrWhiteSpace(slug) ? name : slug;
        return string.IsNullOrWhiteSpace(preferred)
            ? "parfait-product"
            : preferred.Trim().ToLowerInvariant();
    }

    private static bool KeysMatch(string? slug, string? name, string key)
    {
        return NormalizeKey(slug, name).Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(string? json, string key)
    {
        var value = ReadString(json, key);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? ReadViewport(string? json)
    {
        var explicitViewport = ReadString(json, "viewport");
        if (!string.IsNullOrWhiteSpace(explicitViewport))
            return explicitViewport;

        var width = ReadInt(json, "viewportWidth");
        var height = ReadInt(json, "viewportHeight");
        if (width.HasValue && height.HasValue)
            return $"{width.Value} x {height.Value}";

        return null;
    }

    private sealed record AnalyticsEventSnapshot
    {
        public DateTime EventUtc { get; init; }
        public string EventType { get; init; } = "";
        public string? PageKey { get; init; }
        public string? Path { get; init; }
        public string? SessionId { get; init; }
        public string? VisitorId { get; init; }
        public string? UtmSource { get; init; }
        public string? UtmCampaign { get; init; }
        public string? MetadataJson { get; init; }
        public string? ProductName { get; init; }
        public string? ProductSlug { get; init; }
        public string? Size { get; init; }
        public int? Quantity { get; init; }
        public int? ValueCents { get; init; }
        public string? OrderNumber { get; init; }
        public string? Source { get; init; }
        public string? Campaign { get; init; }
        public string? TrafficClassification { get; init; }
        public string? Fbclid { get; init; }
        public string? Fbc { get; init; }
        public string? Fbp { get; init; }
        public string? Device { get; init; }
        public string? Browser { get; init; }
        public string? OperatingSystem { get; init; }
        public string? Viewport { get; init; }
    }
}

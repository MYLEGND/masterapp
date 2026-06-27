using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitAnalyticsDashboardService
{
    private readonly MasterAppDbContext _db;

    public ParfaitAnalyticsDashboardService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<ParfaitAnalyticsDashboardViewModel> GetDashboardAsync(CancellationToken ct = default)
    {
        var events = await _db.AnalyticsEvents
            .AsNoTracking()
            .Where(e => e.MetadataJson != null && e.MetadataJson.Contains("\"siteKey\":\"ParfaitApp\""))
            .OrderByDescending(e => e.EventUtc)
            .Take(1000)
            .Select(e => new
            {
                e.EventUtc,
                e.EventType,
                e.VisitorId,
                e.SessionId,
                e.MetadataJson
            })
            .ToListAsync(ct);

        var parsed = events.Select(e => new ParsedEvent(
            e.EventUtc,
            e.EventType,
            e.VisitorId,
            e.SessionId,
            ReadString(e.MetadataJson, "productName"),
            ReadString(e.MetadataJson, "size"),
            ReadInt(e.MetadataJson, "quantity"),
            ReadInt(e.MetadataJson, "valueCents")
        )).ToList();

        var storeViewEvents = EventsFor(parsed, "ViewContent");
        var productViewedEvents = EventsFor(parsed, "ProductViewed");
        var addToCartEvents = EventsFor(parsed, "AddToCart");
        var checkoutEvents = EventsFor(parsed, "CheckoutStarted");
        var purchaseEvents = EventsFor(parsed, "Purchase");

        var productViews = parsed.Count(e => e.EventType == "ProductViewed");
        var addToCarts = parsed.Count(e => e.EventType == "AddToCart");
        var checkoutStarts = parsed.Count(e => e.EventType == "CheckoutStarted");
        var purchases = parsed.Count(e => e.EventType == "Purchase");

        var storeViewSessions = StepSessions(storeViewEvents);
        var productViewSessions = StepSessions(parsed, "ProductViewed");
        var addToCartSessions = StepSessions(parsed, "AddToCart");
        var checkoutStartSessions = StepSessions(parsed, "CheckoutStarted");
        var purchaseSessions = StepSessions(parsed, "Purchase");

        var storeViewToProductViewRate = Rate(productViewSessions.Count, storeViewSessions.Count);
        var productViewToCartRate = Rate(addToCartSessions.Count, productViewSessions.Count);
        var cartToCheckoutRate = Rate(checkoutStartSessions.Count, addToCartSessions.Count);
        var checkoutToPurchaseRate = Rate(purchaseSessions.Count, checkoutStartSessions.Count);

        return new ParfaitAnalyticsDashboardViewModel
        {
            Visitors = parsed.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId).Distinct().Count(),
            Sessions = parsed.Where(e => !string.IsNullOrWhiteSpace(e.SessionId)).Select(e => e.SessionId).Distinct().Count(),
            StoreViews = storeViewEvents.Count,
            ProductViews = productViews,
            AddToCarts = addToCarts,
            CheckoutStarts = checkoutStarts,
            Purchases = purchases,
            RevenueCents = purchaseEvents.Sum(e => e.ValueCents ?? 0),
            ProductViewToCartRate = productViewToCartRate,
            CartToCheckoutRate = cartToCheckoutRate,
            CheckoutToPurchaseRate = checkoutToPurchaseRate,
            ActionBreakdowns = new List<ParfaitAnalyticsActionBreakdownViewModel>
            {
                BuildActionBreakdown(
                    key: "view-content",
                    label: "View Content",
                    tone: storeViewEvents.Count > 0 ? "info" : "warning",
                    tileNote: storeViewEvents.Count > 0
                        ? $"{storeViewToProductViewRate:0.0}% reached product detail"
                        : "No storefront activity tracked yet.",
                    conversionRate: storeViewToProductViewRate,
                    conversionLabel: "store-view sessions reached product detail",
                    events: storeViewEvents),
                BuildActionBreakdown(
                    key: "product-viewed",
                    label: "Product Viewed",
                    tone: productViewedEvents.Count > 0 ? "info" : "warning",
                    tileNote: productViewedEvents.Count > 0
                        ? $"{productViewToCartRate:0.0}% moved into cart"
                        : "No product detail views tracked yet.",
                    conversionRate: productViewToCartRate,
                    conversionLabel: "product-view sessions added to cart",
                    events: productViewedEvents),
                BuildActionBreakdown(
                    key: "add-to-cart",
                    label: "Add To Cart",
                    tone: addToCartEvents.Count > 0 ? "warning" : "danger",
                    tileNote: addToCartEvents.Count > 0
                        ? $"{cartToCheckoutRate:0.0}% reached checkout"
                        : "No cart actions tracked yet.",
                    conversionRate: cartToCheckoutRate,
                    conversionLabel: "cart sessions reached checkout",
                    events: addToCartEvents),
                BuildActionBreakdown(
                    key: "checkout-started",
                    label: "Checkout",
                    tone: checkoutEvents.Count > 0 ? "info" : "warning",
                    tileNote: checkoutEvents.Count > 0
                        ? $"{checkoutToPurchaseRate:0.0}% converted to purchase"
                        : "No checkout starts tracked yet.",
                    conversionRate: checkoutToPurchaseRate,
                    conversionLabel: "checkout sessions converted to purchase",
                    events: checkoutEvents),
                BuildActionBreakdown(
                    key: "purchase",
                    label: "Purchase",
                    tone: purchaseEvents.Count > 0 ? "success" : "danger",
                    tileNote: purchaseEvents.Count > 0
                        ? $"${(purchaseEvents.Sum(e => e.ValueCents ?? 0) / 100m):0.00} tracked revenue"
                        : "No purchases tracked yet.",
                    conversionRate: checkoutToPurchaseRate,
                    conversionLabel: "checkout sessions converted to purchase",
                    events: purchaseEvents)
            }
        };
    }

    private static List<ParsedEvent> EventsFor(IEnumerable<ParsedEvent> events, string eventType)
    {
        return events
            .Where(e => string.Equals(e.EventType, eventType, StringComparison.Ordinal))
            .ToList();
    }

    private static HashSet<string> StepSessions(IEnumerable<ParsedEvent> events, string eventType)
    {
        return events
            .Where(e => e.EventType == eventType && !string.IsNullOrWhiteSpace(e.SessionId))
            .Select(e => e.SessionId!)
            .Distinct()
            .ToHashSet();
    }

    private static HashSet<string> StepSessions(IEnumerable<ParsedEvent> events)
    {
        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .Select(e => e.SessionId!)
            .Distinct()
            .ToHashSet();
    }

    private static ParfaitAnalyticsActionBreakdownViewModel BuildActionBreakdown(
        string key,
        string label,
        string tone,
        string tileNote,
        decimal conversionRate,
        string conversionLabel,
        List<ParsedEvent> events)
    {
        return new ParfaitAnalyticsActionBreakdownViewModel
        {
            Key = key,
            Label = label,
            Tone = tone,
            TileNote = tileNote,
            ConversionRate = conversionRate,
            ConversionLabel = conversionLabel,
            Count = events.Count,
            UniqueSessions = events
                .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
                .Select(e => e.SessionId!)
                .Distinct()
                .Count(),
            UniqueVisitors = events
                .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
                .Select(e => e.VisitorId!)
                .Distinct()
                .Count(),
            TotalQuantity = events.Sum(e => e.Quantity ?? 0),
            RevenueCents = events.Sum(e => e.ValueCents ?? 0),
            ProductRows = events
                .Where(e => !string.IsNullOrWhiteSpace(e.ProductName))
                .GroupBy(e => e.ProductName!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ParfaitAnalyticsActionProductViewModel
                {
                    ProductName = group.First().ProductName!,
                    EventCount = group.Count(),
                    SessionCount = group
                        .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
                        .Select(e => e.SessionId!)
                        .Distinct()
                        .Count(),
                    TotalQuantity = group.Sum(e => e.Quantity ?? 0),
                    RevenueCents = group.Sum(e => e.ValueCents ?? 0)
                })
                .OrderByDescending(row => row.EventCount)
                .ThenByDescending(row => row.RevenueCents)
                .Take(8)
                .ToList(),
            Events = events
                .Select(e => new ParfaitAnalyticsRecentEventViewModel
                {
                    EventUtc = e.EventUtc,
                    ProductName = e.ProductName,
                    Size = e.Size,
                    Quantity = e.Quantity,
                    ValueCents = e.ValueCents,
                    SessionId = e.SessionId,
                    VisitorId = e.VisitorId
                })
                .ToList()
        };
    }

    private static decimal Rate(int numerator, int denominator)
    {
        return denominator <= 0 ? 0 : Math.Round((decimal)numerator / denominator * 100m, 1);
    }

    private static string? ReadString(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

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

    private sealed record ParsedEvent(
        DateTime EventUtc,
        string EventType,
        string? VisitorId,
        string? SessionId,
        string? ProductName,
        string? Size,
        int? Quantity,
        int? ValueCents);
}

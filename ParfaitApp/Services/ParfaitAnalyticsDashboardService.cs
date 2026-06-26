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

        var productViews = parsed.Count(e => e.EventType == "ProductViewed");
        var addToCarts = parsed.Count(e => e.EventType == "AddToCart");
        var checkoutStarts = parsed.Count(e => e.EventType == "CheckoutStarted");
        var purchases = parsed.Count(e => e.EventType == "Purchase");

        var productViewSessions = StepSessions(parsed, "ProductViewed");
        var addToCartSessions = StepSessions(parsed, "AddToCart");
        var checkoutStartSessions = StepSessions(parsed, "CheckoutStarted");
        var purchaseSessions = StepSessions(parsed, "Purchase");

        return new ParfaitAnalyticsDashboardViewModel
        {
            Visitors = parsed.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId).Distinct().Count(),
            Sessions = parsed.Where(e => !string.IsNullOrWhiteSpace(e.SessionId)).Select(e => e.SessionId).Distinct().Count(),
            StoreViews = parsed.Count(e => e.EventType == "ViewContent"),
            ProductViews = productViews,
            AddToCarts = addToCarts,
            CheckoutStarts = checkoutStarts,
            Purchases = purchases,
            RevenueCents = parsed.Where(e => e.EventType == "Purchase").Sum(e => e.ValueCents ?? 0),
            ProductViewToCartRate = Rate(addToCartSessions.Count, productViewSessions.Count),
            CartToCheckoutRate = Rate(checkoutStartSessions.Count, addToCartSessions.Count),
            CheckoutToPurchaseRate = Rate(purchaseSessions.Count, checkoutStartSessions.Count),
            TopProducts = parsed
                .Where(e => !string.IsNullOrWhiteSpace(e.ProductName))
                .GroupBy(e => e.ProductName!)
                .Select(g => new ParfaitAnalyticsProductMetricViewModel
                {
                    ProductName = g.Key,
                    ProductViews = g.Count(e => e.EventType == "ProductViewed"),
                    AddToCarts = g.Count(e => e.EventType == "AddToCart"),
                    CheckoutStarts = g.Count(e => e.EventType == "CheckoutStarted"),
                    Purchases = g.Count(e => e.EventType == "Purchase"),
                    RevenueCents = g.Where(e => e.EventType == "Purchase").Sum(e => e.ValueCents ?? 0)
                })
                .OrderByDescending(x => x.AddToCarts + x.CheckoutStarts + x.Purchases)
                .ThenByDescending(x => x.ProductViews)
                .Take(10)
                .ToList(),
            RecentEvents = parsed
                .Take(25)
                .Select(e => new ParfaitAnalyticsRecentEventViewModel
                {
                    EventUtc = e.EventUtc,
                    EventType = e.EventType,
                    ProductName = e.ProductName,
                    Size = e.Size,
                    Quantity = e.Quantity,
                    ValueCents = e.ValueCents,
                    SessionId = e.SessionId
                })
                .ToList()
        };
    }

    private static HashSet<string> StepSessions(IEnumerable<ParsedEvent> events, string eventType)
    {
        return events
            .Where(e => e.EventType == eventType && !string.IsNullOrWhiteSpace(e.SessionId))
            .Select(e => e.SessionId!)
            .Distinct()
            .ToHashSet();
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

using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ParfaitApp.Models;
using Shared.Analytics;

namespace ParfaitApp.Services;

public sealed class ParfaitMetaSignalBridgeService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<ParfaitMetaSignalBridgeService> _logger;

    public ParfaitMetaSignalBridgeService(
        MasterAppDbContext db,
        ILogger<ParfaitMetaSignalBridgeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordPurchaseAsync(
        ParfaitOrderRecord order,
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        if (!string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            return;

        var dedupKey = $"ParfaitPurchase:{order.OrderNumber}";

        var alreadyRecorded = await _db.MetaSignalEvents
            .AsNoTracking()
            .AnyAsync(x => x.EventName == "Purchase" && x.MetaDeduplicationKey == dedupKey, ct);

        if (alreadyRecorded)
            return;

        var eventId = $"parfait_purchase_{StableToken(order.OrderNumber)}";
        var primaryItem = order.Items.FirstOrDefault();
        var sourceAnalytics = await ResolveLatestParfaitAnalyticsContextAsync(order.OrderNumber, ct);

        var payload = new
        {
            siteKey = "ParfaitApp",
            businessType = "Ecommerce",
            reportingOwner = "ParfaitApp",
            sourceUrl = BuildStoreSuccessUrl(httpContext, order.OrderNumber),
            sourceHost = httpContext.Request.Host.Value,
            sourcePath = "/store/success",
            orderNumber = order.OrderNumber,
            paymentStatus = order.PaymentStatus,
            squarePaymentId = order.SquarePaymentId,
            currency = "USD",
            valueCents = order.TotalCents,
            subtotalCents = order.SubtotalCents,
            shippingCents = order.ShippingCents,
            taxCents = order.TaxCents,
            productId = primaryItem?.Id,
            productName = primaryItem?.Name,
            size = primaryItem?.Size,
            quantity = order.Items.Sum(i => i.Quantity),
            items = JsonSerializer.Serialize(order.Items.Select(i => new
            {
                i.Id,
                i.Name,
                i.Size,
                i.Quantity,
                i.UnitPriceCents,
                i.LineTotalCents
            })),
            customer = new
            {
                order.FirstName,
                order.LastName,
                order.Email,
                order.Phone,
                order.City,
                order.State,
                order.PostalCode
            },
            fbclid = FirstNonBlank(httpContext.Request.Query["fbclid"].FirstOrDefault(), sourceAnalytics?.Fbclid),
            fbc = FirstNonBlank(ReadCookie(httpContext, "_fbc"), sourceAnalytics?.Fbc),
            fbp = FirstNonBlank(ReadCookie(httpContext, "_fbp"), sourceAnalytics?.Fbp),
            sourceClientIpAddress = FirstNonBlank(order.RequestIp, sourceAnalytics?.IpAddress),
            sourceClientUserAgent = FirstNonBlank(order.UserAgent, sourceAnalytics?.UserAgent),
            sourceWebDriver = sourceAnalytics?.WebDriver,
            sourceIsHeadless = sourceAnalytics?.IsHeadless,
            sourceHumanInteractionCount = sourceAnalytics?.HumanInteractionCount,
            sourceMouseMoveCount = sourceAnalytics?.MouseMoveCount,
            sourceVisibilityChangeCount = sourceAnalytics?.VisibilityChangeCount
        };

        var row = new MetaSignalEvent
        {
            CreatedUtc = DateTime.UtcNow,
            EventId = eventId,
            EventName = "Purchase",
            EventCategory = "conversion",
            QuoteType = "ecommerce",
            PageKey = "parfait_checkout_success",
            EffectivePageKey = "parfait_checkout_success",
            PageMode = "store",
            TrafficType = "ecommerce",
            FunnelStep = 8,
            StepName = "purchase",
            IntentScore = 500,
            EngagementScore = 500,
            QualificationScore = 500,
            FrictionScore = 0,
            TotalSignalScore = 500,
            ScoreTier = "Purchase",
            MetaBrowserSent = false,
            MetaServerSent = false,
            MetaDeduplicationKey = dedupKey,
            FbclidPresent = !string.IsNullOrWhiteSpace(FirstNonBlank(httpContext.Request.Query["fbclid"].FirstOrDefault(), sourceAnalytics?.Fbclid)),
            FbcPresent = !string.IsNullOrWhiteSpace(FirstNonBlank(ReadCookie(httpContext, "_fbc"), sourceAnalytics?.Fbc)),
            FbpPresent = !string.IsNullOrWhiteSpace(FirstNonBlank(ReadCookie(httpContext, "_fbp"), sourceAnalytics?.Fbp)),
            Referrer = httpContext.Request.Headers.Referer.ToString(),
            UserAgent = FirstNonBlank(order.UserAgent, sourceAnalytics?.UserAgent),
            WebDriver = sourceAnalytics?.WebDriver,
            IsHeadless = sourceAnalytics?.IsHeadless,
            MouseMoveCount = sourceAnalytics?.MouseMoveCount,
            HumanInteractionCount = sourceAnalytics?.HumanInteractionCount,
            VisibilityChangeCount = sourceAnalytics?.VisibilityChangeCount,
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            Host = httpContext.Request.Host.Value,
            MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
                "Purchase",
                leadId: null,
                sessionId: null,
                payload: payload,
                isBrowserSignal: false,
                isServerAuthority: true,
                metaServerAuthorityEligible: true,
                metaSingleTruthDispatchEligible: true,
                metaPipelineOrigin: "ParfaitApp>CommercePurchaseBridge")
        };

        _db.MetaSignalEvents.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Parfait MetaSignal purchase bridge recorded orderNumber={OrderNumber} eventId={EventId}",
            order.OrderNumber,
            eventId);
    }

    private async Task<ParfaitAnalyticsContext?> ResolveLatestParfaitAnalyticsContextAsync(string orderNumber, CancellationToken ct)
    {
        var orderMarker = $"\\\"orderNumber\\\":\\\"{orderNumber}\\\"";

        var source = await _db.AnalyticsEvents
            .AsNoTracking()
            .Where(x => x.MetadataJson != null &&
                        x.MetadataJson.Contains("\"siteKey\":\"ParfaitApp\"") &&
                        x.MetadataJson.Contains(orderMarker))
            .OrderByDescending(x => x.EventUtc)
            .Select(x => new
            {
                x.UserAgent,
                x.IpAddress,
                x.WebDriver,
                x.IsHeadless,
                x.MouseMoveCount,
                x.HumanInteractionCount,
                x.VisibilityChangeCount,
                x.MetadataJson
            })
            .FirstOrDefaultAsync(ct);

        source ??= await _db.AnalyticsEvents
            .AsNoTracking()
            .Where(x => x.MetadataJson != null && x.MetadataJson.Contains("\"siteKey\":\"ParfaitApp\""))
            .OrderByDescending(x => x.EventUtc)
            .Select(x => new
            {
                x.UserAgent,
                x.IpAddress,
                x.WebDriver,
                x.IsHeadless,
                x.MouseMoveCount,
                x.HumanInteractionCount,
                x.VisibilityChangeCount,
                x.MetadataJson
            })
            .FirstOrDefaultAsync(ct);

        if (source is null)
            return null;

        return new ParfaitAnalyticsContext(
            source.UserAgent,
            source.IpAddress,
            source.WebDriver,
            source.IsHeadless,
            source.MouseMoveCount,
            source.HumanInteractionCount,
            source.VisibilityChangeCount,
            ReadMetadataString(source.MetadataJson, "fbclid"),
            ReadMetadataString(source.MetadataJson, "fbc"),
            ReadMetadataString(source.MetadataJson, "fbp"));
    }

    private static string BuildStoreSuccessUrl(HttpContext httpContext, string orderNumber)
    {
        var scheme = httpContext.Request.Scheme;
        var host = httpContext.Request.Host.Value;
        return $"{scheme}://{host}/store/success?orderNumber={Uri.EscapeDataString(orderNumber)}";
    }

    private static string? ReadMetadataString(string? json, string key)
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

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? ReadCookie(HttpContext httpContext, string name)
        => httpContext.Request.Cookies.TryGetValue(name, out var value) ? value : null;

    private static string StableToken(string value)
        => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private sealed record ParfaitAnalyticsContext(
        string? UserAgent,
        string? IpAddress,
        bool? WebDriver,
        bool? IsHeadless,
        int? MouseMoveCount,
        int? HumanInteractionCount,
        int? VisibilityChangeCount,
        string? Fbclid,
        string? Fbc,
        string? Fbp);
}

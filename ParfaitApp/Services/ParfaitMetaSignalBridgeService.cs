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

        var payload = new
        {
            siteKey = "ParfaitApp",
            businessType = "Ecommerce",
            reportingOwner = "ParfaitApp",
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
            fbclid = httpContext.Request.Query["fbclid"].FirstOrDefault(),
            fbc = ReadCookie(httpContext, "_fbc"),
            fbp = ReadCookie(httpContext, "_fbp")
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
            FbclidPresent = !string.IsNullOrWhiteSpace(httpContext.Request.Query["fbclid"].FirstOrDefault()),
            FbcPresent = !string.IsNullOrWhiteSpace(ReadCookie(httpContext, "_fbc")),
            FbpPresent = !string.IsNullOrWhiteSpace(ReadCookie(httpContext, "_fbp")),
            Referrer = httpContext.Request.Headers.Referer.ToString(),
            UserAgent = order.UserAgent,
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

    private static string? ReadCookie(HttpContext httpContext, string name)
        => httpContext.Request.Cookies.TryGetValue(name, out var value) ? value : null;

    private static string StableToken(string value)
        => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

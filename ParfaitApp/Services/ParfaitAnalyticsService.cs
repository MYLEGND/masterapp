using System.Reflection;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public sealed class ParfaitAnalyticsService
{
    private const string SiteKey = "ParfaitApp";
    private const string BusinessType = "Ecommerce";
    private const string ReportingOwner = "ParfaitApp";

    private readonly MasterAppDbContext _db;

    public ParfaitAnalyticsService(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task TrackAsync(
        ParfaitAnalyticsEventRequest request,
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        var eventName = NormalizeEventName(request.EventName);
        if (eventName is null)
            return;

        var now = DateTime.UtcNow;
        var visitorId = Clean(request.VisitorId) ?? GetOrCreateCookie(httpContext, "pf_vid", "pfv");
        var sessionId = Clean(request.SessionId) ?? GetOrCreateCookie(httpContext, "pf_sid", "pfs", sessionOnly: true);
        var eventId = Clean(request.EventId) ?? Guid.NewGuid().ToString("N");
        var url = Clean(request.Url) ?? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.Path}{httpContext.Request.QueryString}";
        var referrer = Clean(request.Referrer) ?? httpContext.Request.Headers.Referer.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();

        var metadata = BuildMetadata(request, httpContext, eventName, eventId, visitorId, sessionId, url, referrer);

        var analyticsEvent = new AnalyticsEvent();
        Set(analyticsEvent, "EventId", Guid.TryParse(eventId, out var parsedEventId) ? parsedEventId : Guid.NewGuid());
        Set(analyticsEvent, "ClientEventId", eventId);
        Set(analyticsEvent, "EventType", eventName);
        Set(analyticsEvent, "EventName", eventName);
        Set(analyticsEvent, "EventUtc", now);
        Set(analyticsEvent, "CreatedUtc", now);
        Set(analyticsEvent, "VisitorId", visitorId);
        Set(analyticsEvent, "SessionId", sessionId);
        Set(analyticsEvent, "Url", url);
        Set(analyticsEvent, "PageUrl", url);
        Set(analyticsEvent, "Path", httpContext.Request.Path.ToString());
        Set(analyticsEvent, "Host", httpContext.Request.Host.Host);
        Set(analyticsEvent, "Referrer", referrer);
        Set(analyticsEvent, "UserAgent", userAgent);
        Set(analyticsEvent, "IpAddress", ip);
        Set(analyticsEvent, "Environment", "ParfaitApp");
        Set(analyticsEvent, "SourceApp", "ParfaitApp");
        Set(analyticsEvent, "MetadataJson", JsonSerializer.Serialize(metadata));
        Set(analyticsEvent, "PipelineStamp", "ParfaitApp>CommerceAnalytics");
        SetUtmAndMetaFields(analyticsEvent, httpContext);

        _db.AnalyticsEvents.Add(analyticsEvent);

        await _db.SaveChangesAsync(ct);
    }

    public async Task TrackPurchaseAsync(
        ParfaitOrderRecord order,
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        await TrackAsync(new ParfaitAnalyticsEventRequest
        {
            EventName = "Purchase",
            OrderNumber = order.OrderNumber,
            ValueCents = order.TotalCents,
            Metadata = new Dictionary<string, string?>
            {
                ["paymentStatus"] = order.PaymentStatus,
                ["squarePaymentId"] = order.SquarePaymentId,
                ["items"] = JsonSerializer.Serialize(order.Items.Select(i => new
                {
                    i.Id,
                    i.Name,
                    i.Size,
                    i.Quantity,
                    i.UnitPriceCents,
                    i.LineTotalCents
                }))
            }
        }, httpContext, ct);
    }

    private static Dictionary<string, object?> BuildMetadata(
        ParfaitAnalyticsEventRequest request,
        HttpContext httpContext,
        string eventName,
        string eventId,
        string visitorId,
        string sessionId,
        string url,
        string referrer)
    {
        var query = httpContext.Request.Query;

        var metadata = new Dictionary<string, object?>
        {
            ["siteKey"] = SiteKey,
            ["businessType"] = BusinessType,
            ["reportingOwner"] = ReportingOwner,
            ["eventName"] = eventName,
            ["eventId"] = eventId,
            ["visitorId"] = visitorId,
            ["sessionId"] = sessionId,
            ["sourceUrl"] = url,
            ["sourceHost"] = httpContext.Request.Host.Host,
            ["sourcePath"] = httpContext.Request.Path.ToString(),
            ["referrer"] = referrer,
            ["productId"] = request.ProductId,
            ["productName"] = request.ProductName,
            ["productSlug"] = request.ProductSlug,
            ["size"] = request.Size,
            ["quantity"] = request.Quantity,
            ["valueCents"] = request.ValueCents,
            ["orderNumber"] = request.OrderNumber,
            ["utm_source"] = Read(query, "utm_source"),
            ["utm_medium"] = Read(query, "utm_medium"),
            ["utm_campaign"] = Read(query, "utm_campaign"),
            ["utm_content"] = Read(query, "utm_content"),
            ["utm_term"] = Read(query, "utm_term"),
            ["fbclid"] = Read(query, "fbclid"),
            ["fbc"] = httpContext.Request.Cookies["_fbc"],
            ["fbp"] = httpContext.Request.Cookies["_fbp"]
        };

        foreach (var pair in request.Metadata)
            metadata[pair.Key] = pair.Value;

        return metadata;
    }

    private static string? NormalizeEventName(string? value)
    {
        var clean = Clean(value);
        if (clean is null) return null;

        return clean switch
        {
            "ViewContent" => "ViewContent",
            "ProductViewed" => "ProductViewed",
            "AddToCart" => "AddToCart",
            "CheckoutStarted" => "CheckoutStarted",
            "Purchase" => "Purchase",
            _ => null
        };
    }

    private static void SetUtmAndMetaFields(object entity, HttpContext httpContext)
    {
        var q = httpContext.Request.Query;

        Set(entity, "UtmSource", Read(q, "utm_source"));
        Set(entity, "UtmMedium", Read(q, "utm_medium"));
        Set(entity, "UtmCampaign", Read(q, "utm_campaign"));
        Set(entity, "UtmContent", Read(q, "utm_content"));
        Set(entity, "UtmTerm", Read(q, "utm_term"));
        Set(entity, "Fbclid", Read(q, "fbclid"));
        Set(entity, "Fbc", httpContext.Request.Cookies["_fbc"]);
        Set(entity, "Fbp", httpContext.Request.Cookies["_fbp"]);
    }

    private static string? Read(IQueryCollection query, string key)
    {
        return query.TryGetValue(key, out var value) ? Clean(value.ToString()) : null;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string GetOrCreateCookie(HttpContext httpContext, string name, string prefix, bool sessionOnly = false)
    {
        if (httpContext.Request.Cookies.TryGetValue(name, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        var value = $"{prefix}_{Guid.NewGuid():N}";

        httpContext.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = false,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = sessionOnly ? null : DateTimeOffset.UtcNow.AddYears(1)
        });

        return value;
    }

    private static void Set(object target, string propertyName, object? value)
    {
        if (value is null) return;

        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanWrite) return;

        try
        {
            var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (targetType == typeof(string))
                property.SetValue(target, value.ToString());
            else if (targetType == typeof(DateTime) && value is DateTime dt)
                property.SetValue(target, dt);
            else if (targetType == typeof(Guid) && value is Guid guid)
                property.SetValue(target, guid);
            else if (targetType == typeof(int) && int.TryParse(value.ToString(), out var i))
                property.SetValue(target, i);
            else if (targetType == typeof(long) && long.TryParse(value.ToString(), out var l))
                property.SetValue(target, l);
            else if (targetType == typeof(bool) && bool.TryParse(value.ToString(), out var b))
                property.SetValue(target, b);
        }
        catch
        {
            // Ignore optional property mismatches so Parfait can adapt to the shared schema safely.
        }
    }
}

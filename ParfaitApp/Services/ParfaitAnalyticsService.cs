using System.Reflection;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.WebUtilities;
using Shared.Analytics;
using ParfaitApp.Models;

namespace ParfaitApp.Services;

public interface IParfaitAnalyticsService
{
    Task TrackAsync(
        ParfaitAnalyticsEventRequest request,
        HttpContext httpContext,
        CancellationToken ct = default);

    Task TrackPurchaseAsync(
        ParfaitOrderRecord order,
        HttpContext httpContext,
        CancellationToken ct = default);
}

public sealed class ParfaitAnalyticsService : IParfaitAnalyticsService
{
    private const string SiteKey = "ParfaitApp";
    private const string BusinessType = "Ecommerce";
    private const string ReportingOwner = "ParfaitApp";
    private static readonly string[] NonProductionHostHints =
    [
        "localhost",
        "127.0.0.1",
        "::1",
        ".local",
        "dev",
        "staging",
        "preview",
        "sandbox",
        "ngrok",
        "azurewebsites.net"
    ];
    private static readonly IReadOnlyDictionary<string, string> CommerceEventNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ViewContent"] = "ViewContent",
        ["ProductViewed"] = "ProductViewed",
        ["AddToCart"] = "AddToCart",
        ["CheckoutStarted"] = "CheckoutStarted",
        ["Purchase"] = "Purchase"
    };

    private readonly MasterAppDbContext _db;
    private readonly HashSet<string> _publicHosts;

    public ParfaitAnalyticsService(MasterAppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _publicHosts = BuildPublicHosts(configuration);
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
        var sourceUri = TryParseAbsoluteUri(url);
        var sourcePath = Clean(sourceUri?.AbsolutePath) ?? httpContext.Request.Path.ToString();
        var sourceHost = Clean(sourceUri?.Host) ?? httpContext.Request.Host.Host;
        var sourceQuery = ParseQueryParameters(sourceUri);
        var pageKey = ResolvePageKey(sourcePath, request.PageKey, request.ProductSlug);
        var referrerHost = ResolveHost(referrer);
        var isInternalTraffic = IsInternalTrafficSource(httpContext, sourceHost, sourcePath);
        var environment = ResolveEnvironment(httpContext, sourceHost, isInternalTraffic);

        var metadata = BuildMetadata(request, httpContext, eventName, eventId, visitorId, sessionId, url, referrer, sourceHost, sourcePath, sourceQuery);

        var analyticsEvent = new AnalyticsEvent();
        Set(analyticsEvent, "EventId", Guid.TryParse(eventId, out var parsedEventId) ? parsedEventId : Guid.NewGuid());
        Set(analyticsEvent, "ClientEventId", eventId);
        Set(analyticsEvent, "EventType", eventName);
        Set(analyticsEvent, "EventName", eventName);
        Set(analyticsEvent, "EventUtc", now);
        Set(analyticsEvent, "ReceivedUtc", now);
        Set(analyticsEvent, "VisitorId", visitorId);
        Set(analyticsEvent, "SessionId", sessionId);
        Set(analyticsEvent, "Url", url);
        Set(analyticsEvent, "PageUrl", url);
        Set(analyticsEvent, "Path", sourcePath);
        Set(analyticsEvent, "Host", sourceHost);
        Set(analyticsEvent, "PageKey", pageKey);
        Set(analyticsEvent, "SectionKey", request.SectionKey);
        Set(analyticsEvent, "ElementKey", request.ElementKey);
        Set(analyticsEvent, "ButtonLabel", request.ButtonLabel);
        Set(analyticsEvent, "Referrer", referrer);
        Set(analyticsEvent, "ReferrerHost", referrerHost);
        Set(analyticsEvent, "UserAgent", userAgent);
        Set(analyticsEvent, "IpAddress", ip);
        Set(analyticsEvent, "IsInternal", isInternalTraffic);
        Set(analyticsEvent, "Environment", environment);
        Set(analyticsEvent, "SourceApp", "ParfaitApp");
        Set(analyticsEvent, "DeviceType", request.DeviceType);
        Set(analyticsEvent, "Browser", request.Browser);
        Set(analyticsEvent, "OperatingSystem", request.OperatingSystem);
        Set(analyticsEvent, "TimeZone", request.TimeZone);
        Set(analyticsEvent, "Language", request.Language);
        Set(analyticsEvent, "ScreenWidth", request.ScreenWidth);
        Set(analyticsEvent, "ScreenHeight", request.ScreenHeight);
        Set(analyticsEvent, "ViewportWidth", request.ViewportWidth);
        Set(analyticsEvent, "ViewportHeight", request.ViewportHeight);
        Set(analyticsEvent, "ScrollPercent", request.ScrollPercent);
        Set(analyticsEvent, "DwellMilliseconds", request.DwellMilliseconds);
        Set(analyticsEvent, "EngagedMilliseconds", request.EngagedMilliseconds);
        Set(analyticsEvent, "IsBounceCandidate", request.IsBounceCandidate);
        Set(analyticsEvent, "IsExitPage", request.IsExitPage);
        Set(analyticsEvent, "WebDriver", request.WebDriver);
        Set(analyticsEvent, "IsHeadless", request.IsHeadless);
        Set(analyticsEvent, "MouseMoveCount", request.MouseMoveCount);
        Set(analyticsEvent, "HumanInteractionCount", request.HumanInteractionCount);
        Set(analyticsEvent, "VisibilityChangeCount", request.VisibilityChangeCount);
        Set(analyticsEvent, "TrackingVersion", Clean(request.TrackingVersion) ?? "parfait-commerce-tracking-v2");
        Set(analyticsEvent, "SchemaVersion", 2);
        Set(analyticsEvent, "MetadataJson", JsonSerializer.Serialize(metadata));
        Set(analyticsEvent, "PipelineStamp", "ParfaitApp>CommerceAnalytics");
        SetUtmAndMetaFields(analyticsEvent, httpContext, sourceQuery);

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
            Url = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/store/checkout",
            PageKey = "parfait_checkout",
            Metadata = new Dictionary<string, string?>
            {
                ["paymentStatus"] = order.PaymentStatus,
                ["paymentReferenceId"] = order.PaymentReferenceId,
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
        string referrer,
        string sourceHost,
        string sourcePath,
        IReadOnlyDictionary<string, string?> sourceQuery)
    {
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
            ["sourceHost"] = sourceHost,
            ["sourcePath"] = sourcePath,
            ["referrer"] = referrer,
            ["pageKey"] = ResolvePageKey(sourcePath, request.PageKey, request.ProductSlug),
            ["sectionKey"] = request.SectionKey,
            ["elementKey"] = request.ElementKey,
            ["buttonLabel"] = request.ButtonLabel,
            ["productId"] = request.ProductId,
            ["productName"] = request.ProductName,
            ["productSlug"] = request.ProductSlug,
            ["size"] = request.Size,
            ["quantity"] = request.Quantity,
            ["valueCents"] = request.ValueCents,
            ["orderNumber"] = request.OrderNumber,
            ["deviceType"] = request.DeviceType,
            ["browser"] = request.Browser,
            ["operatingSystem"] = request.OperatingSystem,
            ["timezone"] = request.TimeZone,
            ["language"] = request.Language,
            ["screenWidth"] = request.ScreenWidth,
            ["screenHeight"] = request.ScreenHeight,
            ["viewportWidth"] = request.ViewportWidth,
            ["viewportHeight"] = request.ViewportHeight,
            ["scrollPercent"] = request.ScrollPercent,
            ["dwellMilliseconds"] = request.DwellMilliseconds,
            ["engagedMilliseconds"] = request.EngagedMilliseconds,
            ["isBounceCandidate"] = request.IsBounceCandidate,
            ["isExitPage"] = request.IsExitPage,
            ["webDriver"] = request.WebDriver,
            ["isHeadless"] = request.IsHeadless,
            ["mouseMoveCount"] = request.MouseMoveCount,
            ["humanInteractionCount"] = request.HumanInteractionCount,
            ["visibilityChangeCount"] = request.VisibilityChangeCount,
            ["trackingVersion"] = request.TrackingVersion,
            ["utm_source"] = Read(sourceQuery, "utm_source"),
            ["utm_medium"] = Read(sourceQuery, "utm_medium"),
            ["utm_campaign"] = Read(sourceQuery, "utm_campaign"),
            ["utm_content"] = Read(sourceQuery, "utm_content"),
            ["utm_term"] = Read(sourceQuery, "utm_term"),
            ["utm_id"] = Read(sourceQuery, "utm_id"),
            ["fbclid"] = Read(sourceQuery, "fbclid"),
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

        if (CommerceEventNames.TryGetValue(clean, out var commerceEventName))
        {
            return commerceEventName;
        }

        return AnalyticsEventCatalog.IsBrowserAllowed(clean)
            ? clean
            : null;
    }

    private static void SetUtmAndMetaFields(object entity, HttpContext httpContext, IReadOnlyDictionary<string, string?> sourceQuery)
    {
        Set(entity, "UtmSource", Read(sourceQuery, "utm_source"));
        Set(entity, "UtmMedium", Read(sourceQuery, "utm_medium"));
        Set(entity, "UtmCampaign", Read(sourceQuery, "utm_campaign"));
        Set(entity, "UtmContent", Read(sourceQuery, "utm_content"));
        Set(entity, "UtmTerm", Read(sourceQuery, "utm_term"));
        Set(entity, "UtmId", Read(sourceQuery, "utm_id"));
        Set(entity, "Fbclid", Read(sourceQuery, "fbclid"));
        Set(entity, "Fbc", httpContext.Request.Cookies["_fbc"]);
        Set(entity, "Fbp", httpContext.Request.Cookies["_fbp"]);
    }

    private static Uri? TryParseAbsoluteUri(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static Dictionary<string, string?> ParseQueryParameters(Uri? sourceUri)
    {
        if (sourceUri is null || string.IsNullOrWhiteSpace(sourceUri.Query))
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        return QueryHelpers.ParseQuery(sourceUri.Query)
            .ToDictionary(
                pair => pair.Key,
                pair => Clean(pair.Value.ToString()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolvePageKey(string? sourcePath, string? explicitPageKey, string? productSlug)
    {
        var pageKey = Clean(explicitPageKey);
        if (pageKey is not null)
        {
            return pageKey;
        }

        var path = (sourcePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return "parfait_home";
        }

        var normalizedPath = path.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return "parfait_home";
        }

        if (string.Equals(normalizedPath, "/store", StringComparison.OrdinalIgnoreCase))
        {
            return "parfait_store_home";
        }

        if (string.Equals(normalizedPath, "/store/cart", StringComparison.OrdinalIgnoreCase))
        {
            return "parfait_cart";
        }

        if (string.Equals(normalizedPath, "/store/checkout", StringComparison.OrdinalIgnoreCase))
        {
            return "parfait_checkout";
        }

        if (string.Equals(normalizedPath, "/store/success", StringComparison.OrdinalIgnoreCase))
        {
            return "parfait_checkout_success";
        }

        if (normalizedPath.StartsWith("/store/product/", StringComparison.OrdinalIgnoreCase))
        {
            var slug = Clean(productSlug) ?? normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return $"parfait_product_{NormalizeKey(slug)}";
        }

        return $"parfait_{NormalizeKey(normalizedPath.Trim('/').Replace('/', '_'))}";
    }

    private bool IsInternalTrafficSource(HttpContext httpContext, string? sourceHost, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath) &&
            sourcePath.StartsWith("/internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsCanonicalPublicHost(sourceHost) || IsCanonicalPublicHost(httpContext.Request.Host.Host))
            return false;

        return IsKnownNonProductionHost(sourceHost) || IsKnownNonProductionHost(httpContext.Request.Host.Host);
    }

    private string ResolveEnvironment(HttpContext httpContext, string? sourceHost, bool isInternalTraffic)
    {
        if (IsCanonicalPublicHost(sourceHost) || IsCanonicalPublicHost(httpContext.Request.Host.Host))
            return "production";

        if (isInternalTraffic)
            return "development";

        var current = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(current))
        {
            var normalized = current.Trim();
            if (normalized.StartsWith("prod", StringComparison.OrdinalIgnoreCase)) return "production";
            if (normalized.StartsWith("dev", StringComparison.OrdinalIgnoreCase)) return "development";
            if (normalized.StartsWith("stag", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("preview", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase))
            {
                return "development";
            }

            return normalized;
        }

        if (IsKnownNonProductionHost(sourceHost) || IsKnownNonProductionHost(httpContext.Request.Host.Host))
            return "development";

        return "production";
    }

    private static HashSet<string> BuildPublicHosts(IConfiguration configuration)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shopparfait.com",
            "www.shopparfait.com"
        };

        AddConfiguredHost(hosts, configuration["Store:PublicBaseUrl"]);
        AddConfiguredHost(hosts, configuration["PublicSite:BaseUrl"]);
        return hosts;
    }

    private static void AddConfiguredHost(HashSet<string> hosts, string? configuredValue)
    {
        var normalizedHost = NormalizeHost(configuredValue);
        if (!string.IsNullOrWhiteSpace(normalizedHost))
            hosts.Add(normalizedHost);
    }

    private bool IsCanonicalPublicHost(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return !string.IsNullOrWhiteSpace(normalizedHost) && _publicHosts.Contains(normalizedHost);
    }

    private static bool IsKnownNonProductionHost(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return false;

        return NonProductionHostHints.Any(hint =>
            normalizedHost.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeHost(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null)
            return null;

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.Host.Trim().ToLowerInvariant();

        var slashIndex = cleaned.IndexOf('/');
        var hostOnly = slashIndex >= 0 ? cleaned[..slashIndex] : cleaned;
        return hostOnly.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string NormalizeKey(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return "unknown";
        }

        var chars = cleaned
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var normalized = new string(chars);

        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        var result = normalized.Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static string? ResolveHost(string? value)
    {
        return Clean(TryParseAbsoluteUri(value)?.Host);
    }

    private static string? Read(IReadOnlyDictionary<string, string?> query, string key)
    {
        return query.TryGetValue(key, out var value) ? Clean(value) : null;
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

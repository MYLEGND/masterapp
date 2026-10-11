using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;

namespace Infrastructure.WebsiteRuntime;

/// <summary>Custom domains serve only immutable pages from the verified business publication.</summary>
public sealed class BusinessWebsiteMiddleware(RequestDelegate next, IWebHostEnvironment environment, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context, MasterAppDbContext db, WebsiteDomainService domains)
    {
        // The proof endpoint must reach the binding authority before the active-domain
        // publication gate. This also covers path segments normalized by the host proxy.
        if (context.Request.Path.StartsWithSegments("/.well-known/legend-website", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var host = WebsiteRequestHostResolver.Resolve(context, configuration);
        var bridged = !string.Equals(host, context.Request.Host.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
        var originHost = configuration["WEBSITE_HOSTNAME"];
        if (host == "protect.mylegnd.com" || host == "masterapp-protect.azurewebsites.net" || host == originHost || environment.IsDevelopment() && (host == "localhost" || host == "127.0.0.1"))
        {
            await next(context);
            return;
        }
        var path = context.Request.Path.Value ?? "/";

        var binding = await domains.ResolveAsync(host, context.RequestAborted);
        if (binding is null) { await Unavailable(context, bridged, "binding"); return; }
        var version = await WebsiteContentStore.PublishedBusinessAsync(db, binding.Value, context.RequestAborted);
        if (string.IsNullOrWhiteSpace(version?.CompiledPagesJson)) { await Unavailable(context, bridged, "publication"); return; }
        // The production shared commerce manager is private, not part of the
        // public website compiler. Only an explicitly admitted business/domain
        // cutover may reach it on a customer hostname. Every operation is
        // subsequently re-authorized by the single CommerceCore controller.
        if (path.StartsWith("/commerce/manage/", StringComparison.OrdinalIgnoreCase))
        {
            if (!CommerceSharedHostCutoverGate.IsAdmitted(configuration, binding.Value, host))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Meta's callback carries a signed state instead of a direct
            // website ticket. The action validates that state and its owner.
            var oauthCallback = string.Equals(path,
                "/commerce/manage/analytics/meta-callback", StringComparison.OrdinalIgnoreCase) &&
                HttpMethods.IsGet(context.Request.Method);
            if (!oauthCallback)
            {
                var ticketText = context.Request.Query["ticket"].ToString();
                var protector = context.RequestServices.GetRequiredService<WebsiteEditorTicketProtector>();
                var actor = await WebsiteTicketAuthorization.ResolveAsync(
                    db, protector, configuration, ticketText, context.RequestAborted);
                if (actor?.SiteKey != WebsiteEditorSiteKeys.Business ||
                    actor.CommerceBusinessId != binding.Value)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next(context);
            return;
        }

        // The exact same internal CSS/JS package is linked into the shared host.
        // Keep static assets read-only and available on the admitted hostname.
        if (CommerceSharedHostCutoverGate.IsAdmitted(configuration, binding.Value, host) &&
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            (path is "/css/internal.css" or "/css/parfait-agentportal-analytics.css" or
                     "/js/parfait-agentportal-analytics.js" or "/images/favicon/parfait-logo.png" ||
             path.StartsWith("/lib/bootstrap/", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        // Preview the tenant's original public editorial pages only after the
        // verified domain and immutable published website admission above.
        if (CommerceSharedHostPreviewGate.TryMapPublicPage(
                configuration, binding.Value, host, context.Request.Path,
                context.Request.Method, out var previewPath))
        {
            var originalPath = context.Request.Path;
            context.Items[CommerceSharedHostPreviewGate.OriginalPagePathItem] = originalPath;
            context.Request.Path = previewPath;
            try { await next(context); }
            finally { context.Request.Path = originalPath; }
            return;
        }
        // Production commerce is a distinct, exact-business gate. Existing
        // custom-domain binding and immutable website publication were verified
        // above; all other business hosts remain on the current storefront origin.
        if (CommerceSharedHostCutoverGate.MayRoute(
                configuration, binding.Value, host, context.Request.Path, context.Request.Method))
        {
            if (context.Request.Path.StartsWithSegments("/uploads/parfait-products", StringComparison.OrdinalIgnoreCase))
                context.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = binding.Value;
            await next(context);
            return;
        }

        // Validated custom domain + immutable published website first; only a
        // separately configured, exact-business read-only preview may reach
        // the commerce GET controllers and original Parfait static assets.
        if (CommerceSharedHostPreviewGate.MayRoute(
                configuration, binding.Value, host, context.Request.Path, context.Request.Method))
        {
            if (context.Request.Path.StartsWithSegments("/uploads/parfait-products", StringComparison.OrdinalIgnoreCase))
                context.Items[CommerceSharedHostPreviewGate.PreviewMediaBusinessIdItem] = binding.Value;
            await next(context);
            return;
        }
        // APIs retain their own authenticated/business-scoped authorities. Resolve the host first.
        if (path.StartsWith("/api/website-content/", StringComparison.Ordinal) ||
            path == "/api/website-inquiries/public" ||
            path == "/api/tracking/ingest")
        {
            await next(context);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        context.Response.Headers.CacheControl = "no-store,no-cache,must-revalidate,max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        var publicApiBase = (configuration["WebsiteContentApiBaseUrl"] ?? "https://masterapp-protect.azurewebsites.net").TrimEnd('/');
        var publicApiOrigin = Uri.TryCreate(publicApiBase, UriKind.Absolute, out var apiUri)
            ? apiUri.GetLeftPart(UriPartial.Authority)
            : "https://masterapp-protect.azurewebsites.net";
        context.Response.Headers["Content-Security-Policy"] =
            $"default-src 'self'; script-src 'self' https://connect.facebook.net https://bzrcdn.openai.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https: https://bzr.openai.com; media-src 'self' https:; connect-src 'self' {publicApiOrigin} https://www.facebook.com https://connect.facebook.net https://bzr.openai.com https://bzrcdn.openai.com; frame-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        if (path is "/site.css" or "/public-inquiry-form.css" or "/legend-public-web.js" or "/legend-public-inquiry.js" or "/legend-public-cms.js" or
            "/legend-public-tracking.js" or "/legend-public-meta-signal-intelligence.js" or "/legend-public-openai-measurement.js")
        {
            var asset = Path.Combine(environment.ContentRootPath, "WebsiteCompiler", "dist", path.TrimStart('/'));
            if (!File.Exists(asset)) { await Unavailable(context, bridged, "asset"); return; }
            context.Response.ContentType = path.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.SendFileAsync(asset, context.RequestAborted);
            return;
        }
        if (path == "/favicon.jpg")
        {
            // Project the icon from the same immutable published version as the HTML.
            // Do not cache this selector: a new publication must become visible immediately.
            context.Response.Headers.CacheControl = "no-store,no-cache,must-revalidate,max-age=0";
            using var publishedDocument = JsonDocument.Parse(version.DocumentJson);
            var icon = publishedDocument.RootElement.TryGetProperty("faviconImageDataUrl", out var rawIcon) &&
                rawIcon.ValueKind == JsonValueKind.String ? rawIcon.GetString() : null;
            if (!string.IsNullOrWhiteSpace(icon))
            {
                if (Uri.TryCreate(icon, UriKind.Absolute, out var iconUri) && iconUri.Scheme == Uri.UriSchemeHttps)
                {
                    context.Response.Redirect(icon, permanent: false);
                    return;
                }
                foreach (var mime in new[] { "image/png", "image/jpeg" })
                {
                    var prefix = "data:" + mime + ";base64,";
                    if (!icon.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var encoded = icon[prefix.Length..];
                        if (encoded.Length > 7_000_000) break;
                        var bytes = Convert.FromBase64String(encoded);
                        if (bytes.Length > 5_000_000) break;
                        context.Response.ContentType = mime;
                        if (!HttpMethods.IsHead(context.Request.Method))
                            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                        return;
                    }
                    catch (FormatException) { break; }
                }
            }
            var faviconPath = Path.Combine(environment.ContentRootPath, "WebsiteCompiler", "dist", "favicon.jpg");
            if (!File.Exists(faviconPath)) { await Unavailable(context, bridged, "favicon"); return; }
            context.Response.ContentType = "image/jpeg";
            if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.SendFileAsync(faviconPath, context.RequestAborted);
            return;
        }
        using var compiled = JsonDocument.Parse(version.CompiledPagesJson);
        var pages = compiled.RootElement.GetProperty("pages");
        var origin = "https://" + host;
        if (path == "/robots.txt")
        {
            context.Response.Headers["X-Robots-Tag"] = "index, follow";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("User-agent: *\nAllow: /\nSitemap: " + origin + "/sitemap.xml\n", context.RequestAborted);
            return;
        }
        if (path == "/sitemap.xml")
        {
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            var lastModifiedUtc = version.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var sitemap = new XDocument(new XElement(ns + "urlset", pages.EnumerateObject().Select(page =>
                new XElement(ns + "url",
                    new XElement(ns + "loc", origin + page.Name),
                    new XElement(ns + "lastmod", lastModifiedUtc)))));
            context.Response.Headers["X-Robots-Tag"] = "index, follow";
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(sitemap.ToString(), context.RequestAborted);
            return;
        }
        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0) normalized = "/";
        if (!pages.TryGetProperty(normalized, out var page)) { await Unavailable(context, bridged, "page"); return; }
        var html = page.GetProperty("html").GetString() ?? "";
        html = html.Replace("__LEGEND_CANONICAL_URL__", WebUtility.HtmlEncode(origin + normalized), StringComparison.Ordinal);
        context.Response.Headers["X-Robots-Tag"] = "index, follow";
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static Task Unavailable(HttpContext context, bool bridged, string reason)
    {
        if (bridged)
            context.Response.Headers["X-Legend-Website-Route"] = reason + ":" + context.Request.Path.Value;
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync("Website or page unavailable.", context.RequestAborted);
    }
}

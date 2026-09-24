using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Infrastructure.Data;
using Infrastructure.WebsiteEditing;

namespace ProtectWebsite.Services;

/// <summary>Custom domains serve only immutable pages from the verified business publication.</summary>
public sealed class BusinessWebsiteMiddleware(RequestDelegate next, IWebHostEnvironment environment, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context, MasterAppDbContext db, WebsiteDomainService domains)
    {
        var host = WebsiteRequestHostResolver.Resolve(context, configuration);
        var originHost = configuration["WEBSITE_HOSTNAME"];
        if (host == "protect.mylegnd.com" || host == "masterapp-protect.azurewebsites.net" || host == originHost || environment.IsDevelopment() && (host == "localhost" || host == "127.0.0.1"))
        {
            await next(context);
            return;
        }
        var path = context.Request.Path.Value ?? "/";

        // Domain activation proof must be reachable while the binding is still pending.
        // The controller verifies the exact binding/business pair; all other custom-host
        // traffic still requires active provider evidence and a published website.
        if (string.Equals(path, "/.well-known/legend-website", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var binding = await domains.ResolveAsync(host, context.RequestAborted);
        if (binding is null) { await Unavailable(context); return; }
        var version = await WebsiteContentStore.PublishedBusinessAsync(db, binding.Value, context.RequestAborted);
        if (string.IsNullOrWhiteSpace(version?.CompiledPagesJson)) { await Unavailable(context); return; }
        // APIs retain their own authenticated/business-scoped authorities. Resolve the host first.
        if (path.StartsWith("/api/website-content/", StringComparison.Ordinal) ||
            path == "/api/website-inquiries/public" ||
            path == "/api/tracking/ingest" ||
            path == "/api/analytics/ingest" ||
            path == "/analytics/meta-signal" ||
            path == "/analytics/business-page")
        {
            await next(context);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        context.Response.Headers.CacheControl = "public,max-age=0,must-revalidate";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        var publicApiBase = (configuration["WebsiteContentApiBaseUrl"] ?? "https://masterapp-protect.azurewebsites.net").TrimEnd('/');
        var publicApiOrigin = Uri.TryCreate(publicApiBase, UriKind.Absolute, out var apiUri)
            ? apiUri.GetLeftPart(UriPartial.Authority)
            : "https://masterapp-protect.azurewebsites.net";
        context.Response.Headers["Content-Security-Policy"] =
            $"default-src 'self'; script-src 'self' https://connect.facebook.net; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; media-src 'self' https:; connect-src 'self' {publicApiOrigin} https://www.facebook.com https://connect.facebook.net; frame-src 'none'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        if (path is "/site.css" or "/legend-public-web.js" or "/business-inquiry.js" or "/legend-public-cms.js" or
            "/legend-public-tracking.js" or "/legend-public-meta-signal-intelligence.js")
        {
            var asset = Path.Combine(environment.ContentRootPath, "WebsiteCompiler", "dist", path.TrimStart('/'));
            if (!File.Exists(asset)) { await Unavailable(context); return; }
            context.Response.ContentType = path.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
            if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.SendFileAsync(asset, context.RequestAborted);
            return;
        }
        using var compiled = JsonDocument.Parse(version.CompiledPagesJson);
        var pages = compiled.RootElement.GetProperty("pages");
        var origin = "https://" + host;
        if (path == "/robots.txt")
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("User-agent: *\nAllow: /\nSitemap: " + origin + "/sitemap.xml\n", context.RequestAborted);
            return;
        }
        if (path == "/sitemap.xml")
        {
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            var sitemap = new XDocument(new XElement(ns + "urlset", pages.EnumerateObject().Select(page => new XElement(ns + "url", new XElement(ns + "loc", origin + page.Name)))));
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(sitemap.ToString(), context.RequestAborted);
            return;
        }
        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0) normalized = "/";
        if (!pages.TryGetProperty(normalized, out var page)) { await Unavailable(context); return; }
        var html = page.GetProperty("html").GetString() ?? "";
        html = html.Replace("__LEGEND_CANONICAL_URL__", WebUtility.HtmlEncode(origin + normalized), StringComparison.Ordinal);
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static Task Unavailable(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync("Website or page unavailable.", context.RequestAborted);
    }
}

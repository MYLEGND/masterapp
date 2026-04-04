using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProtectWebsite.Services.Tracking;

public sealed class SlugRoutingMiddleware : IMiddleware
{
    private readonly AgentTrackingResolver _resolver;
    private readonly ILogger<SlugRoutingMiddleware> _logger;
    private readonly string _founderUpn;

    public SlugRoutingMiddleware(AgentTrackingResolver resolver, ILogger<SlugRoutingMiddleware> logger, IConfiguration config)
    {
        _resolver = resolver;
        _logger = logger;
        _founderUpn = config["Founder:Upn"] ?? "zac.owen@mylegnd.com";
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Only treat "/a" and "/a/{slug}" as tracked-slug routes.
        // This avoids hijacking unrelated endpoints like "/api/...".
        var isAgentSlugRoute =
            path.Equals("/a", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/a/", StringComparison.OrdinalIgnoreCase);

        if (!isAgentSlugRoute)
        {
            // Default domain behavior: founder owns non-agent pages.
            // Skip API/static requests to avoid unnecessary DB lookups.
            if (!ShouldSkipFounderContext(path))
            {
                await AttachFounderContextAsync(context);
            }
            await next(context);
            return;
        }

        // Expect /a/{slug}/optional/parts
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing agent slug.");
            return;
        }

        var slug = segments[1];
        var remainder = segments.Length > 2 ? "/" + string.Join('/', segments.Skip(2)) : "/";

        ResolveResult result;
        try
        {
            result = await _resolver.ResolveBySlugAsync(slug, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlugRouting: slug resolve failed for {Slug}; continuing without profile attribution.", slug);
            context.Items["TrackingSlug"] = slug;
            context.Items["IsFounderPath"] = false;
            context.Request.Path = remainder;
            await next(context);
            return;
        }

        if (!result.Found || result.Profile == null)
        {
            _logger.LogInformation("SlugRouting: unknown slug {Slug} -> 404", slug);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("The requested link is not valid.");
            return;
        }

        // Founder canonical path is always root-domain routes (no "/a/{slug}/...").
        // Preserve attribution through founder context on the target route instead.
        if (string.Equals(result.Profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase))
        {
            var founderCanonicalUri = new UriBuilder(context.Request.GetDisplayUrl())
            {
                Path = remainder
            };
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers.Location = founderCanonicalUri.Uri.ToString();
            return;
        }

        // redirect aliases
        if (!result.IsCanonical && !string.IsNullOrWhiteSpace(result.CanonicalSlug))
        {
            var uri = new UriBuilder(context.Request.GetDisplayUrl())
            {
                Path = $"/a/{result.CanonicalSlug}{remainder}"
            };
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers.Location = uri.Uri.ToString();
            return;
        }

        // Attach context and rewrite to underlying content path
        context.Items["TrackingProfile"] = result.Profile;
        context.Items["IsFounderPath"] = string.Equals(result.Profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase);
        context.Items["TrackingSlug"] = result.CanonicalSlug ?? slug;

        context.Request.Path = remainder;
        await next(context);
    }

    private async Task AttachFounderContextAsync(HttpContext context)
    {
        try
        {
            var founderProfile = await _resolver.ResolveByUpnAsync(_founderUpn, context.RequestAborted);
            if (founderProfile.Found && founderProfile.Profile != null)
            {
                context.Items["TrackingProfile"] = founderProfile.Profile;
                context.Items["TrackingSlug"] = founderProfile.CanonicalSlug ?? founderProfile.Profile.Slug;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlugRouting: founder profile resolve failed; continuing without tracking profile.");
        }

        context.Items["IsFounderPath"] = true;
    }

    private static bool ShouldSkipFounderContext(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return false;

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Most static assets include an extension.
        if (Path.HasExtension(path))
            return true;

        return false;
    }
}

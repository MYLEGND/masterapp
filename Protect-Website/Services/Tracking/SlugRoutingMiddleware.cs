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

        // Root => founder context
        if (path == "/" || string.IsNullOrWhiteSpace(path))
        {
            var founderProfile = await _resolver.ResolveByUpnAsync(_founderUpn, context.RequestAborted);
            if (founderProfile.Found && founderProfile.Profile != null)
            {
                context.Items["TrackingProfile"] = founderProfile.Profile;
                context.Items["TrackingSlug"] = founderProfile.CanonicalSlug ?? founderProfile.Profile.Slug;
            }
            context.Items["IsFounderPath"] = true;
            await next(context);
            return;
        }

        if (!path.StartsWith("/a", StringComparison.OrdinalIgnoreCase))
        {
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

        var result = await _resolver.ResolveBySlugAsync(slug, context.RequestAborted);
        if (!result.Found || result.Profile == null)
        {
            _logger.LogInformation("SlugRouting: unknown slug {Slug} -> 404", slug);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("The requested link is not valid.");
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
}

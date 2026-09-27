using Infrastructure.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProtectWebsite.Services.Meta;

namespace ProtectWebsite.Services.Tracking;

/// <summary>
/// Pushes tracking context into ViewData for layout injection.
/// </summary>
public sealed class TrackingViewDataFilter : IAsyncActionFilter
{
    private readonly IHttpContextAccessor _http;
    private readonly IMetaPixelResolutionService _metaPixelResolution;
    private readonly IOpenAiAdsAccountConnectionAuthority _openAiConnections;

    public TrackingViewDataFilter(
        IHttpContextAccessor http,
        IMetaPixelResolutionService metaPixelResolution,
        IOpenAiAdsAccountConnectionAuthority openAiConnections)
    {
        _http = http;
        _metaPixelResolution = metaPixelResolution;
        _openAiConnections = openAiConnections;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is Controller controller)
        {
            var httpContext = _http.HttpContext ?? context.HttpContext;
            var profile = httpContext.Items["TrackingProfile"] as Domain.Entities.AgentTrackingProfile;
            var isFounder = httpContext.Items.ContainsKey("IsFounderPath") && (httpContext.Items["IsFounderPath"] as bool? == true);
            var slug = httpContext.Items["TrackingSlug"] as string;
            var metaPixelContext = await _metaPixelResolution.ResolveForCurrentRequestAsync(
                httpContext,
                context.HttpContext.RequestAborted);

            var resolvedAgentContext =
                metaPixelContext.AgentTrackingProfileId.HasValue ||
                !string.IsNullOrWhiteSpace(metaPixelContext.AgentSlug);

            controller.ViewData["TrackingProfileId"] = metaPixelContext.AgentTrackingProfileId ?? profile?.Id;
            controller.ViewData["TrackingSlug"] = !string.IsNullOrWhiteSpace(metaPixelContext.AgentSlug)
                ? metaPixelContext.AgentSlug
                : slug;
            controller.ViewData["IsFounderPath"] = resolvedAgentContext ? false : isFounder;
            controller.ViewData["ResolvedMetaPixelId"] = metaPixelContext.PixelId;
            controller.ViewData["MetaPixelOwnerType"] = metaPixelContext.PixelOwnerType;

            var openAiOwner = metaPixelContext.AgentTrackingProfileId.HasValue
                ? Shared.Analytics.MarketingOwnerScope.Agent(metaPixelContext.AgentTrackingProfileId.Value)
                : Shared.Analytics.MarketingOwnerScope.Founder;
            var openAi = await _openAiConnections.GetAsync(openAiOwner, context.HttpContext.RequestAborted);
            controller.ViewData["ResolvedOpenAiPixelId"] = openAi.Connected && openAi.PixelConfigured ? openAi.PixelId : null;
        }

        await next();
    }
}

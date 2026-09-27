using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProtectWebsite.Services.Tracking;

/// <summary>Tracking ownership is resolved before either optional advertising destination.</summary>
public sealed class TrackingViewDataFilter(IHttpContextAccessor http, AgentTrackingResolver profiles,
    IConfiguration configuration, MasterAppDbContext db, MarketingBrowserConfigurationService marketing) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is Controller controller)
        {
            var request = http.HttpContext ?? context.HttpContext;
            var resolved = await ProtectWebsiteOwnerResolver.ResolveAsync(request, profiles, configuration["Founder:Upn"], ct: request.RequestAborted);
            var owner = resolved is null ? null : await CanonicalAdvertisingEventProjection.ResolveOwnerAsync(db, configuration, resolved.Profile, request.RequestAborted);
            var browser = await marketing.GetAsync(owner, request.RequestAborted);
            controller.ViewData["TrackingProfileId"] = resolved?.Profile.Id;
            controller.ViewData["TrackingSlug"] = resolved?.Slug;
            controller.ViewData["IsFounderPath"] = resolved?.IsFounder == true;
            controller.ViewData["ResolvedMetaPixelId"] = browser.MetaPixelId;
            controller.ViewData["MetaPixelOwnerType"] = browser.MetaPixelOwnerType;
            controller.ViewData["ResolvedOpenAiPixelId"] = browser.OpenAiPixelId;
        }
        await next();
    }
}

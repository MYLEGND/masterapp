using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProtectWebsite.Services.Tracking;

/// <summary>
/// Pushes tracking context into ViewData for layout injection.
/// </summary>
public sealed class TrackingViewDataFilter : IAsyncActionFilter
{
    private readonly IHttpContextAccessor _http;

    public TrackingViewDataFilter(IHttpContextAccessor http)
    {
        _http = http;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is Controller controller)
        {
            var httpContext = _http.HttpContext ?? context.HttpContext;
            var profile = httpContext.Items["TrackingProfile"] as Domain.Entities.AgentTrackingProfile;
            var isFounder = httpContext.Items.ContainsKey("IsFounderPath") && (httpContext.Items["IsFounderPath"] as bool? == true);
            var slug = httpContext.Items["TrackingSlug"] as string;

            controller.ViewData["TrackingProfileId"] = profile?.Id;
            controller.ViewData["TrackingSlug"] = slug;
            controller.ViewData["IsFounderPath"] = isFounder;
        }

        await next();
    }
}

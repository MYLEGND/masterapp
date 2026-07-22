using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ClientApp.Infrastructure;

public sealed class ClientSubscriptionAuthorizeFilter : IAsyncAuthorizationFilter
{
    private readonly IAuthorizationService _authorizationService;

    public ClientSubscriptionAuthorizeFilter(IAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.Filters.OfType<IAllowAnonymousFilter>().Any())
            return;

        if (!(context.HttpContext.User.Identity?.IsAuthenticated ?? false))
            return;

        var endpoint = context.HttpContext.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<BypassClientSubscriptionRequirementAttribute>() is not null)
            return;

        var authorized = await _authorizationService.AuthorizeAsync(
            context.HttpContext.User,
            null,
            ClientAppAuthorizationPolicies.ClientSubscriptionActive);

        if (authorized.Succeeded)
            return;

        if (IsApiRequest(context.HttpContext.Request))
        {
            context.Result = new ForbidResult();
            return;
        }

        var returnUrl = $"{context.HttpContext.Request.Path}{context.HttpContext.Request.QueryString}";
        context.Result = new RedirectToActionResult("Index", "Subscription", new { returnUrl });
    }

    private static bool IsApiRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }
}

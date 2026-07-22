using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using ClientApp.Services;

namespace ClientApp.Infrastructure;

public sealed class ClientSubscriptionAuthorizeFilter : IAsyncAuthorizationFilter
{
    private readonly IAuthorizationService _authorizationService;
    private readonly ClientAppReturnUrlNormalizer _returnUrlNormalizer;
    public ClientSubscriptionAuthorizeFilter(
        IAuthorizationService authorizationService,
        ClientAppReturnUrlNormalizer returnUrlNormalizer)
    {
        _authorizationService = authorizationService;
        _returnUrlNormalizer = returnUrlNormalizer;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        if (context.Filters.OfType<IAllowAnonymousFilter>().Any() ||
            endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return;

        if (!(context.HttpContext.User.Identity?.IsAuthenticated ?? false))
            return;

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

        var returnUrl = _returnUrlNormalizer.Normalize(
            $"{context.HttpContext.Request.Path}{context.HttpContext.Request.QueryString}");
        context.Result = new RedirectToActionResult("Index", "Subscription", new { returnUrl });
    }

    private static bool IsApiRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }
}

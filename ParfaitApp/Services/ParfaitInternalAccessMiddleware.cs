using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ParfaitApp.Security;

namespace ParfaitApp.Services;

public sealed class ParfaitInternalAccessMiddleware
{
    private readonly RequestDelegate _next;

    public ParfaitInternalAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IParfaitTeamAccessService teamAccess)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/internal", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (path.Equals("/internal", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/internal/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/internal/logout", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/internal/denied", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var pageAccess = endpoint?.Metadata.GetMetadata<ParfaitInternalPageAccessAttribute>();
        var result = await teamAccess.AuthorizePageAsync(
            context.User,
            path,
            pageAccess?.PageKey,
            context.RequestAborted);
        if (result.Allowed)
        {
            await _next(context);
            return;
        }

        await context.ForbidAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

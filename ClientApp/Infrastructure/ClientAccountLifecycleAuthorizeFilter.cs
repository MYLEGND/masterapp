using ClientApp.Services;
using Domain.Accounts;
using Domain.Messaging;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Shared.Auth;

namespace ClientApp.Infrastructure;

/// <summary>
/// Enforces the server-owned pause and closure state before the normal client
/// workspace can run. A paused client may reach only the profile route, which
/// contains the resume control. Agent view-as-client sessions are excluded:
/// they are an agent authority, not a second client session.
/// </summary>
public sealed class ClientAccountLifecycleAuthorizeFilter : IAsyncAuthorizationFilter
{
    private readonly EffectiveClientContextService _clientContext;
    private readonly IAccountLifecycleService _lifecycle;

    public ClientAccountLifecycleAuthorizeFilter(
        EffectiveClientContextService clientContext,
        IAccountLifecycleService lifecycle)
    {
        _clientContext = clientContext;
        _lifecycle = lifecycle;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        if (context.Filters.OfType<IAllowAnonymousFilter>().Any() ||
            endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
            !(context.HttpContext.User.Identity?.IsAuthenticated ?? false))
        {
            return;
        }

        var client = await _clientContext.ResolveAsync(
            context.HttpContext.User,
            context.HttpContext.Request.Cookies,
            allowRelink: false);
        if (client is null || client.IsAgentView)
            return;

        var userId = context.HttpContext.User.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return;

        var lifecycle = await _lifecycle.GetAsync(
            new AccountLifecycleSubject(
                userId,
                MessagingParticipantTypes.Client,
                client.ClientProfileId),
            context.HttpContext.RequestAborted);
        if (lifecycle.AllowsFullAccess || IsProfileRoute(context))
            return;

        if (IsApiRequest(context.HttpContext.Request))
        {
            context.Result = new ForbidResult();
            return;
        }

        context.Result = new RedirectToActionResult("MyProfile", "Profile", null);
    }

    private static bool IsProfileRoute(AuthorizationFilterContext context) =>
        string.Equals(
            context.RouteData.Values["controller"]?.ToString(),
            "Profile",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
}

using AgentPortal.Services;
using Domain.Accounts;
using Domain.Messaging;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Shared.Auth;

namespace AgentPortal.Security;

/// <summary>
/// Applies the same account lifecycle authority to the agent web workspace.
/// A paused agent can reach only their profile-management page, which contains
/// the resume action; browser routes cannot bypass the mobile lifecycle gate.
/// </summary>
public sealed class AgentAccountLifecycleAuthorizeFilter : IAsyncAuthorizationFilter
{
    private readonly AgentProfileAccessResolver _profiles;
    private readonly IAccountLifecycleService _lifecycle;

    public AgentAccountLifecycleAuthorizeFilter(
        AgentProfileAccessResolver profiles,
        IAccountLifecycleService lifecycle)
    {
        _profiles = profiles;
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

        var profile = await _profiles.ResolveCurrentAsync(
            context.HttpContext.User,
            requireActive: false,
            context.HttpContext.RequestAborted);
        var userId = context.HttpContext.User.GetCanonicalUserId();
        if (profile is null || string.IsNullOrWhiteSpace(userId))
            return;

        var lifecycle = await _lifecycle.GetAsync(
            new AccountLifecycleSubject(userId, MessagingParticipantTypes.Agent, profile.Id),
            context.HttpContext.RequestAborted);
        if (lifecycle.AllowsFullAccess || IsAccountProfileRoute(context))
            return;

        if (context.HttpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.HttpContext.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ForbidResult();
            return;
        }

        context.Result = new RedirectToActionResult("ManageProfile", "Account", null);
    }

    private static bool IsAccountProfileRoute(AuthorizationFilterContext context)
    {
        if (!string.Equals(context.RouteData.Values["controller"]?.ToString(), "Account", StringComparison.OrdinalIgnoreCase))
            return false;

        var action = context.RouteData.Values["action"]?.ToString();
        return string.Equals(action, "ManageProfile", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(action, "AccountAccess", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(action, "Logout", StringComparison.OrdinalIgnoreCase);
    }
}

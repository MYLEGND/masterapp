using AgentPortal.Mobile;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Infrastructure.Mobile;
using Shared.Messaging;

namespace AgentPortal.Services;

public sealed class AgentPortalMessagingActorContextResolver : IMessagingActorContextResolver
{
    private readonly EffectiveAgentContext _agentContext;
    private readonly IMobileActorResolver _mobileActorResolver;

    public AgentPortalMessagingActorContextResolver(
        EffectiveAgentContext agentContext,
        IMobileActorResolver mobileActorResolver)
    {
        _agentContext = agentContext;
        _mobileActorResolver = mobileActorResolver;
    }

    public async Task<(string UserId, string ParticipantType)?> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.Items.TryGetValue("IsAssistant", out var value) && value is true)
            return null;

        // A JWT identity's AuthenticationType is not its ASP.NET handler name.
        // Use the validated ticket, then the same mobile scope and typed-profile
        // authority used by REST. Never reinterpret a rejected mobile Client as
        // the Agent profile sharing its Entra object ID.
        var mobile = await httpContext.AuthenticateAsync(MobileApiAuthorization.BearerScheme);
        if (mobile.Succeeded && mobile.Principal is { } principal)
        {
            var authorization = httpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            if (!(await authorization.AuthorizeAsync(principal, httpContext, MobileApiAuthorization.PolicyName)).Succeeded)
                return null;
            return await ResolveMobileActorAsync(httpContext, principal, cancellationToken);
        }
        if (mobile.Failure != null || httpContext.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var cookie = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!cookie.Succeeded) return null;

        var userId = _agentContext.EffectiveAgentOid?.Trim();
        return string.IsNullOrWhiteSpace(userId) ? null : (userId, "Agent");
    }

    private async Task<(string UserId, string ParticipantType)?> ResolveMobileActorAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var participantType = httpContext.Request
            .Headers[MobileApiAuthorization.ParticipantTypeHeader]
            .FirstOrDefault();
        var resolution = await _mobileActorResolver.ResolveAsync(
            principal,
            participantType,
            cancellationToken);
        if (!resolution.Succeeded ||
            resolution.RequiresParticipantSelection ||
            resolution.SelectedActor is null)
        {
            return null;
        }

        var actor = resolution.SelectedActor.Actor;
        return (actor.UserId, actor.ParticipantType);
    }
}

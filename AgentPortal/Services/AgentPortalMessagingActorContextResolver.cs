using AgentPortal.Mobile;
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

    public Task<(string UserId, string ParticipantType)?> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.Items.TryGetValue("IsAssistant", out var value) && value is true)
            return Task.FromResult<(string UserId, string ParticipantType)?>(null);

        if (httpContext.User.Identities.Any(identity =>
                string.Equals(
                    identity.AuthenticationType,
                    MobileApiAuthorization.BearerScheme,
                    StringComparison.Ordinal)))
        {
            return ResolveMobileActorAsync(httpContext, cancellationToken);
        }

        var userId = _agentContext.EffectiveAgentOid?.Trim();
        return Task.FromResult<(string UserId, string ParticipantType)?>(
            string.IsNullOrWhiteSpace(userId) ? null : (userId, "Agent"));
    }

    private async Task<(string UserId, string ParticipantType)?> ResolveMobileActorAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var participantType = httpContext.Request
            .Headers[MobileApiAuthorization.ParticipantTypeHeader]
            .FirstOrDefault();
        var resolution = await _mobileActorResolver.ResolveAsync(
            httpContext.User,
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

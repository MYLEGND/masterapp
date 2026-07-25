using Shared.Messaging;

namespace AgentPortal.Services;

public sealed class AgentPortalMessagingActorContextResolver : IMessagingActorContextResolver
{
    private readonly EffectiveAgentContext _agentContext;

    public AgentPortalMessagingActorContextResolver(EffectiveAgentContext agentContext)
    {
        _agentContext = agentContext;
    }

    public Task<(string UserId, string ParticipantType)?> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.Items.TryGetValue("IsAssistant", out var value) && value is true)
            return Task.FromResult<(string UserId, string ParticipantType)?>(null);

        var userId = _agentContext.EffectiveAgentOid?.Trim();
        return Task.FromResult<(string UserId, string ParticipantType)?>(
            string.IsNullOrWhiteSpace(userId) ? null : (userId, "Agent"));
    }
}

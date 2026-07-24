using AgentPortal.Filters;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public sealed class MessagingController : MessagingControllerBase
{
    private readonly EffectiveAgentContext _agentContext;

    public MessagingController(
        EffectiveAgentContext agentContext,
        IMessagingService messagingService,
        IMessageAttachmentStorage attachmentStorage,
        IMessagingRealtimePublisher realtimePublisher)
        : base(messagingService, attachmentStorage, realtimePublisher)
    {
        _agentContext = agentContext;
    }

    protected override Task<MessagingActor?> ResolveMessagingActorAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Items.TryGetValue("IsAssistant", out var value) && value is true)
            return Task.FromResult<MessagingActor?>(null);

        var userId = _agentContext.EffectiveAgentOid?.Trim();
        return Task.FromResult<MessagingActor?>(string.IsNullOrWhiteSpace(userId)
            ? null
            : new MessagingActor(userId, MessagingParticipantTypes.Agent));
    }
}

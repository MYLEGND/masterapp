using ClientApp.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;

namespace ClientApp.Controllers;

[Authorize]
public sealed class MessagingController : MessagingControllerBase
{
    private readonly EffectiveClientContextService _clientContextService;

    public MessagingController(
        EffectiveClientContextService clientContextService,
        IMessagingService messagingService,
        IMessageAttachmentStorage attachmentStorage,
        IMessagingRealtimePublisher realtimePublisher)
        : base(messagingService, attachmentStorage, realtimePublisher)
    {
        _clientContextService = clientContextService;
    }

    protected override async Task<MessagingActor?> ResolveMessagingActorAsync(CancellationToken cancellationToken)
    {
        var context = await _clientContextService.ResolveAsync(User, Request.Cookies, allowRelink: false);
        if (context is null || context.IsAgentView || string.IsNullOrWhiteSpace(context.ClientUserId))
            return null;

        return new MessagingActor(context.ClientUserId, MessagingParticipantTypes.Client);
    }
}

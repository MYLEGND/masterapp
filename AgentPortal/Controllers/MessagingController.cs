using AgentPortal.Filters;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Shared.Messaging;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public sealed class MessagingController : MessagingControllerBase
{
    public MessagingController(
        IMessagingService messagingService,
        IMessageAttachmentStorage attachmentStorage,
        IMessagingRealtimePublisher realtimePublisher,
        IMessagingProfileImageResolver profileImageResolver,
        IMessagingContactKeyProtector contactKeys,
        IMessagingActorContextResolver actorContextResolver)
        : base(messagingService, attachmentStorage, realtimePublisher, profileImageResolver, contactKeys, actorContextResolver)
    {
    }
}

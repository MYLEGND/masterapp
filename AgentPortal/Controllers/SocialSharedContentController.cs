using Domain.Social;
using Infrastructure.Messaging;
using Infrastructure.Social;
using Microsoft.AspNetCore.Authorization;
using Shared.Messaging;

namespace AgentPortal.Controllers;

[Authorize]
public sealed class SocialSharedContentController(
    IMessagingActorContextResolver actors,
    IMessagingProfileImageResolver identities,
    ISocialFeedService social) : SocialSharedContentControllerBase(actors, identities, social)
{
}

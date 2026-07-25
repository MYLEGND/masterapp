using Microsoft.AspNetCore.Http;

namespace Shared.Messaging;

public interface IMessagingActorContextResolver
{
    Task<(string UserId, string ParticipantType)?> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

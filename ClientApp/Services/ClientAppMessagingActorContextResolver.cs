using Shared.Messaging;

namespace ClientApp.Services;

public sealed class ClientAppMessagingActorContextResolver : IMessagingActorContextResolver
{
    private readonly EffectiveClientContextService _clientContextService;

    public ClientAppMessagingActorContextResolver(EffectiveClientContextService clientContextService)
    {
        _clientContextService = clientContextService;
    }

    public async Task<(string UserId, string ParticipantType)?> ResolveAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var context = await _clientContextService.ResolveAsync(
            httpContext.User,
            httpContext.Request.Cookies,
            allowRelink: false);
        if (context is null || context.IsAgentView || string.IsNullOrWhiteSpace(context.ClientUserId))
            return null;

        return (context.ClientUserId, "Client");
    }
}

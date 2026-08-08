using Microsoft.AspNetCore.SignalR;
namespace Shared.Messaging;

// Authentication is deliberately applied by each host when it maps this shared
// hub. ClientApp has cookie authentication only, while AgentPortal also accepts
// its registered mobile bearer scheme. A shared scheme list would make either
// host attempt an authentication handler it does not own.
public sealed class MessagingHub : Hub
{
    private readonly IMessagingActorContextResolver _actorContextResolver;

    public MessagingHub(IMessagingActorContextResolver actorContextResolver)
    {
        _actorContextResolver = actorContextResolver;
    }

    public static string GroupName(string userId, string participantType) =>
        $"messaging:{participantType.Trim().ToLowerInvariant()}:{userId.Trim().ToLowerInvariant()}";

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            Context.Abort();
            return;
        }

        var actor = await _actorContextResolver.ResolveAsync(httpContext, Context.ConnectionAborted);
        if (actor is null ||
            string.IsNullOrWhiteSpace(actor.Value.UserId) ||
            string.IsNullOrWhiteSpace(actor.Value.ParticipantType))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(actor.Value.UserId, actor.Value.ParticipantType));
        await base.OnConnectedAsync();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
namespace Shared.Messaging;

[Authorize]
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

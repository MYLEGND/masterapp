using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shared.Auth;

namespace Shared.Messaging;

[Authorize]
public sealed class MessagingHub : Hub
{
    public static string GroupName(string userId) => $"messaging:{userId.Trim().ToLowerInvariant()}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.GetStableUserId();
        if (!string.IsNullOrWhiteSpace(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(userId));

        var effectiveAgentUserId = Context.GetHttpContext()?.Items["EffectiveAgentOid"] as string;
        if (!string.IsNullOrWhiteSpace(effectiveAgentUserId) &&
            !string.Equals(effectiveAgentUserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(effectiveAgentUserId));
        }

        await base.OnConnectedAsync();
    }
}

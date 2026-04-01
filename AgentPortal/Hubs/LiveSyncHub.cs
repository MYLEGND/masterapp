using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AgentPortal.Hubs;

[Authorize]
public class LiveSyncHub : Hub
{
    private string? UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    public override async Task OnConnectedAsync()
    {
        if (!string.IsNullOrWhiteSpace(UserId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserId);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (!string.IsNullOrWhiteSpace(UserId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public Task BroadcastCall(string leadId, int callCount)
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(leadId)) return Task.CompletedTask;
        return Clients.OthersInGroup(UserId).SendAsync("callUpdated", leadId, callCount);
    }

    public Task BroadcastDelete(string leadId)
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(leadId)) return Task.CompletedTask;
        return Clients.OthersInGroup(UserId).SendAsync("leadDeleted", leadId);
    }

    public Task BroadcastPage(string pageKey, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(pageKey)) return Task.CompletedTask;
        return Clients.OthersInGroup(UserId).SendAsync("pageChanged", pageKey, pageNumber);
    }

    public Task BroadcastOrder(string stageKey, IEnumerable<string> orderedIds)
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(stageKey)) return Task.CompletedTask;
        return Clients.OthersInGroup(UserId).SendAsync("orderChanged", stageKey, orderedIds ?? Array.Empty<string>());
    }

    public Task BroadcastUpdate(object payload)
    {
        if (string.IsNullOrWhiteSpace(UserId) || payload == null) return Task.CompletedTask;
        return Clients.OthersInGroup(UserId).SendAsync("leadUpdated", payload);
    }
}

using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shared.Auth;

namespace AgentPortal.Hubs;

[Authorize]
public class LeadBridgeHub : Hub
{
    public static string GroupName(string agentUserId) => $"agent:{agentUserId}";

    private string GetAgentId()
    {
        var http = Context.GetHttpContext();
        if (http?.Items.TryGetValue("EffectiveAgentOid", out var cached) == true
            && cached is string effectiveOid
            && !string.IsNullOrWhiteSpace(effectiveOid))
            return effectiveOid.Trim();

        return (Context.User?.GetStableUserId() ?? string.Empty).Trim();
    }

    public override async Task OnConnectedAsync()
    {
        var agentId = GetAgentId();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(agentId));
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agentId = GetAgentId();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(agentId));
        }
        await base.OnDisconnectedAsync(exception);
    }
}

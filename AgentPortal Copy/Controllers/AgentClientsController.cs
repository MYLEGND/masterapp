using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Filters;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Controllers;

[Authorize] // or [Authorize(Policy="AgentOnly")] if you have it
[AssistantBlock]
public class AgentClientsController : Controller
{
    private readonly MasterAppDbContext _db;

    public AgentClientsController(MasterAppDbContext db)
    {
        _db = db;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private string GetAgentUpn()
    {
        return (User.FindFirst("preferred_username")?.Value
             ?? User.FindFirst("upn")?.Value
             ?? User.Identity?.Name
             ?? "").Trim().ToLowerInvariant();
    }

    // NOTE: route param is clientObjectId (client Entra object id GUID)
    [HttpGet("/agent/clients/{clientObjectId}")]
    public async Task<IActionResult> ViewClient(string clientObjectId)
    {
        var canonicalAgentId = Norm(User.GetStableUserId()); // agent oid
        var clientId = Norm(clientObjectId);                 // client oid

        if (string.IsNullOrWhiteSpace(canonicalAgentId) || string.IsNullOrWhiteSpace(clientId))
            return Forbid();

        // 1) Canonical match
        var link = await _db.AgentClients.FirstOrDefaultAsync(x =>
            (x.AgentUserId ?? "").ToLower() == canonicalAgentId &&
            (x.ClientUserId ?? "").ToLower() == clientId);

        // 2) Legacy candidates match
        if (link == null)
        {
            var candidates = User.GetUserIdCandidates()
                .Select(Norm)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToArray();

            if (candidates.Length > 0)
            {
                link = await _db.AgentClients.FirstOrDefaultAsync(x =>
                    (x.ClientUserId ?? "").ToLower() == clientId &&
                    candidates.Contains((x.AgentUserId ?? "").ToLower()));
            }
        }

        // 3) Relink by AgentUpn if oid changed
        if (link == null)
        {
            var agentUpn = GetAgentUpn();
            if (!string.IsNullOrWhiteSpace(agentUpn))
            {
                link = await _db.AgentClients.FirstOrDefaultAsync(x =>
                    (x.ClientUserId ?? "").ToLower() == clientId &&
                    (x.AgentUpn ?? "").ToLower() == agentUpn);

                if (link != null && Norm(link.AgentUserId) != canonicalAgentId)
                {
                    link.AgentUserId = canonicalAgentId;

                    if (string.IsNullOrWhiteSpace(link.AgentUpn))
                        link.AgentUpn = agentUpn;

                    await _db.SaveChangesAsync();
                }
            }
        }

        if (link == null)
            return Forbid();

        // 4) Backfill AgentUpn
        var upnNow = GetAgentUpn();
        if (!string.IsNullOrWhiteSpace(upnNow) && string.IsNullOrWhiteSpace(link.AgentUpn))
        {
            link.AgentUpn = upnNow;
            await _db.SaveChangesAsync();
        }

        // ✅ Redirect to AgentPortal profile view (agent-side)
        // If your agent profile action is in ClientsController, update this to match.
        return RedirectToAction(
            actionName: "Profile",
            controllerName: "Clients",
            routeValues: new { clientUserId = clientId }
        );
    }
}

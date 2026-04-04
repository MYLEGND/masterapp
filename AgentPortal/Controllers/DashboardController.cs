using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Services;
using AgentPortal.Models;
using Shared.Auth;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
public class DashboardController : Controller
{
    private readonly IExecutionEngine _execution;
    private readonly IBlockerService _blockers;
    private readonly MasterAppDbContext _db;
    private readonly EffectiveAgentContext _agentContext;

    public DashboardController(IExecutionEngine execution, IBlockerService blockers, MasterAppDbContext db, EffectiveAgentContext agentContext)
    {
        _execution = execution;
        _blockers = blockers;
        _db = db;
        _agentContext = agentContext;
    }

    [HttpGet]
    public async Task<IActionResult> Counts()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var today = await _execution.GetTodayAsync(ownerId);
        var overdue = await _execution.GetOverdueAsync(ownerId);
        var blockers = await _blockers.GetOpenByOwnerAsync(ownerId);
        return Json(new { today = today.Count, overdue = overdue.Count, blockers = blockers.Count });
    }

    private string CurrentUserId()
    {
        var effective = _agentContext.EffectiveAgentOid;
        if (!string.IsNullOrWhiteSpace(effective))
            return effective.Trim().ToLowerInvariant();
        return User.GetStableUserId();
    }

    // GET: /Dashboard  (and /Dashboard/Index)
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();

        var vm = new DashboardExecutionViewModel
        {
            Today = await _execution.GetTodayAsync(ownerId),
            Overdue = await _execution.GetOverdueAsync(ownerId),
            Blockers = await _blockers.GetOpenByOwnerAsync(ownerId)
        };

        return View(vm); // renders Views/Dashboard/Index.cshtml
    }

    [HttpGet]
    public async Task<IActionResult> Today()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _execution.GetTodayAsync(ownerId);
        ViewData["RelatedEntityNames"] = await ResolveRelatedEntityNamesAsync(items.Select(x => (x.RelatedEntityType, x.RelatedEntityId)));
        ViewData["ShowActionControls"] = true;
        return PartialView("~/Views/Shared/_ActionList.cshtml", items);
    }

    [HttpGet]
    public async Task<IActionResult> Overdue()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _execution.GetOverdueAsync(ownerId);
        ViewData["RelatedEntityNames"] = await ResolveRelatedEntityNamesAsync(items.Select(x => (x.RelatedEntityType, x.RelatedEntityId)));
        ViewData["ShowActionControls"] = true;
        return PartialView("~/Views/Shared/_ActionList.cshtml", items);
    }

    [HttpGet]
    public async Task<IActionResult> Blockers()
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        var items = await _blockers.GetOpenByOwnerAsync(ownerId);
        ViewData["RelatedEntityNames"] = await ResolveRelatedEntityNamesAsync(items.Select(x => (x.RelatedEntityType, x.RelatedEntityId)));
        return PartialView("~/Views/Shared/_BlockerList.cshtml", items);
    }

    public record CompleteActionRequest(Guid Id);

    [HttpPost]
    [IgnoreAntiforgeryToken] // quick-view actions use authenticated AJAX and can outlive stale anti-forgery tokens
    public async Task<IActionResult> CompleteAction([FromBody] CompleteActionRequest request)
    {
        var ownerId = CurrentUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Challenge();
        if (request == null || request.Id == Guid.Empty) return BadRequest();

        var updated = await _execution.CompleteActionAsync(request.Id, ownerId);
        if (updated == null) return NotFound();

        return Ok(new { success = true });
    }

    // Key format used by the dashboard partials: "{typeInt}:{relatedId-lower}".
    // This lets us enrich action/blocker rows with the originating Lead/Client card name.
    private async Task<Dictionary<string, string>> ResolveRelatedEntityNamesAsync(IEnumerable<(RelatedEntityType Type, string Id)> refs)
    {
        static string BuildKey(RelatedEntityType type, string id)
            => $"{(int)type}:{id.Trim().ToLowerInvariant()}";

        var normalized = refs
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => (x.Type, Id: x.Id.Trim()))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0) return result;

        var leadIdsLower = normalized
            .Where(x => x.Type == RelatedEntityType.Lead)
            .Select(x => x.Id.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (leadIdsLower.Count > 0)
        {
            var leads = await _db.WorkstationLeadProfiles.AsNoTracking()
                .Where(l => l.LeadId != null && leadIdsLower.Contains(l.LeadId.ToLower()))
                .Select(l => new { l.LeadId, l.FirstName, l.LastName, l.Email })
                .ToListAsync();

            foreach (var lead in leads)
            {
                var name = string.Join(" ", new[] { lead.FirstName, lead.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = lead.Email?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[BuildKey(RelatedEntityType.Lead, lead.LeadId)] = name;
            }
        }

        var clientIdsLower = normalized
            .Where(x => x.Type == RelatedEntityType.Client)
            .Select(x => x.Id.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (clientIdsLower.Count > 0)
        {
            var clients = await _db.ClientProfiles.AsNoTracking()
                .Where(c => c.ClientUserId != null && clientIdsLower.Contains(c.ClientUserId.ToLower()))
                .Select(c => new { c.ClientUserId, c.FirstName, c.LastName, c.Email })
                .ToListAsync();

            foreach (var client in clients)
            {
                var name = string.Join(" ", new[] { client.FirstName, client.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = client.Email?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[BuildKey(RelatedEntityType.Client, client.ClientUserId)] = name;
            }
        }

        return result;
    }
}

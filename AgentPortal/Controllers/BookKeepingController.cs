using AgentPortal.Filters;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System.Security.Claims;

namespace AgentPortal.Controllers;

[Authorize]
[AssistantBlock]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public class BookKeepingController : Controller
{
    private readonly MasterAppDbContext _db;

    public BookKeepingController(MasterAppDbContext db)
    {
        _db = db;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private static string ResolveRecordType(ClientProfile client)
    {
        var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
        return ClientCrmMetaSerializer.NormalizeRecordType(meta.RecordType);
    }

    private string GetAgentUpn() =>
        Norm(User.FindFirstValue("preferred_username")
            ?? User.FindFirstValue(ClaimTypes.Upn)
            ?? User.FindFirstValue(ClaimTypes.Email)
            ?? User.Identity?.Name);

    private string[] GetAgentIdCandidates() =>
        User.GetUserIdCandidates()
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

    private string GetAgentOidOrThrow()
    {
        var oid = Norm(
            User.FindFirstValue("oid")
            ?? User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier"));

        if (string.IsNullOrWhiteSpace(oid))
            throw new InvalidOperationException("Missing agent OID claim.");

        return oid;
    }

    private async Task<bool> AgentOwnsBusinessClientAsync(string agentOid, string clientUserId)
    {
        var clientId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientId)) return false;

        var upn = GetAgentUpn();
        var agentIds = GetAgentIdCandidates();

        var owns = await _db.AgentClients.AnyAsync(x =>
            (x.ClientUserId ?? "").ToLower() == clientId &&
            (
                (x.AgentUserId ?? "").ToLower() == agentOid ||
                agentIds.Contains((x.AgentUserId ?? "").ToLower()) ||
                (!string.IsNullOrWhiteSpace(upn) && (x.AgentUpn ?? "").ToLower() == upn)
            ));

        if (!owns) return false;

        var client = await _db.ClientProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == clientId);

        return client != null &&
               string.Equals(ResolveRecordType(client), "BusinessClient", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? clientUserId, string? monthKey = null)
    {
        if (string.IsNullOrWhiteSpace(clientUserId))
        {
            TempData["QuickBooksMigrationNotice"] = "Bookkeeping is now QuickBooks-powered inside each business client workspace.";
            return RedirectToAction("Index", "Clients");
        }

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var normalized = Norm(clientUserId);
        if (!await AgentOwnsBusinessClientAsync(agentOid, normalized))
            return Forbid();

        return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId = normalized, monthKey });
    }

    [HttpGet]
    public async Task<IActionResult> Reports(string? clientUserId)
    {
        if (string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction(nameof(Index));

        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        var normalized = Norm(clientUserId);
        if (!await AgentOwnsBusinessClientAsync(agentOid, normalized))
            return Forbid();

        return RedirectToAction("BookKeeping", "ClientWorkspace", new { clientUserId = normalized });
    }

    // Legacy write routes are intentionally disabled.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult AddEntry(string? clientUserId, string? monthKey = null)
        => Forbid();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteEntry(string? clientUserId, string? monthKey = null)
        => Forbid();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateEntry(string? clientUserId, string? monthKey = null)
        => Forbid();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult AddRecurring(string? clientUserId, string? monthKey = null)
        => Forbid();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateRecurring(string? clientUserId, string? monthKey = null)
        => Forbid();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteRecurring(string? clientUserId, string? monthKey = null)
        => Forbid();
}

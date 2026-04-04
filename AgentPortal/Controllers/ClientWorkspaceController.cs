using AgentPortal.Models;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared.Auth;
using System;
using System.Security.Claims;

namespace AgentPortal.Controllers;

[Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public class ClientWorkspaceController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly IConfiguration _config;

        public ClientWorkspaceController(MasterAppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

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

    // Assistant-aware: for assistants returns parent agent's OID; for agents returns own OID.
    private string GetAgentOidOrThrow()
    {
        if (HttpContext.Items.TryGetValue("EffectiveAgentOid", out var cached)
            && cached is string oid && !string.IsNullOrWhiteSpace(oid))
            return oid;

        var raw = Norm(
            User.FindFirstValue("oid")
            ?? User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
        );

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Missing agent OID claim.");

        return raw;
    }

    private async Task<bool> AgentOwnsClientAsync(string agentOid, string clientUserId)
    {
        return await _db.AgentOwnsClientAsync(
            agentOid,
            clientUserId,
            GetAgentUpn(),
            GetAgentIdCandidates());
    }

    private async Task<Domain.Entities.ClientProfile?> GetClientAsync(string clientUserId)
    {
        clientUserId = Norm(clientUserId);

        return await _db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == clientUserId);
    }

        private static string ResolveRecordType(Domain.Entities.ClientProfile client)
        {
            var meta = ClientCrmMetaSerializer.Deserialize(client.CrmNotes);
            return ClientCrmMetaSerializer.NormalizeRecordType(meta.RecordType);
        }

        private string GetClientPortalBaseUrl()
        {
            return _config["Provisioning:ClientPortalBaseUrl"]?.TrimEnd('/')
                   ?? "https://client.mylegnd.com";
        }

        private async Task<IActionResult> RedirectToClientPortalAsync(Domain.Entities.ClientProfile client, string returnPath)
        {
            if (string.IsNullOrWhiteSpace(returnPath))
                returnPath = "/";

            if (!returnPath.StartsWith("/"))
                returnPath = "/" + returnPath;

            var baseUrl = GetClientPortalBaseUrl();
            var portalUrl = $"{baseUrl}/support/view-as-client/{client.Id}?returnUrl={Uri.EscapeDataString(returnPath)}";
            return Redirect(portalUrl);
        }

    [HttpGet]
    public async Task<IActionResult> Index(string clientUserId)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        clientUserId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("Index", "Clients");

        if (!await AgentOwnsClientAsync(agentOid, clientUserId))
            return Forbid();

        var client = await GetClientAsync(clientUserId);
        if (client == null)
            return NotFound();

        ViewBag.ClientUserId = clientUserId;
        ViewBag.ClientName = $"{client.FirstName} {client.LastName}".Trim();
        ViewBag.ClientRecordType = ResolveRecordType(client);

        return View(client);
    }

    [HttpGet]
        public async Task<IActionResult> Finance(string clientUserId)
        {
            string agentOid;
            try { agentOid = GetAgentOidOrThrow(); }
            catch { return Challenge(); }

        clientUserId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("Index", "Clients");

        if (!await AgentOwnsClientAsync(agentOid, clientUserId))
            return Forbid();

        var client = await GetClientAsync(clientUserId);
        if (client == null) return NotFound();

        return await RedirectToClientPortalAsync(client, "/finance");
        }

    [HttpGet]
        public async Task<IActionResult> Profile(string clientUserId)
        {
            string agentOid;
            try { agentOid = GetAgentOidOrThrow(); }
            catch { return Challenge(); }

        clientUserId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("Index", "Clients");

        if (!await AgentOwnsClientAsync(agentOid, clientUserId))
            return Forbid();

        var client = await GetClientAsync(clientUserId);
        if (client == null) return NotFound();

        // Shared client-facing profile page
        return await RedirectToClientPortalAsync(client, "/profile");
        }

    [HttpGet]
    public async Task<IActionResult> Resources(string clientUserId)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        clientUserId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("Index", "Clients");

        if (!await AgentOwnsClientAsync(agentOid, clientUserId))
            return Forbid();

        return RedirectToAction("Index", "Resources", new { clientUserId });
    }

    [HttpGet]
    public async Task<IActionResult> Training(string clientUserId)
    {
        string agentOid;
        try { agentOid = GetAgentOidOrThrow(); }
        catch { return Challenge(); }

        clientUserId = Norm(clientUserId);
        if (string.IsNullOrWhiteSpace(clientUserId))
            return RedirectToAction("Index", "Clients");

        if (!await AgentOwnsClientAsync(agentOid, clientUserId))
            return Forbid();

        return RedirectToAction("Index", "Training", new { clientUserId });
    }
}

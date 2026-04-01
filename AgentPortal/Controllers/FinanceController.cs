using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System;
using System.Threading.Tasks;
using System.Security.Claims;

namespace AgentPortal.Controllers
{
    [Authorize]
    [AssistantBlock]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
public class FinanceController : Controller
    {
        private readonly MasterAppDbContext _db;

        public FinanceController(MasterAppDbContext db)
        {
            _db = db;
        }

        private static string Norm(string? value)
            => (value ?? string.Empty).Trim().ToLowerInvariant();

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
                User.FindFirstValue("oid") ??
                User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
            );

            if (string.IsNullOrWhiteSpace(oid))
                throw new InvalidOperationException("Missing agent OID claim.");

            return oid;
        }

        private async Task<bool> AgentOwnsClientAsync(string agentOid, string clientUserId)
        {
            var clientUserIdNorm = Norm(clientUserId);
            if (string.IsNullOrWhiteSpace(agentOid) || string.IsNullOrWhiteSpace(clientUserIdNorm))
                return false;

            var upn = GetAgentUpn();
            var agentIds = GetAgentIdCandidates();

            return await _db.AgentClients.AnyAsync(link =>
                (link.ClientUserId ?? string.Empty).ToLower() == clientUserIdNorm &&
                (
                    (link.AgentUserId ?? string.Empty).ToLower() == agentOid ||
                    agentIds.Contains((link.AgentUserId ?? string.Empty).ToLower()) ||
                    (!string.IsNullOrWhiteSpace(upn) && (link.AgentUpn ?? string.Empty).ToLower() == upn)
                ));
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? clientUserId)
        {
            if (!string.IsNullOrWhiteSpace(clientUserId))
                return RedirectToAction("Finance", "ClientWorkspace", new { clientUserId });

            string agentOid;
            try
            {
                agentOid = GetAgentOidOrThrow();
            }
            catch
            {
                return Challenge();
            }

            clientUserId = Norm(clientUserId);

            // Allow /Finance to serve as a generic landing page from the main nav.
            if (string.IsNullOrWhiteSpace(clientUserId))
            {
                ViewData["Title"] = "Finance";
                ViewBag.ClientUserId = "";
                ViewBag.ClientDisplayName = "Finance Workspace";
                ViewBag.ClientProfileId = null;
                return View();
            }

            if (!await AgentOwnsClientAsync(agentOid, clientUserId))
                return Forbid();

            var client = await _db.ClientProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => (x.ClientUserId ?? string.Empty).ToLower() == clientUserId);

            if (client == null)
                return NotFound();

            var firstName = (client.FirstName ?? string.Empty).Trim();
            var lastName = (client.LastName ?? string.Empty).Trim();
            var displayName = $"{firstName} {lastName}".Trim();

            ViewData["Title"] = "Finance";
            ViewBag.ClientUserId = clientUserId;
            ViewBag.ClientDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Client" : displayName;
            ViewBag.ClientProfileId = client.Id;

            return View();
        }
    }
}

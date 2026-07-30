using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using Domain.FinancialIntelligence;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System;
using System.Security.Claims;

namespace AgentPortal.Controllers
{
    [Authorize]
    [AssistantBlock]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public class FinanceController : Controller
    {
        private readonly MasterAppDbContext _db;
        private readonly IFinancialIntelligenceEvaluationService _financialIntelligence;

        public FinanceController(
            MasterAppDbContext db,
            IFinancialIntelligenceEvaluationService financialIntelligence)
        {
            _db = db;
            _financialIntelligence = financialIntelligence;
        }

        private static string Norm(string? value)
            => (value ?? string.Empty).Trim().ToLowerInvariant();

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

        private string GetAgentUpn() => Norm(
            User.FindFirstValue("preferred_username") ??
            User.FindFirstValue(ClaimTypes.Upn) ??
            User.FindFirstValue(ClaimTypes.Email) ??
            User.Identity?.Name);

        private string[] GetAgentIdCandidates() => User.GetUserIdCandidates()
            .Select(Norm)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .ToArray();

        private async Task<Domain.Entities.ClientProfile?> GetAuthorizedClientAsync(
            string clientUserId,
            string agentOid,
            CancellationToken cancellationToken)
        {
            clientUserId = Norm(clientUserId);
            if (string.IsNullOrWhiteSpace(clientUserId) ||
                !await _db.AgentCanAccessClientWorkspaceAsync(
                    agentOid,
                    clientUserId,
                    GetAgentUpn(),
                    GetAgentIdCandidates(),
                    cancellationToken))
            {
                return null;
            }

            return await _db.ClientProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    profile => (profile.ClientUserId ?? string.Empty).ToLower() == clientUserId,
                    cancellationToken);
        }

        private FinancialIntelligenceActor BuildAgentActor(
            Guid clientProfileId,
            string agentOid) => new(
            clientProfileId,
            agentOid,
            FinancialIntelligenceActorTypes.Agent,
            GetAgentUpn(),
            GetAgentIdCandidates());

        [HttpGet]
        public IActionResult Index(string? clientUserId)
        {
            if (!string.IsNullOrWhiteSpace(clientUserId))
                return RedirectToAction("Finance", "ClientWorkspace", new { clientUserId });

            try
            {
                _ = GetAgentOidOrThrow();
            }
            catch
            {
                return Challenge();
            }

            ViewData["Title"] = "Finance";
            ViewBag.ClientUserId = "";
            ViewBag.ClientDisplayName = "Finance Workspace";
            ViewBag.ClientProfileId = null;
            ViewBag.IsBusinessClient = false;
            ViewBag.ClientFirstName = "";
            ViewBag.SpouseFirstName = "";
            ViewBag.HasSpouse = false;
            return View();
        }

        [HttpGet("/Finance/Intelligence")]
        public async Task<IActionResult> Intelligence(string clientUserId, CancellationToken cancellationToken)
        {
            string agentOid;
            try
            {
                agentOid = GetAgentOidOrThrow();
            }
            catch
            {
                return Challenge();
            }

            var client = await GetAuthorizedClientAsync(clientUserId, agentOid, cancellationToken);
            if (client == null)
                return Forbid();

            var snapshot = await _financialIntelligence.GetSnapshotAsync(
                BuildAgentActor(client.Id, agentOid),
                cancellationToken);
            if (snapshot == null)
                return Forbid();

            ViewBag.ClientUserId = client.ClientUserId;
            ViewBag.ClientDisplayName = $"{client.FirstName} {client.LastName}".Trim();
            return View(snapshot);
        }

        [HttpPost("/Finance/Intelligence/Evaluate")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EvaluateIntelligence(
            string clientUserId,
            CancellationToken cancellationToken)
        {
            string agentOid;
            try
            {
                agentOid = GetAgentOidOrThrow();
            }
            catch
            {
                return Challenge();
            }

            var client = await GetAuthorizedClientAsync(clientUserId, agentOid, cancellationToken);
            if (client == null)
                return Forbid();

            var result = await _financialIntelligence.EvaluateAsync(
                BuildAgentActor(client.Id, agentOid),
                cancellationToken);
            TempData[result.Success ? "Success" : "Error"] = result.SanitizedSummary;
            return RedirectToAction(nameof(Intelligence), new { clientUserId = client.ClientUserId });
        }

        [HttpPost("/Finance/Intelligence/Feedback")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IntelligenceFeedback(
            string clientUserId,
            Guid findingId,
            string feedbackType,
            string? reasonCode,
            CancellationToken cancellationToken)
        {
            string agentOid;
            try
            {
                agentOid = GetAgentOidOrThrow();
            }
            catch
            {
                return Challenge();
            }

            var client = await GetAuthorizedClientAsync(clientUserId, agentOid, cancellationToken);
            if (client == null)
                return Forbid();

            var result = await _financialIntelligence.RecordFeedbackAsync(
                BuildAgentActor(client.Id, agentOid),
                new FinancialIntelligenceFeedbackCommand(findingId, feedbackType, reasonCode),
                cancellationToken);
            TempData[result.Success ? "Success" : "Error"] = result.SanitizedSummary;
            return RedirectToAction(nameof(Intelligence), new { clientUserId = client.ClientUserId });
        }
    }
}

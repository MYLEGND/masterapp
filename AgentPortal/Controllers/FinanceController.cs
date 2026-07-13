using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;

namespace AgentPortal.Controllers
{
    [Authorize]
    [AssistantBlock]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public class FinanceController : Controller
    {
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
    }
}

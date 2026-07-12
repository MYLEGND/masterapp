using Microsoft.AspNetCore.Authorization;
using AgentPortal.Filters;
using Microsoft.AspNetCore.Mvc;
using Shared.Finance;
using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;

namespace AgentPortal.Controllers
{
    [Authorize]
    [AssistantBlock]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public class FinanceController : Controller
    {
        private static readonly JsonSerializerOptions CoachingToolJsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IWebHostEnvironment _environment;

        public FinanceController(IWebHostEnvironment environment)
        {
            _environment = environment;
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
            var webRootPath = string.IsNullOrWhiteSpace(_environment.WebRootPath)
                ? Path.Combine(_environment.ContentRootPath, "wwwroot")
                : _environment.WebRootPath;
            ViewBag.CoachingToolsJson = JsonSerializer.Serialize(
                CoachingToolCatalog.Load(Path.Combine(webRootPath, "images", "illustrations")),
                CoachingToolJsonOptions);
            return View();
        }
    }
}

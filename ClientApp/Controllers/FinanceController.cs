using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClientApp.Services;
using System.Text.Json;

namespace ClientApp.Controllers
{
    [Authorize]
    public class FinanceController : Controller
    {
        private readonly EffectiveClientContextService _clientContext;

        public FinanceController(EffectiveClientContextService clientContext)
        {
            _clientContext = clientContext;
        }

        private static bool IsBusinessClient(string? crmNotes)
        {
            if (string.IsNullOrWhiteSpace(crmNotes))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(crmNotes);
                if (doc.RootElement.TryGetProperty("recordType", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var value = (prop.GetString() ?? string.Empty).Trim();
                    return value.Equals("BusinessClient", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("Business Client", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Treat malformed CRM metadata as a regular client.
            }

            return false;
        }

        // /Finance (generic view; won't persist without a clientProfileId)
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null) return Forbid();

            ViewData["Title"] = "Finance";
            ViewBag.ClientProfileId = context.ClientProfileId;
            ViewBag.ClientUserId = context.ClientUserId;
            ViewBag.IsBusinessClient = IsBusinessClient(context.Profile.CrmNotes);
            return View();
        }

        // /Finance/Client/123 (Bookkeeping-style: shared finance for a specific client)
        [HttpGet("/Finance/Client/{clientProfileId:guid}")]
        public async Task<IActionResult> Client(Guid clientProfileId)
        {
            var context = await _clientContext.ResolveAsync(User, Request.Cookies);
            if (context == null || context.ClientProfileId != clientProfileId)
                return Forbid();

            ViewData["Title"] = "Finance";
            ViewBag.ClientProfileId = clientProfileId;
            ViewBag.ClientUserId = context.ClientUserId;
            ViewBag.IsBusinessClient = IsBusinessClient(context.Profile.CrmNotes);
            return View("Index");
        }
    }
}

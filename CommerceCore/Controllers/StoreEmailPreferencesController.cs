using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParfaitApp.Services;

namespace ParfaitApp.Controllers;

[AllowAnonymous]
[Route("store/email-preferences")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StoreEmailPreferencesController(ParfaitCustomerAutomationService automations) : Controller
{
    [HttpGet]
    public IActionResult Index(Guid businessId, Guid leadId, string token)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var model = automations.GetEmailPreferences(businessId, leadId, token);
        return model is null ? NotFound() : View("~/Views/Store/EmailPreferences.cshtml", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Unsubscribe(Guid businessId, Guid leadId, string token)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!automations.Unsubscribe(businessId, leadId, token)) return NotFound();
        return View("~/Views/Store/EmailPreferences.cshtml", automations.GetEmailPreferences(businessId, leadId, token));
    }
}

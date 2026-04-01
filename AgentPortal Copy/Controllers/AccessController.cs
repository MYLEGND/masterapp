using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
public class AccessController : Controller
{
    [HttpGet]
    public IActionResult Limited(string? reason = null, string? returnUrl = null)
    {
        ViewData["Reason"] = string.IsNullOrWhiteSpace(reason) ? "restricted" : reason.Trim().ToLowerInvariant();
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpGet]
    public IActionResult Denied()
    {
        return View();
    }
}
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AgentPortal.Models;
using Shared.Diagnostics;

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
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Denied(string? returnUrl = null)
    {
        var diagnostics = AppFailureDiagnosticsBuilder.BuildForAccessDenied(
            HttpContext,
            "AgentPortal",
            returnUrl,
            summary: "The current signed-in session does not have permission to open this portal route.");

        var model = new ErrorViewModel
        {
            RequestId = diagnostics.RequestId,
            Diagnostics = diagnostics
        };

        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers["X-Legend-Failure-Kind"] = diagnostics.FailureKind;
        return View("~/Views/Shared/Error.cshtml", model);
    }
}

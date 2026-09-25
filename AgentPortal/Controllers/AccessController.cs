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
            Diagnostics = null
        };

        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers.Remove("X-Legend-Failure-Kind");
        Response.Headers.Remove("X-Legend-Failing-Point");
        Response.Headers.Remove("X-Legend-Redirect-Depth");
        Response.Headers["X-Legend-Request-Id"] = diagnostics.RequestId;
        Response.Headers.CacheControl = "no-store";
        ViewData["FounderDiagnosticsUrl"] = AgentPortal.Security.FounderGuard.IsFounder(User)
            ? "/founder/diagnostics" : null;
        if (AppFailureDiagnosticsBuilder.RequestPrefersJson(Request))
            return new ObjectResult(new
            {
                error = "access_denied",
                message = "Your current account does not have permission to perform this action.",
                requestId = diagnostics.RequestId
            }) { StatusCode = StatusCodes.Status403Forbidden };
        return View("~/Views/Shared/Error.cshtml", model);
    }
}

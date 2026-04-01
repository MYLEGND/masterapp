using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[FounderOnly]
[ValidateAntiForgeryToken]
public class ImpersonationController : Controller
{
    private readonly FounderImpersonationService _impersonation;
    private readonly ILogger<ImpersonationController> _logger;

    public ImpersonationController(FounderImpersonationService impersonation, ILogger<ImpersonationController> logger)
    {
        _impersonation = impersonation;
        _logger = logger;
    }

    [HttpPost]
    [Route("impersonation/start")]
    public async Task<IActionResult> Start(string agentId, string? returnUrl = null)
    {
        await _impersonation.StartAsync(HttpContext, User, agentId);
        var dest = string.IsNullOrWhiteSpace(returnUrl) ? "/Home/Index" : returnUrl;
        _logger.LogInformation("Founder started impersonation of {AgentId}", agentId);
        return Redirect(dest);
    }

    [HttpPost]
    [Route("impersonation/stop")]
    public async Task<IActionResult> Stop(string? returnUrl = null)
    {
        await _impersonation.StopAsync(HttpContext, User);
        var dest = string.IsNullOrWhiteSpace(returnUrl) ? "/Home/Index" : returnUrl;
        _logger.LogInformation("Founder stopped impersonation");
        return Redirect(dest);
    }
}

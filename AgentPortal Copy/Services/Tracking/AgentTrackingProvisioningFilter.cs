using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgentPortal.Services.Tracking;

/// <summary>
/// Ensures an AgentTrackingProfile exists for authenticated users (fallback path).
/// Non-intrusive: no-op for unauthenticated requests.
/// </summary>
public class AgentTrackingProvisioningFilter : IAsyncActionFilter
{
    private readonly IAgentTrackingService _service;
    private readonly ILogger<AgentTrackingProvisioningFilter> _logger;

    public AgentTrackingProvisioningFilter(IAgentTrackingService service, ILogger<AgentTrackingProvisioningFilter> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User?.Identity?.IsAuthenticated == true)
        {
            var user = context.HttpContext.User;
            var oid = user.FindFirst("oid")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.Identity?.Name;
            var upn = user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst("preferred_username")?.Value
                ?? user.Identity?.Name
                ?? string.Empty;
            var display = user.FindFirst("name")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? upn;

            if (!string.IsNullOrWhiteSpace(oid))
            {
                try
                {
                    await _service.EnsureProfileAsync(oid, upn ?? string.Empty, display);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to ensure AgentTrackingProfile for user {User}", oid);
                }
            }
        }

        await next();
    }
}

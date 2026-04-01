using AgentPortal.Services;
using AgentPortal.Security;

namespace AgentPortal.Middleware;

/// <summary>
/// Applies founder-only impersonation context if a protected cookie is present.
/// Does NOT change the authenticated identity; only sets EffectiveAgentOid for downstream consumers.
/// </summary>
public class FounderImpersonationMiddleware
{
    private readonly RequestDelegate _next;

    public FounderImpersonationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, FounderImpersonationService service)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true && FounderGuard.IsFounder(user))
        {
            var impersonation = await service.GetAsync(context, user);
            if (impersonation != null)
            {
                context.Items["ImpersonatedAgentOid"] = impersonation.AgentUserId;
                context.Items["ImpersonatedAgentName"] = impersonation.AgentName;
                context.Items["ImpersonatedAgentEmail"] = impersonation.AgentEmail;
                // Effective agent override for downstream consumers.
                context.Items["EffectiveAgentOid"] = impersonation.AgentUserId;
            }
        }

        await _next(context);
    }
}

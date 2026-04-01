using AgentPortal.Services;
using Microsoft.Extensions.Configuration;

namespace AgentPortal.Middleware;

public class AssistantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _tenantId;
    private readonly string _firstPartyDomain;
    private static readonly HashSet<string> AssistantAllowedControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Assistant",
        "Leads",
        "LeadBridge",
        "Workstation",
        "WorkstationNotes",
        "Access",
        "Account",
        "MicrosoftIdentity"
    };

    private static readonly HashSet<string> AssistantAllowedCalendarActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connect",
        "Connected",
        "Status",
        "DayAvailability",
        "CreateEvent"
    };

    private static readonly HashSet<string> AssistantAllowedClientActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddActivity",
        "ClearActivities",
        "BulkUpdate",
        "EnablePortalAccess",
        "Delete",
        "Queue",
        "ImportTemplateCsv"
    };

    private static bool IsAssistantAllowedRoute(string? controller, string? action)
    {
        if (string.IsNullOrWhiteSpace(controller)) return false;

        if (AssistantAllowedControllers.Contains(controller))
            return true;

        if (string.Equals(controller, "Calendar", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(action) && AssistantAllowedCalendarActions.Contains(action);

        if (string.Equals(controller, "Clients", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(action) && AssistantAllowedClientActions.Contains(action);

        return false;
    }

    private static bool WantsHtml(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        var accept = context.Request.Headers.Accept.ToString();
        return string.IsNullOrWhiteSpace(accept)
            || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase);
    }

    public AssistantResolutionMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _tenantId = (config["AzureAd:TenantId"]
                     ?? config["AzureAd__TenantId"]
                     ?? string.Empty).Trim();
        _firstPartyDomain = (config["AzureAd:Domain"]
                             ?? config["AzureAd__Domain"]
                             ?? string.Empty).Trim();
    }

    private static string BuildSafeReturnUrl(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        // Avoid recursive /Access/Limited?returnUrl=... nesting
        if (path.StartsWith("/access/limited", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var raw = $"{path}{context.Request.QueryString}";
        // Guard against IIS / Azure 404.15 (query string too long) from runaway nesting
        const int maxLen = 700;
        return raw.Length <= maxLen ? raw : raw[..maxLen];
    }

    public async Task InvokeAsync(
        HttpContext context,
        AssistantContextService assistantContext,
        AgentRegistryService agentRegistry)
    {
        // Public onboarding links must bypass assistant/guest restrictions so clients can complete forms without login.
        var path = context.Request.Path.Value ?? "";
        var pathLower = path.ToLowerInvariant();
        if (pathLower.StartsWith("/onboarding/start") ||
            pathLower.StartsWith("/onboarding/submitted") ||
            pathLower.StartsWith("/onboardingpublic"))
        {
            await _next(context);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            // Ensure every authenticated user gets an AgentProfile (source of truth) even if they have no leads/clients.
            await agentRegistry.UpsertAgentProfileAsync(context.User);

            // Founder impersonation takes precedence over assistant logic.
            if (context.Items.TryGetValue("ImpersonatedAgentOid", out var impObj) &&
                impObj is string impAgent &&
                !string.IsNullOrWhiteSpace(impAgent))
            {
                context.Items["EffectiveAgentOid"] = impAgent;
                context.Items["IsAssistant"] = false;
                await _next(context);
                return;
            }

            await assistantContext.BindAssistantOidIfNeededAsync(context.User);

            var assistantRecord = await assistantContext.GetAssistantRecordForUserAsync(context.User, activeOnly: false);
            if (assistantRecord != null && !assistantRecord.IsActive)
            {
                if (WantsHtml(context))
                {
                    var returnUrl = BuildSafeReturnUrl(context);
                    var target = $"/Access/Limited?reason=disabled&returnUrl={Uri.EscapeDataString(returnUrl)}";
                    context.Response.Redirect(target);
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Assistant access has been disabled.");
                }
                return;
            }

            var effectiveAgentOid = await assistantContext.ResolveEffectiveAgentOidAsync(context.User);
            var isAssistant = assistantRecord?.IsActive == true;

            if (!isAssistant && AssistantContextService.IsLikelyGuestUser(context.User, _tenantId, _firstPartyDomain))
            {
                if (WantsHtml(context))
                {
                    var returnUrl = BuildSafeReturnUrl(context);
                    var target = $"/Access/Limited?reason=unassigned&returnUrl={Uri.EscapeDataString(returnUrl)}";
                    context.Response.Redirect(target);
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Guest users must be assigned as assistants to access this portal.");
                }
                return;
            }

            context.Items["EffectiveAgentOid"] = effectiveAgentOid;
            context.Items["IsAssistant"] = isAssistant;

            if (isAssistant)
            {
                var controller = context.Request.RouteValues.TryGetValue("controller", out var c)
                    ? c?.ToString()
                    : null;
                var action = context.Request.RouteValues.TryGetValue("action", out var a)
                    ? a?.ToString()
                    : null;

                if (string.Equals(controller, "Home", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect("/Assistant");
                    return;
                }

                if (!IsAssistantAllowedRoute(controller, action))
                {
                    if (WantsHtml(context))
                    {
                        var returnUrl = BuildSafeReturnUrl(context);
                        var target = $"/Access/Limited?reason=restricted&returnUrl={Uri.EscapeDataString(returnUrl)}";
                        context.Response.Redirect(target);
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Assistant access is restricted to Leads and Workstation.");
                    }
                    return;
                }
            }
        }

        await _next(context);
    }
}

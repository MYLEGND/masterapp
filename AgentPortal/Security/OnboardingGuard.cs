using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgentPortal.Security;

/// <summary>
/// Centralized owner check for the onboarding area.
/// Owner identity is resolved from the OWNER_EMAIL environment variable.
/// Must be set in Azure App Service → Configuration → Application Settings.
/// </summary>
public static class OnboardingGuard
{
    // Resolved once at startup. Both casing variants accepted for platform compatibility.
    public static readonly string OwnerEmail =
        (Environment.GetEnvironmentVariable("OWNER_EMAIL")
         ?? Environment.GetEnvironmentVariable("OwnerEmail")
         ?? string.Empty).Trim();

    public static bool IsOwner(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        var candidates = new[]
        {
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue("email"),
            user.FindFirstValue("preferred_username"),
            user.FindFirstValue("upn"),
            user.FindFirstValue("unique_name")
        };

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate?.Trim(), OwnerEmail, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Action filter to enforce owner-only access on controllers/actions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class OnboardingOwnerOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!OnboardingGuard.IsOwner(context.HttpContext.User))
        {
            context.Result = new ForbidResult();
        }
    }
}

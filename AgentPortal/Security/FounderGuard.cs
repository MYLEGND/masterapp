using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgentPortal.Security;

/// <summary>
/// Centralized founder-only check. Mirrors onboarding owner rules but isolated
/// so future founder-only areas stay consistent.
/// </summary>
public static class FounderGuard
{
    // Delegates to OnboardingGuard — single source of truth for the owner email.
    public static string FounderEmail => OnboardingGuard.OwnerEmail;

    // Resolved once at startup. Both casing variants accepted for Azure App Service
    // Application Settings (which uppercases env var names on some platforms).
    private static readonly string FounderOid =
        (Environment.GetEnvironmentVariable("FOUNDER_OID")
         ?? Environment.GetEnvironmentVariable("FounderOid")
         ?? string.Empty).Trim();

    public static bool IsFounder(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        // Primary gate: Azure AD Object ID. Immune to email alias changes and
        // account renames. Requires FOUNDER_OID to be set in environment config.
        if (!string.IsNullOrWhiteSpace(FounderOid))
        {
            var oid =
                user.FindFirstValue("oid") ??
                user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

            if (!string.IsNullOrWhiteSpace(oid) &&
                oid.Equals(FounderOid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Fallback gate: email. Active when FOUNDER_OID is not configured (e.g. local dev
        // without the env var set). Do not remove — keeps local dev workflow functional
        // and provides a recovery path if the OID env var is misconfigured.
        var email =
            user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            user.FindFirstValue("preferred_username") ??
            user.FindFirstValue("upn") ??
            user.Identity?.Name;

        return !string.IsNullOrWhiteSpace(email) &&
               email.Equals(FounderEmail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Throws <see cref="ForbidResult"/> if the current principal is not founder.
    /// Use inside sensitive services as defense-in-depth.
    /// </summary>
    public static void EnsureFounderOrThrow(ClaimsPrincipal? user)
    {
        if (!IsFounder(user))
            throw new ForbidResultException();
    }
}

/// <summary>
/// Authorization filter to enforce founder-only access at the route layer.
/// Keep this lightweight; service-layer checks still run for defense-in-depth.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class FounderOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!FounderGuard.IsFounder(context.HttpContext.User))
        {
            // Explicit forbid avoids redirect loops and makes the intent clear.
            context.Result = new ForbidResult();
        }
    }
}

/// <summary>
/// Exception used to bubble a founder-only failure from deep in a service layer
/// without coupling everything to MVC abstractions.
/// </summary>
public sealed class ForbidResultException : Exception { }

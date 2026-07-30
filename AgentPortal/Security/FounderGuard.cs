using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Shared.Auth;

namespace AgentPortal.Security;

/// <summary>
/// Centralized founder-only check. Mirrors onboarding owner rules but isolated
/// so future founder-only areas stay consistent.
/// </summary>
public static class FounderGuard
{
    // Delegates to OnboardingGuard — single source of truth for the owner email.
    public static string FounderEmail => OnboardingGuard.OwnerEmail;

    // Read at call time (both casing variants accepted for Azure App Service
    // Application Settings). Startup binds Founder:Oid into FOUNDER_OID.
    public static string FounderOid =>
        (Environment.GetEnvironmentVariable("FOUNDER_OID")
         ?? Environment.GetEnvironmentVariable("FounderOid")
         ?? string.Empty).Trim();

    /// <summary>
    /// Founder authority is decided by the shared fail-closed rule
    /// (<see cref="FounderAuthority"/>): canonical Entra Object ID must match a
    /// valid configured FOUNDER_OID. Email is consulted only as a development
    /// convenience when no OID is configured and the environment is not
    /// production; it never grants founder access in production.
    /// </summary>
    public static bool IsFounder(ClaimsPrincipal? user)
        => FounderAuthority.Evaluate(
            user,
            FounderOid,
            FounderAuthority.IsProductionEnvironment(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")),
            MatchesOwnerEmail);

    private static bool MatchesOwnerEmail(ClaimsPrincipal user)
    {
        var email = user.GetEmailCandidate();
        return !string.IsNullOrWhiteSpace(email) &&
               !string.IsNullOrWhiteSpace(OnboardingGuard.OwnerEmail) &&
               string.Equals(email, OnboardingGuard.OwnerEmail, StringComparison.OrdinalIgnoreCase);
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

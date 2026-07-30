using System;
using System.Security.Claims;

namespace Shared.Auth;

/// <summary>
/// Shared, fail-closed founder authority rule used by app-specific founder
/// guards (AgentPortal, ParfaitApp). The guards remain distinct composition
/// boundaries (they resolve their own owner-email configuration), but both
/// delegate the authoritative decision here so the rule cannot drift:
///
///  * The canonical Entra Object ID (oid) is the authoritative founder identity.
///  * A configured FOUNDER_OID must be present AND a valid GUID before founder
///    authority can be granted; the caller's canonical oid must match it.
///  * Email / preferred_username / UPN / display name / NameIdentifier never
///    independently grant founder access.
///  * Missing or malformed FOUNDER_OID fails closed.
///  * In production the email fallback is never consulted. It is available only
///    as an explicit development convenience when NO oid is configured at all.
/// </summary>
public static class FounderAuthority
{
    /// <summary>
    /// Evaluates founder authority.
    /// </summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="configuredFounderOid">Value of FOUNDER_OID from configuration/environment.</param>
    /// <param name="isProduction">True when running in the Production environment.</param>
    /// <param name="developmentEmailFallback">
    /// App-specific development-only email check. Consulted ONLY when no oid is
    /// configured and the environment is not production.
    /// </param>
    public static bool Evaluate(
        ClaimsPrincipal? user,
        string? configuredFounderOid,
        bool isProduction,
        Func<ClaimsPrincipal, bool> developmentEmailFallback)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        var founderOid = Normalize(configuredFounderOid);
        var configured = !string.IsNullOrWhiteSpace(founderOid);
        var valid = configured && Guid.TryParse(founderOid, out _);

        // Authoritative path: canonical Object ID must match a valid configured OID.
        if (valid)
        {
            var oid = user.GetCanonicalUserId(); // oid-only, normalized
            return !string.IsNullOrWhiteSpace(oid) &&
                   string.Equals(oid, founderOid, StringComparison.Ordinal);
        }

        // FOUNDER_OID missing or malformed:
        //  * Production always fails closed.
        //  * A configured-but-malformed value is treated as a misconfiguration
        //    and also fails closed (never silently falls back to email).
        if (isProduction || configured)
            return false;

        // Development-only convenience when no OID is configured at all.
        return developmentEmailFallback is not null && developmentEmailFallback(user);
    }

    /// <summary>
    /// True when FOUNDER_OID is present and a valid GUID. Used by startup guards
    /// to fail closed in production before the first request.
    /// </summary>
    public static bool IsConfiguredAndValid(string? configuredFounderOid)
    {
        var founderOid = Normalize(configuredFounderOid);
        return !string.IsNullOrWhiteSpace(founderOid) && Guid.TryParse(founderOid, out _);
    }

    /// <summary>
    /// Treats an unset environment name as Production for fail-closed safety
    /// (ASP.NET Core also defaults to Production when the variable is unset).
    /// </summary>
    public static bool IsProductionEnvironment(string? aspNetCoreEnvironment)
        => string.IsNullOrWhiteSpace(aspNetCoreEnvironment) ||
           aspNetCoreEnvironment.Equals("Production", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();
}

using System.Security.Claims;

namespace Shared.Auth;

public static class UserIdExtensions
{
    private static string? Claim(ClaimsPrincipal user, string type)
        => user?.FindFirst(type)?.Value;

    private static string Norm(string? v)
        => (v ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Canonical ID we will store in the DB for agents/clients.
    /// For Entra ID this MUST be ObjectId (oid). Everything else is legacy.
    /// </summary>
    public static string GetCanonicalUserId(this ClaimsPrincipal user)
    {
        var oid =
            Claim(user, "oid") ??
            Claim(user, "http://schemas.microsoft.com/identity/claims/objectidentifier");

        return Norm(oid);
    }

    /// <summary>
    /// Legacy candidates that may have been stored historically.
    /// Used ONLY for self-healing migrations when an old link exists.
    /// </summary>
    public static string[] GetUserIdCandidates(this ClaimsPrincipal user)
    {
        var oid = GetCanonicalUserId(user);

        var nameId = Norm(Claim(user, ClaimTypes.NameIdentifier));
        var sub = Norm(Claim(user, "sub"));

        // keep distinct + non-empty
        return new[] { oid, nameId, sub }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Backward compatible name (your code calls this everywhere).
    /// Now returns the canonical ID (oid).
    /// </summary>
    public static string GetStableUserId(this ClaimsPrincipal user)
        => GetCanonicalUserId(user);
}

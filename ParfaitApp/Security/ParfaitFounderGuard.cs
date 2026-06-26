using System.Security.Claims;

namespace ParfaitApp.Security;

public static class ParfaitFounderGuard
{
    public static string FounderEmail =>
        (Environment.GetEnvironmentVariable("OWNER_EMAIL") ?? string.Empty).Trim();

    public static string FounderOid =>
        (Environment.GetEnvironmentVariable("FOUNDER_OID")
         ?? Environment.GetEnvironmentVariable("FounderOid")
         ?? string.Empty).Trim();

    public static bool IsFounder(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return false;

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

        var email =
            user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            user.FindFirstValue("preferred_username") ??
            user.FindFirstValue("upn") ??
            user.Identity?.Name;

        return !string.IsNullOrWhiteSpace(email) &&
               !string.IsNullOrWhiteSpace(FounderEmail) &&
               email.Equals(FounderEmail, StringComparison.OrdinalIgnoreCase);
    }
}

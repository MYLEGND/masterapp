using System.Security.Claims;

namespace ParfaitApp.Security;

public static class ParfaitFounderGuard
{
    public static string FounderEmail => OwnerEmails.FirstOrDefault() ?? string.Empty;

    public static IReadOnlyList<string> OwnerEmails =>
        ResolveOwnerEmails();

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

        return IsOwnerEmail(email);
    }

    public static bool IsOwnerEmail(string? email)
    {
        var normalized = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return OwnerEmails.Any(value => value.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string OwnerEmailSummary()
    {
        return string.Join(", ", OwnerEmails);
    }

    private static IReadOnlyList<string> ResolveOwnerEmails()
    {
        var raw =
            Environment.GetEnvironmentVariable("OWNER_EMAILS")
            ?? Environment.GetEnvironmentVariable("OwnerEmails")
            ?? Environment.GetEnvironmentVariable("OWNER_EMAIL")
            ?? string.Empty;

        var emails = raw
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return emails;
    }
}

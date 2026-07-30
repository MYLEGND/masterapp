using System.Security.Claims;
using Shared.Auth;

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

    /// <summary>
    /// Delegates to the shared fail-closed founder rule
    /// (<see cref="FounderAuthority"/>): canonical Entra Object ID must match a
    /// valid configured FOUNDER_OID. Email is consulted only as a development
    /// convenience when no OID is configured and the environment is not
    /// production; a configured OID that does not match never falls through to
    /// email, and production never grants founder access by email.
    /// </summary>
    public static bool IsFounder(ClaimsPrincipal? user)
        => FounderAuthority.Evaluate(
            user,
            FounderOid,
            FounderAuthority.IsProductionEnvironment(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")),
            u => IsOwnerEmail(u.GetEmailCandidate()));

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

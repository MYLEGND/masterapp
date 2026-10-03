using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Shared.PortalShell;

public sealed record PortalShellIdentity(
    string DisplayName,
    string Initials,
    string RoleLabel,
    string AvatarUrl,
    string StoreUrl);

public interface IPortalShellIdentityResolver
{
    Task<PortalShellIdentity> ResolveAsync(
        ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

public static class PortalShellIdentityFactory
{
    public const string CanonicalStoreUrl = "https://www.mylegnd.com/store";

    public static string? CleanHumanToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Contains('@') || text.Contains('.')) return null;
        if (text.Equals("connect", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return null;

        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: >= 2 } ? first : null;
    }

    public static string BuildDisplayName(
        string fallback,
        string? storedDisplayName,
        string? first,
        string? last,
        string? claimDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(storedDisplayName))
            return storedDisplayName.Trim();

        var combined = string.Join(" ", new[] { first, last }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrWhiteSpace(combined))
            return combined;

        var claim = claimDisplayName?.Trim();
        if (!string.IsNullOrWhiteSpace(claim) && !claim.Contains('@'))
            return claim;

        if (!string.IsNullOrWhiteSpace(claim) && claim.Contains('@'))
        {
            var local = claim.Split('@')[0].Trim();
            if (!string.IsNullOrWhiteSpace(local))
                return local;
        }

        return fallback;
    }

    public static string Initials(string? first, string? last, string displayName, string fallback = "A")
    {
        static string Initial(string? value)
        {
            var text = value?.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : text[..1].ToUpperInvariant();
        }

        var initials = Initial(first) + Initial(last);
        return !string.IsNullOrWhiteSpace(initials)
            ? initials
            : (Initial(displayName) is { Length: > 0 } displayInitial ? displayInitial : fallback);
    }

    public static string ClaimDisplayName(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value
        ?? user.FindFirst(ClaimTypes.Name)?.Value
        ?? user.Identity?.Name
        ?? string.Empty;
}

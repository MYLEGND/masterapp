using System.Security.Claims;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.PortalShell;

namespace AgentPortal.Services;

public sealed class AgentPortalShellIdentityResolver(
    MasterAppDbContext db) : IPortalShellIdentityResolver
{
    public async Task<PortalShellIdentity> ResolveAsync(
        ClaimsPrincipal user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var fallback = "Agent";
        var first = PortalShellIdentityFactory.CleanHumanToken(
            user.FindFirst(ClaimTypes.GivenName)?.Value
            ?? user.FindFirst("given_name")?.Value
            ?? user.FindFirst("first_name")?.Value);
        var last = PortalShellIdentityFactory.CleanHumanToken(
            user.FindFirst(ClaimTypes.Surname)?.Value
            ?? user.FindFirst("family_name")?.Value
            ?? user.FindFirst("last_name")?.Value);

        var userId =
            user.FindFirst("oid")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.Identity?.Name;

        Domain.Entities.AgentProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var normalized = userId.Trim().ToLowerInvariant();
            profile = await db.AgentProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => (x.AgentUserId ?? "").ToLower() == normalized,
                    cancellationToken);
        }

        if (profile is not null && string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(profile.FullName))
        {
            var parts = profile.FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            first = parts.FirstOrDefault();
            last = parts.Length > 1 ? parts[^1] : last;
        }

        var displayName = PortalShellIdentityFactory.BuildDisplayName(
            fallback,
            profile?.FullName,
            first,
            last,
            PortalShellIdentityFactory.ClaimDisplayName(user));

        var isAssistant = httpContext.Items.TryGetValue("IsAssistant", out var assistantValue)
            && assistantValue is bool assistant
            && assistant;

        var roleLabel = !string.IsNullOrWhiteSpace(profile?.Title)
            ? profile.Title.Trim()
            : (isAssistant ? "Assistant" : "Agent");

        return new PortalShellIdentity(
            displayName,
            PortalShellIdentityFactory.Initials(first, last, displayName),
            roleLabel,
            "/avatar/current",
            PortalShellIdentityFactory.CanonicalStoreUrl);
    }
}

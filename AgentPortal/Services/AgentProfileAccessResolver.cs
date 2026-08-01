using System.Security.Claims;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

namespace AgentPortal.Services;

/// <summary>
/// Resolves the authenticated Entra agent to the one AgentProfile already
/// assigned by the portal. The object ID remains the authoritative sign-in
/// identity; the normalized directory email is only a reconciliation key for
/// an existing Azure-synced profile whose historical object ID has changed.
/// </summary>
public sealed class AgentProfileAccessResolver
{
    private readonly MasterAppDbContext _db;

    public AgentProfileAccessResolver(MasterAppDbContext db)
    {
        _db = db;
    }

    public async Task<AgentProfile?> ResolveCurrentAsync(
        ClaimsPrincipal user,
        bool requireActive,
        CancellationToken cancellationToken = default)
    {
        if (user.Identity?.IsAuthenticated != true)
            return null;

        var agentUserId = user.GetCanonicalUserId();
        if (string.IsNullOrWhiteSpace(agentUserId))
            return null;

        return await ResolveAsync(
            agentUserId,
            GetDirectoryEmail(user),
            requireActive,
            cancellationToken);
    }

    public async Task<AgentProfile?> ResolveAsync(
        string agentUserId,
        string? directoryEmail,
        bool requireActive,
        CancellationToken cancellationToken = default)
    {
        var normalizedAgentUserId = IdentityKey.Normalize(agentUserId);
        if (string.IsNullOrWhiteSpace(normalizedAgentUserId))
            return null;

        IQueryable<AgentProfile> profiles = _db.AgentProfiles;
        if (requireActive)
            profiles = profiles.Where(profile => profile.IsActive);

        var byObjectId = await profiles.FirstOrDefaultAsync(
            profile => profile.AgentUserId != null &&
                       profile.AgentUserId.ToLower() == normalizedAgentUserId,
            cancellationToken);
        if (byObjectId is not null)
            return byObjectId;

        var normalizedEmail = NormalizeEmail(directoryEmail);
        if (normalizedEmail is null)
            return null;

        // Azure AD has authenticated this principal. This fallback does not
        // grant a role; it reconnects that signed-in principal to the existing
        // one-per-email AgentProfile created by the directory sync.
        return await profiles.FirstOrDefaultAsync(
            profile => profile.NormalizedEmail == normalizedEmail ||
                       (profile.NormalizedEmail == null &&
                        profile.AgentUpn != null &&
                        profile.AgentUpn.ToLower() == normalizedEmail),
            cancellationToken);
    }

    public static string? GetDirectoryEmail(ClaimsPrincipal user)
    {
        var candidates = new[]
        {
            user.FindFirstValue("preferred_username"),
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue("email"),
            user.FindFirstValue("upn"),
            user.FindFirstValue(ClaimTypes.Upn),
            user.Identity?.Name
        };

        return candidates
            .Select(candidate => candidate?.Trim())
            .FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate) && candidate.Contains('@'));
    }

    private static string? NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace ClientApp.Services;

public sealed class EffectiveClientContext
{
    public required Guid ClientProfileId { get; init; }
    public required string ClientUserId { get; init; }
    public required ClientProfile Profile { get; init; }
    public required bool IsAgentView { get; init; }

    public string? AgentDisplayName { get; init; }
    public string? AgentNpn { get; init; }
    public string? AgentEmail { get; init; }
    public string? AgentPhone { get; init; }
}

public sealed class EffectiveClientContextService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<EffectiveClientContextService>? _logger;

    public EffectiveClientContextService(MasterAppDbContext db, ILogger<EffectiveClientContextService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    private static string Norm(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static string GetUpn(ClaimsPrincipal user)
    {
        return Norm(
            user.FindFirstValue("preferred_username")
            ?? user.FindFirstValue(ClaimTypes.Upn)
            ?? user.FindFirstValue(ClaimTypes.Email)
            ?? user.Identity?.Name
        );
    }

    private static int ProfileCompletenessScore(AgentProfile profile)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(profile.Phone)) score += 4;
        if (!string.IsNullOrWhiteSpace(profile.Npn)) score += 4;
        if (!string.IsNullOrWhiteSpace(profile.FullName)) score += 2;
        if (!string.IsNullOrWhiteSpace(profile.AgentUpn)) score += 1;
        return score;
    }

    private async Task<AgentProfile?> ResolveBestAgentProfileAsync(params string?[] identifiers)
    {
        var candidates = identifiers
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        if (candidates.Length == 0)
            return null;

        var profiles = await _db.AgentProfiles
            .AsNoTracking()
            .Where(x =>
                candidates.Contains((x.AgentUserId ?? "").ToLower()) ||
                candidates.Contains((x.AgentUpn ?? "").ToLower()) ||
                candidates.Contains((x.NormalizedEmail ?? "").ToLower()))
            .ToListAsync();

        if (profiles.Count == 0)
            return null;

        return profiles
            .OrderByDescending(ProfileCompletenessScore)
            .ThenByDescending(x => x.UpdatedUtc)
            .FirstOrDefault();
    }

    private static EffectiveClientContext ToContext(
        ClientProfile profile,
        bool isAgentView,
        string? agentDisplayName = null,
        string? agentNpn = null,
        string? agentEmail = null,
        string? agentPhone = null)
    {
        return new EffectiveClientContext
        {
            ClientProfileId = profile.Id,
            ClientUserId = Norm(profile.ClientUserId),
            Profile = profile,
            IsAgentView = isAgentView,
            AgentDisplayName = agentDisplayName,
            AgentNpn = agentNpn,
            AgentEmail = agentEmail,
            AgentPhone = agentPhone
        };
    }

    public async Task<EffectiveClientContext?> ResolveAsync(
        ClaimsPrincipal user,
        IRequestCookieCollection cookies,
        bool allowRelink = true)
    {
        var upn = GetUpn(user);
        var canonicalUserId = Norm(user.GetCanonicalUserId());

        // If impersonation cookie is present, honor agent view regardless of UPN domain.
        if (cookies.TryGetValue("impClientProfileId", out _))
        {
            return await ResolveAgentAsync(user, cookies, upn);
        }

        // If UPN looks like an agent domain, still attempt agent resolution (keeps previous behavior).
        if (upn.EndsWith("@mylegnd.com", StringComparison.OrdinalIgnoreCase))
        {
            var agentCtx = await ResolveAgentAsync(user, cookies, upn);
            if (agentCtx != null) return agentCtx;
        }

        return await ResolveClientAsync(user, cookies, upn, canonicalUserId, allowRelink);
    }

    private async Task<EffectiveClientContext?> ResolveAgentAsync(
        ClaimsPrincipal user,
        IRequestCookieCollection cookies,
        string upn)
    {
        if (!cookies.TryGetValue("impClientProfileId", out var raw) ||
            !Guid.TryParse(raw, out var clientProfileId))
        {
            return null;
        }

        var profile = await _db.ClientProfiles
            .FirstOrDefaultAsync(x => x.Id == clientProfileId);

        if (profile == null)
            return null;

        var clientUserId = Norm(profile.ClientUserId);
        if (string.IsNullOrWhiteSpace(clientUserId))
            return null;

        // Pull agent link + profile for display
        var agentLink = await _db.AgentClients
            .AsNoTracking()
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == clientUserId);

        AgentProfile? agentProfile = null;
        if (agentLink != null)
        {
            if (!string.IsNullOrWhiteSpace(agentLink.AgentUserId))
            {
                var agentUserIdNorm = Norm(agentLink.AgentUserId);
                agentProfile = await _db.AgentProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => (x.AgentUserId ?? "").ToLower() == agentUserIdNorm);

                // Secondary fallback: some legacy rows stored UPN in AgentUserId — match that to AgentUpn
                if (agentProfile == null)
                {
                    agentProfile = await _db.AgentProfiles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => (x.AgentUpn ?? "").ToLower() == agentUserIdNorm);
                }
            }

            // Fallback: match on AgentUpn if we couldn't locate by user id
            if (agentProfile == null && !string.IsNullOrWhiteSpace(agentLink.AgentUpn))
            {
                var upnNorm = Norm(agentLink.AgentUpn);
                agentProfile = await _db.AgentProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => (x.AgentUpn ?? "").ToLower() == upnNorm);
            }

            // Final fallback: try any identifier we have (AgentUserId or AgentUpn) against both columns
            if (agentProfile == null)
            {
                var candidates = new[]
                {
                    Norm(agentLink.AgentUserId),
                    Norm(agentLink.AgentUpn),
                    Norm(upn),
                    Norm(user.FindFirst("oid")?.Value),
                    Norm(user.FindFirst(ClaimTypes.NameIdentifier)?.Value)
                }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();

                if (candidates.Length > 0)
                {
                    agentProfile = await _db.AgentProfiles
                        .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        candidates.Contains((x.AgentUserId ?? "").ToLower()) ||
                        candidates.Contains((x.AgentUpn ?? "").ToLower()));
                }
            }

            if (agentProfile == null || string.IsNullOrWhiteSpace(agentProfile.Phone) || string.IsNullOrWhiteSpace(agentProfile.Npn))
            {
                var bestProfile = await ResolveBestAgentProfileAsync(
                    agentLink.AgentUserId,
                    agentLink.AgentUpn,
                    upn,
                    user.FindFirst("oid")?.Value,
                    user.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                if (bestProfile != null)
                    agentProfile = bestProfile;
            }
        }

        var agentDisplayName = agentProfile?.FullName;
        if (string.IsNullOrWhiteSpace(agentDisplayName))
            agentDisplayName = string.IsNullOrWhiteSpace(agentLink?.AgentUpn) ? "Your Agent" : agentLink!.AgentUpn;

        var agentNpn = string.IsNullOrWhiteSpace(agentProfile?.Npn) ? null : agentProfile!.Npn;

        var agentIdCandidates = user.GetUserIdCandidates()
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();

        var ownsClient = await _db.AgentClients
            .AsNoTracking()
            .AnyAsync(link =>
                (link.ClientUserId ?? "").ToLower() == clientUserId &&
                (
                    (!string.IsNullOrWhiteSpace(upn) && (link.AgentUpn ?? "").ToLower() == upn) ||
                    agentIdCandidates.Contains((link.AgentUserId ?? "").ToLower())
                ));

        if (!ownsClient)
            return null;

        // If we still don't have phone/NPN, try resolving by current user's UPN as a final fallback.
        if ((agentProfile == null || string.IsNullOrWhiteSpace(agentProfile.Phone) || string.IsNullOrWhiteSpace(agentNpn)) && !string.IsNullOrWhiteSpace(upn))
        {
            var upnNorm = Norm(upn);
            var fallbackProfile = await _db.AgentProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    (x.AgentUpn ?? "").ToLower() == upnNorm ||
                    (x.AgentUserId ?? "").ToLower() == upnNorm);

            if (fallbackProfile != null)
            {
                if (agentProfile == null) agentProfile = fallbackProfile;
                if (string.IsNullOrWhiteSpace(agentProfile.Phone)) agentProfile.Phone = fallbackProfile.Phone;
                if (string.IsNullOrWhiteSpace(agentNpn) && !string.IsNullOrWhiteSpace(fallbackProfile.Npn)) agentNpn = fallbackProfile.Npn;
            }
        }

        if (string.IsNullOrWhiteSpace(agentProfile?.Phone) || string.IsNullOrWhiteSpace(agentNpn))
        {
            _logger?.LogWarning("Agent pill missing phone/npn (agent view). upn={Upn} linkUserId={LinkUserId} linkUpn={LinkUpn} profUserId={ProfUserId} profUpn={ProfUpn} phone={Phone} npn={Npn}",
                upn,
                agentLink?.AgentUserId,
                agentLink?.AgentUpn,
                agentProfile?.AgentUserId,
                agentProfile?.AgentUpn,
                agentProfile?.Phone,
                agentNpn);
        }

        var agentEmail = agentProfile?.AgentUpn ?? agentLink?.AgentUpn;
        var agentPhone = string.IsNullOrWhiteSpace(agentProfile?.Phone) ? null : agentProfile!.Phone;

        return ToContext(
            profile,
            isAgentView: true,
            agentDisplayName: agentDisplayName,
            agentNpn: agentNpn,
            agentEmail: agentEmail,
            agentPhone: agentPhone);
    }

    private async Task<EffectiveClientContext?> ResolveClientAsync(
        ClaimsPrincipal user,
        IRequestCookieCollection cookies,
        string upn,
        string canonicalUserId,
        bool allowRelink)
    {
        ClientProfile? profile = null;
        AgentClient? agentLink = null;
        AgentProfile? agentProfile = null;

        if (cookies.TryGetValue("selfClientProfileId", out var rawProfileId) &&
            Guid.TryParse(rawProfileId, out var pinnedClientProfileId))
        {
            var pinnedProfile = await _db.ClientProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == pinnedClientProfileId);

            if (pinnedProfile != null)
            {
                var pinnedClientUserId = Norm(pinnedProfile.ClientUserId);
                var candidates = user.GetUserIdCandidates()
                    .Select(Norm)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .ToArray();

                var matchesPinnedProfile =
                    (!string.IsNullOrWhiteSpace(canonicalUserId) &&
                     string.Equals(pinnedClientUserId, canonicalUserId, StringComparison.Ordinal)) ||
                    candidates.Contains(pinnedClientUserId) ||
                    (!string.IsNullOrWhiteSpace(upn) &&
                     string.Equals(Norm(pinnedProfile.Email), upn, StringComparison.Ordinal));

                if (matchesPinnedProfile)
                    return ToContext(pinnedProfile, isAgentView: false);
            }
        }

        if (!string.IsNullOrWhiteSpace(canonicalUserId))
        {
            profile = await _db.ClientProfiles
                .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == canonicalUserId);
        }

        if (profile == null)
        {
            var candidates = user.GetUserIdCandidates()
                .Select(Norm)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToArray();

            if (candidates.Length > 0)
            {
                profile = await _db.ClientProfiles
                    .FirstOrDefaultAsync(x => candidates.Contains((x.ClientUserId ?? "").ToLower()));
            }
        }

        if (profile == null && !string.IsNullOrWhiteSpace(upn))
        {
            profile = await _db.ClientProfiles
                .FirstOrDefaultAsync(x => (x.Email ?? "").ToLower() == upn);
        }

        if (profile == null)
            return null;

        agentLink = await _db.AgentClients
            .AsNoTracking()
            .FirstOrDefaultAsync(x => (x.ClientUserId ?? "").ToLower() == Norm(profile.ClientUserId));

        if (agentLink != null)
        {
            if (!string.IsNullOrWhiteSpace(agentLink.AgentUserId))
            {
                var agentUserIdNorm = Norm(agentLink.AgentUserId);
                agentProfile = await _db.AgentProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => (x.AgentUserId ?? "").ToLower() == agentUserIdNorm);

                // Secondary fallback: legacy rows with UPN stored in AgentUserId
                if (agentProfile == null)
                {
                    agentProfile = await _db.AgentProfiles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => (x.AgentUpn ?? "").ToLower() == agentUserIdNorm);
                }
            }

            // Fallback: match on AgentUpn when AgentUserId is missing
            if (agentProfile == null && !string.IsNullOrWhiteSpace(agentLink.AgentUpn))
            {
                var upnNorm = Norm(agentLink.AgentUpn);
                agentProfile = await _db.AgentProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => (x.AgentUpn ?? "").ToLower() == upnNorm);
            }

            // Final fallback: try any identifier we have (AgentUserId or AgentUpn) against both columns
            if (agentProfile == null)
            {
                var candidates = new[]
                {
                    Norm(agentLink.AgentUserId),
                    Norm(agentLink.AgentUpn),
                    Norm(upn),
                    Norm(user.FindFirst("oid")?.Value),
                    Norm(user.FindFirst(ClaimTypes.NameIdentifier)?.Value)
                }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();

                if (candidates.Length > 0)
                {
                    agentProfile = await _db.AgentProfiles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x =>
                            candidates.Contains((x.AgentUserId ?? "").ToLower()) ||
                            candidates.Contains((x.AgentUpn ?? "").ToLower()));
                }
            }

            if (agentProfile == null || string.IsNullOrWhiteSpace(agentProfile.Phone) || string.IsNullOrWhiteSpace(agentProfile.Npn))
            {
                var bestProfile = await ResolveBestAgentProfileAsync(
                    agentLink.AgentUserId,
                    agentLink.AgentUpn,
                    upn,
                    user.FindFirst("oid")?.Value,
                    user.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                if (bestProfile != null)
                    agentProfile = bestProfile;
            }
        }

        var agentDisplayName = agentProfile?.FullName;
        if (string.IsNullOrWhiteSpace(agentDisplayName))
            agentDisplayName = string.IsNullOrWhiteSpace(agentLink?.AgentUpn) ? "Your Agent" : agentLink!.AgentUpn;

        var agentNpn = string.IsNullOrWhiteSpace(agentProfile?.Npn) ? null : agentProfile!.Npn;

        // If we still don't have phone/NPN, try resolving by current user's UPN as a final fallback.
        if ((agentProfile == null || string.IsNullOrWhiteSpace(agentProfile.Phone) || string.IsNullOrWhiteSpace(agentNpn)) && !string.IsNullOrWhiteSpace(upn))
        {
            var upnNorm = Norm(upn);
            var fallbackProfile = await _db.AgentProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    (x.AgentUpn ?? "").ToLower() == upnNorm ||
                    (x.AgentUserId ?? "").ToLower() == upnNorm);

            if (fallbackProfile != null)
            {
                if (agentProfile == null) agentProfile = fallbackProfile;
                if (string.IsNullOrWhiteSpace(agentProfile.Phone)) agentProfile.Phone = fallbackProfile.Phone;
                if (string.IsNullOrWhiteSpace(agentNpn) && !string.IsNullOrWhiteSpace(fallbackProfile.Npn)) agentNpn = fallbackProfile.Npn;
            }
        }

        if (string.IsNullOrWhiteSpace(agentProfile?.Phone) || string.IsNullOrWhiteSpace(agentNpn))
        {
            _logger?.LogWarning("Agent pill missing phone/npn (client view). upn={Upn} linkUserId={LinkUserId} linkUpn={LinkUpn} profUserId={ProfUserId} profUpn={ProfUpn} phone={Phone} npn={Npn}",
                upn,
                agentLink?.AgentUserId,
                agentLink?.AgentUpn,
                agentProfile?.AgentUserId,
                agentProfile?.AgentUpn,
                agentProfile?.Phone,
                agentNpn);
        }

        var agentEmail = agentProfile?.AgentUpn ?? agentLink?.AgentUpn;
        var agentPhone = string.IsNullOrWhiteSpace(agentProfile?.Phone) ? null : agentProfile!.Phone;

        return ToContext(
            profile,
            isAgentView: false,
            agentDisplayName: agentDisplayName,
            agentNpn: agentNpn,
            agentEmail: agentEmail,
            agentPhone: agentPhone);
    }
}

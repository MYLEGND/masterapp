using Domain.Entities;
using System.Diagnostics.CodeAnalysis;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Analytics;

public sealed class AgentTrackingResolver
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgentTrackingResolver> _logger;

    public AgentTrackingResolver(MasterAppDbContext db, ILogger<AgentTrackingResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Resolve an explicitly selected owner without falling through to another identity.
    public async Task<ResolveResult> ResolveAsync(string? slug, Guid? profileId, CancellationToken ct = default)
    {
        if (profileId.HasValue)
        {
            var byId = await ResolveByIdAsync(profileId.Value, ct);
            if (!byId.Found || string.IsNullOrWhiteSpace(slug)) return byId;
            var bySlug = await ResolveBySlugAsync(slug, ct);
            return bySlug.Found && bySlug.Profile?.Id == byId.Profile?.Id
                ? bySlug : ResolveResult.NotFound;
        }
        return string.IsNullOrWhiteSpace(slug)
            ? ResolveResult.NotFound : await ResolveBySlugAsync(slug, ct);
    }

    public async Task<ResolveResult> ResolveBySlugAsync(string slug, CancellationToken ct = default)
    {
        try
        {
            var alias = await _db.AgentTrackingAliases
                .Include(a => a.Profile)
                .FirstOrDefaultAsync(a => a.Slug == slug, ct);

            if (alias != null)
            {
                var canonical = alias.IsCanonical ? alias.Slug :
                    await _db.AgentTrackingAliases
                        .Where(a => a.AgentTrackingProfileId == alias.AgentTrackingProfileId && a.IsCanonical)
                        .Select(a => a.Slug)
                        .FirstOrDefaultAsync(ct) ?? alias.Slug;

                var profile = alias.Profile ?? await _db.AgentTrackingProfiles.FindAsync(new object[] { alias.AgentTrackingProfileId }, ct);
                return profile is null ? ResolveResult.NotFound : new ResolveResult(profile, alias.Slug, canonical, alias.IsCanonical, Found: true);
            }

            var profileOnly = await _db.AgentTrackingProfiles.FirstOrDefaultAsync(p => p.Slug == slug, ct);
            if (profileOnly != null)
            {
                return new ResolveResult(profileOnly, profileOnly.Slug, profileOnly.Slug, true, Found: true);
            }

            _logger.LogInformation("SlugResolution: unknown slug '{Slug}'", slug);
            return ResolveResult.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlugResolution: DB resolve failed for slug '{Slug}'", slug);
            return ResolveResult.NotFound;
        }
    }

    public async Task<ResolveResult> ResolveByIdAsync(Guid profileId, CancellationToken ct = default)
    {
        try
        {
            var profile = await _db.AgentTrackingProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId, ct);

            if (profile == null)
            {
                _logger.LogInformation("SlugResolution: unknown profile id {ProfileId}", profileId);
                return ResolveResult.NotFound;
            }

            var canonical = await _db.AgentTrackingAliases
                .AsNoTracking()
                .Where(a => a.AgentTrackingProfileId == profileId && a.IsCanonical)
                .Select(a => a.Slug)
                .FirstOrDefaultAsync(ct) ?? profile.Slug;

            return new ResolveResult(profile, canonical, canonical, true, Found: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlugResolution: DB resolve failed for profile id {ProfileId}", profileId);
            return ResolveResult.NotFound;
        }
    }

    public async Task<ResolveResult> ResolveByUpnAsync(string upn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upn)) return ResolveResult.NotFound;
        try
        {
            var profile = await _db.AgentTrackingProfiles.AsNoTracking()
                .Where(p => p.AgentUpn == upn)
                .OrderBy(p => p.CreatedUtc)
                .ThenBy(p => p.Id)
                .FirstOrDefaultAsync(ct);
            if (profile == null) return ResolveResult.NotFound;
            return new ResolveResult(profile, profile.Slug, profile.Slug, IsCanonical: true, Found: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SlugResolution: DB resolve failed for founder upn");
            return ResolveResult.NotFound;
        }
    }
}

public sealed record ResolveResult(
    AgentTrackingProfile? Profile,
    string? RequestedSlug,
    string? CanonicalSlug,
    bool IsCanonical,
    [property: MemberNotNullWhen(true, "Profile")] bool Found)
{
    public static ResolveResult NotFound => new(null, null, null, false, false);
}

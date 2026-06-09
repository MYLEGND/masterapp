using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ProtectWebsite.Services.Tracking;

public sealed class AgentTrackingResolver
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgentTrackingResolver> _logger;

    public AgentTrackingResolver(MasterAppDbContext db, ILogger<AgentTrackingResolver> logger)
    {
        _db = db;
        _logger = logger;
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
                return new ResolveResult(profile, alias.Slug, canonical, alias.IsCanonical, Found: true);
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
    bool Found)
{
    public static ResolveResult NotFound => new(null, null, null, false, false);
}

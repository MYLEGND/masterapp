using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services.Tracking;

public sealed class AgentTrackingResolver
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgentTrackingResolver> _logger;

    public AgentTrackingResolver(MasterAppDbContext db, ILogger<AgentTrackingResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ResolveResult> ResolveAsync(string? slug, Guid? profileId, CancellationToken ct = default)
    {
        AgentTrackingProfile? profile = null;
        string? canonicalSlug = null;
        bool isCanonical = true;

        if (profileId.HasValue)
        {
            profile = await _db.AgentTrackingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId.Value, ct);
            if (profile == null)
            {
                _logger.LogWarning("AgentTrackingResolver: unknown profile id {ProfileId}", profileId.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var alias = await _db.AgentTrackingAliases
                .Include(a => a.Profile)
                .FirstOrDefaultAsync(a => a.Slug == slug, ct);
            if (alias != null)
            {
                if (profile != null && alias.AgentTrackingProfileId != profile.Id)
                {
                    _logger.LogWarning(
                        "AgentTrackingResolver: slug/profile mismatch slug={Slug} aliasProfileId={AliasProfileId} payloadProfileId={PayloadProfileId}; keeping payload profile id.",
                        slug,
                        alias.AgentTrackingProfileId,
                        profile.Id);

                    canonicalSlug = await _db.AgentTrackingAliases
                        .Where(a => a.AgentTrackingProfileId == profile.Id && a.IsCanonical)
                        .Select(a => a.Slug)
                        .FirstOrDefaultAsync(ct) ?? profile.Slug;
                    isCanonical = string.Equals(canonicalSlug, slug, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    profile = alias.Profile ?? profile;
                    isCanonical = alias.IsCanonical;
                    canonicalSlug = alias.IsCanonical ? alias.Slug :
                        await _db.AgentTrackingAliases
                            .Where(a => a.AgentTrackingProfileId == alias.AgentTrackingProfileId && a.IsCanonical)
                            .Select(a => a.Slug)
                            .FirstOrDefaultAsync(ct) ?? alias.Slug;
                }
            }
            else
            {
                // maybe slug equals canonical on profile
                var profBySlug = await _db.AgentTrackingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug, ct);
                if (profBySlug != null)
                {
                    if (profile != null && profBySlug.Id != profile.Id)
                    {
                        _logger.LogWarning(
                            "AgentTrackingResolver: profile slug mismatch slug={Slug} slugProfileId={SlugProfileId} payloadProfileId={PayloadProfileId}; keeping payload profile id.",
                            slug,
                            profBySlug.Id,
                            profile.Id);

                        canonicalSlug = await _db.AgentTrackingAliases
                            .Where(a => a.AgentTrackingProfileId == profile.Id && a.IsCanonical)
                            .Select(a => a.Slug)
                            .FirstOrDefaultAsync(ct) ?? profile.Slug;
                        isCanonical = string.Equals(canonicalSlug, slug, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        profile = profBySlug;
                        canonicalSlug = profBySlug.Slug;
                        isCanonical = true;
                    }
                }
                else
                {
                    _logger.LogInformation("AgentTrackingResolver: unknown slug {Slug}", slug);
                }
            }
        }

        if (profile == null)
        {
            return ResolveResult.NotFound;
        }

        canonicalSlug ??= profile.Slug;
        return new ResolveResult(profile, canonicalSlug, isCanonical);
    }
}

public sealed record ResolveResult(AgentTrackingProfile Profile, string CanonicalSlug, bool IsCanonical)
{
    public static ResolveResult NotFound => new(null!, string.Empty, false);
    public bool Found => Profile != null;
}

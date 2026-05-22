using System.Text;
using System.Text.RegularExpressions;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services.Tracking;

public sealed class AgentTrackingService : IAgentTrackingService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgentTrackingService> _logger;
    private readonly string _publicBaseUrl;
    private readonly string _founderUpn;

    public AgentTrackingService(MasterAppDbContext db, ILogger<AgentTrackingService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _publicBaseUrl = config["Protect:PublicBaseUrl"] ?? "https://protect.mylegnd.com";
        _founderUpn = config["Founder:Upn"] ?? throw new InvalidOperationException("Founder:Upn configuration is required");
    }

    private static string? NormalizeUpn(string? agentUpn)
    {
        var value = agentUpn?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task<AgentTrackingProfile?> GetByUserIdAsync(string agentUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentUserId)) return null;
        var key = agentUserId.Trim();
        var profile = await _db.AgentTrackingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentUserId == key, ct);
        if (profile != null) return profile;

        // SQLite can behave case-sensitively for text equality; fall back to normalized match.
        var lower = key.ToLowerInvariant();
        return await _db.AgentTrackingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AgentUserId != null && x.AgentUserId.ToLower() == lower, ct);
    }

    public async Task<AgentTrackingProfile?> GetByUpnAsync(string agentUpn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentUpn)) return null;
        return await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(x => x.AgentUpn == agentUpn)
            .OrderBy(x => x.CreatedUtc)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AgentTrackingProfile> EnsureProfileAsync(string agentUserId, string agentUpn, string? displayName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentUserId))
            throw new ArgumentException("agentUserId is required", nameof(agentUserId));
        agentUpn ??= string.Empty;

        var agentUserIdLower = agentUserId.ToLowerInvariant();
        var agentUpnNorm = NormalizeUpn(agentUpn);
        var existing = await _db.AgentTrackingProfiles
            .Include(x => x.Aliases)
            .FirstOrDefaultAsync(x => x.AgentUserId == agentUserId || (x.AgentUserId != null && x.AgentUserId.ToLower() == agentUserIdLower), ct);
        if (existing == null && agentUpnNorm != null)
        {
            var upnMatches = await _db.AgentTrackingProfiles
                .Include(x => x.Aliases)
                .Where(x => x.AgentUpn != null && x.AgentUpn.ToLower() == agentUpnNorm)
                .ToListAsync(ct);

            if (upnMatches.Count > 0)
            {
                existing = upnMatches
                    .OrderBy(x => Regex.IsMatch(x.Slug ?? string.Empty, "-\\d+$") ? 1 : 0)
                    .ThenBy(x => x.CreatedUtc)
                    .ThenBy(x => x.Id)
                    .First();

                _logger.LogWarning(
                    "AgentTracking: reusing tracking profile {ProfileId} for oid {Oid} via agent UPN {AgentUpn} to avoid creating a duplicate tracking slug.",
                    existing.Id,
                    agentUserId,
                    agentUpnNorm);
            }
        }
        if (existing != null)
        {
            // refresh UPN / display name if changed
            var changed = false;
            if (!string.IsNullOrWhiteSpace(agentUpn) && !string.Equals(existing.AgentUpn, agentUpn, StringComparison.OrdinalIgnoreCase))
            {
                existing.AgentUpn = agentUpn;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(existing.DisplayName))
            {
                existing.DisplayName = displayName;
                changed = true;
            }
            if (changed)
            {
                existing.UpdatedUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return existing;
        }

        var baseSlug = GenerateBaseSlug(displayName, agentUpn, agentUserId);
        var uniqueSlug = await EnsureUniqueSlugAsync(baseSlug, ct);

        var profile = new AgentTrackingProfile
        {
            AgentUserId = agentUserId,
            AgentUpn = agentUpn,
            Slug = uniqueSlug,
            DisplayName = displayName,
            Status = "active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var alias = new AgentTrackingAlias
        {
            AgentTrackingProfileId = profile.Id,
            Slug = uniqueSlug,
            IsCanonical = true,
            CreatedUtc = DateTime.UtcNow
        };

        _db.AgentTrackingProfiles.Add(profile);
        _db.AgentTrackingAliases.Add(alias);
        await _db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<AgentUrlInfo> GetPersonalUrlsAsync(AgentTrackingProfile profile, CancellationToken ct = default)
    {
        var baseUrl = _publicBaseUrl.TrimEnd('/');
        var slugUrl = $"{baseUrl}/a/{profile.Slug}";
        string? preferredSlug = profile.Slug;

        // Prefer a clean slug without numeric suffix when available and unique
        var cleanSlug = Regex.Replace(profile.Slug ?? string.Empty, "-\\d+$", string.Empty).Trim('-');
        if (!string.IsNullOrWhiteSpace(cleanSlug) && !string.Equals(cleanSlug, profile.Slug, StringComparison.OrdinalIgnoreCase))
        {
            var existsElsewhere = await _db.AgentTrackingAliases.AsNoTracking().AnyAsync(a => a.Slug == cleanSlug && a.AgentTrackingProfileId != profile.Id, ct)
                || await _db.AgentTrackingProfiles.AsNoTracking().AnyAsync(p => p.Slug == cleanSlug && p.Id != profile.Id, ct);
            if (!existsElsewhere)
            {
                var hasAlias = await _db.AgentTrackingAliases.AsNoTracking().AnyAsync(a => a.Slug == cleanSlug && a.AgentTrackingProfileId == profile.Id, ct);
                if (!hasAlias)
                {
                    _db.AgentTrackingAliases.Add(new AgentTrackingAlias
                    {
                        AgentTrackingProfileId = profile.Id,
                        Slug = cleanSlug,
                        IsCanonical = false,
                        CreatedUtc = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync(ct);
                }
                preferredSlug = cleanSlug;
                slugUrl = $"{baseUrl}/a/{preferredSlug}";
            }
        }
        var isFounder = string.Equals(profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase);
        var primary = isFounder ? $"{baseUrl}/" : slugUrl;
        var alternate = isFounder ? slugUrl : (preferredSlug != profile.Slug ? slugUrl : null);
        var campaignBase = $"{baseUrl}/c"; // placeholder for future campaign links
        return new AgentUrlInfo(primary, alternate, campaignBase, null);
    }

    public async Task<List<AgentTrackingProfile>> GetAllProfilesAsync(CancellationToken ct = default)
    {
        return await _db.AgentTrackingProfiles.AsNoTracking()
            .OrderBy(x => x.DisplayName ?? x.AgentUpn)
            .ToListAsync(ct);
    }

    public async Task<AgentTrackingBackfillResult> BackfillAsync(bool dryRun = false, CancellationToken ct = default)
    {
        var results = new AgentTrackingBackfillResult();

        // treat AgentProfiles as the source of truth for agents
        var agents = await _db.AgentProfiles.AsNoTracking().ToListAsync(ct);
        foreach (var agent in agents)
        {
            var displayName = string.IsNullOrWhiteSpace(agent.FullName)
                ? agent.AgentUpn ?? agent.AgentUserId
                : agent.FullName;

            var existing = await _db.AgentTrackingProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AgentUserId == agent.AgentUserId, ct);
            if (existing != null)
            {
                results.SkippedExisting++;
                continue;
            }

            var baseSlug = GenerateBaseSlug(displayName, agent.AgentUpn, agent.AgentUserId);
            var uniqueSlug = await EnsureUniqueSlugAsync(baseSlug, ct);

            results.Created.Add((agent.AgentUserId, uniqueSlug));
            if (dryRun) continue;

            var profile = new AgentTrackingProfile
            {
                AgentUserId = agent.AgentUserId,
                AgentUpn = agent.AgentUpn ?? string.Empty,
                Slug = uniqueSlug,
                DisplayName = displayName,
                Status = "active",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            var alias = new AgentTrackingAlias
            {
                AgentTrackingProfileId = profile.Id,
                Slug = uniqueSlug,
                IsCanonical = true,
                CreatedUtc = DateTime.UtcNow
            };
            _db.AgentTrackingProfiles.Add(profile);
            _db.AgentTrackingAliases.Add(alias);
        }

        // ensure founder exists
        var founder = agents.FirstOrDefault(a => string.Equals(a.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase));
        if (founder != null)
        {
            var founderProfile = await _db.AgentTrackingProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AgentUserId == founder.AgentUserId, ct);
            if (founderProfile == null)
            {
                var baseSlug = GenerateBaseSlug(founder.FullName ?? "founder", founder.AgentUpn, founder.AgentUserId, preferFounder:true);
                var uniqueSlug = await EnsureUniqueSlugAsync(baseSlug, ct);
                results.Created.Add((founder.AgentUserId, uniqueSlug));
                if (!dryRun)
                {
                    var profile = new AgentTrackingProfile
                    {
                        AgentUserId = founder.AgentUserId,
                        AgentUpn = founder.AgentUpn ?? _founderUpn,
                        Slug = uniqueSlug,
                        DisplayName = founder.FullName ?? "Founder",
                        Status = "active",
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    _db.AgentTrackingProfiles.Add(profile);
                    _db.AgentTrackingAliases.Add(new AgentTrackingAlias
                    {
                        AgentTrackingProfileId = profile.Id,
                        Slug = uniqueSlug,
                        IsCanonical = true,
                        CreatedUtc = DateTime.UtcNow
                    });
                }
            }
        }

        if (!dryRun)
        {
            await _db.SaveChangesAsync(ct);
        }

        return results;
    }

    // -----------------------------------------------------
    // Helpers
    // -----------------------------------------------------
    private static readonly Regex NonSlugChars = new("[^a-z0-9-]+", RegexOptions.Compiled);

    private string GenerateBaseSlug(string? name, string? upn, string userId, bool preferFounder = false)
    {
        var source = name;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = upn?.Split('@').FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            source = preferFounder ? "founder" : "agent";
        }

        var slug = ToKebab(source);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = preferFounder ? "founder" : "agent";
        }

        // avoid exposing OID; only use suffix if collisions persist
        return slug;
    }

    private string ToKebab(string input)
    {
        var normalized = input.Trim().ToLowerInvariant();
        normalized = normalized.Replace("_", "-").Replace(".", "-");
        normalized = NonSlugChars.Replace(normalized, "-");
        normalized = Regex.Replace(normalized, "-{2,}", "-");
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "agent" : normalized;
    }

    private async Task<string> EnsureUniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (true)
        {
            var exists = await _db.AgentTrackingProfiles.AsNoTracking().AnyAsync(x => x.Slug == slug, ct)
                || await _db.AgentTrackingAliases.AsNoTracking().AnyAsync(x => x.Slug == slug, ct);
            if (!exists) return slug;
            slug = $"{baseSlug}-{suffix}";
            suffix++;
        }
    }
}

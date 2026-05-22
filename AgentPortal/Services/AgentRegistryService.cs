using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Ensures every authenticated agent has an AgentProfile record.
/// Prefers OID (AgentUserId) lookups, but falls back to normalized email so the
/// same person does not silently fork into duplicate profiles when identity keys
/// drift across environments.
/// </summary>
public class AgentRegistryService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AgentRegistryService> _logger;
    private readonly Tracking.IAgentTrackingService _tracking;

    public AgentRegistryService(MasterAppDbContext db, ILogger<AgentRegistryService> logger, Tracking.IAgentTrackingService tracking)
    {
        _db = db;
        _logger = logger;
        _tracking = tracking;
    }

    private static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetOid(ClaimsPrincipal user)
    {
        var oid =
            user.FindFirstValue("oid") ??
            user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        oid = oid?.Trim();
        return string.IsNullOrWhiteSpace(oid) ? null : oid;
    }

    private static string? GetEmail(ClaimsPrincipal user)
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

        foreach (var c in candidates)
        {
            var v = c?.Trim();
            if (!string.IsNullOrWhiteSpace(v) && v.Contains("@"))
                return v;
        }

        return null;
    }

    private static string BuildDisplayName(ClaimsPrincipal user)
    {
        var given =
            user.FindFirstValue(ClaimTypes.GivenName) ??
            user.FindFirstValue("given_name") ??
            user.FindFirstValue("first_name");

        var family =
            user.FindFirstValue(ClaimTypes.Surname) ??
            user.FindFirstValue("family_name") ??
            user.FindFirstValue("last_name");

        var name = $"{given} {family}".Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;

        var fallback = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name);
        return string.IsNullOrWhiteSpace(fallback) ? "Agent" : fallback.Trim();
    }

    /// <summary>
    /// Upsert AgentProfile for the authenticated user. Returns true if a valid OID was present.
    /// </summary>
    public async Task<bool> UpsertAgentProfileAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        var oid = GetOid(user);
        if (oid == null)
        {
            _logger.LogWarning("AgentRegistry: missing OID on authenticated principal; cannot upsert AgentProfile.");
            return false;
        }

        var email = GetEmail(user) ?? string.Empty;
        var emailNorm = NormalizeEmail(email);
        var displayName = BuildDisplayName(user);
        var oidLower = oid.ToLowerInvariant();

        try
        {
            var existing = await _db.AgentProfiles.FirstOrDefaultAsync(
                a => a.AgentUserId == oid || (a.AgentUserId != null && a.AgentUserId.ToLower() == oidLower),
                ct);

            List<Domain.Entities.AgentProfile> emailMatches = new();
            if (emailNorm != null)
            {
                emailMatches = await _db.AgentProfiles
                    .Where(a => a.NormalizedEmail == emailNorm || (a.NormalizedEmail == null && a.AgentUpn != null && a.AgentUpn.ToLower() == emailNorm))
                    .OrderBy(a => a.CreatedUtc)
                    .ThenBy(a => a.Id)
                    .ToListAsync(ct);
            }

            if (existing == null && emailMatches.Count > 0)
            {
                existing = PickPreferredProfile(emailMatches);
                _logger.LogWarning(
                    "AgentRegistry: reusing AgentProfile {ProfileId} for oid {Oid} via normalized email {EmailNorm} to avoid creating a duplicate profile.",
                    existing.Id,
                    oid,
                    emailNorm);
            }

            if (existing == null)
            {
                _db.AgentProfiles.Add(new Domain.Entities.AgentProfile
                {
                    AgentUserId = oid,
                    AgentUpn = email,
                    NormalizedEmail = emailNorm,
                    FullName = displayName,
                    UpdatedUtc = DateTime.UtcNow
                });
            }
            else
            {
                var changed = false;

                if (emailMatches.Count > 1)
                {
                    _logger.LogWarning(
                        "AgentRegistry: found {MatchCount} AgentProfiles for normalized email {EmailNorm}; backfilling missing data into profile {ProfileId}.",
                        emailMatches.Count,
                        emailNorm,
                        existing.Id);

                    foreach (var sibling in emailMatches
                        .Where(profile => profile.Id != existing.Id)
                        .OrderByDescending(GetProfileCompletenessScore)
                        .ThenBy(profile => profile.CreatedUtc)
                        .ThenBy(profile => profile.Id))
                    {
                        changed |= CopyMissingProfileData(existing, sibling);
                    }
                }

                // Keep email/name fresh but do not overwrite with empty values.
                if (!string.IsNullOrWhiteSpace(email) && !string.Equals(existing.AgentUpn, email, StringComparison.OrdinalIgnoreCase))
                {
                    existing.AgentUpn = email;
                    changed = true;
                }
                if (emailNorm != null && !string.Equals(existing.NormalizedEmail, emailNorm, StringComparison.Ordinal))
                {
                    existing.NormalizedEmail = emailNorm;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(existing.FullName, displayName, StringComparison.Ordinal))
                {
                    existing.FullName = displayName;
                    changed = true;
                }
                existing.UpdatedUtc = DateTime.UtcNow;
                if (!changed && existing.CreatedUtc == default)
                {
                    existing.CreatedUtc = DateTime.UtcNow;
                }
            }

            await _db.SaveChangesAsync(ct);
            try
            {
                await _tracking.EnsureProfileAsync(oid, email, displayName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AgentRegistry: failed to ensure tracking profile for oid {Oid}", oid);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentRegistry: failed to upsert AgentProfile for oid {Oid}", oid);
            throw;
        }
    }

    private static Domain.Entities.AgentProfile PickPreferredProfile(IEnumerable<Domain.Entities.AgentProfile> matches)
    {
        return matches
            .OrderByDescending(GetProfileCompletenessScore)
            .ThenBy(profile => profile.CreatedUtc)
            .ThenBy(profile => profile.Id)
            .First();
    }

    private static int GetProfileCompletenessScore(Domain.Entities.AgentProfile profile)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(profile.NormalizedEmail)) score += 2;
        if (!string.IsNullOrWhiteSpace(profile.FullName)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.Title)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.Phone)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.ShortBio)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.Npn)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.MetaPixelId)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.MetaCapiAccessToken)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.MetaTestEventCode)) score += 1;
        if (profile.BookingEnabled.HasValue) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.MicrosoftBookingsEmbedUrl)) score += 2;
        if (!string.IsNullOrWhiteSpace(profile.FallbackBookingUrl)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.BookingPageIdOrMailbox)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.CalendarUserId)) score += 1;
        if (!string.IsNullOrWhiteSpace(profile.CalendarEmail)) score += 1;
        if (profile.PreferModalOnMobile.HasValue) score += 1;
        if (profile.DisplayOrder.HasValue) score += 1;
        return score;
    }

    private static bool CopyMissingProfileData(Domain.Entities.AgentProfile target, Domain.Entities.AgentProfile source)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(target.AgentUpn) && !string.IsNullOrWhiteSpace(source.AgentUpn))
        {
            target.AgentUpn = source.AgentUpn;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.NormalizedEmail) && !string.IsNullOrWhiteSpace(source.NormalizedEmail))
        {
            target.NormalizedEmail = source.NormalizedEmail;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.FullName) && !string.IsNullOrWhiteSpace(source.FullName))
        {
            target.FullName = source.FullName;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Title) && !string.IsNullOrWhiteSpace(source.Title))
        {
            target.Title = source.Title;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Npn) && !string.IsNullOrWhiteSpace(source.Npn))
        {
            target.Npn = source.Npn;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Phone) && !string.IsNullOrWhiteSpace(source.Phone))
        {
            target.Phone = source.Phone;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.ShortBio) && !string.IsNullOrWhiteSpace(source.ShortBio))
        {
            target.ShortBio = source.ShortBio;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.MetaPixelId) && !string.IsNullOrWhiteSpace(source.MetaPixelId))
        {
            target.MetaPixelId = source.MetaPixelId;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.MetaCapiAccessToken) && !string.IsNullOrWhiteSpace(source.MetaCapiAccessToken))
        {
            target.MetaCapiAccessToken = source.MetaCapiAccessToken;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.MetaTestEventCode) && !string.IsNullOrWhiteSpace(source.MetaTestEventCode))
        {
            target.MetaTestEventCode = source.MetaTestEventCode;
            changed = true;
        }
        if (!target.BookingEnabled.HasValue && source.BookingEnabled.HasValue)
        {
            target.BookingEnabled = source.BookingEnabled;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.MicrosoftBookingsEmbedUrl) && !string.IsNullOrWhiteSpace(source.MicrosoftBookingsEmbedUrl))
        {
            target.MicrosoftBookingsEmbedUrl = source.MicrosoftBookingsEmbedUrl;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.FallbackBookingUrl) && !string.IsNullOrWhiteSpace(source.FallbackBookingUrl))
        {
            target.FallbackBookingUrl = source.FallbackBookingUrl;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.BookingPageIdOrMailbox) && !string.IsNullOrWhiteSpace(source.BookingPageIdOrMailbox))
        {
            target.BookingPageIdOrMailbox = source.BookingPageIdOrMailbox;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.CalendarUserId) && !string.IsNullOrWhiteSpace(source.CalendarUserId))
        {
            target.CalendarUserId = source.CalendarUserId;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.CalendarEmail) && !string.IsNullOrWhiteSpace(source.CalendarEmail))
        {
            target.CalendarEmail = source.CalendarEmail;
            changed = true;
        }
        if (!target.PreferModalOnMobile.HasValue && source.PreferModalOnMobile.HasValue)
        {
            target.PreferModalOnMobile = source.PreferModalOnMobile;
            changed = true;
        }
        if (!target.DisplayOrder.HasValue && source.DisplayOrder.HasValue)
        {
            target.DisplayOrder = source.DisplayOrder;
            changed = true;
        }

        return changed;
    }
}

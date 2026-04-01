using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

/// <summary>
/// Ensures every authenticated agent has an AgentProfile record.
/// Upserts on OID (AgentUserId) as the canonical key and keeps email/name in sync.
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
        var emailNorm = !string.IsNullOrWhiteSpace(email) ? email.Trim().ToLowerInvariant() : null;
        var displayName = BuildDisplayName(user);

        try
        {
            var existing = await _db.AgentProfiles.FirstOrDefaultAsync(a => a.AgentUserId == oid, ct);
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
                // Keep email/name fresh but do not overwrite with empty values.
                if (!string.IsNullOrWhiteSpace(email)) existing.AgentUpn = email;
                if (emailNorm != null) existing.NormalizedEmail = emailNorm;
                if (!string.IsNullOrWhiteSpace(displayName)) existing.FullName = displayName;
                existing.UpdatedUtc = DateTime.UtcNow;
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
}

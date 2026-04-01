using System.Security.Claims;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Services;

public record ImpersonationContext(string AgentUserId, string? AgentEmail, string? AgentName);

/// <summary>
/// Manages founder-only app-context impersonation. Does NOT alter authentication identity.
/// Stores encrypted cookie with target agent OID; validated per-request server-side.
/// </summary>
public class FounderImpersonationService
{
    private const string CookieName = "legend.impersonation";
    private readonly IDataProtector _protector;
    private readonly MasterAppDbContext _db;
    private readonly ILogger<FounderImpersonationService> _logger;

    public FounderImpersonationService(
        IDataProtectionProvider provider,
        MasterAppDbContext db,
        ILogger<FounderImpersonationService> logger)
    {
        _protector = provider.CreateProtector("FounderImpersonation");
        _db = db;
        _logger = logger;
    }

    // Maximum lifetime for an impersonation session. After this the cookie is treated as
    // expired and cleared. 8 hours covers a full working day without requiring re-entry.
    private static readonly TimeSpan ImpersonationTtl = TimeSpan.FromHours(8);

    private static string? GetOid(ClaimsPrincipal user)
        => user.FindFirstValue("oid") ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

    public async Task StartAsync(HttpContext http, ClaimsPrincipal founder, string agentUserId, CancellationToken ct = default)
    {
        AgentPortal.Security.FounderGuard.EnsureFounderOrThrow(founder);

        var oid = (agentUserId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(oid))
            throw new ArgumentException("AgentUserId is required for impersonation.", nameof(agentUserId));

        var payload = JsonSerializer.Serialize(new { agentUserId = oid, ts = DateTimeOffset.UtcNow });
        var protectedValue = _protector.Protect(payload);

        http.Response.Cookies.Append(
            CookieName,
            protectedValue,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/"
            });

        _logger.LogInformation("Founder impersonation START by {FounderOid} -> {AgentOid}", GetOid(founder), oid);
    }

    public Task StopAsync(HttpContext http, ClaimsPrincipal founder)
    {
        AgentPortal.Security.FounderGuard.EnsureFounderOrThrow(founder);
        http.Response.Cookies.Delete(CookieName);
        _logger.LogInformation("Founder impersonation STOP by {FounderOid}", GetOid(founder));
        return Task.CompletedTask;
    }

    public async Task<ImpersonationContext?> GetAsync(HttpContext http, ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!AgentPortal.Security.FounderGuard.IsFounder(user))
        {
            http.Response.Cookies.Delete(CookieName);
            return null;
        }

        if (!http.Request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        string json;
        try
        {
            json = _protector.Unprotect(raw);
        }
        catch
        {
            http.Response.Cookies.Delete(CookieName);
            return null;
        }

        string? agentUserId;
        try
        {
            var doc = JsonDocument.Parse(json);
            agentUserId = doc.RootElement.GetProperty("agentUserId").GetString();

            // Enforce TTL. The ts field is written by StartAsync; a missing or unparseable
            // value is treated as expired so legacy cookies without the field also expire.
            if (doc.RootElement.TryGetProperty("ts", out var tsProp) &&
                tsProp.TryGetDateTimeOffset(out var issuedAt))
            {
                if (DateTimeOffset.UtcNow - issuedAt > ImpersonationTtl)
                {
                    _logger.LogInformation(
                        "Founder impersonation session expired (issued {IssuedAt}, TTL {Ttl}h). Cookie cleared.",
                        issuedAt, ImpersonationTtl.TotalHours);
                    http.Response.Cookies.Delete(CookieName);
                    return null;
                }
            }
            else
            {
                // No parseable timestamp — treat as expired to safely clear legacy cookies.
                _logger.LogWarning("Impersonation cookie missing or unparseable timestamp. Cookie cleared.");
                http.Response.Cookies.Delete(CookieName);
                return null;
            }
        }
        catch
        {
            http.Response.Cookies.Delete(CookieName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(agentUserId))
        {
            http.Response.Cookies.Delete(CookieName);
            return null;
        }

        var profile = await _db.AgentProfiles.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AgentUserId == agentUserId, ct);

        var email = profile?.AgentUpn;
        var name = profile?.FullName ?? profile?.AgentUpn ?? agentUserId;

        return new ImpersonationContext(agentUserId, email, name);
    }
}

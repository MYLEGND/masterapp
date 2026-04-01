using Domain.Entities;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AgentPortal.Services;

public class AssistantContextService
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AssistantContextService> _logger;

    public AssistantContextService(MasterAppDbContext db, ILogger<AssistantContextService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    private static IEnumerable<string> GetEmailCandidates(ClaimsPrincipal user)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool LooksLikeEmail(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.Contains('@');

        var preferredClaims = new[]
        {
            user.FindFirstValue("preferred_username"),
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue("email"),
            user.FindFirstValue("upn"),
            user.FindFirstValue(ClaimTypes.Upn),
            user.FindFirstValue("unique_name")
        };

        foreach (var candidate in preferredClaims)
        {
            var email = Norm(candidate);
            if (LooksLikeEmail(email) && seen.Add(email))
                yield return email;
        }

        foreach (var claim in user.Claims)
        {
            var email = Norm(claim.Value);
            if (LooksLikeEmail(email) && seen.Add(email))
                yield return email;
        }
    }

    public static string? GetRawOid(ClaimsPrincipal user)
    {
        var oid = user.FindFirstValue("oid")
               ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        oid = Norm(oid);
        return string.IsNullOrWhiteSpace(oid) ? null : oid;
    }

    public static bool IsLikelyGuestUser(ClaimsPrincipal user, string? firstPartyTenantId = null, string? firstPartyDomain = null)
    {
        var tid = user.FindFirstValue("tid")
                  ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        if (!string.IsNullOrWhiteSpace(firstPartyTenantId)
            && !string.IsNullOrWhiteSpace(tid)
            && string.Equals(tid.Trim(), firstPartyTenantId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // Same-tenant user; treat as first-party even if other heuristics would flag guest.
            return false;
        }

        if (!string.IsNullOrWhiteSpace(firstPartyDomain))
        {
            var domain = firstPartyDomain.Trim().ToLowerInvariant();
            foreach (var email in GetEmailCandidates(user))
            {
                var parts = email.Split('@');
                if (parts.Length == 2 && parts[1].Equals(domain, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        var candidates = new[]
        {
            user.FindFirstValue("upn"),
            user.FindFirstValue("preferred_username"),
            user.FindFirstValue(ClaimTypes.Upn),
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue("email"),
            user.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn")
        };

        if (candidates.Any(v => !string.IsNullOrWhiteSpace(v) && v.Contains("#EXT#", StringComparison.OrdinalIgnoreCase)))
            return true;

        var idp = user.FindFirstValue("idp")
            ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/identityprovider");

        if (!string.IsNullOrWhiteSpace(idp)
            && !idp.Contains("sts.windows.net", StringComparison.OrdinalIgnoreCase)
            && !idp.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public async Task<AgentAssistant?> GetAssistantRecordAsync(string userOid, bool activeOnly = true)
    {
        userOid = Norm(userOid);
        if (string.IsNullOrWhiteSpace(userOid)) return null;

        try
        {
            return await _db.AgentAssistants
                .FirstOrDefaultAsync(a =>
                    (!activeOnly || a.IsActive) &&
                    a.AssistantUserId != null &&
                    a.AssistantUserId.ToLower() == userOid);
        }
        catch (SqliteException ex) when (IsMissingAssistantsTable(ex))
        {
            _logger.LogWarning(ex, "AgentAssistants table is missing. Assistant scoping is bypassed until migrations are applied.");
            return null;
        }
    }

    public async Task<AgentAssistant?> GetAssistantRecordForUserAsync(ClaimsPrincipal user, bool activeOnly = true)
    {
        var rawOid = GetRawOid(user);
        if (rawOid != null)
        {
            var byOid = await GetAssistantRecordAsync(rawOid, activeOnly);
            if (byOid != null) return byOid;
        }

        try
        {
            foreach (var email in GetEmailCandidates(user))
            {
                var byEmail = await _db.AgentAssistants
                    .FirstOrDefaultAsync(a => (!activeOnly || a.IsActive) && a.Email.ToLower() == email);

                if (byEmail != null) return byEmail;
            }
        }
        catch (SqliteException ex) when (IsMissingAssistantsTable(ex))
        {
            _logger.LogWarning(ex, "AgentAssistants table is missing. Assistant lookup by email is unavailable until migrations are applied.");
        }

        return null;
    }

    public async Task<string> ResolveEffectiveAgentOidAsync(ClaimsPrincipal user)
    {
        var rawOid = GetRawOid(user)
            ?? throw new InvalidOperationException("Missing OID claim.");

        var assistant = await GetAssistantRecordForUserAsync(user, activeOnly: true);
        return assistant?.ParentAgentUserId ?? rawOid;
    }

    public async Task<bool> IsAssistantAsync(ClaimsPrincipal user)
    {
        return await GetAssistantRecordForUserAsync(user, activeOnly: true) != null;
    }

    public async Task<AgentAssistant?> BindAssistantOidIfNeededAsync(ClaimsPrincipal user)
    {
        var rawOid = GetRawOid(user);
        if (rawOid == null) return null;

        var existing = await GetAssistantRecordAsync(rawOid, activeOnly: false);
        if (existing != null) return existing;

        AgentAssistant? record;
        try
        {
            record = null;
            foreach (var email in GetEmailCandidates(user))
            {
                record = await _db.AgentAssistants
                    .FirstOrDefaultAsync(a => a.Email.ToLower() == email);

                if (record != null) break;
            }
        }
        catch (SqliteException ex) when (IsMissingAssistantsTable(ex))
        {
            _logger.LogWarning(ex, "AgentAssistants table is missing. Cannot bind assistant OID until migrations are applied.");
            return null;
        }

        if (record == null) return null;

        record.AssistantUserId = rawOid;
        await _db.SaveChangesAsync();
        return record;
    }

    private static bool IsMissingAssistantsTable(SqliteException ex)
        => ex.SqliteErrorCode == 1
           && ex.Message.Contains("no such table: AgentAssistants", StringComparison.OrdinalIgnoreCase);
}

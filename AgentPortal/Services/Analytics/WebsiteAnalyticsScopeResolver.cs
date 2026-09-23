using AgentPortal.Services.Tracking;
using AgentPortal.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Shared.Auth;

namespace AgentPortal.Services.Analytics;

/// <summary>Canonical portal reporting scope for dashboards, details, exports and AI.</summary>
public sealed class WebsiteAnalyticsScopeResolver(
    EffectiveAgentContext effectiveContext,
    IAgentTrackingService tracking,
    MasterAppDbContext db,
    ILogger logger)
{
    private readonly EffectiveAgentContext _effectiveContext = effectiveContext;
    private readonly IAgentTrackingService _tracking = tracking;
    private readonly MasterAppDbContext _db = db;
    private readonly ILogger _logger = logger;

    public async Task<ScopeContext> ResolveAsync(HttpContext httpContext, Guid? requestedAgentId, bool team = false)
    {
        var isFounder = FounderGuard.IsFounder(httpContext.User);

        // Effective agent (includes View-as-Agent)
        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        var effectiveProfileId = effectiveProfile?.Id;

        if (team && !isFounder)
        {
            _logger.LogWarning("WebsiteAnalytics denied team scope elevation for non-founder caller.");
        }
        else if (team && isFounder)
        {
            return ScopeContext.Global;
        }

        // Founder default on Website Analytics is founder personal unless Global/team is explicitly selected.
        if (isFounder)
        {
            if (requestedAgentId.HasValue) return ScopeContext.ForAgent(requestedAgentId.Value);
            if (_effectiveContext.IsViewingAsAgent)
            {
                return await ResolveEffectiveImpersonatedAgentScopeAsync(httpContext);
            }

            var founderProfile = await GetCallerProfileAsync();
            if (founderProfile != null)
            {
                return ScopeContext.ForAgent(founderProfile.Id);
            }

            return ScopeContext.Global;
        }

        // If founder is impersonating an agent, analytics must scope to that agent.
        // Never fall back to founder scope for view-as-agent requests.
        if (_effectiveContext.IsViewingAsAgent)
        {
            return await ResolveEffectiveImpersonatedAgentScopeAsync(httpContext);
        }

        // Agent (or assistant) uses effective profile
        if (effectiveProfileId.HasValue)
        {
            return ScopeContext.ForAgent(effectiveProfileId.Value);
        }

        _logger.LogWarning("Scope resolution: no agent profile for caller; returning empty scope (no data)");
        return ScopeContext.ForAgent(Guid.Empty); // will match nothing
    }

    private async Task<ScopeContext> ResolveEffectiveImpersonatedAgentScopeAsync(HttpContext httpContext)
    {
        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        var effectiveProfileId = effectiveProfile?.Id;
        if (effectiveProfileId.HasValue)
        {
            return ScopeContext.ForAgent(effectiveProfileId.Value);
        }

        var effectiveOid = (_effectiveContext.EffectiveAgentOid ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(effectiveOid))
        {
            // Fallback path: if a tracking profile is missing, provision one from AgentProfile metadata.
            var byOid = await _tracking.GetByUserIdAsync(effectiveOid);
            if (byOid != null)
            {
                return ScopeContext.ForAgent(byOid.Id);
            }

            var oidLower = effectiveOid.ToLowerInvariant();
            var agentProfile = await _db.AgentProfiles.AsNoTracking()
                .Where(a => a.AgentUserId != null && a.AgentUserId.ToLower() == oidLower)
                .OrderByDescending(a => a.UpdatedUtc)
                .FirstOrDefaultAsync();

            var upn = agentProfile?.AgentUpn
                ?? (httpContext.Items.TryGetValue("ImpersonatedAgentEmail", out var emailObj) ? emailObj as string : null);
            var displayName = agentProfile?.FullName
                ?? (httpContext.Items.TryGetValue("ImpersonatedAgentName", out var nameObj) ? nameObj as string : null);

            if (!string.IsNullOrWhiteSpace(upn))
            {
                var ensured = await _tracking.EnsureProfileAsync(effectiveOid, upn, displayName);
                return ScopeContext.ForAgent(ensured.Id);
            }
        }

        _logger.LogWarning(
            "WebsiteAnalytics scope resolution failed for impersonated agent. effectiveOid={EffectiveOid}. Returning empty scope.",
            _effectiveContext.EffectiveAgentOid ?? "(null)");
        return ScopeContext.ForAgent(Guid.Empty);
    }

    public async Task<Domain.Entities.AgentTrackingProfile?> GetCallerProfileAsync()
    {
        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        if (effectiveProfile != null) return effectiveProfile;

        var upn = _effectiveContext.ActualUserUpn;
        if (!string.IsNullOrWhiteSpace(upn))
        {
            return await _tracking.GetByUpnAsync(upn);
        }
        return null;
    }

}

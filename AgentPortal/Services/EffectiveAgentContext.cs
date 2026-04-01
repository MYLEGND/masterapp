using System.Security.Claims;
using AgentPortal.Security;
using AgentPortal.Services.Tracking;
using Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentPortal.Services;

/// <summary>
/// Central accessor for effective agent identity (real user or impersonated View-as-Agent target).
/// Uses HttpContext.Items[\"EffectiveAgentOid\"] when present; otherwise falls back to the signed-in user.
/// </summary>
public sealed class EffectiveAgentContext
{
    private readonly IHttpContextAccessor _http;
    private readonly IAgentTrackingService _tracking;
    private readonly ILogger<EffectiveAgentContext> _logger;

    private AgentTrackingProfile? _cachedProfile;
    private bool _resolved;

    public EffectiveAgentContext(IHttpContextAccessor http, IAgentTrackingService tracking, ILogger<EffectiveAgentContext> logger)
    {
        _http = http;
        _tracking = tracking;
        _logger = logger;
    }

    private ClaimsPrincipal? User => _http.HttpContext?.User;

    public string? ActualUserOid =>
        User?.FindFirstValue("oid") ??
        User?.FindFirstValue(ClaimTypes.NameIdentifier) ??
        User?.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

    public string? ActualUserUpn =>
        User?.FindFirstValue(ClaimTypes.Email) ??
        User?.FindFirstValue("preferred_username") ??
        User?.FindFirstValue("email") ??
        User?.FindFirstValue("upn") ??
        User?.Identity?.Name;

    public string? EffectiveAgentOid
    {
        get
        {
            if (_http.HttpContext?.Items.TryGetValue("EffectiveAgentOid", out var val) == true && val is string s && !string.IsNullOrWhiteSpace(s))
                return s;
            return ActualUserOid;
        }
    }

    public bool IsViewingAsAgent
    {
        get
        {
            var eff = EffectiveAgentOid;
            var actual = ActualUserOid;
            return FounderGuard.IsFounder(User) && !string.IsNullOrWhiteSpace(eff) && eff != actual;
        }
    }

    public async Task<AgentTrackingProfile?> GetEffectiveTrackingProfileAsync(CancellationToken ct = default)
    {
        if (_resolved) return _cachedProfile;
        _resolved = true;
        var oid = EffectiveAgentOid;
        if (string.IsNullOrWhiteSpace(oid)) return null;
        try
        {
            _cachedProfile = await _tracking.GetByUserIdAsync(oid, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EffectiveAgentContext: failed to load tracking profile for oid {Oid}", oid);
        }
        return _cachedProfile;
    }

    public async Task<Guid?> GetEffectiveTrackingProfileIdAsync(CancellationToken ct = default)
    {
        var prof = await GetEffectiveTrackingProfileAsync(ct);
        return prof?.Id;
    }
}

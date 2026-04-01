using System;
using System.Linq;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

[Authorize]
[Route("WebsiteAnalytics")]
[Route("website-analytics")]
    public class WebsiteAnalyticsController : Controller
    {
        private readonly IAnalyticsQueryService _analytics;
        private readonly Services.Tracking.IAgentTrackingService _tracking;
        private readonly ILogger<WebsiteAnalyticsController> _logger;
        private readonly Infrastructure.Data.MasterAppDbContext _db;
        private readonly string _founderUpn;
        private readonly IConfiguration _config;
        private readonly EffectiveAgentContext _effectiveContext;

        public WebsiteAnalyticsController(IAnalyticsQueryService analytics, Services.Tracking.IAgentTrackingService tracking, ILogger<WebsiteAnalyticsController> logger, Infrastructure.Data.MasterAppDbContext db, IConfiguration config, EffectiveAgentContext effectiveContext)
        {
            _analytics = analytics;
            _tracking = tracking;
            _logger = logger;
            _db = db;
            _founderUpn = config["Founder:Upn"] ?? throw new InvalidOperationException("Founder:Upn configuration is required");
            _config = config;
            _effectiveContext = effectiveContext;
        }

    [HttpGet("")]
    [HttpGet("Index")]
    [HttpGet("/website-analytics")]
    [HttpGet("/website-analytics/index")]
    public async Task<IActionResult> Index()
    {
        var range = TimeRangeRequest.FromPreset("30d");
        var scope = await ResolveScopeAsync(null);
        var summary = await _analytics.GetSummaryAsync(range, scope);
        ViewData["InitialRangePreset"] = range.Preset;
        ViewData["InitialRangeLabel"] = range.Label;
        ViewData["InitialSummaryJson"] = System.Text.Json.JsonSerializer.Serialize(summary);

        var callerProfile = await GetCallerProfileAsync();
        if (callerProfile != null)
        {
            var urls = await _tracking.GetPersonalUrlsAsync(callerProfile);
            ViewData["PersonalLink"] = urls.PrimaryUrl;
            ViewData["PersonalLinkAlt"] = urls.AlternateSlugUrl;
            ViewData["CallerProfileId"] = callerProfile.Id;
        }

        if (FounderGuard.IsFounder(User))
        {
            // Ensure founder personal link is root
            var rootBase = _config["Protect:PublicBaseUrl"] ?? "https://protect.mylegnd.com";
            ViewData["PersonalLink"] = rootBase.EndsWith("/") ? rootBase : rootBase + "/";
            ViewData["PersonalLinkAlt"] = null;

            var agents = await _tracking.GetAllProfilesAsync();
            var agentOptions = new List<object>();
            foreach (var agent in agents)
            {
                var urls = await _tracking.GetPersonalUrlsAsync(agent);
                // Founder should surface root as primary
                var primaryOverride = string.Equals(agent.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase)
                    ? (rootBase.EndsWith("/") ? rootBase : rootBase + "/")
                    : urls.PrimaryUrl;
                agentOptions.Add(new { id = agent.Id, name = agent.DisplayName ?? agent.AgentUpn ?? agent.Slug, slug = agent.Slug, primaryUrl = primaryOverride, altUrl = urls.AlternateSlugUrl });
            }
            ViewData["AgentOptionsJson"] = System.Text.Json.JsonSerializer.Serialize(agentOptions);
        }

        return View();
    }

    // JSON endpoints -------------------------------------------------
    [HttpGet("summary")]
    [HttpGet("/website-analytics/summary")]
    public async Task<IActionResult> Summary([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetSummaryAsync(range, scope);
        return Json(result);
    }

    [HttpGet("traffic")]
    [HttpGet("/website-analytics/traffic")]
    public async Task<IActionResult> Traffic([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetTrafficAsync(range, scope);
        return Json(result);
    }

    [HttpGet("page-performance")]
    [HttpGet("/website-analytics/page-performance")]
    public async Task<IActionResult> PagePerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetPagePerformanceAsync(range, scope);
        return Json(result);
    }

    [HttpGet("cta-performance")]
    [HttpGet("/website-analytics/cta-performance")]
    public async Task<IActionResult> CtaPerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetCtaPerformanceAsync(range, scope);
        return Json(result);
    }

    [HttpGet("quote-funnel")]
    [HttpGet("/website-analytics/quote-funnel")]
    public async Task<IActionResult> QuoteFunnel([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetQuoteFunnelAsync(range, scope);
        return Json(result);
    }

    [HttpGet("conversions")]
    [HttpGet("/website-analytics/conversions")]
    public async Task<IActionResult> Conversions([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetConversionsAsync(range, scope);
        return Json(result);
    }

    [HttpGet("leads")]
    [HttpGet("/website-analytics/leads")]
    public async Task<IActionResult> Leads([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetLeadsAsync(range, scope, 200);
        return Json(result);
    }

    [HttpGet("agent-performance")]
    [HttpGet("/website-analytics/agent-performance")]
    public async Task<IActionResult> AgentPerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] string? orderBy = null, [FromQuery] bool desc = true, [FromQuery] int? take = null, [FromQuery] int? skip = null)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var options = new AnalyticsQueryOptions { OrderBy = orderBy ?? "leads", Desc = desc, Take = take, Skip = skip };
        var result = await _analytics.GetAgentPerformanceAsync(range, ScopeContext.Global, options);
        return Json(result);
    }

    private async Task<ScopeContext> ResolveScopeAsync(Guid? requestedAgentId, bool team = false)
    {
        var isFounder = FounderGuard.IsFounder(User);
        if (team && !isFounder) return ScopeContext.Global; // only founder can request team rollup

        // Effective agent (includes View-as-Agent)
        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        var effectiveProfileId = effectiveProfile?.Id;

        if (team)
        {
            return ScopeContext.Global;
        }

        // If founder is impersonating an agent, use that agent scope
        if (_effectiveContext.IsViewingAsAgent && effectiveProfileId.HasValue)
        {
            return ScopeContext.ForAgent(effectiveProfileId.Value);
        }

        // Founder default (not viewing as agent)
        if (isFounder)
        {
            if (requestedAgentId.HasValue) return ScopeContext.ForAgent(requestedAgentId.Value);
            var founderProfile = await _tracking.GetByUpnAsync(_founderUpn);
            if (founderProfile != null) return ScopeContext.ForAgent(founderProfile.Id);
            if (effectiveProfileId.HasValue) return ScopeContext.ForAgent(effectiveProfileId.Value);
            return ScopeContext.Global;
        }

        // Agent (or assistant) uses effective profile
        if (effectiveProfileId.HasValue)
        {
            return ScopeContext.ForAgent(effectiveProfileId.Value);
        }

        _logger.LogWarning("Scope resolution: no agent profile for caller; returning empty scope (no data)");
        return ScopeContext.ForAgent(Guid.Empty); // will match nothing
    }

    private async Task<Domain.Entities.AgentTrackingProfile?> GetCallerProfileAsync()
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

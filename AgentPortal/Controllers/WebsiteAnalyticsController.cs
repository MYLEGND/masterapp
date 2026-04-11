using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Security;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Controllers;

[Authorize]
[Route("WebsiteAnalytics")]
[Route("website-analytics")]
    public class WebsiteAnalyticsController : Controller
    {
        private readonly IAnalyticsQueryService _analytics;
        private readonly IMetaAdsService _metaAds;
        private readonly IMetaAdsOAuthService _metaAdsOAuth;
        private readonly IMetaAdsConnectionStore _metaAdsConnectionStore;
        private readonly Services.Tracking.IAgentTrackingService _tracking;
        private readonly ILogger<WebsiteAnalyticsController> _logger;
        private readonly Infrastructure.Data.MasterAppDbContext _db;
        private readonly string _founderUpn;
        private readonly IConfiguration _config;
        private readonly EffectiveAgentContext _effectiveContext;

        public WebsiteAnalyticsController(IAnalyticsQueryService analytics, IMetaAdsService metaAds, IMetaAdsOAuthService metaAdsOAuth, IMetaAdsConnectionStore metaAdsConnectionStore, Services.Tracking.IAgentTrackingService tracking, ILogger<WebsiteAnalyticsController> logger, Infrastructure.Data.MasterAppDbContext db, IConfiguration config, EffectiveAgentContext effectiveContext)
        {
            _analytics = analytics;
            _metaAds = metaAds;
            _metaAdsOAuth = metaAdsOAuth;
            _metaAdsConnectionStore = metaAdsConnectionStore;
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

        var isViewingAsAgent = _effectiveContext.IsViewingAsAgent;
        var canViewFounderTeamUi = FounderGuard.IsFounder(User) && !isViewingAsAgent;
        ViewData["CanViewFounderTeamUi"] = canViewFounderTeamUi;
        if (canViewFounderTeamUi)
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

    // ── Behavior Intelligence ─────────────────────────────────────
    [HttpGet("behavior/summary")]
    public async Task<IActionResult> BehaviorSummary([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetEngagementSummaryAsync(range, scope);
        return Json(result);
    }

    [HttpGet("behavior/time-on-page")]
    public async Task<IActionResult> BehaviorTimeOnPage([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetTimeOnPageAsync(range, scope);
        return Json(result);
    }

    [HttpGet("behavior/exit-analysis")]
    public async Task<IActionResult> BehaviorExit([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetExitAnalysisAsync(range, scope);
        return Json(result);
    }

    [HttpGet("behavior/journey")]
    public async Task<IActionResult> BehaviorJourney([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetJourneyAnalysisAsync(range, scope);
        return Json(result);
    }

    [HttpGet("behavior/source-performance")]
    public async Task<IActionResult> BehaviorSourcePerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetSourcePerformanceAsync(range, scope);
        return Json(result);
    }

    [HttpGet("quote-funnel/abandonment")]
    public async Task<IActionResult> QuoteFunnelAbandonment([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetFormAbandonmentAsync(range, scope);
        return Json(result);
    }

    [HttpGet("ai-review-snapshot")]
    [HttpGet("/website-analytics/ai-review-snapshot")]
    public async Task<IActionResult> AiReviewSnapshot([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false)
    {
        try
        {
            var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
            var scope = await ResolveScopeAsync(agentProfileId, team);

            async Task<(T Value, string? Warning)> SafeSnapshotLoadAsync<T>(Func<Task<T>> loader, Func<T> fallbackFactory, string area)
            {
                try
                {
                    return (await loader(), null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI snapshot partial load failed for {Area}.", area);
                    return (fallbackFactory(), $"{area} unavailable due to internal error.");
                }
            }

            var (summary, summaryWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetSummaryAsync(range, scope),
                () => new SummaryKpiDto
                {
                    RangeLabel = range.Label,
                    EnvironmentLabel = "Environment: Mixed/Legacy",
                    IntentDenominatorLabel = "Quote Submits / Quote Starts"
                },
                "Summary metrics");
            var (traffic, trafficWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetTrafficAsync(range, scope),
                () => new TrafficOverviewDto { RangeLabel = range.Label },
                "Traffic metrics");
            var (quote, quoteWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetQuoteFunnelAsync(range, scope),
                () => new QuoteFunnelDto { RangeLabel = range.Label },
                "Quote funnel metrics");
            var (conversions, conversionsWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetConversionsAsync(range, scope),
                () => new ConversionCenterDto { RangeLabel = range.Label },
                "Conversion metrics");
            var (leads, leadsWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetLeadsAsync(range, scope, 200),
                () => new LeadSnapshotDto { RangeLabel = range.Label },
                "Lead snapshot metrics");
            var (pagePerf, pagePerfWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetPagePerformanceAsync(range, scope),
                () => new PagePerformanceDto { RangeLabel = range.Label },
                "Page performance metrics");
            var (ctaPerf, ctaPerfWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetCtaPerformanceAsync(range, scope),
                () => new CtaPerformanceDto { RangeLabel = range.Label },
                "CTA performance metrics");
            var (timeOnPage, timeOnPageWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetTimeOnPageAsync(range, scope),
                () => new TimeOnPageDto { RangeLabel = range.Label },
                "Time-on-page metrics");
            var (exit, exitWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetExitAnalysisAsync(range, scope),
                () => new ExitAnalysisDto { RangeLabel = range.Label },
                "Exit analysis metrics");
            var (source, sourceWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetSourcePerformanceAsync(range, scope),
                () => new SourcePerformanceDto { RangeLabel = range.Label },
                "Source performance metrics");
            var (abandonment, abandonmentWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetFormAbandonmentAsync(range, scope),
                () => new FormAbandonmentDto { RangeLabel = range.Label },
                "Form abandonment metrics");
            MetaCampaignsDto? metaCampaigns = null;
            string? activeCampaignWarning = null;

            try
            {
                metaCampaigns = await _metaAds.GetCampaignsAsync(range, scope, HttpContext.RequestAborted);
            }
            catch (InvalidOperationException ex)
            {
                activeCampaignWarning = $"Active campaign performance unavailable: {ex.Message}";
                _logger.LogInformation(ex, "AI snapshot active campaign section unavailable due to Meta connection/configuration.");
            }
            catch (Exception ex)
            {
                activeCampaignWarning = "Active campaign performance unavailable due to Meta campaigns fetch error.";
                _logger.LogWarning(ex, "AI snapshot active campaign section failed unexpectedly.");
            }

            var generatedLocal = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
            string scopeLabel;
            string? scopeWarning = null;
            try
            {
                scopeLabel = await ResolveScopeLabelAsync(scope, team);
            }
            catch (Exception ex)
            {
                scopeLabel = "Current Scope";
                scopeWarning = "Scope label unavailable due to internal error.";
                _logger.LogWarning(ex, "AI snapshot scope label resolution failed.");
            }
            var rangeLabel = !string.IsNullOrWhiteSpace(summary.RangeLabel) ? summary.RangeLabel : range.Label;

            var warnings = BuildSnapshotWarnings(summary);
            var partialWarnings = new[]
            {
                summaryWarning, trafficWarning, quoteWarning, conversionsWarning, leadsWarning,
                pagePerfWarning, ctaPerfWarning, timeOnPageWarning, exitWarning, sourceWarning,
                abandonmentWarning, scopeWarning
            }.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).ToList();
            if (partialWarnings.Count > 0)
                warnings.AddRange(partialWarnings);
            if (!string.IsNullOrWhiteSpace(activeCampaignWarning))
                warnings.Add(activeCampaignWarning);
            var snapshotText = BuildAiReviewSnapshotText(
                metaCampaigns,
                summary,
                traffic,
                quote,
                conversions,
                leads,
                pagePerf,
                ctaPerf,
                timeOnPage,
                exit,
                source,
                abandonment,
                generatedLocal,
                scopeLabel,
                rangeLabel,
                warnings);

            return Json(new AiReviewSnapshotDto
            {
                SnapshotText = snapshotText,
                GeneratedAtLocal = generatedLocal,
                ScopeLabel = scopeLabel,
                RangeLabel = rangeLabel,
                Warnings = warnings
            });
        }
        catch (Exception ex)
        {
            var requestId = HttpContext.TraceIdentifier;
            TimeRangeRequest fallbackRange;
            try
            {
                fallbackRange = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
            }
            catch
            {
                fallbackRange = TimeRangeRequest.FromPreset("30d");
            }
            var fallbackGenerated = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
            var warnings = new List<string>
            {
                $"Snapshot generation failed. requestId={requestId}",
                "Check Azure Log Stream / Application Logs for the full exception."
            };
            _logger.LogError(ex, "AI snapshot endpoint failed. requestId={RequestId}", requestId);

            return Json(new AiReviewSnapshotDto
            {
                SnapshotText = BuildAiReviewSnapshotFailureText(fallbackGenerated, fallbackRange.Label, warnings),
                GeneratedAtLocal = fallbackGenerated,
                ScopeLabel = "Current Scope",
                RangeLabel = fallbackRange.Label,
                Warnings = warnings
            });
        }
    }

    [HttpGet("meta-campaigns")]
    [HttpGet("/website-analytics/meta-campaigns")]
    public async Task<IActionResult> MetaCampaigns([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null)
    {
        try
        {
            var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc);
            var scope = await ResolveScopeAsync(agentProfileId, team: false);
            var result = await _metaAds.GetCampaignsAsync(range, scope, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Meta campaigns request failed due to configuration/scope constraints.");
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("meta-connect")]
    [HttpGet("/website-analytics/meta-connect")]
    public async Task<IActionResult> MetaConnect([FromQuery] string? returnUrl = null)
    {
        var target = string.IsNullOrWhiteSpace(returnUrl) ? "/WebsiteAnalytics/Index" : returnUrl!;
        try
        {
            var agentId = await ResolveMetaConnectionAgentIdAsync();
            if (!agentId.HasValue || agentId.Value == Guid.Empty)
                return Redirect($"{target}?meta=error&message={Uri.EscapeDataString("Unable to resolve agent context for Meta Ads connection.")}");

            var connectUrl = _metaAdsOAuth.BuildConnectUrl(agentId.Value, returnUrl);
            return Redirect(connectUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Meta connect request failed.");
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
    }

    [AllowAnonymous]
    [HttpGet("meta-callback")]
    public async Task<IActionResult> MetaCallback([FromQuery] string? code = null, [FromQuery] string? state = null, [FromQuery] string? error = null, [FromQuery(Name = "error_description")] string? errorDescription = null)
    {
        var target = "/WebsiteAnalytics/Index";
        if (!string.IsNullOrWhiteSpace(error))
        {
            var msg = string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription;
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString(msg)}");
        }

        try
        {
            await _metaAdsOAuth.CompleteCallbackAsync(code ?? string.Empty, state ?? string.Empty, HttpContext.RequestAborted);
            return Redirect($"{target}?meta=connected");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Meta callback failed.");
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString(ex.Message)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meta callback failed with unexpected error.");
            return Redirect($"{target}?meta=error&message={Uri.EscapeDataString("Meta connection failed unexpectedly. Please try again.")}");
        }
    }

    [HttpGet("meta-connection-status")]
    [HttpGet("/website-analytics/meta-connection-status")]
    public async Task<IActionResult> MetaConnectionStatus()
    {
        var agentId = await ResolveMetaConnectionAgentIdAsync();
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
        {
            return Json(new MetaAdsConnectionStatusDto
            {
                Connected = false,
                AgentTrackingProfileId = null
            });
        }

        var record = await _metaAdsConnectionStore.GetAsync(agentId.Value, HttpContext.RequestAborted);
        if (record == null)
        {
            return Json(new MetaAdsConnectionStatusDto
            {
                Connected = false,
                AgentTrackingProfileId = agentId
            });
        }

        return Json(new MetaAdsConnectionStatusDto
        {
            Connected = true,
            AgentTrackingProfileId = agentId,
            AccountId = record.AccountId,
            AccountName = record.AccountName,
            MetaUserName = record.MetaUserName,
            ConnectedUtc = record.ConnectedUtc,
            AccessTokenExpiresUtc = record.AccessTokenExpiresUtc
        });
    }

    [HttpPost("meta-disconnect")]
    [ValidateAntiForgeryToken]
    [HttpPost("/website-analytics/meta-disconnect")]
    public async Task<IActionResult> MetaDisconnect()
    {
        var agentId = await ResolveMetaConnectionAgentIdAsync();
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
            return BadRequest(new { message = "Unable to resolve agent context for Meta Ads disconnect." });

        await _metaAdsConnectionStore.DeleteAsync(agentId.Value, HttpContext.RequestAborted);
        return Json(new { ok = true });
    }

    private async Task<ScopeContext> ResolveScopeAsync(Guid? requestedAgentId, bool team = false)
    {
        var isFounder = FounderGuard.IsFounder(User);

        // Effective agent (includes View-as-Agent)
        var effectiveProfile = await _effectiveContext.GetEffectiveTrackingProfileAsync();
        var effectiveProfileId = effectiveProfile?.Id;

        // If founder is impersonating an agent, analytics must scope to that agent.
        // Never fall back to founder scope for view-as-agent requests.
        if (_effectiveContext.IsViewingAsAgent)
        {
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
                    ?? (HttpContext.Items.TryGetValue("ImpersonatedAgentEmail", out var emailObj) ? emailObj as string : null);
                var displayName = agentProfile?.FullName
                    ?? (HttpContext.Items.TryGetValue("ImpersonatedAgentName", out var nameObj) ? nameObj as string : null);

                if (!string.IsNullOrWhiteSpace(upn))
                {
                    var ensured = await _tracking.EnsureProfileAsync(effectiveOid, upn, displayName);
                    return ScopeContext.ForAgent(ensured.Id);
                }
            }

            _logger.LogWarning(
                "WebsiteAnalytics scope resolution failed for impersonated agent. effectiveOid={EffectiveOid}. Returning empty scope.",
                _effectiveContext.EffectiveAgentOid ?? "(null)");
            return ScopeContext.ForAgent(Guid.Empty); // no data, never founder fallback in impersonation mode
        }

        if (team && !isFounder)
        {
            _logger.LogWarning("WebsiteAnalytics denied team scope elevation for non-founder caller.");
        }
        else if (team && isFounder)
        {
            return ScopeContext.Global;
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

    private async Task<Guid?> ResolveMetaConnectionAgentIdAsync()
    {
        var scope = await ResolveScopeAsync(null, team: false);
        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue && scope.AgentTrackingProfileId.Value != Guid.Empty)
            return scope.AgentTrackingProfileId.Value;

        var caller = await GetCallerProfileAsync();
        return caller?.Id;
    }

    private async Task<string> ResolveScopeLabelAsync(ScopeContext scope, bool team)
    {
        if (team && FounderGuard.IsFounder(User))
            return "Founder Team";

        if (scope.ScopeType == ScopeType.Global)
            return FounderGuard.IsFounder(User) ? "Founder Global" : "Global";

        var agentId = scope.AgentTrackingProfileId;
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
            return "Agent Scope";

        var profile = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.Id == agentId.Value)
            .Select(p => new { p.DisplayName, p.AgentUpn, p.Slug })
            .FirstOrDefaultAsync();

        if (profile == null)
            return "Agent Scope";

        var agentName = profile.DisplayName ?? profile.AgentUpn ?? profile.Slug;
        return string.IsNullOrWhiteSpace(agentName) ? "Agent Scope" : $"Agent: {agentName}";
    }

    private static List<string> BuildSnapshotWarnings(SummaryKpiDto summary)
    {
        var warnings = new List<string>();
        if (summary.SessionLowSample)
            warnings.Add("Session conversion is based on a low sample size.");
        if (summary.IntentLowSample)
            warnings.Add("Intent conversion is based on a low sample size.");
        if (string.Equals(summary.EnvironmentLabel, "Environment: Mixed/Legacy", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Environment filter is Mixed/Legacy; confirm production-only filtering before high-stakes decisions.");
        return warnings;
    }

    private static string BuildAiReviewSnapshotFailureText(string generatedAtLocal, string rangeLabel, IReadOnlyCollection<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SECTION A — HEADER");
        sb.AppendLine("WEBSITE ANALYTICS AI REVIEW SNAPSHOT");
        sb.AppendLine($"Generated: {generatedAtLocal} (server local time)");
        sb.AppendLine($"Range: {rangeLabel}");
        sb.AppendLine("Scope: Current Scope");
        sb.AppendLine();
        sb.AppendLine("SECTION B — ACTIVE CAMPAIGN PERFORMANCE");
        sb.AppendLine("No active campaigns in range.");
        sb.AppendLine();
        sb.AppendLine("SECTION I — DATA QUALITY / CONTEXT NOTES");
        sb.AppendLine("- Snapshot generation encountered an internal error.");
        if (warnings.Any())
        {
            sb.AppendLine("- Current warnings:");
            foreach (var warning in warnings)
                sb.AppendLine($"  - {warning}");
        }
        sb.AppendLine();
        sb.AppendLine("SECTION J — CHATGPT COPY PROMPT FOOTER");
        sb.AppendLine("CHATGPT ANALYSIS REQUEST");
        sb.AppendLine("Analyze this snapshot and identify likely causes of tracking/reporting issues.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildAiReviewSnapshotText(
        MetaCampaignsDto? metaCampaigns,
        SummaryKpiDto summary,
        TrafficOverviewDto traffic,
        QuoteFunnelDto quote,
        ConversionCenterDto conversions,
        LeadSnapshotDto leads,
        PagePerformanceDto pagePerf,
        CtaPerformanceDto ctaPerf,
        TimeOnPageDto timeOnPage,
        ExitAnalysisDto exit,
        SourcePerformanceDto source,
        FormAbandonmentDto abandonment,
        string generatedAtLocal,
        string scopeLabel,
        string rangeLabel,
        IReadOnlyCollection<string> warnings)
    {
        var sb = new StringBuilder();

        void Line(string value = "") => sb.AppendLine(value);

        static string Safe(string? value, string fallback = "—") =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        static string Pct(decimal? value) =>
            value.HasValue ? $"{value.Value:0.##}%" : "—";

        static string Money(decimal value) => $"${value.ToString("0.00", CultureInfo.InvariantCulture)}";

        static string Whole(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        static string Duration(double ms)
        {
            if (ms <= 0) return "—";
            var span = TimeSpan.FromMilliseconds(ms);
            if (span.TotalMinutes < 1) return $"{Math.Round(span.TotalSeconds)}s";
            return $"{(int)span.TotalMinutes}m {span.Seconds:00}s";
        }

        static List<KeyCountDto> TopKeyCounts(IEnumerable<KeyCountDto>? items, int take = 5)
        {
            return (items ?? Enumerable.Empty<KeyCountDto>())
                .OrderByDescending(x => x.Count)
                .Take(take)
                .ToList();
        }

        void AddKeyCountBlock(string title, IEnumerable<KeyCountDto>? rows, int take = 5)
        {
            Line(title);
            var top = TopKeyCounts(rows, take);
            if (!top.Any())
            {
                Line("No data in range.");
                return;
            }

            foreach (var row in top)
                Line($"- {Safe(row.Key)} ({row.Count})");
        }

        Line("SECTION A — HEADER");
        Line("WEBSITE ANALYTICS AI REVIEW SNAPSHOT");
        Line($"Generated: {generatedAtLocal} (server local time)");
        Line($"Range: {rangeLabel}");
        Line($"Scope: {scopeLabel}");
        Line();

        var activeCampaigns = (metaCampaigns?.Rows ?? new List<MetaCampaignRow>())
            .Where(r => string.Equals(r.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Spend)
            .ThenByDescending(r => r.Impressions)
            .ThenBy(r => r.CampaignName)
            .ToList();

        Line("SECTION B — ACTIVE CAMPAIGN PERFORMANCE");
        Line($"Total active campaigns in range: {activeCampaigns.Count}");
        Line($"Total active campaign spend in range: {Money(activeCampaigns.Sum(x => x.Spend))}");
        if (activeCampaigns.Count == 0)
        {
            Line("- No active campaigns in range.");
        }
        else
        {
            foreach (var c in activeCampaigns)
            {
                Line($"- {Safe(c.CampaignName)} | {Safe(c.Status)} | {Safe(c.Objective)} | spend {Money(c.Spend)} | impr {Whole(c.Impressions)} | reach {Whole(c.Reach)} | clicks {Whole(c.Clicks)} | CTR {c.Ctr:0.##}% | CPC {Money(c.Cpc)} | CPM {Money(c.Cpm)} | leads {Whole(c.Leads)}");
            }

            var bestCtr = activeCampaigns
                .OrderByDescending(x => x.Ctr)
                .ThenByDescending(x => x.Impressions)
                .FirstOrDefault();
            if (bestCtr != null)
                Line($"Best CTR campaign: {Safe(bestCtr.CampaignName)} ({bestCtr.Ctr:0.##}%)");

            var lowestCpc = activeCampaigns
                .Where(x => x.Cpc > 0)
                .OrderBy(x => x.Cpc)
                .ThenByDescending(x => x.Clicks)
                .FirstOrDefault();
            if (lowestCpc != null)
                Line($"Lowest CPC campaign: {Safe(lowestCpc.CampaignName)} ({Money(lowestCpc.Cpc)})");
        }
        Line();

        Line("SECTION C — TRAFFIC HEALTH");
        Line($"Page Views: {summary.PageViews}");
        Line($"Unique Visitors: {summary.UniqueVisitors}");
        Line($"Sessions: {summary.Sessions}");
        AddKeyCountBlock("Top Pages (Top 5):", traffic.TopPages, 5);
        AddKeyCountBlock("Entry Pages (Top 5):", traffic.EntryPages, 5);
        AddKeyCountBlock("Top Sources (Top 5):", traffic.TopSources, 5);
        AddKeyCountBlock("Top Campaigns (Top 5):", traffic.TopCampaigns, 5);
        Line();

        Line("SECTION D — FUNNEL HEALTH");
        Line($"Quote Starts: {quote.QuoteStarts}");
        Line($"Quote Form Starts: {quote.QuoteFormStarts}");
        Line($"Successful Quote Submits: {quote.QuoteFormSubmits}");
        Line($"Leads: {leads.Total}");
        Line($"Intent Conversion: {(summary.IntentAvailable ? Pct(summary.IntentConversionRate) : "—")}");
        Line($"Session Conversion: {Pct(summary.SessionConversionRate)}");
        Line($"Total Conversions: {conversions.TotalConversions}");
        Line();

        var leadPages = (pagePerf.Rows ?? new List<PagePerformanceRow>())
            .Where(r => r.Leads > 0)
            .OrderByDescending(r => r.Leads)
            .Take(5)
            .ToList();

        Line("SECTION E — LEAD PICTURE");
        Line($"Total Leads in Range: {leads.Total}");
        Line("Lead volume by source page (Top 5):");
        if (leadPages.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in leadPages)
                Line($"- {Safe(row.PageKey)} ({row.Leads})");
        }
        Line(leads.Total > 0
            ? $"Recent lead activity summary: {leads.Total} leads captured in this range."
            : "Recent lead activity summary: No leads in range.");
        Line($"Top lead source page: {(leadPages.Count > 0 ? $"{Safe(leadPages[0].PageKey)} ({leadPages[0].Leads})" : "No data in range.")}");
        Line();

        Line("SECTION F — PAGE + CTA PERFORMANCE");
        Line($"Top Page: {Safe(summary.TopPage)}");
        Line($"Top CTA: {Safe(summary.TopCta)}");
        Line("Top 5 page performance rows:");
        var topPagesPerf = (pagePerf.Rows ?? new List<PagePerformanceRow>()).Take(5).ToList();
        if (topPagesPerf.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in topPagesPerf)
                Line($"- {Safe(row.PageKey)} | views {row.Views} | cta clicks {row.CtaClicks} | leads {row.Leads} | conv {row.ConversionRate:0.##}%");
        }
        Line("Top 5 CTA performance rows:");
        var topCtasPerf = (ctaPerf.Rows ?? new List<CtaPerformanceRow>()).Take(5).ToList();
        if (topCtasPerf.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in topCtasPerf)
                Line($"- {Safe(row.PageKey)} / {Safe(row.ElementKey)} | clicks {row.Clicks}");
        }
        Line();

        var topSources = TopKeyCounts(traffic.TopSources, 5);
        var topCampaigns = TopKeyCounts(traffic.TopCampaigns, 5);
        var topSourceTotal = topSources.Sum(x => x.Count);
        var topCampaignTotal = topCampaigns.Sum(x => x.Count);
        var topSourceLeadCount = topSources.Any() ? topSources[0].Count : 0;
        var topCampaignLeadCount = topCampaigns.Any() ? topCampaigns[0].Count : 0;
        var topSourceShare = topSourceTotal > 0 ? Math.Round((decimal)topSourceLeadCount / topSourceTotal * 100, 2) : 0;
        var topCampaignShare = topCampaignTotal > 0 ? Math.Round((decimal)topCampaignLeadCount / topCampaignTotal * 100, 2) : 0;

        Line("SECTION G — CAMPAIGN / SOURCE READ");
        if (topSources.Any())
        {
            Line($"Top source by events: {Safe(topSources[0].Key)} ({topSources[0].Count})");
            Line($"Top source concentration (within top source set): {topSourceShare:0.##}%");
        }
        else
        {
            Line("Top source by events: No data in range.");
        }
        if (topCampaigns.Any())
        {
            Line($"Top campaign by events: {Safe(topCampaigns[0].Key)} ({topCampaigns[0].Count})");
            Line($"Top campaign concentration (within top campaign set): {topCampaignShare:0.##}%");
        }
        else
        {
            Line("Top campaign by events: No data in range.");
        }

        var sourceRows = (source.Rows ?? new List<SourcePerformanceRow>()).Take(3).ToList();
        Line("Best performing source rows (Top 3 by sessions):");
        if (sourceRows.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in sourceRows)
                Line($"- {Safe(row.Source)} | sessions {row.Sessions} | leads {row.VerifiedLeads} | session conv {row.SessionConversionRate:0.##}%");
        }
        Line();

        Line("SECTION H — BEHAVIOR SIGNALS (DIRECTIONAL)");
        Line("Avg Time on Top Pages (Top 5):");
        var dwellRows = (timeOnPage.LongestAvgDwell ?? new List<DwellPageRow>()).Take(5).ToList();
        if (dwellRows.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in dwellRows)
                Line($"- {Safe(row.PageKey)} | avg dwell {Duration(row.AvgDwellMs)}");
        }
        Line("Exit Analysis (Top 3 exit pages):");
        var exitRows = (exit.TopExitPages ?? new List<ExitPageRow>()).Take(3).ToList();
        if (exitRows.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in exitRows)
                Line($"- {Safe(row.PageKey)} | exits {row.Exits} | exit rate {row.ExitRate:0.##}%");
        }
        Line("Form Abandonment Summary:");
        var abandonSummary = (abandonment.Summary ?? new List<FormAbandonSummaryRow>()).Take(3).ToList();
        if (abandonSummary.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var row in abandonSummary)
                Line($"- {Safe(row.QuoteType)} | abandons {row.Abandons} | abandon rate {row.AbandonRate:0.##}%");
        }
        Line("Top Abandoned Fields:");
        var topFields = (abandonment.TopAbandonedFields ?? new List<TopAbandonedFieldRow>()).Take(5).ToList();
        if (topFields.Count == 0)
        {
            Line("No data in range.");
        }
        else
        {
            foreach (var field in topFields)
                Line($"- {Safe(field.FieldName)} ({field.AbandonCount})");
        }
        Line();

        Line("SECTION I — DATA QUALITY / CONTEXT NOTES");
        Line("- Metrics reflect the currently selected range and current scope.");
        Line("- Behavior signals are directional and should be interpreted with context.");
        Line("- Snapshot excludes sensitive lead details.");
        Line($"- Production/local filtering follows current analytics configuration: {summary.EnvironmentLabel}.");
        Line("- Use this summary together with campaign context and recent page changes.");
        if (warnings.Any())
        {
            Line("- Current warnings:");
            foreach (var warning in warnings)
                Line($"  - {warning}");
        }
        Line();

        Line("SECTION J — CHATGPT COPY PROMPT FOOTER");
        Line("CHATGPT ANALYSIS REQUEST");
        Line("Analyze this website and ad performance snapshot.");
        Line("Identify:");
        Line("1. what is working");
        Line("2. what is underperforming");
        Line("3. likely causes");
        Line("4. the top 3 priorities");
        Line("5. what should be changed now");
        Line("6. what should be monitored longer before changing");
        Line("7. whether the data suggests a website issue, ad issue, funnel issue, traffic quality issue, or tracking issue");
        Line();
        Line("Provide a blunt, practical breakdown with priority order.");

        return sb.ToString().TrimEnd();
    }
}

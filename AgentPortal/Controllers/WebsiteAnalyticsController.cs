using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Data;
using System.Data.Common;
using System.Text;
using AgentPortal.Models.Analytics;
using AgentPortal.Services.Analytics;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Auth;

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
        private readonly IMetaSignalAnalyticsService _metaSignalAnalytics;
        private readonly ILandingRouteDiscoveryService _landingRouteDiscovery;
        private readonly ILogger<WebsiteAnalyticsController> _logger;
        private readonly Infrastructure.Data.MasterAppDbContext _db;
        private readonly string _founderUpn;
        private readonly IConfiguration _config;
        private readonly EffectiveAgentContext _effectiveContext;

        public WebsiteAnalyticsController(IAnalyticsQueryService analytics, IMetaAdsService metaAds, IMetaAdsOAuthService metaAdsOAuth, IMetaAdsConnectionStore metaAdsConnectionStore, Services.Tracking.IAgentTrackingService tracking, IMetaSignalAnalyticsService metaSignalAnalytics, ILandingRouteDiscoveryService landingRouteDiscovery, ILogger<WebsiteAnalyticsController> logger, Infrastructure.Data.MasterAppDbContext db, IConfiguration config, EffectiveAgentContext effectiveContext)
        {
            _analytics = analytics;
            _metaAds = metaAds;
            _metaAdsOAuth = metaAdsOAuth;
            _metaAdsConnectionStore = metaAdsConnectionStore;
            _tracking = tracking;
            _metaSignalAnalytics = metaSignalAnalytics;
            _landingRouteDiscovery = landingRouteDiscovery;
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
    public async Task<IActionResult> Index([FromQuery] Guid? agentProfileId = null, [FromQuery] string? preset = null, [FromQuery] DateTime? fromUtc = null, [FromQuery] DateTime? toUtc = null)
    {
        var viewerTimeZone = GetViewerTimeZone();
        preset = string.IsNullOrWhiteSpace(preset) ? "today" : preset;
        TimeRangeRequest range;
        try
        {
            range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, viewerTimeZone);
        }
        catch (ArgumentException)
        {
            range = TimeRangeRequest.FromPreset("today", null, null, viewerTimeZone);
        }

        var scope = await ResolveScopeAsync(agentProfileId);
        var summary = await _analytics.GetSummaryAsync(range, scope);
        summary.ScopeLabel = await ResolveScopeLabelAsync(scope, team: false);
        ViewData["InitialRangePreset"] = range.Preset;
        ViewData["InitialRangeLabel"] = range.Label;
        ViewData["InitialRangeFrom"] = range.Preset == "custom"
            ? TimeZoneInfo.ConvertTimeFromUtc(range.FromUtc, range.ViewerTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        ViewData["InitialRangeTo"] = range.Preset == "custom"
            ? TimeZoneInfo.ConvertTimeFromUtc(range.ToUtc, range.ViewerTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        ViewData["InitialSummaryJson"] = System.Text.Json.JsonSerializer.Serialize(summary);
        ViewData["InitialScopeLabel"] = summary.ScopeLabel;
        ViewData["InitialScopeProfileId"] = scope.ScopeType == ScopeType.Agent
            ? scope.AgentTrackingProfileId
            : null;
        var landingRoutes = _landingRouteDiscovery.GetAllRoutes();
        ViewData["LandingRoutesBaseUrl"] = _landingRouteDiscovery.GetBaseUrl();
        ViewData["LandingRoutesJson"] = System.Text.Json.JsonSerializer.Serialize(
            landingRoutes,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        var callerProfile = await GetCallerProfileAsync();
        if (callerProfile != null)
        {
            var urls = await _tracking.GetPersonalUrlsAsync(callerProfile);
            ViewData["PersonalLink"] = urls.PrimaryUrl;
            ViewData["PersonalLinkAlt"] = urls.AlternateSlugUrl;
            ViewData["CallerProfileId"] = callerProfile.Id;
        }

        var canViewFounderTeamUi = FounderGuard.IsFounder(User);
        var canDeleteAnalyticsLeads = CanDeleteAnalyticsLeads();
        ViewData["CanViewFounderTeamUi"] = canViewFounderTeamUi;
        ViewData["CanDeleteAnalyticsLeads"] = canDeleteAnalyticsLeads;
        if (canViewFounderTeamUi)
        {
            // Ensure founder personal link is root
            var rootBase = _landingRouteDiscovery.GetBaseUrl();
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
    private TimeZoneInfo GetViewerTimeZone()
    {
        // 1. Try IANA or Windows timezone ID (e.g. "America/Phoenix").
        //    TimeZoneInfo.FindSystemTimeZoneById accepts both IANA and Windows IDs on .NET 6+.
        if (Request.Query.TryGetValue("timezoneId", out var tzIdRaw))
        {
            var tzId = tzIdRaw.ToString().Trim();
            if (!string.IsNullOrEmpty(tzId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(tzId); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
        }

        // 2. Fall back to browser UTC offset (minutes west of UTC — positive for UTC-7).
        //    CreateCustomTimeZone expects offset FROM UTC, so invert the sign.
        if (Request.Query.TryGetValue("timezoneOffsetMinutes", out var offsetRaw) &&
            int.TryParse(offsetRaw, out var offsetMinutes) &&
            offsetMinutes >= -840 && offsetMinutes <= 840)
        {
            try
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    $"viewer-offset-{offsetMinutes}",
                    TimeSpan.FromMinutes(-offsetMinutes),
                    "Viewer Local",
                    "Viewer Local");
            }
            catch { }
        }

        // 3. Safe fallback: UTC.
        return TimeZoneInfo.Utc;
    }

    [HttpGet("summary")]
    [HttpGet("/website-analytics/summary")]
    public async Task<IActionResult> Summary([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetSummaryAsync(range, scope, trafficType);
        result.ScopeLabel = await ResolveScopeLabelAsync(scope, team);
        return Json(result);
    }

    [HttpGet("DeviceIntelligence")]
    public async Task<IActionResult> DeviceIntelligence([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetDeviceIntelligenceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("traffic")]
    public async Task<IActionResult> Traffic([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetTrafficAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("page-performance")]
    [HttpGet("/website-analytics/page-performance")]
    public async Task<IActionResult> PagePerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetPagePerformanceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("cta-performance")]
    [HttpGet("/website-analytics/cta-performance")]
    public async Task<IActionResult> CtaPerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetCtaPerformanceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("quote-funnel")]
    [HttpGet("/website-analytics/quote-funnel")]
    public async Task<IActionResult> QuoteFunnel([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetQuoteFunnelAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("marketing-health")]
    [HttpGet("/website-analytics/marketing-health")]
    public async Task<IActionResult> MarketingHealth([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetMarketingHealthAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("conversions")]
    [HttpGet("/website-analytics/conversions")]
    public async Task<IActionResult> Conversions([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] int recentTake = 100)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetConversionsAsync(range, scope, trafficType, recentTake);
        return Json(result);
    }

    [HttpGet("leads")]
    [HttpGet("/website-analytics/leads")]
    public async Task<IActionResult> Leads([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All, [FromQuery] int limit = 200)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetLeadsAsync(range, scope, trafficType, limit);
        return Json(result);
    }

    [HttpGet("meta-signal")]
    [HttpGet("/website-analytics/meta-signal")]
    public async Task<IActionResult> MetaSignal(
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false,
        [FromQuery] TrafficType trafficType = TrafficType.All,
        [FromQuery] string? quoteType = null,
        [FromQuery] string? campaign = null,
        [FromQuery] string? pageMode = null,
        [FromQuery] string? scoreTier = null)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _metaSignalAnalytics.GetDashboardAsync(range, scope, trafficType, quoteType, campaign, pageMode, scoreTier, HttpContext.RequestAborted);
        result.ScopeLabel = await ResolveScopeLabelAsync(scope, team);
        return Json(result);
    }

    [HttpPost("DeleteLead")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLead([FromBody] DeleteLeadRequest? request)
    {
        if (!CanDeleteAnalyticsLeads())
            return Forbid();

        if (request == null || request.LeadId == Guid.Empty)
            return BadRequest(new { message = "A valid leadId is required." });

        try
        {
            var actorId = (User.GetStableUserId() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                actorId = (User.FindFirstValue(ClaimTypes.Email)
                    ?? User.FindFirstValue("preferred_username")
                    ?? User.FindFirstValue("upn")
                    ?? User.Identity?.Name
                    ?? "unknown").Trim();
            }

            if (actorId.Length > 200)
                actorId = actorId[..200];

            var reason = string.IsNullOrWhiteSpace(request.Reason)
                ? "Test lead cleanup"
                : request.Reason.Trim();
            if (reason.Length > 500)
                reason = reason[..500];

            var cancellationToken = HttpContext.RequestAborted;
            var leadColumns = await GetWebsiteLeadColumnSetAsync(cancellationToken);
            var supportsSoftDelete = leadColumns.Contains("IsDeleted");
            var provider = _db.Database.ProviderName ?? string.Empty;
            var isSqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose)
                await conn.OpenAsync(cancellationToken);

            try
            {
                var lead = await FindLeadAsync(conn, request.LeadId, supportsSoftDelete, isSqlite, cancellationToken);
                if (lead == null)
                    return NotFound(new { message = "Lead not found." });

                if (lead.IsDeleted)
                {
                    return Json(new
                    {
                        ok = true,
                        alreadyDeleted = true,
                        leadId = lead.LeadId
                    });
                }

                DateTime? deletedAtUtc = null;
                string? deleteReason = null;

                if (supportsSoftDelete)
                {
                    await using var updateCmd = conn.CreateCommand();
                    var assignments = new List<string>
                    {
                        $"{QuoteIdentifier("IsDeleted", isSqlite)} = @isDeleted"
                    };
                    AddParameter(updateCmd, "@isDeleted", isSqlite ? 1 : true);

                    if (leadColumns.Contains("DeletedAtUtc"))
                    {
                        deletedAtUtc = DateTime.UtcNow;
                        assignments.Add($"{QuoteIdentifier("DeletedAtUtc", isSqlite)} = @deletedAtUtc");
                        AddParameter(updateCmd, "@deletedAtUtc", deletedAtUtc.Value);
                    }

                    if (leadColumns.Contains("DeletedByUserId"))
                    {
                        assignments.Add($"{QuoteIdentifier("DeletedByUserId", isSqlite)} = @deletedByUserId");
                        AddParameter(updateCmd, "@deletedByUserId", actorId);
                    }

                    if (leadColumns.Contains("DeleteReason"))
                    {
                        deleteReason = reason;
                        assignments.Add($"{QuoteIdentifier("DeleteReason", isSqlite)} = @deleteReason");
                        AddParameter(updateCmd, "@deleteReason", reason);
                    }

                    updateCmd.CommandText = $"""
                        UPDATE {QuoteIdentifier("WebsiteLeads", isSqlite)}
                        SET {string.Join(", ", assignments)}
                        WHERE {QuoteIdentifier("Id", isSqlite)} = @id
                        """;
                    AddParameter(updateCmd, "@id", lead.Id);
                    await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    await using var deleteCmd = conn.CreateCommand();
                    deleteCmd.CommandText = $"""
                        DELETE FROM {QuoteIdentifier("WebsiteLeads", isSqlite)}
                        WHERE {QuoteIdentifier("Id", isSqlite)} = @id
                        """;
                    AddParameter(deleteCmd, "@id", lead.Id);
                    await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
                    _logger.LogWarning(
                        "Hard deleting website lead {LeadId} because WebsiteLeads.IsDeleted is unavailable in the current database schema.",
                        lead.LeadId);
                }

                return Json(new
                {
                    ok = true,
                    leadId = lead.LeadId,
                    deletedAtUtc,
                    deleteReason,
                    usedHardDelete = !supportsSoftDelete
                });
            }
            finally
            {
                if (shouldClose)
                    await conn.CloseAsync();
            }
        }
        catch (AntiforgeryValidationException ex)
        {
            _logger.LogWarning(ex, "DeleteLead antiforgery validation failed for lead {LeadId}.", request.LeadId);
            return BadRequest(new { message = "Your session expired. Refresh the page and try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "DeleteLead failed for lead {LeadId}. Request={@Request}",
                request?.LeadId,
                request);
            return StatusCode(500, new { message = "Unable to delete lead right now." });
        }

        static async Task<WebsiteLeadDeleteLookup?> FindLeadAsync(
            DbConnection conn,
            Guid leadId,
            bool supportsSoftDelete,
            bool isSqlite,
            CancellationToken cancellationToken)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = isSqlite
                ? $"""
                    SELECT "Id", "LeadId", {(supportsSoftDelete ? "COALESCE(\"IsDeleted\", 0)" : "0")} AS "IsDeleted"
                    FROM "WebsiteLeads"
                    WHERE "LeadId" = @leadId
                    LIMIT 1
                    """
                : $"""
                    SELECT TOP (1) [Id], [LeadId], {(supportsSoftDelete ? "CASE WHEN [IsDeleted] = 1 THEN 1 ELSE 0 END" : "0")} AS [IsDeleted]
                    FROM [WebsiteLeads]
                    WHERE [LeadId] = @leadId
                    """;
            AddParameter(cmd, "@leadId", isSqlite ? leadId.ToString() : leadId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new WebsiteLeadDeleteLookup
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                LeadId = ReadGuid(reader, "LeadId"),
                IsDeleted = ReadBoolean(reader, "IsDeleted")
            };
        }

        static string QuoteIdentifier(string identifier, bool isSqlite)
            => isSqlite ? $"\"{identifier}\"" : $"[{identifier}]";

        static void AddParameter(DbCommand cmd, string name, object? value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        static Guid ReadGuid(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal))
                return Guid.Empty;

            var value = reader.GetValue(ordinal);
            return value switch
            {
                Guid guidValue => guidValue,
                string stringValue when Guid.TryParse(stringValue, out var parsed) => parsed,
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                _ => Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var fallback)
                    ? fallback
                    : Guid.Empty
            };
        }

        static bool ReadBoolean(DbDataReader reader, string columnName)
        {
            var ordinal = reader.GetOrdinal(columnName);
            if (reader.IsDBNull(ordinal))
                return false;

            var value = reader.GetValue(ordinal);
            return value switch
            {
                bool boolValue => boolValue,
                byte byteValue => byteValue != 0,
                short shortValue => shortValue != 0,
                int intValue => intValue != 0,
                long longValue => longValue != 0,
                string stringValue when bool.TryParse(stringValue, out var parsedBool) => parsedBool,
                string stringValue when long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong) => parsedLong != 0,
                _ => false
            };
        }
    }

    [HttpGet("agent-performance")]
    [HttpGet("/website-analytics/agent-performance")]
    public async Task<IActionResult> AgentPerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] string? orderBy = null, [FromQuery] bool desc = true, [FromQuery] int? take = null, [FromQuery] int? skip = null)
    {
        if (!FounderGuard.IsFounder(User)) return Forbid();
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var options = new AnalyticsQueryOptions { OrderBy = orderBy ?? "leads", Desc = desc, Take = take, Skip = skip };
        var result = await _analytics.GetAgentPerformanceAsync(range, ScopeContext.Global, options);
        return Json(result);
    }

    // ── Behavior Intelligence ─────────────────────────────────────
    [HttpGet("behavior/summary")]
    public async Task<IActionResult> BehaviorSummary([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetEngagementSummaryAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/time-on-page")]
    public async Task<IActionResult> BehaviorTimeOnPage([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetTimeOnPageAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/exit-analysis")]
    public async Task<IActionResult> BehaviorExit([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetExitAnalysisAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/journey")]
    public async Task<IActionResult> BehaviorJourney([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetJourneyAnalysisAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("behavior/source-performance")]
    public async Task<IActionResult> BehaviorSourcePerformance([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetSourcePerformanceAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("quote-funnel/abandonment")]
    public async Task<IActionResult> QuoteFunnelAbandonment([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);
        var result = await _analytics.GetFormAbandonmentAsync(range, scope, trafficType);
        return Json(result);
    }

    [HttpGet("ai-review-snapshot")]
    [HttpGet("/website-analytics/ai-review-snapshot")]
    public async Task<IActionResult> AiReviewSnapshot([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null, [FromQuery] bool team = false, [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        try
        {
            var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
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

            // All sections use the same trafficType so every metric is computed on a consistent
            // population. Default is TrafficType.All (matches the dashboard default view).
            var (summary, summaryWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetSummaryAsync(range, scope, trafficType),
                () => new SummaryKpiDto
                {
                    RangeLabel = range.Label,
                    EnvironmentLabel = "Environment: Mixed/Legacy",
                    IntentDenominatorLabel = "Quote Submits / Quote Starts"
                },
                "Summary metrics");
            var (traffic, trafficWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetTrafficAsync(range, scope, trafficType),
                () => new TrafficOverviewDto { RangeLabel = range.Label },
                "Traffic metrics");
            var (quote, quoteWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetQuoteFunnelAsync(range, scope, trafficType),
                () => new QuoteFunnelDto { RangeLabel = range.Label },
                "Quote funnel metrics");
            var (conversions, conversionsWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetConversionsAsync(range, scope, trafficType),
                () => new ConversionCenterDto { RangeLabel = range.Label },
                "Conversion metrics");
            var (leads, leadsWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetLeadsAsync(range, scope, trafficType),
                () => new LeadSnapshotDto { RangeLabel = range.Label },
                "Lead snapshot metrics");
            var (pagePerf, pagePerfWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetPagePerformanceAsync(range, scope, trafficType),
                () => new PagePerformanceDto { RangeLabel = range.Label },
                "Page performance metrics");
            var (ctaPerf, ctaPerfWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetCtaPerformanceAsync(range, scope, trafficType),
                () => new CtaPerformanceDto { RangeLabel = range.Label },
                "CTA performance metrics");
            var (timeOnPage, timeOnPageWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetTimeOnPageAsync(range, scope, trafficType),
                () => new TimeOnPageDto { RangeLabel = range.Label },
                "Time-on-page metrics");
            var (exit, exitWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetExitAnalysisAsync(range, scope, trafficType),
                () => new ExitAnalysisDto { RangeLabel = range.Label },
                "Exit analysis metrics");
            var (source, sourceWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetSourcePerformanceAsync(range, scope, trafficType),
                () => new SourcePerformanceDto { RangeLabel = range.Label },
                "Source performance metrics");
            var (abandonment, abandonmentWarning) = await SafeSnapshotLoadAsync(
                () => _analytics.GetFormAbandonmentAsync(range, scope, trafficType),
                () => new FormAbandonmentDto { RangeLabel = range.Label },
                "Form abandonment metrics");
            MetaCampaignsDto? metaCampaigns = null;
            MetaSignalDashboardDto? metaSignal = null;
            string? activeCampaignWarning = null;
            string? metaSignalWarning = null;

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

            try
            {
                metaSignal = await _metaSignalAnalytics.GetDashboardAsync(range, scope, trafficType, ct: HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                metaSignalWarning = "Meta Signal Intelligence unavailable due to internal error.";
                _logger.LogWarning(ex, "AI snapshot meta signal section failed unexpectedly.");
            }

            var generatedUtc = DateTime.UtcNow;
            var generatedDisplay = generatedUtc.ToString("MM/dd/yyyy h:mm tt") + " UTC";
            var generatedUtcIso = generatedUtc.ToString("o");
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
            if (!string.IsNullOrWhiteSpace(metaSignalWarning))
                warnings.Add(metaSignalWarning);
            var snapshotText = BuildAiReviewSnapshotText(
                metaCampaigns,
                metaSignal,
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
                generatedDisplay,
                scopeLabel,
                rangeLabel,
                TrafficAttribution.BucketLabel(trafficType),
                warnings);

            return Json(new AiReviewSnapshotDto
            {
                SnapshotText = snapshotText,
                GeneratedAtLocal = generatedUtcIso,
                ScopeLabel = scopeLabel,
                RangeLabel = rangeLabel,
                TrafficFilterLabel = TrafficAttribution.BucketLabel(trafficType),
                Warnings = warnings
            });
        }
        catch (Exception ex)
        {
            var requestId = HttpContext.TraceIdentifier;
            TimeRangeRequest fallbackRange;
            try
            {
                fallbackRange = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
            }
            catch
            {
                fallbackRange = TimeRangeRequest.FromPreset("today");
            }
            var fallbackUtc = DateTime.UtcNow;
            var fallbackGenerated = fallbackUtc.ToString("MM/dd/yyyy h:mm tt") + " UTC";
            var warnings = new List<string>
            {
                $"Snapshot generation failed. requestId={requestId}",
                "Check Azure Log Stream / Application Logs for the full exception."
            };
            _logger.LogError(ex, "AI snapshot endpoint failed. requestId={RequestId}", requestId);

            return Json(new AiReviewSnapshotDto
            {
                SnapshotText = BuildAiReviewSnapshotFailureText(fallbackGenerated, fallbackRange.Label, warnings),
                GeneratedAtLocal = fallbackUtc.ToString("o"),
                ScopeLabel = "Current Scope",
                RangeLabel = fallbackRange.Label,
                TrafficFilterLabel = TrafficAttribution.BucketLabel(trafficType),
                Warnings = warnings
            });
        }
    }

    // ── KPI Detail Modal Endpoint ─────────────────────────────────────────────


    [HttpGet("visitor-timeline")]
    public async Task<IActionResult> VisitorTimeline(
        string visitorId,
        string? sessionId = null,
        string preset = "today")
    {
        visitorId = (visitorId ?? "").Trim();
        sessionId = (sessionId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(visitorId) &&
            string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new
            {
                error = "visitorId or sessionId required"
            });
        }

        var nowUtc = DateTime.UtcNow;
        var key = (preset ?? "today").Trim().ToLowerInvariant();

        var fromUtc = key switch
        {
            "7d" or "last7" or "last_7_days" => nowUtc.AddDays(-7),
            "30d" or "last30" or "last_30_days" => nowUtc.AddDays(-30),
            "90d" or "last90" or "last_90_days" => nowUtc.AddDays(-90),
            "yesterday" => nowUtc.Date.AddDays(-1),
            _ => nowUtc.Date
        };

        var toUtc = key == "yesterday"
            ? nowUtc.Date
            : nowUtc;

        var query = _db.AnalyticsEvents
            .AsNoTracking()
            .Where(x => x.EventUtc >= fromUtc &&
                        x.EventUtc <= toUtc);

        if (!string.IsNullOrWhiteSpace(visitorId))
            query = query.Where(x => x.VisitorId == visitorId);

        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(x => x.SessionId == sessionId);

        var events = await query
            .OrderBy(x => x.EventUtc)
            .Take(500)
            .Select(x => new
            {
                x.EventUtc,
                x.EventType,
                x.PageKey,
                x.Path,
                x.FormKey,
                x.ElementId,
                x.ScrollPercent,
                x.DwellMilliseconds,
                x.EngagedMilliseconds,
                x.DeviceType,
                x.Browser,
                x.OperatingSystem,
                x.UtmSource,
                x.UtmMedium,
                x.UtmCampaign,
                x.ReferrerHost,
                x.IsInternal,
                x.SessionId,
                x.VisitorId
            })
            .ToListAsync();

        var totalEvents = events.Count;
        var sessions = events
            .Select(x => x.SessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .Count();

        var maxScroll = events
            .Where(x => x.ScrollPercent.HasValue)
            .Select(x => x.ScrollPercent!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var formStarts = events.Count(x =>
            x.EventType == "form_start");

        var ctaClicks = events.Count(x =>
            x.EventType == "cta_click" ||
            x.EventType == "quote_click");

        var trustScore = 100;
        var signals = new List<string>();

        if (totalEvents >= 120)
        {
            trustScore -= 20;
            signals.Add("High event volume");
        }

        if (formStarts >= 4)
        {
            trustScore -= 15;
            signals.Add("Repeated form starts");
        }

        if (ctaClicks >= 12)
        {
            trustScore -= 15;
            signals.Add("Repeated CTA clicks");
        }

        if (maxScroll < 10 && totalEvents >= 10)
        {
            trustScore -= 10;
            signals.Add("Low/no scroll behavior");
        }

        if (events.Any(x => x.IsInternal))
        {
            trustScore -= 25;
            signals.Add("Internal/test traffic");
        }

        trustScore = Math.Max(0, Math.Min(100, trustScore));

        var trustTier =
            trustScore >= 85 ? "Trusted" :
            trustScore >= 65 ? "Review" :
            trustScore >= 40 ? "Suspicious" :
            "Likely Bot";

        return Ok(new
        {
            visitorId,
            sessionId,
            trustScore,
            trustTier,
            signals,
            totalEvents,
            sessions,
            maxScroll,
            formStarts,
            ctaClicks,
            events
        });
    }


    [HttpGet("kpi-detail")]
    public async Task<IActionResult> KpiDetail(
        [FromQuery] string metric,
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false,
        [FromQuery] TrafficType trafficType = TrafficType.All)
    {
        if (string.IsNullOrWhiteSpace(metric))
            return BadRequest(new { message = "metric is required" });

        metric = metric.ToLowerInvariant().Trim();
        if (metric != "pageviews" && metric != "visitors" && metric != "sessions" && metric != "leads")
            return BadRequest(new { message = $"Unknown metric: {metric}" });

        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);

        var span = range.ToUtc - range.FromUtc;
        var prevFrom = range.FromUtc - span;
        var prevTo = range.ToUtc - span;
        var prevRange = new TimeRangeRequest
        {
            FromUtc = prevFrom,
            ToUtc = prevTo,
            Grouping = range.Grouping,
            Label = range.Label,
            Preset = range.Preset,
            ViewerTimeZone = range.ViewerTimeZone
        };

        // Pull the data we need — reuse existing service methods, no duplication
        var traffic = await _analytics.GetTrafficAsync(range, scope, trafficType);
        var prevTraffic = await _analytics.GetTrafficAsync(prevRange, scope, trafficType);

        int total, prevTotal;
        List<TrendPointDto> series;

        switch (metric)
        {
            case "pageviews":
                total = traffic.PageViewTrend.Sum(p => p.Value);
                prevTotal = prevTraffic.PageViewTrend.Sum(p => p.Value);
                series = traffic.PageViewTrend;
                break;
            case "visitors":
                total = traffic.VisitorTrend.Sum(p => p.Value);
                prevTotal = prevTraffic.VisitorTrend.Sum(p => p.Value);
                series = traffic.VisitorTrend;
                break;
            case "sessions":
                total = traffic.SessionTrend.Sum(p => p.Value);
                prevTotal = prevTraffic.SessionTrend.Sum(p => p.Value);
                series = traffic.SessionTrend;
                break;
            case "leads":
                var leads = await _analytics.GetLeadsAsync(range, scope, trafficType, 5000);
                var prevLeads = await _analytics.GetLeadsAsync(prevRange, scope, trafficType, 5000);
                total = leads.Total;
                prevTotal = prevLeads.Total;
                series = BuildLeadDailySeries(leads, range);
                break;
            default:
                total = 0; prevTotal = 0; series = new List<TrendPointDto>();
                break;
        }

        var deltaCount = total - prevTotal;
        var deltaPct = prevTotal > 0 ? Math.Round((decimal)deltaCount / prevTotal * 100, 1) : 0;
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.FromUtc, DateTimeKind.Utc), range.ViewerTimeZone).Date;
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.ToUtc, DateTimeKind.Utc), range.ViewerTimeZone).Date;
        var days = Math.Max(1, (localEnd - localStart).TotalDays + 1);
        var avgPerDay = Math.Round((decimal)total / (decimal)days, 1);

        // Build breakdown
        var breakdown = new Models.Analytics.KpiDetailBreakdownDto();

        switch (metric)
        {
            case "pageviews":
                breakdown.TopPages = traffic.TopPages.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopSources = traffic.TopSources.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopCampaigns = traffic.TopCampaigns.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                break;

            case "visitors":
                breakdown.TopLandingPages = traffic.EntryPages.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();

                breakdown.TopSources = traffic.TopSources.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();

                var visitorEvents = await _db.AnalyticsEvents
                    .AsNoTracking()
                    .Where(e =>
                        e.EventUtc >= range.FromUtc &&
                        e.EventUtc < range.ToUtc &&
                        e.Environment != null &&
                        (e.Environment.ToLower() == "production" || e.Environment.ToLower() == "prod"))
                    .Select(e => new
                    {
                        e.VisitorId,
                        e.SessionId,
                        e.EventUtc,
                        e.DeviceType,
                        e.UtmSource,
                        e.IsInternal
                    })
                    .ToListAsync();

                breakdown.VisitorConcentration = visitorEvents
                    .Where(x => !string.IsNullOrWhiteSpace(x.VisitorId))
                    .GroupBy(x => x.VisitorId!)
                    .Select(g =>
                    {
                        var sessions = g
                            .Select(x => x.SessionId)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct()
                            .Count();

                        var events = g.Count();

                        var device = g
                            .Select(x => x.DeviceType)
                            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                            ?? "Unknown";

                        var source = g
                            .Select(x => x.UtmSource)
                            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                            ?? "Direct";

                        var firstSeen = g.Min(x => x.EventUtc);
                        var lastSeen = g.Max(x => x.EventUtc);

                        var likelyInternal =
                            g.Any(x => x.IsInternal) ||
                            sessions >= 5 ||
                            events >= 100;

                        return new Models.Analytics.VisitorConcentrationDto
                        {
                            VisitorId = g.Key,
                            VisitorShortId = g.Key.Length > 8 ? g.Key.Substring(0, 8) + "…" : g.Key,
                            Sessions = sessions,
                            Events = events,
                            Device = device,
                            Source = source,
                            FirstSeenLocal = firstSeen.ToLocalTime().ToString("M/d h:mm tt"),
                            LastSeenLocal = lastSeen.ToLocalTime().ToString("M/d h:mm tt"),
                            LikelyInternal = likelyInternal
                        };
                    })
                    .OrderByDescending(x => x.Events)
                    .ThenByDescending(x => x.Sessions)
                    .Take(15)
                    .ToList();

                break;

            case "sessions":
                breakdown.TopLandingPages = traffic.EntryPages.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopSources = traffic.TopSources.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                breakdown.TopCampaigns = traffic.TopCampaigns.Take(10)
                    .Select(x => new Models.Analytics.KpiDetailBreakdownItemDto { Label = x.Key, Value = x.Count }).ToList();
                break;

            case "leads":
                var leadsForBreakdown = await _analytics.GetLeadsAsync(range, scope, trafficType, 5000);
                breakdown.TopSources = leadsForBreakdown.Leads
                    .Where(l => !string.IsNullOrWhiteSpace(l.ResolvedSource))
                    .GroupBy(l => l.ResolvedSource!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new Models.Analytics.KpiDetailBreakdownItemDto { Label = g.Key, Value = g.Count() })
                    .ToList();
                breakdown.TopCampaigns = leadsForBreakdown.Leads
                    .Where(l => !string.IsNullOrWhiteSpace(l.ResolvedCampaign))
                    .GroupBy(l => l.ResolvedCampaign!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new Models.Analytics.KpiDetailBreakdownItemDto { Label = g.Key, Value = g.Count() })
                    .ToList();
                breakdown.TopPages = leadsForBreakdown.Leads
                    .Where(l => !string.IsNullOrWhiteSpace(l.SourcePage))
                    .GroupBy(l => l.SourcePage!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new Models.Analytics.KpiDetailBreakdownItemDto { Label = g.Key, Value = g.Count() })
                    .ToList();
                breakdown.RecentLeads = leadsForBreakdown.Leads.Take(15)
                    .Select(l => new Models.Analytics.KpiDetailBreakdownItemDto
                    {
                        Label = string.IsNullOrWhiteSpace(l.Name) ? l.Email : l.Name,
                        Value = 1,
                        Meta = l.ResolvedSource ?? "Direct"
                    })
                    .ToList();
                break;
        }

        var metricLabel = metric switch
        {
            "pageviews" => "Page Views",
            "visitors" => "Unique Visitors",
            "sessions" => "Sessions",
            "leads" => "Leads",
            _ => metric
        };

        var result = new Models.Analytics.KpiDetailDto
        {
            Metric = metric,
            Label = metricLabel,
            StartDateLocal = localStart.ToString("MMM d, yyyy"),
            EndDateLocal = localEnd.ToString("MMM d, yyyy"),
            Totals = new Models.Analytics.KpiDetailTotalsDto
            {
                Total = total,
                PreviousTotal = prevTotal,
                DeltaCount = deltaCount,
                DeltaPct = deltaPct,
                AvgPerDay = avgPerDay
            },
            Series = series,
            Breakdown = breakdown
        };

        return Json(result);
    }

    private static List<TrendPointDto> BuildLeadDailySeries(LeadSnapshotDto leads, TimeRangeRequest range)
    {
        var tz = range.ViewerTimeZone;
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.FromUtc, DateTimeKind.Utc), tz).Date;
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(range.ToUtc, DateTimeKind.Utc), tz).Date;
        var grouped = leads.Leads
            .GroupBy(l => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(l.CreatedUtc, DateTimeKind.Utc), tz).Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var series = new List<TrendPointDto>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            series.Add(new TrendPointDto
            {
                Label = day.ToString("yyyy-MM-dd"),
                Value = grouped.TryGetValue(day, out var value) ? value : 0
            });
        }

        return series;
    }

    [HttpGet("meta-campaigns")]
    [HttpGet("/website-analytics/meta-campaigns")]
    public async Task<IActionResult> MetaCampaigns([FromQuery] string? preset, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] Guid? agentProfileId = null)
    {
        try
        {
            var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
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
        var hasConfiguredFallback = !string.IsNullOrWhiteSpace(_config["MetaAds:AccessToken"]) &&
                                    !string.IsNullOrWhiteSpace(_config["MetaAds:DefaultAccountId"]);
        if (!agentId.HasValue || agentId.Value == Guid.Empty)
        {
            return Json(new MetaAdsConnectionStatusDto
            {
                Connected = hasConfiguredFallback,
                AgentTrackingProfileId = null
            });
        }

        var record = await _metaAdsConnectionStore.GetAsync(agentId.Value, HttpContext.RequestAborted);
        if (record == null)
        {
            return Json(new MetaAdsConnectionStatusDto
            {
                Connected = hasConfiguredFallback,
                AgentTrackingProfileId = agentId,
                AccountId = hasConfiguredFallback ? _config["MetaAds:DefaultAccountId"] : null,
                AccountName = hasConfiguredFallback ? "Configured fallback account" : null,
                MetaUserName = hasConfiguredFallback ? "Configured fallback" : null
            });
        }

        return Json(new MetaAdsConnectionStatusDto
        {
            Connected = true,
            AgentTrackingProfileId = agentId,
            AccountId = record.AccountId,
            AccountName = record.AccountName,
            BusinessId = record.BusinessId,
            BusinessName = record.BusinessName,
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
            if (_effectiveContext.IsViewingAsAgent)
            {
                return await ResolveEffectiveImpersonatedAgentScopeAsync();
            }
            return ScopeContext.Global;
        }

        // If founder is impersonating an agent, analytics must scope to that agent.
        // Never fall back to founder scope for view-as-agent requests.
        if (_effectiveContext.IsViewingAsAgent)
        {
            return await ResolveEffectiveImpersonatedAgentScopeAsync();
        }

        // Agent (or assistant) uses effective profile
        if (effectiveProfileId.HasValue)
        {
            return ScopeContext.ForAgent(effectiveProfileId.Value);
        }

        _logger.LogWarning("Scope resolution: no agent profile for caller; returning empty scope (no data)");
        return ScopeContext.ForAgent(Guid.Empty); // will match nothing
    }

    private async Task<ScopeContext> ResolveEffectiveImpersonatedAgentScopeAsync()
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
        return ScopeContext.ForAgent(Guid.Empty);
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
        if (scope.ScopeType == ScopeType.Global)
            return "Global";

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
        if (FounderGuard.IsFounder(User) &&
            !string.IsNullOrWhiteSpace(profile.AgentUpn) &&
            string.Equals(profile.AgentUpn, _founderUpn, StringComparison.OrdinalIgnoreCase))
        {
            return "Founder Personal";
        }
        return string.IsNullOrWhiteSpace(agentName) ? "Agent Scope" : $"Agent: {agentName}";
    }

    private bool CanDeleteAnalyticsLeads()
    {
        if (FounderGuard.IsFounder(User))
            return true;

        var oid = (User?.FindFirst("oid")?.Value ?? string.Empty).Trim();
        var upn = (User?.FindFirstValue(ClaimTypes.Email)
            ?? User?.FindFirstValue("preferred_username")
            ?? User?.FindFirstValue("upn")
            ?? User?.Identity?.Name
            ?? string.Empty).Trim();

        return MatchesConfiguredUser(oid, Environment.GetEnvironmentVariable("LEGEND_ADMIN_OIDS"))
            || MatchesConfiguredUser(upn, Environment.GetEnvironmentVariable("LEGEND_ADMIN_UPNS"));
    }

    private static bool MatchesConfiguredUser(string value, string? configuredList)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(configuredList))
            return false;

        return configuredList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<HashSet<string>> GetWebsiteLeadColumnSetAsync(CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose)
            await conn.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conn.CreateCommand();
            var provider = _db.Database.ProviderName ?? string.Empty;

            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                cmd.CommandText = "PRAGMA table_info(\"WebsiteLeads\")";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                var nameOrdinal = reader.GetOrdinal("name");
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(nameOrdinal))
                        columns.Add(reader.GetString(nameOrdinal));
                }

                return columns;
            }

            cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName";
            var tableName = cmd.CreateParameter();
            tableName.ParameterName = "@tableName";
            tableName.Value = "WebsiteLeads";
            cmd.Parameters.Add(tableName);

            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0))
                        columns.Add(reader.GetString(0));
                }
            }

            return columns;
        }
        finally
        {
            if (shouldClose)
                await conn.CloseAsync();
        }
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
        sb.AppendLine($"Generated: {generatedAtLocal}");
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
        MetaSignalDashboardDto? metaSignal,
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
        string trafficScopeLabel,
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
        Line($"Generated: {generatedAtLocal}");
        Line($"Range: {rangeLabel}");
        Line($"Scope: {scopeLabel}");
        Line($"Traffic Filter: {trafficScopeLabel}");
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

        Line("SECTION C — META SIGNAL INTELLIGENCE");
        if (metaSignal == null)
        {
            Line("Meta Signal Intelligence unavailable in this snapshot.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(metaSignal.LearningScopeNote))
                Line(metaSignal.LearningScopeNote);
            Line($"Total signal events: {metaSignal.TotalSignalEvents}");
            Line($"Total visitors with signal: {metaSignal.TotalVisitors}");
            Line($"High-intent visitors: {metaSignal.HighIntentVisitors}");
            Line($"Lead-ready visitors: {metaSignal.LeadReadyVisitors}");
            Line($"Submitted leads: {metaSignal.SubmittedLeads}");
            Line($"Submit attempts without confirmed lead: {metaSignal.SubmitAttemptsWithoutLead}");
            Line($"High-intent abandons: {metaSignal.HighIntentAbandons}");
            Line($"Contact-step abandons: {metaSignal.ContactStepAbandons}");
            Line($"Excluded signal events: {metaSignal.ExcludedSignalEvents}");
            Line($"Excluded signal visitors: {metaSignal.ExcludedSignalVisitors}");
            Line($"Signal-to-lead conversion: {metaSignal.SignalToLeadConversionRate:0.##}%");
            Line($"Recommended optimization event right now: {Safe(metaSignal.RecommendedOptimizationEvent)}");
            Line($"Best-performing landing version: {Safe(metaSignal.BestPerformingLandingPageVersion)}");
            Line($"Worst friction step: {Safe(metaSignal.WorstFrictionStep)}");
            Line("Visitors by score tier:");
            var visitorsByTier = metaSignal.VisitorsByScoreTier ?? new List<MetaSignalTierRowDto>();
            if (visitorsByTier.Count == 0)
            {
                Line("No data in range.");
            }
            else
            {
                foreach (var tier in visitorsByTier)
                    Line($"- {Safe(tier.ScoreTier)}: {tier.Visitors}");
            }
            Line("Event ladder:");
            var signalLadder = metaSignal.EventLadder ?? new List<MetaSignalLadderRowDto>();
            if (signalLadder.Count == 0)
            {
                Line("No paid Meta-attributed funnel progression in range.");
            }
            else
            {
                foreach (var stage in signalLadder)
                {
                    var rateText = stage.ProgressionRate.HasValue ? $"{stage.ProgressionRate.Value:0.##}%" : "—";
                    Line($"- {Safe(stage.StepLabel)} | visitors {stage.Visitors} | progression {rateText}");
                }
            }
        }
        Line();

        Line("SECTION D — TRAFFIC HEALTH");
        Line($"Page Views: {summary.PageViews}");
        Line($"Unique Visitors: {summary.UniqueVisitors}");
        Line($"Sessions: {summary.Sessions}");
        AddKeyCountBlock("Top Pages (Top 5):", traffic.TopPages, 5);
        AddKeyCountBlock("Entry Pages (Top 5):", traffic.EntryPages, 5);
        AddKeyCountBlock("Top Sources (Top 5):", traffic.TopSources, 5);
        AddKeyCountBlock("Top Campaigns (Top 5):", traffic.TopCampaigns, 5);
        Line();

        Line("SECTION E — FUNNEL HEALTH");
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

        Line("SECTION F — LEAD PICTURE");
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

        Line("SECTION G — PAGE + CTA PERFORMANCE");
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

        Line("SECTION H — CAMPAIGN / SOURCE READ");
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

        Line("SECTION I — BEHAVIOR SIGNALS (DIRECTIONAL)");
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
            {
                var abandonRate = row.AbandonRate.HasValue ? $"{row.AbandonRate.Value:0.##}%" : "—";
                Line($"- {Safe(row.QuoteType)} | abandons {row.Abandons} | abandon rate {abandonRate}");
            }
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

        Line("SECTION J — DATA QUALITY / CONTEXT NOTES");
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

        Line("SECTION K — CHATGPT COPY PROMPT FOOTER");
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

    /// <summary>
    /// Developer-only diagnostic endpoint. Returns traffic bucket counts and attribution
    /// distribution for the requested range/scope. Safe to call; never modifies data.
    /// To hide from normal users, add [Authorize(Policy = "FounderOnly")] or restrict by role.
    /// </summary>
    [HttpGet("debug/traffic-buckets")]
    [HttpGet("/website-analytics/debug/traffic-buckets")]
    public async Task<IActionResult> DebugTrafficBuckets(
        [FromQuery] string? preset,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] Guid? agentProfileId = null,
        [FromQuery] bool team = false)
    {
        if (!FounderGuard.IsFounder(User))
            return Forbid();

        var range = TimeRangeRequest.FromPreset(preset, fromUtc, toUtc, GetViewerTimeZone());
        var scope = await ResolveScopeAsync(agentProfileId, team);

        // Load all raw events + leads
        var scopedAgentIds = await _db.AgentTrackingProfiles
            .Where(p => true)
            .Select(p => p.Id)
            .ToArrayAsync();

        var allEvents = await _db.AnalyticsEvents.AsNoTracking()
            .Where(e => !e.IsInternal && e.EventUtc >= range.FromUtc && e.EventUtc <= range.ToUtc)
            .ToListAsync();

        var allLeads = await _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsInternal && !l.IsDeleted && l.CreatedUtc >= range.FromUtc && l.CreatedUtc <= range.ToUtc)
            .ToListAsync();

        // Compute attributed event rows
        var attributed = allEvents
            .Select(e =>
            {
                var src  = e.UtmSource?.Trim();
                var med  = e.UtmMedium?.Trim();
                var camp = e.UtmCampaign?.Trim();
                var fb   = e.Fbclid?.Trim();
                var t    = TrafficAttribution.Classify(
                    src,
                    med,
                    camp,
                    fb,
                    referrerHost: e.ReferrerHost,
                    metaCampaignId: e.MetaCampaignId,
                    metaAdSetId: e.MetaAdSetId,
                    metaAdId: e.MetaAdId,
                    isInternal: e.IsInternal,
                    environment: e.Environment,
                    host: e.Host);
                return new { e.EventType, e.SessionId, t };
            })
            .ToList();

        var eventBuckets = attributed
            .GroupBy(r => r.t)
            .OrderByDescending(g => g.Count())
            .Select(g => new
            {
                Bucket = g.Key.ToString(),
                Events = g.Count(),
                Sessions = g.Where(r => r.SessionId != null).Select(r => r.SessionId!).Distinct().Count()
            })
            .ToList();

        var leadBuckets = allLeads
            .GroupBy(l => TrafficAttribution.Classify(
                l.UtmSource,
                l.UtmMedium,
                l.UtmCampaign,
                l.Fbclid,
                metaCampaignId: l.MetaCampaignId,
                metaAdSetId: l.MetaAdSetId,
                metaAdId: l.MetaAdId,
                isInternal: l.IsInternal,
                environment: l.Environment,
                host: l.Host))
            .OrderByDescending(g => g.Count())
            .Select(g => new
            {
                Bucket = g.Key.ToString(),
                Leads = g.Count()
            })
            .ToList();

        var zeroDataHints = new List<string>();
        var paidEvents    = eventBuckets.FirstOrDefault(b => b.Bucket == "PaidAds")?.Events ?? 0;
        var unknownEvents = eventBuckets.FirstOrDefault(b => b.Bucket == "Unknown")?.Events ?? 0;
        if (paidEvents == 0 && unknownEvents > 0)
            zeroDataHints.Add($"All traffic is Unknown/unattributed ({unknownEvents} events). PaidAds filter will return 0 rows. Check that utm_source/utm_medium are being sent on landing page_view events.");
        if (paidEvents == 0 && allEvents.Count > 0)
            zeroDataHints.Add("No PaidAds-classified events in range. If you expect paid traffic, verify UTM parameters are present on the first page_view of paid sessions.");

        return Json(new
        {
            Range = range.Label,
            TotalEvents = allEvents.Count,
            TotalLeads = allLeads.Count,
            EventBucketsByDirectAttribution = eventBuckets,
            LeadBucketsByDirectAttribution = leadBuckets,
            ZeroDataHints = zeroDataHints,
            Note = "Attribution shown here is direct-field only (no session fallback). Actual query results use session→visitor fallback and may differ."
        });
    }

    public sealed class DeleteLeadRequest
    {
        public Guid LeadId { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class WebsiteLeadDeleteLookup
    {
        public long Id { get; set; }
        public Guid LeadId { get; set; }
        public bool IsDeleted { get; set; }
    }
}

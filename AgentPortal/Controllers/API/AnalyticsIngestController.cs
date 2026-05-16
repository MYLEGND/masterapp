using System.ComponentModel.DataAnnotations;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace AgentPortal.Controllers.Api;

[ApiController]
[Route("api/analytics/ingest")]
[Route("api/tracking/ingest")] // Backward-compatible alias for older tracking snippets.
[EnableCors("TrackingCors")]
[AllowAnonymous]
public class AnalyticsIngestController : ControllerBase
{
    private readonly MasterAppDbContext _db;
    private readonly IConfiguration _config;
    private readonly Services.Tracking.AgentTrackingResolver _resolver;
    private readonly ILogger<AnalyticsIngestController> _logger;
    private readonly AgentPortal.Models.AppFeatureFlags _flags;
    private readonly IngestSignatureValidator _signatureValidator;

    public AnalyticsIngestController(MasterAppDbContext db, IConfiguration config, Services.Tracking.AgentTrackingResolver resolver, ILogger<AnalyticsIngestController> logger, Microsoft.Extensions.Options.IOptions<AgentPortal.Models.AppFeatureFlags> flags, IngestSignatureValidator signatureValidator)
    {
        _db = db;
        _config = config;
        _resolver = resolver;
        _logger = logger;
        _flags = flags.Value;
        _signatureValidator = signatureValidator;
    }

    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core interaction events (existing)
        "page_view",
        "cta_click",
        "quote_click",
        "risk_assessment_click",
        "form_start",
        "form_submit",
        "outbound_click",
        "lead_modal_open",
        "lead_form_start",
        "lead_form_submit_success",
        "lead_form_submit_failed",
        // Behavior Intelligence Engine events
        "page_engaged_10s",
        "page_engaged_30s",
        "page_engaged_60s",
        "scroll_depth_25",
        "scroll_depth_50",
        "scroll_depth_75",
        "scroll_depth_90",
        "scroll_depth_100",
        "page_exit",
        "session_end",
        "form_field_focus",
        "form_field_complete",
        "form_field_abandon",
        "form_field_error",
        "form_submit_attempt",
        "form_abandon",
        // Life quote funnel micro-step + bridge events
        "life_step1_intro_view",
        "life_step1_protecting_view",
        "life_step1_protecting_select",
        "life_step1_goal_view",
        "life_step1_goal_select",
        "life_step1_tobacco_view",
        "life_step1_tobacco_select",
        "life_step1_age_view",
        "life_step1_age_continue",
        "step1_age_entered",
        "life_processing_bridge_view",
        "life_processing_bridge_complete",
        "life_value_bridge_view",
        "life_value_bridge_continue",
        "life_step2_view",
        "life_step2_back",
        "life_step2_submit_attempt",
        "life_step2_submit_success",
        "mini_results_view",
        "recommendation_generated",
        "results_contact_submit",
        "life_general_form_start",
        "life_general_submit",
        "life_term_form_start",
        "life_term_submit",
        "life_whole_form_start",
        "life_whole_submit",
        "life_finalexpense_form_start",
        "life_finalexpense_submit",
        "life_mp_form_start",
        "life_mp_submit",
        "life_iul_form_start",
        "life_iul_submit",
        "rage_click",
        "dead_click",
        "file_download",
        "section_view",
        "page_visibility_hidden",
        "page_visibility_return",
        // Server-side canonical lead submission events (written by backend, not client JS)
        "website_lead_submitted"
    };

    public sealed class AnalyticsEventRequest
    {
        [Required] public Guid ClientEventId { get; set; }
        [Required] public string EventType { get; set; } = null!;
        public string? PageKey { get; set; }
        public string? SectionKey { get; set; }
        public string? ElementKey { get; set; }
        public string? ButtonLabel { get; set; }
        public string? FormKey { get; set; }
        public string? QuoteType { get; set; }
        public string? Url { get; set; }
        public string? Path { get; set; }
        public string? Referrer { get; set; }
        public string? SessionId { get; set; }
        public string? VisitorId { get; set; }
        public string? UtmSource { get; set; }
        public string? UtmMedium { get; set; }
        public string? UtmCampaign { get; set; }
        public string? Fbclid { get; set; }
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public DateTime? EventUtc { get; set; }
        public string? SubmitOutcome { get; set; }
        public string? MetadataJson { get; set; }
        public bool IsInternal { get; set; }
        // Behavior Intelligence fields (all optional, additive)
        public string? ReferrerHost { get; set; }
        public string? DeviceType { get; set; }
        public string? Browser { get; set; }
        public string? OperatingSystem { get; set; }
        public int? ScreenWidth { get; set; }
        public int? ScreenHeight { get; set; }
        public int? ViewportWidth { get; set; }
        public int? ViewportHeight { get; set; }
        public int? ScrollPercent { get; set; }
        public long? DwellMilliseconds { get; set; }
        public long? EngagedMilliseconds { get; set; }
        public bool? IsBounceCandidate { get; set; }
        public bool? IsExitPage { get; set; }
        public string? UtmTerm { get; set; }
        public string? UtmContent { get; set; }
        public string? MetaCampaignId { get; set; }
        public string? MetaCampaignName { get; set; }
        public string? MetaAdSetId { get; set; }
        public string? MetaAdSetName { get; set; }
        public string? MetaAdId { get; set; }
        public string? MetaAdName { get; set; }
        public string? Placement { get; set; }
        public string? FormId { get; set; }
        public string? FieldName { get; set; }
        public string? ElementId { get; set; }
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("ingest")]
    [RequestSizeLimit(32 * 1024)] // 32 KB — well above any valid analytics event payload
    public async Task<IActionResult> Ingest([FromBody] AnalyticsEventRequest req)
    {
        // Shared secret check
        var expected = _config["Analytics:SharedSecret"] ?? _config["LeadIngest:SharedSecret"];
        var provided = Request.Headers["X-Shared-Secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, provided, StringComparison.Ordinal))
        {
            _logger.LogWarning("AnalyticsIngest: invalid shared secret from {Host}", Request.Host.ToString());
            return Unauthorized(new { error = "invalid_secret" });
        }

        if (_flags.IngestHmacEnabled)
        {
            if (!Guid.TryParse(Request.Headers["X-Request-Id"].FirstOrDefault(), out var requestId))
                return Unauthorized(new { error = "missing_request_id" });
            if (!DateTimeOffset.TryParse(Request.Headers["X-Timestamp"].FirstOrDefault(), out var ts))
                return Unauthorized(new { error = "invalid_timestamp" });

            if (!_signatureValidator.TryValidate(requestId, ts, Request.Headers["X-Signature"].FirstOrDefault(), out var reason))
                return Unauthorized(new { error = reason });
        }

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!AllowedEventTypes.Contains(req.EventType))
            return BadRequest(new { error = "invalid_event_type" });

        var resolved = await _resolver.ResolveAsync(req.AgentSlug, req.AgentTrackingProfileId, HttpContext.RequestAborted);
        if (!resolved.Found)
        {
            _logger.LogInformation("AnalyticsIngest: unknown agent attribution slug={Slug} id={Id}", req.AgentSlug, req.AgentTrackingProfileId);
        }

        // Dedupe on ClientEventId
        var existing = await _db.AnalyticsEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ClientEventId == req.ClientEventId);
        if (existing != null)
            return Ok(new { status = "duplicate_ignored" });

        var ev = new AnalyticsEvent
        {
            EventId = Guid.NewGuid(),
            ClientEventId = req.ClientEventId,
            EventType = req.EventType.Trim(),
            PageKey = TrimOrNull(req.PageKey),
            SectionKey = TrimOrNull(req.SectionKey),
            ElementKey = TrimOrNull(req.ElementKey),
            ButtonLabel = TrimOrNull(req.ButtonLabel),
            FormKey = TrimOrNull(req.FormKey),
            QuoteType = TrimOrNull(req.QuoteType),
            Url = TrimOrNull(req.Url),
            Path = TrimOrNull(req.Path),
            Referrer = TrimOrNull(req.Referrer),
            SessionId = TrimOrNull(req.SessionId),
            VisitorId = TrimOrNull(req.VisitorId),
            UtmSource = TrimOrNull(req.UtmSource),
            UtmMedium = TrimOrNull(req.UtmMedium),
            UtmCampaign = TrimOrNull(req.UtmCampaign),
            Fbclid = TrimOrNull(req.Fbclid),
            Environment = ResolveEnvironment(req.Environment),
            Host = string.IsNullOrWhiteSpace(req.Host) ? Request.Host.ToString() : req.Host,
            EventUtc = req.EventUtc ?? DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            SubmitOutcome = TrimOrNull(req.SubmitOutcome),
            MetadataJson = string.IsNullOrWhiteSpace(req.MetadataJson) ? null : req.MetadataJson,
            IsInternal = req.IsInternal || FounderGuard.IsFounder(User),
            AgentTrackingProfileId = resolved.Found ? resolved.Profile.Id : null,
            AgentSlug = resolved.Found ? resolved.CanonicalSlug : null,
            // Behavior Intelligence fields
            ReferrerHost = TrimOrNull(req.ReferrerHost) ?? ParseReferrerHost(req.Referrer),
            DeviceType = TrimOrNull(req.DeviceType),
            Browser = TrimOrNull(req.Browser),
            OperatingSystem = TrimOrNull(req.OperatingSystem),
            ScreenWidth = req.ScreenWidth,
            ScreenHeight = req.ScreenHeight,
            ViewportWidth = req.ViewportWidth,
            ViewportHeight = req.ViewportHeight,
            ScrollPercent = req.ScrollPercent.HasValue ? Math.Clamp(req.ScrollPercent.Value, 0, 100) : null,
            DwellMilliseconds = req.DwellMilliseconds.HasValue && req.DwellMilliseconds.Value >= 0 ? req.DwellMilliseconds : null,
            EngagedMilliseconds = req.EngagedMilliseconds.HasValue && req.EngagedMilliseconds.Value >= 0 ? req.EngagedMilliseconds : null,
            IsBounceCandidate = req.IsBounceCandidate,
            IsExitPage = req.IsExitPage,
            UtmTerm = TrimOrNull(req.UtmTerm),
            UtmContent = TrimOrNull(req.UtmContent),
            MetaCampaignId = TrimOrNull(req.MetaCampaignId),
            MetaCampaignName = TrimOrNull(req.MetaCampaignName),
            MetaAdSetId = TrimOrNull(req.MetaAdSetId),
            MetaAdSetName = TrimOrNull(req.MetaAdSetName),
            MetaAdId = TrimOrNull(req.MetaAdId),
            MetaAdName = TrimOrNull(req.MetaAdName),
            Placement = TrimOrNull(req.Placement),
            FormId = TrimOrNull(req.FormId),
            // FieldName accepted only for field-level event types; never store free-form values
            FieldName = IsFieldLevelEvent(req.EventType) ? TrimOrNull(req.FieldName) : null,
            ElementId = TrimOrNull(req.ElementId)
        };

        // SQLite local dev: bigint PK is not auto-generated, so assign a unique Id.
        // Use millisecond timestamp + random suffix to avoid concurrent-insert PK collisions.
        if (IsSqliteProvider())
            ev.Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L + Random.Shared.Next(1000);

        _db.AnalyticsEvents.Add(ev);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsClientEventIdDuplicate(ex))
        {
            _logger.LogInformation("AnalyticsIngest: duplicate ClientEventId ignored via DB constraint. clientEventId={ClientEventId}", req.ClientEventId);
            return Ok(new { status = "duplicate_ignored" });
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "AnalyticsIngest: save failed for eventType={EventType} clientEventId={ClientEventId}", req.EventType, req.ClientEventId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "ingest_save_failed" });
        }

        return Ok(new { status = "ok", eventId = ev.EventId });
    }

    private static string? TrimOrNull(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    private string CurrentEnvironment() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

    private string ResolveEnvironment(string? incoming)
    {
        var raw = string.IsNullOrWhiteSpace(incoming) ? CurrentEnvironment() : incoming!;
        var normalized = raw.Trim();
        if (normalized.StartsWith("prod", StringComparison.OrdinalIgnoreCase)) return "production";
        if (normalized.StartsWith("dev", StringComparison.OrdinalIgnoreCase)) return "development";
        return normalized;
    }

    private bool IsSqliteProvider() =>
        _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsClientEventIdDuplicate(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) return false;

        return
            msg.Contains("ClientEventId", StringComparison.OrdinalIgnoreCase) &&
            (
                msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("2601", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("2627", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("2067", StringComparison.OrdinalIgnoreCase)
            );
    }

    private static string? ParseReferrerHost(string? referrer)
    {
        if (string.IsNullOrWhiteSpace(referrer)) return null;
        try { return new Uri(referrer).Host; } catch { return null; }
    }

    private static bool IsFieldLevelEvent(string? eventType) =>
        eventType != null && (
            eventType.Equals("form_field_focus", StringComparison.OrdinalIgnoreCase) ||
            eventType.Equals("form_field_complete", StringComparison.OrdinalIgnoreCase) ||
            eventType.Equals("form_field_abandon", StringComparison.OrdinalIgnoreCase) ||
            eventType.Equals("form_field_error", StringComparison.OrdinalIgnoreCase)
        );

    // Preflight responder for CORS
    [HttpOptions]
    [IgnoreAntiforgeryToken]
    public IActionResult Options() => Ok();
}

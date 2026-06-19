using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Shared.Analytics;

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

    public sealed class AnalyticsEventRequest
    {
        public int? SchemaVersion { get; set; }
        public string? TrackingVersion { get; set; }

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
        public string? UtmId { get; set; }
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
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }
        public string? TimeZone { get; set; }
        public string? Language { get; set; }

        public bool? WebDriver { get; set; }
        public bool? IsHeadless { get; set; }
        public int? MouseMoveCount { get; set; }
        public int? HumanInteractionCount { get; set; }
        public int? VisibilityChangeCount { get; set; }
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

        if (!AnalyticsEventCatalog.TryGet(req.EventType, out var eventDefinition))
        {
            LogRejectedEvent(req, "unknown_event_type");
            return BadRequest(new { error = "invalid_event_type" });
        }

        if (!eventDefinition.AllowBrowser)
        {
            LogRejectedEvent(req, "browser_not_allowed");
            return BadRequest(new { error = "invalid_event_type" });
        }

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
            SchemaVersion = req.SchemaVersion ?? 1,
            TrackingVersion = TrimOrNull(req.TrackingVersion),
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
            UtmId = TrimOrNull(req.UtmId),
            Fbclid = TrimOrNull(req.Fbclid),
            Environment = ResolveEnvironment(req.Environment),
            Host = string.IsNullOrWhiteSpace(req.Host) ? Request.Host.ToString() : req.Host,
            EventUtc = req.EventUtc ?? DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            SubmitOutcome = TrimOrNull(req.SubmitOutcome),
            MetadataJson = MetaSignalSingleTruthPolicy.BuildMetadataJson(
                eventName: req.EventType.Trim(),
                leadId: null,
                sessionId: TrimOrNull(req.SessionId),
                payload: ParseMetadataPayload(req.MetadataJson),
                isBrowserSignal: true,
                isServerAuthority: false,
                metaServerAuthorityEligible: false,
                metaSingleTruthDispatchEligible: false,
                metaPipelineOrigin: "browser_analytics_ingest"),
            IsInternal = req.IsInternal || FounderGuard.IsFounder(User),
            AgentTrackingProfileId = resolved.Found ? resolved.Profile.Id : null,
            AgentSlug = resolved.Found ? resolved.CanonicalSlug : null,
            // Behavior Intelligence fields
            ReferrerHost = TrimOrNull(req.ReferrerHost) ?? ParseReferrerHost(req.Referrer),
            DeviceType = TrimOrNull(req.DeviceType),
            Browser = TrimOrNull(req.Browser),
            OperatingSystem = TrimOrNull(req.OperatingSystem),
            UserAgent = TrimOrNull(req.UserAgent) ?? TrimOrNull(Request.Headers.UserAgent.ToString()),
            IpAddress = TrimOrNull(req.IpAddress) ?? ResolveClientIp(),
            WebDriver = req.WebDriver,
            IsHeadless = req.IsHeadless,
            MouseMoveCount = req.MouseMoveCount.HasValue && req.MouseMoveCount.Value >= 0 ? req.MouseMoveCount : null,
            HumanInteractionCount = req.HumanInteractionCount.HasValue && req.HumanInteractionCount.Value >= 0 ? req.HumanInteractionCount : null,
            VisibilityChangeCount = req.VisibilityChangeCount.HasValue && req.VisibilityChangeCount.Value >= 0 ? req.VisibilityChangeCount : null,
            TimeZone = TrimOrNull(req.TimeZone),
            Language = TrimOrNull(req.Language),
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

    private void LogRejectedEvent(AnalyticsEventRequest req, string reason)
    {
        _logger.LogWarning(
            "AnalyticsIngest rejected event={EventName} reason={Reason} route={Route} pageKey={PageKey} quoteType={QuoteType} source={Source} sessionId={SessionId}",
            req.EventType,
            reason,
            HttpContext?.Request?.Path.Value ?? string.Empty,
            req.PageKey ?? string.Empty,
            req.QuoteType ?? string.Empty,
            "browser",
            req.SessionId ?? string.Empty);
    }

    private static string? TrimOrNull(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    private static object? ParseMetadataPayload(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            return new { rawMetadataJson = metadataJson };
        }
    }

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

    private string? ResolveClientIp()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        var azureClientIp = Request.Headers["X-Azure-ClientIP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(azureClientIp))
        {
            return azureClientIp.Trim();
        }

        return Request.HttpContext.Connection.RemoteIpAddress?.ToString();
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

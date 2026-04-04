using System.ComponentModel.DataAnnotations;
using AgentPortal.Security;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

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

    public AnalyticsIngestController(MasterAppDbContext db, IConfiguration config, Services.Tracking.AgentTrackingResolver resolver, ILogger<AnalyticsIngestController> logger)
    {
        _db = db;
        _config = config;
        _resolver = resolver;
        _logger = logger;
    }

    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
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
        "lead_form_submit_failed"
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
        public Guid? AgentTrackingProfileId { get; set; }
        public string? AgentSlug { get; set; }
        public string? Environment { get; set; }
        public string? Host { get; set; }
        public DateTime? EventUtc { get; set; }
        public string? SubmitOutcome { get; set; }
        public string? MetadataJson { get; set; }
        public bool IsInternal { get; set; }
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
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
            Environment = ResolveEnvironment(req.Environment),
            Host = string.IsNullOrWhiteSpace(req.Host) ? Request.Host.ToString() : req.Host,
            EventUtc = req.EventUtc ?? DateTime.UtcNow,
            ReceivedUtc = DateTime.UtcNow,
            SubmitOutcome = TrimOrNull(req.SubmitOutcome),
            MetadataJson = string.IsNullOrWhiteSpace(req.MetadataJson) ? null : req.MetadataJson,
            IsInternal = req.IsInternal || FounderGuard.IsFounder(User),
            AgentTrackingProfileId = resolved.Found ? resolved.Profile.Id : null,
            AgentSlug = resolved.Found ? resolved.CanonicalSlug : null
        };

        // Legacy local SQLite databases may have AnalyticsEvents.Id as non-rowid bigint PK
        // (not auto-generated). In that case we assign a monotonic Id so local ingest works.
        if (IsSqliteProvider())
        {
            var nextId =
                (await _db.AnalyticsEvents.AsNoTracking().MaxAsync(x => (long?)x.Id, HttpContext.RequestAborted) ?? 0L) + 1L;
            ev.Id = nextId;
        }

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

    // Preflight responder for CORS
    [HttpOptions]
    [IgnoreAntiforgeryToken]
    public IActionResult Options() => Ok();
}

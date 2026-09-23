using Microsoft.EntityFrameworkCore;
using Infrastructure.Data;
using Infrastructure.Leads;
using Microsoft.AspNetCore.Mvc;
using ProtectWebsite.Services.Meta;
using ProtectWebsite.Services.MetaSignal;
using ProtectWebsite.Services;
using ProtectWebsite.Services.Tracking;
using Shared.Analytics;

namespace Protect_Website.Controllers;

[Route("analytics")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("public-ingest")]
public sealed class AnalyticsController : Controller
{
    private readonly MasterAppDbContext _db;
    private readonly ILogger<AnalyticsController> _logger;

    public AnalyticsController(
        MasterAppDbContext db,
        ILogger<AnalyticsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    public sealed record BusinessEventRequest(Guid EventId, Guid SessionId, string Path);

    [HttpPost("business-page")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> BusinessPage([FromBody] BusinessEventRequest input,
        [FromServices] Infrastructure.WebsiteEditing.WebsiteDomainService domains, CancellationToken ct)
    {
        if (input.EventId == Guid.Empty || input.SessionId == Guid.Empty || input.Path is null ||
            input.Path.Length > 160 || !input.Path.StartsWith('/') || input.Path.Contains('?') || input.Path.Contains('#'))
            return BadRequest();
        if (!Uri.TryCreate(Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            origin.Scheme != "https" || !origin.IsDefaultPort || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0 ||
            !origin.IdnHost.Equals(Request.Host.Host, StringComparison.OrdinalIgnoreCase)) return BadRequest();
        var owner = await domains.ResolveAsync(origin.IdnHost, ct);
        if (owner is null) return NotFound();
        var version = await Infrastructure.WebsiteEditing.WebsiteContentStore.PublishedBusinessAsync(_db, owner.Value, ct);
        if (version?.CompiledPagesJson is null) return NotFound();
        using var pages = System.Text.Json.JsonDocument.Parse(version.CompiledPagesJson);
        if (!pages.RootElement.GetProperty("pages").TryGetProperty(input.Path, out _)) return NotFound();
        var prior = await _db.AnalyticsEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ClientEventId == input.EventId, ct);
        if (prior is not null) return prior.CommerceBusinessId == owner && prior.SessionId == input.SessionId.ToString("N") && prior.PageKey == input.Path
            ? Ok(new { accepted = true }) : Conflict();
        var context = UnifiedEventContextBuilder.Build(HttpContext, eventName: "page_view", sessionId: input.SessionId.ToString("N"),
            visitorId: input.SessionId.ToString("N"), pageKey: input.Path, host: origin.IdnHost,
            environment: "Production", isBrowserSignal: true, metaServerAuthorityEligible: false);
        var row = UnifiedEventMapper.ToAnalytics(context with { CommerceBusinessId = owner, WebsiteContentVersionId = version.Id });
        row.ClientEventId = input.EventId;
        UnifiedAnalyticsWriter.Write(_db, row);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            prior = await _db.AnalyticsEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ClientEventId == input.EventId, ct);
            if (prior is null) throw;
            if (prior.CommerceBusinessId != owner || prior.SessionId != input.SessionId.ToString("N") || prior.PageKey != input.Path) return Conflict();
        }
        return Ok(new { accepted = true });
    }

    [HttpPost("meta-signal")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> MetaSignal([FromBody] MetaSignalIngestRequest? request, CancellationToken cancellationToken)
    {
        if (request == null)
            return BadRequest(new { accepted = false, error = "Invalid meta signal payload." });

        var eventName = Normalize(request.EventName);
        if (string.IsNullOrWhiteSpace(eventName) || !MetaSignalEventCatalog.TryGet(eventName, out var definition))
        {
            return BadRequest(new
            {
                accepted = false,
                error = "Unknown meta signal event."
            });
        }

        if (!Guid.TryParse(request.EventId, out var clientEventId) || clientEventId == Guid.Empty)
            return BadRequest(new { accepted = false, error = "A stable event ID is required." });

        var existing = await _db.AnalyticsEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientEventId == clientEventId, cancellationToken);
        if (existing is not null)
            return DuplicateResult(existing, request, eventName, clientEventId);

        try
        {
            var trackingContext = BuildTrackingContext(request, eventName, definition);
            var analyticsEvent = UnifiedEventMapper.ToAnalytics(trackingContext);
            analyticsEvent.ClientEventId = clientEventId;
            UnifiedAnalyticsWriter.Write(_db, analyticsEvent);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _db.Entry(analyticsEvent).State = EntityState.Detached;
                var concurrent = await _db.AnalyticsEvents.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ClientEventId == clientEventId, cancellationToken);
                if (concurrent is null) throw;
                return DuplicateResult(concurrent, request, eventName, clientEventId);
            }

            return Json(new MetaSignalProcessResult
            {
                Accepted = true,
                EventName = eventName,
                EventId = Normalize(request.EventId) ?? analyticsEvent.EventId.ToString("N"),
                ScoreTier = Normalize(request.ScoreTier) ?? string.Empty,
                IntentScore = request.Score?.IntentScore ?? 0,
                EngagementScore = request.Score?.EngagementScore ?? 0,
                QualificationScore = request.Score?.QualificationScore ?? 0,
                FrictionScore = request.Score?.FrictionScore ?? 0,
                TotalSignalScore = request.Score?.TotalSignalScore ?? 0,
                MetaBrowserSent = request.BrowserEventSent,
                MetaServerSent = false,
                MetaServerStatus = "deferred_to_analytics_bridge"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meta signal analytics ingest failed for event={EventName}", eventName);
            return Json(new MetaSignalProcessResult
            {
                Accepted = false,
                Skipped = true,
                EventName = eventName,
                EventId = Normalize(request.EventId) ?? string.Empty,
                MetaServerStatus = "error",
                MetaServerNote = "server_exception"
            });
        }
    }

    private IActionResult DuplicateResult(Domain.Entities.AnalyticsEvent existing,
        MetaSignalIngestRequest request, string eventName, Guid eventId)
    {
        if (!string.Equals(existing.EventType, eventName, StringComparison.OrdinalIgnoreCase) ||
            existing.SessionId != Normalize(request.SessionId) ||
            existing.VisitorId != Normalize(request.VisitorId) ||
            existing.AgentTrackingProfileId != request.AgentTrackingProfileId ||
            !string.Equals(existing.AgentSlug, Normalize(request.AgentSlug), StringComparison.OrdinalIgnoreCase))
            return Conflict(new { accepted = false, error = "Event ID belongs to a different event." });
        return Json(new MetaSignalProcessResult
        {
            Accepted = true, Skipped = true, EventId = eventId.ToString("N"),
            EventName = eventName, MetaServerStatus = "duplicate_ignored"
        });
    }

    private UnifiedEventContext BuildTrackingContext(
        MetaSignalIngestRequest request,
        string eventName,
        MetaSignalEventDefinition definition)
    {
        var attribution = request.Attribution;
        var clientContext = request.ClientContext;
        var pageKey = Normalize(request.PageKey);
        var effectivePageKey = Normalize(request.EffectivePageKey) ?? pageKey;
        var pageVariant = Normalize(request.PageVariant);
        var pageMode = Normalize(request.PageMode);

        return new UnifiedEventContext
        {
            EventId = Normalize(request.EventId),
            EventName = eventName,
            EventCategory = Normalize(request.EventCategory) ?? definition.Category,
            EventUtc = DateTime.UtcNow,
            SessionId = Normalize(request.SessionId),
            VisitorId = Normalize(request.VisitorId),
            Url = Normalize(request.Url),
            Referrer = Normalize(request.Referrer),
            PageKey = pageKey,
            EffectivePageKey = effectivePageKey,
            PageVariant = pageVariant,
            PageMode = pageMode,
            DeviceType = Normalize(clientContext?.DeviceType),
            Browser = Normalize(clientContext?.Browser),
            OperatingSystem = Normalize(clientContext?.OperatingSystem),
            UserAgent = Normalize(clientContext?.UserAgent) ?? Request?.Headers["User-Agent"].ToString(),
            IpAddress = MetaLeadTrackingWorkflow.ResolveClientIpAddress(Request),
            ViewportWidth = clientContext?.ViewportWidth,
            ViewportHeight = clientContext?.ViewportHeight,
            ScreenWidth = clientContext?.ScreenWidth,
            ScreenHeight = clientContext?.ScreenHeight,
            WebDriver = clientContext?.WebDriver,
            IsHeadless = clientContext?.IsHeadless,
            MouseMoveCount = clientContext?.MouseMoveCount,
            HumanInteractionCount = clientContext?.HumanInteractionCount,
            VisibilityChangeCount = clientContext?.VisibilityChangeCount,
            Language = Normalize(clientContext?.Language),
            TimeZone = Normalize(clientContext?.TimeZone),
            UtmSource = Normalize(attribution?.UtmSource),
            UtmMedium = Normalize(attribution?.UtmMedium),
            UtmCampaign = Normalize(attribution?.UtmCampaign),
            UtmId = Normalize(attribution?.UtmId),
            UtmContent = Normalize(attribution?.UtmContent),
            MetaCampaignId = Normalize(attribution?.MetaCampaignId),
            MetaAdSetId = Normalize(attribution?.MetaAdSetId),
            MetaAdId = Normalize(attribution?.MetaAdId),
            Fbclid = Normalize(attribution?.Fbclid),
            AgentSlug = Normalize(request.AgentSlug),
            AgentTrackingProfileId = request.AgentTrackingProfileId,
            IsInternal = WebsiteLeadCaptureSafety.ShouldMarkAsInternalTest(Request?.Host.Host),
            Environment = EnvironmentLabelResolver.Resolve(),
            Host = Request?.Host.ToString(),
            QuoteType = Normalize(request.QuoteType) ?? "life",
            StepNumber = request.StepNumber,
            StepName = Normalize(request.StepName),
            BrowserEventSent = request.BrowserEventSent,
            IsBrowserSignal = true,
            IsServerAuthority = false,
            MetaServerAuthorityEligible = false,
            Metadata = new
            {
                Source = "meta_signal_browser_ingest",
                UpstreamMetaEventId = Normalize(request.EventId),
                EventCategory = Normalize(request.EventCategory) ?? definition.Category,
                PageVariant = pageVariant,
                PageMode = pageMode,
                StepNumber = request.StepNumber,
                StepName = Normalize(request.StepName),
                BrowserEventSent = request.BrowserEventSent,
                Fbc = Normalize(attribution?.Fbc),
                Fbp = Normalize(attribution?.Fbp),
                ScoreTier = Normalize(request.ScoreTier),
                IntentScore = request.Score?.IntentScore,
                EngagementScore = request.Score?.EngagementScore,
                QualificationScore = request.Score?.QualificationScore,
                FrictionScore = request.Score?.FrictionScore,
                TotalSignalScore = request.Score?.TotalSignalScore,
                BrowserMetadata = request.Metadata.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined
                    ? null
                    : (object)request.Metadata
            }
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

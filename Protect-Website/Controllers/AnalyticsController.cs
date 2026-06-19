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

        try
        {
            var trackingContext = BuildTrackingContext(request, eventName, definition);
            var analyticsEvent = UnifiedEventMapper.ToAnalytics(trackingContext);
            UnifiedAnalyticsWriter.Write(_db, analyticsEvent);
            await _db.SaveChangesAsync(cancellationToken);

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

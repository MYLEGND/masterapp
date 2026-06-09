using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProtectWebsite.Services.Meta;
using Shared.Analytics;

namespace ProtectWebsite.Services.MetaSignal;

public interface IMetaSignalIntelligenceService
{
    Task<MetaSignalProcessResult> IngestAsync(MetaSignalIngestRequest request, HttpContext? httpContext, CancellationToken cancellationToken = default);
    Task<MetaSignalProcessResult?> RecordConfirmedLeadAsync(MetaSignalConfirmedLeadRequest request, HttpContext? httpContext, CancellationToken cancellationToken = default);
}

public sealed class MetaSignalIntelligenceService : IMetaSignalIntelligenceService
{
    private static readonly TimeSpan SemanticDeduplicationWindow = TimeSpan.FromHours(2);

    private readonly MasterAppDbContext _db;
    private readonly IMetaConversionsApiService _metaConversionsApi;
    private readonly IMetaPixelResolutionService _metaPixelResolution;
    private readonly MetaSignalIntelligenceOptions _options;
    private readonly ILogger<MetaSignalIntelligenceService> _logger;

    public MetaSignalIntelligenceService(
        MasterAppDbContext db,
        IMetaConversionsApiService metaConversionsApi,
        IMetaPixelResolutionService metaPixelResolution,
        IOptions<MetaSignalIntelligenceOptions> options,
        ILogger<MetaSignalIntelligenceService> logger)
    {
        _db = db;
        _metaConversionsApi = metaConversionsApi;
        _metaPixelResolution = metaPixelResolution;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MetaSignalProcessResult> IngestAsync(MetaSignalIngestRequest request, HttpContext? httpContext, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        if (!_options.Enabled || !normalized.IsPaidLandingExperience)
        {
            return new MetaSignalProcessResult
            {
                Accepted = false,
                Skipped = true,
                EventName = normalized.EventName,
                EventId = normalized.EventId,
                MetaServerStatus = "disabled"
            };
        }

        if (!MetaSignalEventCatalog.TryGet(normalized.EventName, out var metaEventDefinition))
        {
            _logger.LogWarning("MetaSignal rejected unsupported event {EventName}", normalized.EventName);
            return new MetaSignalProcessResult
            {
                Accepted = false,
                Skipped = true,
                EventName = normalized.EventName,
                EventId = normalized.EventId,
                MetaServerStatus = "invalid_event"
            };
        }

        if (string.IsNullOrWhiteSpace(normalized.EventId) || string.IsNullOrWhiteSpace(normalized.QuoteType))
        {
            return new MetaSignalProcessResult
            {
                Accepted = false,
                Skipped = true,
                EventName = normalized.EventName,
                EventId = normalized.EventId,
                MetaServerStatus = "invalid_payload"
            };
        }

        var existing = await _db.MetaSignalEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EventId == normalized.EventId, cancellationToken);
        if (existing != null)
        {
            return ToProcessResult(existing, duplicate: true, metaServerStatus: existing.MetaServerSent ? "sent" : "duplicate");
        }

        var priorEvents = await LoadPriorEventsAsync(normalized.SessionId, normalized.VisitorId, normalized.QuoteType, cancellationToken);
        var attribution = await ResolveAttributionAsync(normalized, priorEvents, cancellationToken);
        var fbp = MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbp");
        var fbc = MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbc");
        var clientIp = MetaLeadTrackingWorkflow.ResolveClientIpAddress(httpContext?.Request);
        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();

        var accumulator = BuildAccumulator(priorEvents, normalized);
        var score = ComputeScores(accumulator);
        var eventCategory = !string.IsNullOrWhiteSpace(normalized.EventCategory)
            ? normalized.EventCategory!
            : ResolveEventCategory(normalized.EventName);
        var deduplicationKey = BuildDeduplicationKey(normalized, score.ScoreTier);

        var semanticDuplicate = await _db.MetaSignalEvents.AsNoTracking()
            .Where(x => x.MetaDeduplicationKey == deduplicationKey)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (semanticDuplicate != null &&
            (DateTime.UtcNow - semanticDuplicate.CreatedUtc) <= SemanticDeduplicationWindow)
        {
            return ToProcessResult(semanticDuplicate, duplicate: true, metaServerStatus: semanticDuplicate.MetaServerSent ? "sent" : "semantic_duplicate");
        }

        _logger.LogInformation(
            "MetaSignal received event={EventName} quoteType={QuoteType} session={SessionId} scoreTier={ScoreTier} totalScore={TotalScore} dedupKey={DedupKey}",
            normalized.EventName,
            normalized.QuoteType,
            normalized.SessionId ?? normalized.VisitorId ?? "(unknown)",
            score.ScoreTier,
            score.TotalSignalScore,
            deduplicationKey);

        var row = new MetaSignalEvent
        {
            CreatedUtc = DateTime.UtcNow,
            EventId = normalized.EventId,
            EventName = normalized.EventName,
            EventCategory = eventCategory,
            SessionId = normalized.SessionId,
            VisitorId = normalized.VisitorId,
            QuoteType = normalized.QuoteType,
            PageKey = normalized.PageKey,
            EffectivePageKey = normalized.EffectivePageKey,
            PageVariant = normalized.PageVariant,
            PageMode = normalized.PageMode,
            TrafficType = attribution.TrafficType,
            FunnelStep = normalized.StepNumber,
            StepName = normalized.StepName,
            IntentScore = score.IntentScore,
            EngagementScore = score.EngagementScore,
            QualificationScore = score.QualificationScore,
            FrictionScore = score.FrictionScore,
            TotalSignalScore = score.TotalSignalScore,
            ScoreTier = score.ScoreTier,
            MetaBrowserSent = _options.SendBrowserEvents && normalized.BrowserEventSent,
            MetaServerSent = false,
            MetaDeduplicationKey = deduplicationKey,
            UtmSource = attribution.UtmSource,
            UtmMedium = attribution.UtmMedium,
            UtmCampaign = attribution.UtmCampaign,
            UtmId = attribution.UtmId,
            UtmContent = attribution.UtmContent,
            FbclidPresent = !string.IsNullOrWhiteSpace(attribution.Fbclid),
            FbcPresent = !string.IsNullOrWhiteSpace(fbc),
            FbpPresent = !string.IsNullOrWhiteSpace(fbp),
            Referrer = normalized.Referrer,
            UserAgentHash = SafeHash(userAgent),
            IpHash = SafeHash(clientIp),
            AgentTrackingProfileId = normalized.AgentTrackingProfileId,
            AgentSlug = normalized.AgentSlug,
            Environment = EnvironmentLabelResolver.Resolve(),
            Host = httpContext?.Request.Host.ToString(),
            MetadataJson = BuildMetadataJson(normalized.Metadata, normalized, attribution, score, browserSent: _options.SendBrowserEvents && normalized.BrowserEventSent, metaServerStatus: "pending", metaServerNote: null)
        };

        MetaConversionsApiResult? metaServerResult = null;
        if (_options.SendServerEvents && metaEventDefinition.AllowServerForward)
        {
            var pixelContext = await ResolvePixelContextAsync(normalized.AgentTrackingProfileId, normalized.AgentSlug, cancellationToken);
            metaServerResult = await _metaConversionsApi.SendEventAsync(
                new MetaConversionsApiEventRequest
                {
                    CorrelationId = Guid.NewGuid(),
                    EventName = normalized.EventName,
                    EventId = normalized.EventId,
                    QuoteType = normalized.QuoteType,
                    PageKey = normalized.EffectivePageKey ?? normalized.PageKey ?? string.Empty,
                    OfferKey = normalized.QuoteType,
                    EventSourceUrl = normalized.Url,
                    ClientIpAddress = clientIp,
                    ClientUserAgent = userAgent,
                    Fbp = fbp,
                    Fbc = fbc,
                    Fbclid = attribution.Fbclid,
                    AllowHashedContactData = false,
                    EventUtc = DateTime.UtcNow,
                    PixelId = pixelContext.PixelId,
                    AccessToken = pixelContext.AccessToken,
                    TestEventCode = pixelContext.TestEventCode,
                    PixelOwnerType = pixelContext.PixelOwnerType,
                    CustomData = BuildMetaCustomData(row)
                },
                cancellationToken);
            row.MetaServerSent = metaServerResult.Sent;
            row.MetadataJson = BuildMetadataJson(normalized.Metadata, normalized, attribution, score, browserSent: row.MetaBrowserSent, metaServerStatus: metaServerResult.Status, metaServerNote: metaServerResult.Note);
            _logger.LogInformation(
                "MetaSignal Meta CAPI attempted event={EventName} quoteType={QuoteType} status={Status} dedupKey={DedupKey}",
                normalized.EventName,
                normalized.QuoteType,
                metaServerResult.Status,
                deduplicationKey);
        }

        if (_options.PersistEvents)
        {
            _db.MetaSignalEvents.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return ToProcessResult(
            row,
            duplicate: false,
            metaServerStatus: metaServerResult?.Status ?? "not_attempted",
            metaServerNote: metaServerResult?.Note);
    }

    public async Task<MetaSignalProcessResult?> RecordConfirmedLeadAsync(MetaSignalConfirmedLeadRequest request, HttpContext? httpContext, CancellationToken cancellationToken = default)
    {
        var pageMode = Normalize(request.PageMode);
        if (!_options.Enabled || !string.Equals(pageMode, "paid_landing", StringComparison.OrdinalIgnoreCase))
            return null;

        var quoteType = NormalizeQuoteType(request.QuoteType);
        if (string.IsNullOrWhiteSpace(quoteType) || request.LeadId == Guid.Empty || string.IsNullOrWhiteSpace(request.LeadEventId))
            return null;

        var priorEvents = await LoadPriorEventsAsync(Normalize(request.SessionId), Normalize(request.VisitorId), quoteType, cancellationToken);
        var attribution = await ResolveAttributionAsync(
            new NormalizedMetaSignalRequest
            {
                EventName = "Lead",
                EventId = request.LeadEventId,
                QuoteType = quoteType,
                PageKey = Normalize(request.PageKey),
                EffectivePageKey = Normalize(request.EffectivePageKey),
                PageVariant = Normalize(request.PageVariant),
                PageMode = pageMode,
                Url = Normalize(request.Url),
                Referrer = Normalize(request.Referrer),
                SessionId = Normalize(request.SessionId),
                VisitorId = Normalize(request.VisitorId),
                AgentTrackingProfileId = request.AgentTrackingProfileId,
                AgentSlug = Normalize(request.AgentSlug),
                Attribution = new MetaSignalAttributionPayload
                {
                    UtmSource = request.UtmSource,
                    UtmMedium = request.UtmMedium,
                    UtmCampaign = request.UtmCampaign,
                    UtmId = request.UtmId,
                    UtmContent = request.UtmContent,
                    Fbclid = request.Fbclid
                },
                Metadata = request.Metadata,
                IsPaidLandingExperience = true
            },
            priorEvents,
            cancellationToken);

        var accumulator = BuildAccumulator(
            priorEvents,
            new NormalizedMetaSignalRequest
            {
                EventName = "Lead",
                EventId = request.LeadEventId,
                QuoteType = quoteType,
                PageKey = Normalize(request.PageKey),
                EffectivePageKey = Normalize(request.EffectivePageKey),
                PageVariant = Normalize(request.PageVariant),
                PageMode = pageMode,
                StepNumber = 3,
                StepName = "lead_submitted",
                Url = Normalize(request.Url),
                Referrer = Normalize(request.Referrer),
                SessionId = Normalize(request.SessionId),
                VisitorId = Normalize(request.VisitorId),
                AgentTrackingProfileId = request.AgentTrackingProfileId,
                AgentSlug = Normalize(request.AgentSlug),
                Attribution = new MetaSignalAttributionPayload
                {
                    UtmSource = request.UtmSource,
                    UtmMedium = request.UtmMedium,
                    UtmCampaign = request.UtmCampaign,
                    UtmId = request.UtmId,
                    UtmContent = request.UtmContent,
                    Fbclid = request.Fbclid
                },
                Metadata = request.Metadata,
                IsPaidLandingExperience = true
            });
        var score = ComputeScores(accumulator);
        var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
        var clientIp = MetaLeadTrackingWorkflow.ResolveClientIpAddress(httpContext?.Request);

        if (!await _db.MetaSignalEvents.AsNoTracking().AnyAsync(x => x.EventId == request.LeadEventId, cancellationToken))
        {
            var leadRow = new MetaSignalEvent
            {
                CreatedUtc = request.CreatedUtc == default ? DateTime.UtcNow : request.CreatedUtc,
                LeadId = request.LeadId,
                EventId = request.LeadEventId,
                EventName = "Lead",
                EventCategory = "conversion",
                SessionId = Normalize(request.SessionId),
                VisitorId = Normalize(request.VisitorId),
                QuoteType = quoteType,
                PageKey = Normalize(request.PageKey),
                EffectivePageKey = Normalize(request.EffectivePageKey),
                PageVariant = Normalize(request.PageVariant),
                PageMode = pageMode,
                TrafficType = attribution.TrafficType,
                FunnelStep = 3,
                StepName = "lead_submitted",
                IntentScore = score.IntentScore,
                EngagementScore = score.EngagementScore,
                QualificationScore = score.QualificationScore,
                FrictionScore = score.FrictionScore,
                TotalSignalScore = Math.Max(100, score.TotalSignalScore),
                ScoreTier = "SubmittedLead",
                MetaBrowserSent = false,
                MetaServerSent = request.LeadMetaServerSent,
                MetaDeduplicationKey = BuildLeadDeduplicationKey(
                    quoteType,
                    Normalize(request.SessionId),
                    Normalize(request.VisitorId),
                    "Lead",
                    request.Phone,
                    request.Email),
                UtmSource = attribution.UtmSource,
                UtmMedium = attribution.UtmMedium,
                UtmCampaign = attribution.UtmCampaign,
                UtmId = attribution.UtmId,
                UtmContent = attribution.UtmContent,
                FbclidPresent = !string.IsNullOrWhiteSpace(attribution.Fbclid),
                FbcPresent = !string.IsNullOrWhiteSpace(MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbc")),
                FbpPresent = !string.IsNullOrWhiteSpace(MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbp")),
                Referrer = Normalize(request.Referrer),
                UserAgentHash = SafeHash(userAgent),
                IpHash = SafeHash(clientIp),
                AgentTrackingProfileId = request.AgentTrackingProfileId,
                AgentSlug = Normalize(request.AgentSlug),
                Environment = EnvironmentLabelResolver.Resolve(),
                Host = httpContext?.Request.Host.ToString(),
                MetadataJson = BuildLeadMetadataJson(request, attribution, score, "Lead", request.LeadMetaServerStatus ?? (request.LeadMetaServerSent ? "sent" : "not_attempted"), request.LeadMetaServerNote)
            };

            var duplicateLeadRow = await _db.MetaSignalEvents.AsNoTracking()
                .Where(x => x.MetaDeduplicationKey == leadRow.MetaDeduplicationKey && x.EventName == "Lead")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicateLeadRow == null ||
                (DateTime.UtcNow - duplicateLeadRow.CreatedUtc) > SemanticDeduplicationWindow)
            {
                if (_options.PersistEvents)
                {
                    _db.MetaSignalEvents.Add(leadRow);
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }

        }

        var isQualifiedLead = IsQualifiedLead(request, score, priorEvents);
        if (!isQualifiedLead)
        {
            return new MetaSignalProcessResult
            {
                Accepted = true,
                EventName = "Lead",
                EventId = request.LeadEventId,
                ScoreTier = "SubmittedLead",
                IntentScore = score.IntentScore,
                EngagementScore = score.EngagementScore,
                QualificationScore = score.QualificationScore,
                FrictionScore = score.FrictionScore,
                TotalSignalScore = Math.Max(100, score.TotalSignalScore),
                MetaBrowserSent = false,
                MetaServerSent = request.LeadMetaServerSent,
                MetaServerStatus = request.LeadMetaServerStatus ?? "not_attempted",
                MetaServerNote = request.LeadMetaServerNote
            };
        }

        var qualifiedEventId = Guid.NewGuid().ToString("N");
        var pixelContext = new ResolvedMetaPixelContext
        {
            PixelId = request.PixelId,
            AccessToken = request.AccessToken,
            TestEventCode = request.TestEventCode,
            PixelOwnerType = request.PixelOwnerType ?? MetaPixelOwnerTypes.None,
            AgentTrackingProfileId = request.AgentTrackingProfileId,
            AgentSlug = Normalize(request.AgentSlug)
        };
        if (string.IsNullOrWhiteSpace(pixelContext.PixelId))
        {
            pixelContext = await ResolvePixelContextAsync(request.AgentTrackingProfileId, Normalize(request.AgentSlug), cancellationToken);
        }

        var capiResult = _options.SendServerEvents
            ? await _metaConversionsApi.SendEventAsync(
                new MetaConversionsApiEventRequest
                {
                    LeadId = request.LeadId,
                    CorrelationId = Guid.NewGuid(),
                    EventName = "QualifiedLead",
                    EventId = qualifiedEventId,
                    QuoteType = quoteType,
                    PageKey = request.EffectivePageKey,
                    OfferKey = quoteType,
                    EventSourceUrl = Normalize(request.Url),
                    ClientIpAddress = clientIp,
                    ClientUserAgent = userAgent,
                    Fbp = MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbp"),
                    Fbc = MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbc"),
                    Fbclid = attribution.Fbclid,
                    Email = request.Email,
                    Phone = request.Phone,
                    AllowHashedContactData = request.AllowHashedContactData,
                    EventUtc = DateTime.UtcNow,
                    PixelId = pixelContext.PixelId,
                    AccessToken = pixelContext.AccessToken,
                    TestEventCode = pixelContext.TestEventCode,
                    PixelOwnerType = pixelContext.PixelOwnerType,
                    CustomData = new Dictionary<string, object?>(BuildMetaCustomData(new MetaSignalEvent
                    {
                        EventName = "QualifiedLead",
                        QuoteType = quoteType,
                        PageKey = request.PageKey,
                        EffectivePageKey = request.EffectivePageKey,
                        PageVariant = request.PageVariant,
                        PageMode = pageMode,
                        SessionId = request.SessionId,
                        UtmSource = attribution.UtmSource,
                        UtmMedium = attribution.UtmMedium,
                        UtmCampaign = attribution.UtmCampaign,
                        UtmId = attribution.UtmId,
                        UtmContent = attribution.UtmContent,
                        FunnelStep = 3,
                        StepName = "qualified_lead",
                        ScoreTier = "SubmittedLead",
                        TotalSignalScore = Math.Max(100, score.TotalSignalScore),
                        EngagementScore = score.EngagementScore,
                        QualificationScore = score.QualificationScore,
                        FrictionScore = score.FrictionScore,
                        TrafficType = attribution.TrafficType,
                        FbclidPresent = !string.IsNullOrWhiteSpace(attribution.Fbclid),
                        FbcPresent = !string.IsNullOrWhiteSpace(MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbc")),
                        FbpPresent = !string.IsNullOrWhiteSpace(MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbp"))
                    }))
                    {
                        ["lead_quality"] = "qualified"
                    }
                },
                cancellationToken)
            : new MetaConversionsApiResult { Attempted = false, Sent = false, Status = "not_attempted" };

        var qualifiedRow = new MetaSignalEvent
        {
            CreatedUtc = DateTime.UtcNow,
            LeadId = request.LeadId,
            EventId = qualifiedEventId,
            EventName = "QualifiedLead",
            EventCategory = "conversion",
            SessionId = Normalize(request.SessionId),
            VisitorId = Normalize(request.VisitorId),
            QuoteType = quoteType,
            PageKey = Normalize(request.PageKey),
            EffectivePageKey = Normalize(request.EffectivePageKey),
            PageVariant = Normalize(request.PageVariant),
            PageMode = pageMode,
            TrafficType = attribution.TrafficType,
            FunnelStep = 3,
            StepName = "qualified_lead",
            IntentScore = score.IntentScore,
            EngagementScore = score.EngagementScore,
            QualificationScore = score.QualificationScore,
            FrictionScore = score.FrictionScore,
            TotalSignalScore = Math.Max(100, score.TotalSignalScore),
            ScoreTier = "SubmittedLead",
            MetaBrowserSent = false,
            MetaServerSent = capiResult.Sent,
            MetaDeduplicationKey = BuildLeadDeduplicationKey(
                quoteType,
                Normalize(request.SessionId),
                Normalize(request.VisitorId),
                "QualifiedLead",
                request.Phone,
                request.Email),
            UtmSource = attribution.UtmSource,
            UtmMedium = attribution.UtmMedium,
            UtmCampaign = attribution.UtmCampaign,
            UtmId = attribution.UtmId,
            UtmContent = attribution.UtmContent,
            FbclidPresent = !string.IsNullOrWhiteSpace(attribution.Fbclid),
            FbcPresent = !string.IsNullOrWhiteSpace(MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbc")),
            FbpPresent = !string.IsNullOrWhiteSpace(MetaLeadTrackingWorkflow.ResolveCookieValue(httpContext?.Request, "_fbp")),
            Referrer = Normalize(request.Referrer),
            UserAgentHash = SafeHash(userAgent),
            IpHash = SafeHash(clientIp),
            AgentTrackingProfileId = request.AgentTrackingProfileId,
            AgentSlug = Normalize(request.AgentSlug),
            Environment = EnvironmentLabelResolver.Resolve(),
            Host = httpContext?.Request.Host.ToString(),
            MetadataJson = BuildLeadMetadataJson(request, attribution, score, "QualifiedLead", capiResult.Status, capiResult.Note)
        };

        var duplicateQualifiedRow = await _db.MetaSignalEvents.AsNoTracking()
            .Where(x => x.MetaDeduplicationKey == qualifiedRow.MetaDeduplicationKey && x.EventName == "QualifiedLead")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (duplicateQualifiedRow != null &&
            (DateTime.UtcNow - duplicateQualifiedRow.CreatedUtc) <= SemanticDeduplicationWindow)
        {
            return ToProcessResult(duplicateQualifiedRow, duplicate: true, metaServerStatus: duplicateQualifiedRow.MetaServerSent ? "sent" : "semantic_duplicate");
        }

        if (_options.PersistEvents)
        {
            _db.MetaSignalEvents.Add(qualifiedRow);
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "MetaSignal qualified lead recorded leadId={LeadId} quoteType={QuoteType} status={Status}",
            request.LeadId,
            quoteType,
            capiResult.Status);

        return new MetaSignalProcessResult
        {
            Accepted = true,
            EventName = "QualifiedLead",
            EventId = qualifiedEventId,
            ScoreTier = "SubmittedLead",
            IntentScore = score.IntentScore,
            EngagementScore = score.EngagementScore,
            QualificationScore = score.QualificationScore,
            FrictionScore = score.FrictionScore,
            TotalSignalScore = Math.Max(100, score.TotalSignalScore),
            MetaBrowserSent = false,
            MetaServerSent = capiResult.Sent,
            MetaServerStatus = capiResult.Status,
            MetaServerNote = capiResult.Note,
            DeduplicationKey = qualifiedRow.MetaDeduplicationKey
        };
    }

    private async Task<List<MetaSignalEvent>> LoadPriorEventsAsync(string? sessionId, string? visitorId, string quoteType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(visitorId))
            return new List<MetaSignalEvent>();

        var query = _db.MetaSignalEvents.AsNoTracking()
            .Where(x => x.QuoteType == quoteType);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            query = query.Where(x => x.SessionId == sessionId);
        }
        else
        {
            query = query.Where(x => x.VisitorId == visitorId);
        }

        return await query
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);
    }

    private async Task<ResolvedAttribution> ResolveAttributionAsync(NormalizedMetaSignalRequest request, List<MetaSignalEvent> priorEvents, CancellationToken cancellationToken)
    {
        var direct = ResolvedAttribution.FromRequest(request.Attribution);
        if (direct.HasSignal)
            return direct.WithTrafficType();

        var prior = priorEvents
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.UtmSource) ||
                !string.IsNullOrWhiteSpace(x.UtmMedium) ||
                !string.IsNullOrWhiteSpace(x.UtmCampaign) ||
                !string.IsNullOrWhiteSpace(x.UtmId) ||
                x.FbclidPresent ||
                !string.IsNullOrWhiteSpace(ReadResolvedAttributionValue(x.MetadataJson, "metaCampaignId")) ||
                !string.IsNullOrWhiteSpace(ReadResolvedAttributionValue(x.MetadataJson, "metaAdSetId")) ||
                !string.IsNullOrWhiteSpace(ReadResolvedAttributionValue(x.MetadataJson, "metaAdId")))
            .OrderBy(x => x.CreatedUtc)
            .Select(ResolvedAttribution.FromEvent)
            .FirstOrDefault(x => x.HasSignal);

        if (prior != null)
            return prior.WithTrafficType();

        if (!string.IsNullOrWhiteSpace(request.SessionId) || !string.IsNullOrWhiteSpace(request.VisitorId))
        {
            var analyticsQuery = _db.AnalyticsEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                analyticsQuery = analyticsQuery.Where(x => x.SessionId == request.SessionId);
            }
            else
            {
                analyticsQuery = analyticsQuery.Where(x => x.VisitorId == request.VisitorId);
            }

            var analyticsRow = await analyticsQuery
                .OrderBy(x => x.EventUtc)
                .Select(x => new
                {
                    x.UtmSource,
                    x.UtmMedium,
                    x.UtmCampaign,
                    x.UtmId,
                    x.UtmContent,
                    x.Fbclid,
                    x.MetaCampaignId,
                    x.MetaAdSetId,
                    x.MetaAdId
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (analyticsRow != null)
            {
                return new ResolvedAttribution
                {
                    UtmSource = Normalize(analyticsRow.UtmSource),
                    UtmMedium = Normalize(analyticsRow.UtmMedium),
                    UtmCampaign = Normalize(analyticsRow.UtmCampaign),
                    UtmId = Normalize(analyticsRow.UtmId),
                    UtmContent = Normalize(analyticsRow.UtmContent),
                    Fbclid = Normalize(analyticsRow.Fbclid),
                    MetaCampaignId = Normalize(analyticsRow.MetaCampaignId),
                    MetaAdSetId = Normalize(analyticsRow.MetaAdSetId),
                    MetaAdId = Normalize(analyticsRow.MetaAdId)
                }.WithTrafficType();
            }
        }

        return direct.WithTrafficType();
    }

    private SignalAccumulator BuildAccumulator(IEnumerable<MetaSignalEvent> priorEvents, NormalizedMetaSignalRequest current)
    {
        var accumulator = new SignalAccumulator();
        foreach (var prior in priorEvents)
        {
            ApplyEvent(accumulator, prior.EventName, prior.FunnelStep, ParseMetadata(prior.MetadataJson));
        }

        ApplyEvent(accumulator, current.EventName, current.StepNumber, current.Metadata);
        return accumulator;
    }

    private void ApplyEvent(SignalAccumulator accumulator, string eventName, int? stepNumber, JsonElement metadata)
    {
        if (TryReadString(metadata, "protectingWho", out var protectingWho))
            accumulator.ProtectingWho = protectingWho;
        if (TryReadString(metadata, "coverageGoal", out var coverageGoal))
            accumulator.CoverageGoal = coverageGoal;
        if (TryReadString(metadata, "ageRange", out var ageRange))
            accumulator.AgeRange = ageRange;

        if (TryReadBool(metadata, "rapidBounce", out var rapidBounce) && rapidBounce)
            accumulator.RapidBounce = true;
        if (TryReadBool(metadata, "contactStepReached", out var reachedContactStep) && reachedContactStep)
            accumulator.ContactStepReached = true;
        if (TryReadBool(metadata, "requiredContactFieldsComplete", out var requiredContactFieldsComplete) && requiredContactFieldsComplete)
            accumulator.RequiredContactFieldsCompleted = true;
        if (TryReadBool(metadata, "phoneCompleted", out var phoneCompleted) && phoneCompleted)
            accumulator.PhoneCompleted = true;
        if (TryReadBool(metadata, "contactInputStarted", out var contactInputStarted) && contactInputStarted)
            accumulator.ContactInputStarted = true;
        if (TryReadBool(metadata, "contactStepAbandon", out var contactStepAbandon) && contactStepAbandon)
            accumulator.ContactStepAbandon = true;

        switch (eventName)
        {
            case "ViewContent":
                accumulator.LandingViewed = true;
                break;
            case "RapidBounce":
                accumulator.RapidBounce = true;
                break;
            case "SessionEngaged5s":
                accumulator.Stayed5Seconds = true;
                break;
            case "SessionEngaged15s":
                accumulator.Stayed15Seconds = true;
                break;
            case "MeaningfulScroll":
                accumulator.MeaningfulScroll = true;
                break;
            case "LeadFormStart":
                accumulator.FirstQuestionAnswered = true;
                break;
            case "DiscoveryComplete":
                accumulator.FirstQuestionAnswered = true;
                accumulator.CompletedSteps.Add(1);
                break;
            case "FunnelStepComplete":
                if (stepNumber.HasValue)
                    accumulator.CompletedSteps.Add(stepNumber.Value);
                break;
            case "RecommendationViewed":
                accumulator.RecommendationViewed = true;
                break;
            case "ContactStepReached":
                accumulator.ContactStepReached = true;
                break;
            case "ContactInputStarted":
                accumulator.ContactInputStarted = true;
                break;
            case "PhoneFieldCompleted":
                accumulator.PhoneCompleted = true;
                break;
            case "RequiredContactFieldsCompleted":
                accumulator.RequiredContactFieldsCompleted = true;
                break;
            case "SubmitAttempt":
                accumulator.SubmitAttempted = true;
                break;
            case "Lead":
            case "QualifiedLead":
                accumulator.LeadSubmitted = true;
                break;
            case "FieldError":
                accumulator.FieldErrorCount++;
                break;
            case "Backtrack":
                accumulator.BacktrackCount++;
                break;
            case "DeadClick":
                accumulator.DeadClickCount++;
                break;
            case "RageClick":
                accumulator.RageClickCount++;
                break;
            case "AbandonedHighIntentLead":
                accumulator.HighIntentAbandon = true;
                accumulator.ContactStepAbandon = accumulator.ContactStepReached || accumulator.RequiredContactFieldsCompleted || accumulator.ContactStepAbandon;
                break;
        }

        BackfillProgressState(accumulator, eventName);
    }

    private static void BackfillProgressState(SignalAccumulator accumulator, string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return;

        switch (eventName)
        {
            case "LeadFormStart":
            case "DiscoveryComplete":
            case "FunnelStepComplete":
            case "RecommendationViewed":
            case "ContactStepReached":
            case "ContactInputStarted":
            case "PhoneFieldCompleted":
            case "RequiredContactFieldsCompleted":
            case "SubmitAttempt":
            case "Lead":
            case "QualifiedLead":
                accumulator.FirstQuestionAnswered = true;
                break;
        }

        switch (eventName)
        {
            case "DiscoveryComplete":
            case "FunnelStepComplete":
            case "RecommendationViewed":
            case "ContactStepReached":
            case "ContactInputStarted":
            case "PhoneFieldCompleted":
            case "RequiredContactFieldsCompleted":
            case "SubmitAttempt":
            case "Lead":
            case "QualifiedLead":
                accumulator.CompletedSteps.Add(1);
                break;
        }

        switch (eventName)
        {
            case "ContactStepReached":
            case "ContactInputStarted":
            case "PhoneFieldCompleted":
            case "RequiredContactFieldsCompleted":
            case "SubmitAttempt":
            case "Lead":
            case "QualifiedLead":
                accumulator.ContactStepReached = true;
                break;
        }
    }

    private MetaSignalScoreResult ComputeScores(SignalAccumulator state)
    {
        var w = _options.Weights;
        var engagement = 0;
        if (state.LandingViewed) engagement += w.LandingViewed;
        if (state.Stayed5Seconds) engagement += w.Stay5Seconds;
        if (state.Stayed15Seconds) engagement += w.Stay15Seconds;
        if (state.MeaningfulScroll) engagement += w.MeaningfulScroll;
        if (state.FirstQuestionAnswered) engagement += w.FirstQuestionAnswered;
        if (state.CompletedSteps.Contains(1)) engagement += w.Step1Completed;
        if (state.CompletedSteps.Contains(2)) engagement += w.Step2Completed;
        if (state.RecommendationViewed) engagement += w.RecommendationViewed;
        if (state.ContactStepReached) engagement += w.ContactStepReached;
        if (state.ContactInputStarted) engagement += w.ContactInputStarted;
        if (state.PhoneCompleted) engagement += w.PhoneCompleted;
        if (state.RequiredContactFieldsCompleted) engagement += w.RequiredContactCompleted;
        if (state.SubmitAttempted) engagement += w.SubmitAttempted;
        if (state.LeadSubmitted) engagement += w.SuccessfulLeadSubmitted;

        var qualification = ResolveProtectingWhoScore(state.ProtectingWho, w)
            + ResolveCoverageGoalScore(state.CoverageGoal, w)
            + ResolveAgeRangeScore(state.AgeRange, w);

        var friction = 0;
        if (state.RapidBounce) friction += w.RapidBounce;
        friction += Math.Min(state.FieldErrorCount, 3) * w.FieldError;
        friction += Math.Min(state.BacktrackCount, 3) * w.Backtrack;
        friction += Math.Min(state.DeadClickCount, 2) * w.DeadClick;
        friction += Math.Min(state.RageClickCount, 2) * w.RageClick;
        if (state.ContactStepReached && !state.RequiredContactFieldsCompleted && (state.FieldErrorCount > 0 || state.ContactStepAbandon))
            friction += w.ContactFriction;
        if (state.ContactStepAbandon)
            friction += w.ContactStepAbandon;
        if (state.HighIntentAbandon)
            friction += w.HighIntentAbandon;

        var intentScore = Math.Clamp(qualification + (int)Math.Round(engagement * 0.4m) + friction, 0, 120);
        var totalScore = ApplyBehaviorScoreCap(Math.Clamp(engagement + qualification + friction, 0, 120), state);

        return new MetaSignalScoreResult
        {
            IntentScore = intentScore,
            EngagementScore = engagement,
            QualificationScore = qualification,
            FrictionScore = friction,
            TotalSignalScore = totalScore,
            ScoreTier = ResolveScoreTier(state)
        };
    }

    private static int ApplyBehaviorScoreCap(int totalScore, SignalAccumulator state)
    {
        if (state.LeadSubmitted)
            return Math.Max(100, totalScore);
        if (state.SubmitAttempted || state.RequiredContactFieldsCompleted)
            return Math.Min(99, totalScore);
        if (state.ContactStepReached || state.ContactInputStarted || state.PhoneCompleted)
            return Math.Min(89, totalScore);
        if (state.RecommendationViewed)
            return Math.Min(79, totalScore);
        if (state.CompletedSteps.Contains(1) || state.FirstQuestionAnswered)
            return Math.Min(64, totalScore);
        if (state.Stayed5Seconds || state.Stayed15Seconds || state.MeaningfulScroll)
            return Math.Min(39, totalScore);
        return Math.Min(19, totalScore);
    }

    private async Task<ResolvedMetaPixelContext> ResolvePixelContextAsync(Guid? agentTrackingProfileId, string? agentSlug, CancellationToken cancellationToken)
    {
        var isFounderPath = !agentTrackingProfileId.HasValue && string.IsNullOrWhiteSpace(agentSlug);
        return await _metaPixelResolution.ResolveForLeadAsync(agentTrackingProfileId, agentSlug, isFounderPath, cancellationToken);
    }

    private static Dictionary<string, object?> BuildMetaCustomData(MetaSignalEvent row)
    {
        var customData = new Dictionary<string, object?>
        {
            ["quote_type"] = row.QuoteType,
            ["page_key"] = row.PageKey,
            ["effective_page_key"] = row.EffectivePageKey,
            ["page_variant"] = row.PageVariant,
            ["page_mode"] = row.PageMode,
            ["funnel_step"] = row.FunnelStep,
            ["step_name"] = row.StepName,
            ["score_tier"] = row.ScoreTier,
            ["total_signal_score"] = row.TotalSignalScore,
            ["engagement_score"] = row.EngagementScore,
            ["qualification_score"] = row.QualificationScore,
            ["friction_score"] = row.FrictionScore,
            ["traffic_type"] = row.TrafficType,
            ["utm_source"] = row.UtmSource,
            ["utm_medium"] = row.UtmMedium,
            ["utm_campaign"] = row.UtmCampaign,
            ["utm_id"] = row.UtmId,
            ["utm_content"] = row.UtmContent,
            ["fbclid_present"] = row.FbclidPresent,
            ["fbc_present"] = row.FbcPresent,
            ["fbp_present"] = row.FbpPresent
        };

        return customData
            .Where(x => x.Value != null && (!(x.Value is string s) || !string.IsNullOrWhiteSpace(s)))
            .ToDictionary(x => x.Key, x => x.Value);
    }

    private static MetaSignalProcessResult ToProcessResult(MetaSignalEvent row, bool duplicate, string metaServerStatus, string? metaServerNote = null)
    {
        return new MetaSignalProcessResult
        {
            Accepted = true,
            Duplicate = duplicate,
            EventName = row.EventName,
            EventId = row.EventId,
            ScoreTier = row.ScoreTier ?? string.Empty,
            IntentScore = row.IntentScore,
            EngagementScore = row.EngagementScore,
            QualificationScore = row.QualificationScore,
            FrictionScore = row.FrictionScore,
            TotalSignalScore = row.TotalSignalScore,
            MetaBrowserSent = row.MetaBrowserSent,
            MetaServerSent = row.MetaServerSent,
            MetaServerStatus = metaServerStatus,
            MetaServerNote = metaServerNote,
            DeduplicationKey = row.MetaDeduplicationKey
        };
    }

    private static string BuildMetadataJson(JsonElement metadata, NormalizedMetaSignalRequest request, ResolvedAttribution attribution, MetaSignalScoreResult score, bool browserSent, string metaServerStatus, string? metaServerNote)
    {
        var root = ParseMetadataObject(metadata);
        root["clientScore"] = JsonSerializer.SerializeToNode(request.Score ?? new MetaSignalScorePayload(), JsonSerializerOptions.Web);
        root["clientScoreTier"] = request.ScoreTier;
        root["resolvedTrafficType"] = attribution.TrafficType;
        root["resolvedAttribution"] = JsonSerializer.SerializeToNode(attribution, JsonSerializerOptions.Web);
        root["serverScore"] = JsonSerializer.SerializeToNode(score, JsonSerializerOptions.Web);
        root["browserEventSent"] = browserSent;
        root["metaServerStatus"] = metaServerStatus;
        if (!string.IsNullOrWhiteSpace(metaServerNote))
            root["metaServerNote"] = metaServerNote;
        return root.ToJsonString(JsonSerializerOptions.Web);
    }

    private static string BuildLeadMetadataJson(MetaSignalConfirmedLeadRequest request, ResolvedAttribution attribution, MetaSignalScoreResult score, string eventName, string metaServerStatus, string? metaServerNote)
    {
        var root = ParseMetadataObject(request.Metadata);
        root["resolvedTrafficType"] = attribution.TrafficType;
        root["resolvedAttribution"] = JsonSerializer.SerializeToNode(attribution, JsonSerializerOptions.Web);
        root["serverScore"] = JsonSerializer.SerializeToNode(score with { ScoreTier = "SubmittedLead", TotalSignalScore = Math.Max(100, score.TotalSignalScore) }, JsonSerializerOptions.Web);
        root["metaSignalEventName"] = eventName;
        root["metaServerStatus"] = metaServerStatus;
        if (!string.IsNullOrWhiteSpace(metaServerNote))
            root["metaServerNote"] = metaServerNote;
        return root.ToJsonString(JsonSerializerOptions.Web);
    }

    private static JsonElement ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return default;
        }
    }

    private static JsonObject ParseMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            try
            {
                return JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }

        return new JsonObject();
    }

    private static string ResolveEventCategory(string eventName) => eventName switch
    {
        "ViewContent" => "page",
        "LeadFormStart" or
        "DiscoveryComplete" or
        "FunnelStepComplete" or
        "RecommendationViewed" or
        "ContactStepReached" or
        "ContactInputStarted" or
        "PhoneFieldCompleted" or
        "RequiredContactFieldsCompleted" or
        "SubmitAttempt" => "funnel",
        "HighIntentLeadSignal" or "LeadReadySignal" => "threshold",
        "Lead" or "QualifiedLead" => "conversion",
        "AbandonedHighIntentLead" => "abandon",
        "FieldError" or "Backtrack" or "DeadClick" or "RageClick" => "friction",
        "RapidBounce" => "friction",
        _ => "engagement"
    };

    private static string BuildDeduplicationKey(NormalizedMetaSignalRequest request, string scoreTier)
    {
        var sessionKey = request.SessionId ?? request.VisitorId ?? "anonymous";
        var stepKey = request.StepNumber?.ToString() ?? request.StepName ?? scoreTier;
        return $"{request.QuoteType}:{sessionKey}:{request.EventName}:{stepKey}";
    }

    private static string BuildLeadDeduplicationKey(
        string quoteType,
        string? sessionId,
        string? visitorId,
        string eventName,
        string? phone,
        string? email)
    {
        var sessionKey = sessionId ?? visitorId ?? "anonymous";
        var contactToken = SafeHash($"{Normalize(phone)}|{Normalize(email)}");
        return $"{quoteType}:{sessionKey}:{eventName}:{contactToken}";
    }

    private static bool IsQualifiedLead(MetaSignalConfirmedLeadRequest request, MetaSignalScoreResult score, IReadOnlyCollection<MetaSignalEvent> priorEvents)
    {
        var evaluation = WebsiteLeadSignalClassifier.Evaluate(
            new WebsiteLeadSignalInput(
                FunnelStartObserved: true,
                ContactStepReached: true,
                ContactInputStarted: true,
                RequiredContactFieldsCompleted: true,
                SubmitAttempted: true,
                ConfirmedWebsiteLead: true,
                Phone: request.Phone,
                Email: request.Email,
                TotalSignalScore: score.TotalSignalScore),
            WebsiteLeadSignalRules.Default);

        return evaluation.QualifiedLead;
    }

    private static int ResolveProtectingWhoScore(string? protectingWho, MetaSignalScoreWeights weights) => Normalize(protectingWho) switch
    {
        "just_me" => weights.ProtectingJustMe,
        "spouse_or_partner" => weights.ProtectingSpouseOrPartner,
        "children" => weights.ProtectingChildren,
        "family" => weights.ProtectingFamily,
        "not_sure" => weights.ProtectingNotSure,
        _ => 0
    };

    private static int ResolveCoverageGoalScore(string? coverageGoal, MetaSignalScoreWeights weights) => Normalize(coverageGoal) switch
    {
        "replace_income" => weights.GoalReplaceIncome,
        "final_expenses" => weights.GoalFinalExpenses,
        "mortgage_or_bills" => weights.GoalMortgageOrBills,
        "leave_something" => weights.GoalLeaveSomething,
        "not_sure" => weights.GoalNotSure,
        _ => 0
    };

    private static int ResolveAgeRangeScore(string? ageRange, MetaSignalScoreWeights weights) => Normalize(ageRange) switch
    {
        "18-24" => weights.Age18To24,
        "25-34" => weights.Age25To34,
        "35-44" => weights.Age35To44,
        "45-54" => weights.Age45To54,
        "55+" => weights.Age55Plus,
        _ => 0
    };

    private static string ResolveScoreTier(SignalAccumulator state)
    {
        if (state.LeadSubmitted)
            return "SubmittedLead";
        if (state.SubmitAttempted || state.RequiredContactFieldsCompleted)
            return "SubmitAttempter";
        if (state.ContactStepReached || state.ContactInputStarted || state.PhoneCompleted)
            return "ContactStepViewer";
        if (state.RecommendationViewed)
            return "RecommendationViewer";
        if (state.CompletedSteps.Contains(1) || state.FirstQuestionAnswered)
            return "FunnelStarter";
        if (state.Stayed5Seconds || state.Stayed15Seconds || state.MeaningfulScroll)
            return "EngagedVisitor";
        return "ColdVisitor";
    }

    private static string? ReadResolvedAttributionValue(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("resolvedAttribution", out var resolvedAttribution) ||
                resolvedAttribution.ValueKind != JsonValueKind.Object ||
                !resolvedAttribution.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return Normalize(property.GetString());
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadString(JsonElement metadata, string propertyName, out string? value)
    {
        value = null;
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty(propertyName, out var property))
            return false;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = Normalize(property.GetString());
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadBool(JsonElement metadata, string propertyName, out bool value)
    {
        value = false;
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty(propertyName, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }
        return false;
    }

    private static string? SafeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeQuoteType(string? value)
    {
        var normalized = Normalize(value)?.ToLowerInvariant();
        return normalized switch
        {
            "term" or "termlife" or "term_life" => "term_life",
            "wholelife" or "whole_life" => "whole_life",
            "finalexpense" or "final_expense" => "final_expense",
            "mortgage" or "mortgageprotection" or "mortgage_protection" => "mortgage_protection",
            "life" => "life",
            "iul" => "iul",
            _ => normalized ?? string.Empty
        };
    }

    private static NormalizedMetaSignalRequest NormalizeRequest(MetaSignalIngestRequest request)
    {
        var pageMode = Normalize(request.PageMode);
        return new NormalizedMetaSignalRequest
        {
            EventName = Normalize(request.EventName) ?? string.Empty,
            EventId = Normalize(request.EventId) ?? string.Empty,
            QuoteType = NormalizeQuoteType(request.QuoteType),
            PageKey = Normalize(request.PageKey),
            EffectivePageKey = Normalize(request.EffectivePageKey) ?? Normalize(request.PageKey),
            PageVariant = Normalize(request.PageVariant),
            PageMode = pageMode,
            EventCategory = Normalize(request.EventCategory),
            StepNumber = request.StepNumber,
            StepName = Normalize(request.StepName),
            Url = Normalize(request.Url),
            Referrer = Normalize(request.Referrer),
            SessionId = Normalize(request.SessionId),
            VisitorId = Normalize(request.VisitorId),
            AgentTrackingProfileId = request.AgentTrackingProfileId,
            AgentSlug = Normalize(request.AgentSlug),
            BrowserEventSent = request.BrowserEventSent,
            ScoreTier = Normalize(request.ScoreTier),
            Score = request.Score,
            Attribution = request.Attribution ?? new MetaSignalAttributionPayload(),
            Metadata = request.Metadata,
            IsPaidLandingExperience = string.Equals(pageMode, "paid_landing", StringComparison.OrdinalIgnoreCase)
        };
    }

    private sealed class SignalAccumulator
    {
        public string? ProtectingWho { get; set; }
        public string? CoverageGoal { get; set; }
        public string? AgeRange { get; set; }
        public bool LandingViewed { get; set; }
        public bool Stayed5Seconds { get; set; }
        public bool Stayed15Seconds { get; set; }
        public bool MeaningfulScroll { get; set; }
        public bool FirstQuestionAnswered { get; set; }
        public HashSet<int> CompletedSteps { get; } = new();
        public bool RecommendationViewed { get; set; }
        public bool ContactStepReached { get; set; }
        public bool ContactInputStarted { get; set; }
        public bool PhoneCompleted { get; set; }
        public bool RequiredContactFieldsCompleted { get; set; }
        public bool SubmitAttempted { get; set; }
        public bool LeadSubmitted { get; set; }
        public int FieldErrorCount { get; set; }
        public int BacktrackCount { get; set; }
        public int DeadClickCount { get; set; }
        public int RageClickCount { get; set; }
        public bool RapidBounce { get; set; }
        public bool HighIntentAbandon { get; set; }
        public bool ContactStepAbandon { get; set; }
    }

    private sealed class NormalizedMetaSignalRequest
    {
        public string EventName { get; init; } = string.Empty;
        public string EventId { get; init; } = string.Empty;
        public string QuoteType { get; init; } = string.Empty;
        public string? PageKey { get; init; }
        public string? EffectivePageKey { get; init; }
        public string? PageVariant { get; init; }
        public string? PageMode { get; init; }
        public string? EventCategory { get; init; }
        public int? StepNumber { get; init; }
        public string? StepName { get; init; }
        public string? Url { get; init; }
        public string? Referrer { get; init; }
        public string? SessionId { get; init; }
        public string? VisitorId { get; init; }
        public Guid? AgentTrackingProfileId { get; init; }
        public string? AgentSlug { get; init; }
        public bool BrowserEventSent { get; init; }
        public string? ScoreTier { get; init; }
        public MetaSignalScorePayload? Score { get; init; }
        public MetaSignalAttributionPayload Attribution { get; init; } = new();
        public JsonElement Metadata { get; init; }
        public bool IsPaidLandingExperience { get; init; }
    }

    private sealed record MetaSignalScoreResult
    {
        public int IntentScore { get; init; }
        public int EngagementScore { get; init; }
        public int QualificationScore { get; init; }
        public int FrictionScore { get; init; }
        public int TotalSignalScore { get; init; }
        public string ScoreTier { get; init; } = string.Empty;
    }

    private sealed record ResolvedAttribution
    {
        public string? UtmSource { get; init; }
        public string? UtmMedium { get; init; }
        public string? UtmCampaign { get; init; }
        public string? UtmId { get; init; }
        public string? UtmContent { get; init; }
        public string? Fbclid { get; init; }
        public string? MetaCampaignId { get; init; }
        public string? MetaAdSetId { get; init; }
        public string? MetaAdId { get; init; }
        public string TrafficType { get; init; } = "Unknown";

        public bool HasSignal =>
            !string.IsNullOrWhiteSpace(UtmSource) ||
            !string.IsNullOrWhiteSpace(UtmMedium) ||
            !string.IsNullOrWhiteSpace(UtmCampaign) ||
            !string.IsNullOrWhiteSpace(UtmId) ||
            !string.IsNullOrWhiteSpace(Fbclid) ||
            !string.IsNullOrWhiteSpace(MetaCampaignId) ||
            !string.IsNullOrWhiteSpace(MetaAdSetId) ||
            !string.IsNullOrWhiteSpace(MetaAdId);

        public ResolvedAttribution WithTrafficType()
        {
            return this with
            {
                TrafficType = ClassifyTrafficType(UtmSource, UtmMedium, UtmCampaign, Fbclid, MetaCampaignId, MetaAdSetId, MetaAdId)
            };
        }

        public static ResolvedAttribution FromRequest(MetaSignalAttributionPayload? payload)
        {
            if (payload == null)
                return new ResolvedAttribution();

            return new ResolvedAttribution
            {
                UtmSource = Normalize(payload.UtmSource),
                UtmMedium = Normalize(payload.UtmMedium),
                UtmCampaign = Normalize(payload.UtmCampaign),
                UtmId = Normalize(payload.UtmId),
                UtmContent = Normalize(payload.UtmContent),
                Fbclid = Normalize(payload.Fbclid),
                MetaCampaignId = Normalize(payload.MetaCampaignId),
                MetaAdSetId = Normalize(payload.MetaAdSetId),
                MetaAdId = Normalize(payload.MetaAdId)
            };
        }

        public static ResolvedAttribution FromEvent(MetaSignalEvent row)
        {
            return new ResolvedAttribution
            {
                UtmSource = Normalize(row.UtmSource) ?? ReadResolvedAttributionValue(row.MetadataJson, "utmSource"),
                UtmMedium = Normalize(row.UtmMedium) ?? ReadResolvedAttributionValue(row.MetadataJson, "utmMedium"),
                UtmCampaign = Normalize(row.UtmCampaign) ?? ReadResolvedAttributionValue(row.MetadataJson, "utmCampaign"),
                UtmId = Normalize(row.UtmId) ?? ReadResolvedAttributionValue(row.MetadataJson, "utmId"),
                UtmContent = Normalize(row.UtmContent) ?? ReadResolvedAttributionValue(row.MetadataJson, "utmContent"),
                Fbclid = row.FbclidPresent ? "present" : null,
                MetaCampaignId = ReadResolvedAttributionValue(row.MetadataJson, "metaCampaignId"),
                MetaAdSetId = ReadResolvedAttributionValue(row.MetadataJson, "metaAdSetId"),
                MetaAdId = ReadResolvedAttributionValue(row.MetadataJson, "metaAdId")
            };
        }
    }

    private static string ClassifyTrafficType(
        string? utmSource,
        string? utmMedium,
        string? utmCampaign,
        string? fbclid,
        string? metaCampaignId,
        string? metaAdSetId,
        string? metaAdId)
    {
        var source = Normalize(utmSource)?.ToLowerInvariant();
        var medium = Normalize(utmMedium)?.ToLowerInvariant();
        var campaign = Normalize(utmCampaign)?.ToLowerInvariant();
        var hasMetaIds =
            !string.IsNullOrWhiteSpace(Normalize(metaCampaignId)) ||
            !string.IsNullOrWhiteSpace(Normalize(metaAdSetId)) ||
            !string.IsNullOrWhiteSpace(Normalize(metaAdId));

        if (!string.IsNullOrWhiteSpace(fbclid) || hasMetaIds)
            return "PaidAds";
        if (medium is "cpc" or "ppc" or "paid" or "paidsearch" or "display" or "paid_social" or "social_paid" or "remarketing" or "retargeting" or "paid_search" or "paid-social")
            return "PaidAds";
        if (source is "adwords" or "googleads" or "google_ads" or "gads" or "bingads" or "meta_ads" or "facebook_ads" or "instagram_ads" or "paidsearch" or "display" or "paid_social" or "cpc" or "ppc" or "remarketing" or "retargeting")
            return "PaidAds";
        if (medium is "organic" or "seo" or "organic_search")
            return "Organic";
        if (medium is "(none)" or "direct")
            return "Direct";
        if (medium is "referral" or "partner")
            return "Referral";
        if (source is "google" or "bing" or "yahoo" or "duckduckgo" or "brave" or "ecosia" or "search")
            return "Organic";
        if (source is "facebook" or "fb" or "meta" or "instagram" or "tiktok" or "youtube" or "linkedin" or "reddit" or "x" or "twitter" or "pinterest" or "nextdoor" or "partner" or "newsletter")
            return "Referral";
        if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(medium) && string.IsNullOrWhiteSpace(campaign))
            return "Direct";
        return "Unknown";
    }
}

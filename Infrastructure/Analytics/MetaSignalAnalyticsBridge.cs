using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Infrastructure.Analytics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Analytics;
using Shared.Meta;

namespace Infrastructure.Analytics;

public sealed class MetaSignalAnalyticsBridge : BackgroundService
{
    public static IReadOnlyList<string> SourceEventTypes { get; } = Array.AsReadOnly(BuildSourceEventTypes());

    private static readonly BridgeMapping ViewContentMapping =
        new("ViewContent", "page", FunnelStep: 1, StepName: "view_content", IntentScore: 5, EngagementScore: 5, QualificationScore: 0, FrictionScore: 0, ScoreTier: "ViewContent");

    private static readonly BridgeMapping LeadMapping =
        new("Lead", "conversion", FunnelStep: 3, StepName: "lead_submitted", IntentScore: 100, EngagementScore: 100, QualificationScore: 100, FrictionScore: 0, ScoreTier: "SubmittedLead");

    private static readonly BridgeMapping QualifiedLeadMapping =
        new("QualifiedLead", "conversion", FunnelStep: 3, StepName: "qualified_lead", IntentScore: 120, EngagementScore: 120, QualificationScore: 120, FrictionScore: 0, ScoreTier: "QualifiedLead");

    private static readonly BridgeMapping AppointmentBookedMapping =
        new("AppointmentBooked", "conversion", FunnelStep: 4, StepName: "appointment_booked", IntentScore: 120, EngagementScore: 120, QualificationScore: 120, FrictionScore: 0, ScoreTier: "AppointmentBooked");

    private static readonly BridgeMapping AppointmentCompletedMapping =
        new("AppointmentCompleted", "conversion", FunnelStep: 5, StepName: "appointment_completed", IntentScore: 160, EngagementScore: 160, QualificationScore: 160, FrictionScore: 0, ScoreTier: "AppointmentCompleted");

    private static readonly BridgeMapping ApplicationSubmittedMapping =
        new("ApplicationSubmitted", "conversion", FunnelStep: 6, StepName: "application_submitted", IntentScore: 220, EngagementScore: 220, QualificationScore: 220, FrictionScore: 0, ScoreTier: "ApplicationSubmitted");

    private static readonly BridgeMapping PolicyIssuedMapping =
        new("PolicyIssued", "conversion", FunnelStep: 7, StepName: "policy_issued", IntentScore: 320, EngagementScore: 320, QualificationScore: 320, FrictionScore: 0, ScoreTier: "PolicyIssued");

    private static readonly BridgeMapping PolicyPaidMapping =
        new("PolicyPaid", "conversion", FunnelStep: 8, StepName: "policy_paid", IntentScore: 500, EngagementScore: 500, QualificationScore: 500, FrictionScore: 0, ScoreTier: "PolicyPaid");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MetaSignalIntelligenceOptions> _options;
    private readonly ILogger<MetaSignalAnalyticsBridge> _logger;

    private long _watermark;
    private bool _initialized;

    public MetaSignalAnalyticsBridge(
        IServiceScopeFactory scopeFactory,
        IOptions<MetaSignalIntelligenceOptions> options,
        ILogger<MetaSignalAnalyticsBridge> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ProcessBatchAsync(stoppingToken))
                {
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MetaSignalAnalyticsBridge batch failed");
            }

            await Task.Delay(GetPollInterval(), stoppingToken);
        }
    }

    private async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var bridgeOptions = _options.Value;
        if (!bridgeOptions.Enabled || !bridgeOptions.PersistEvents || !bridgeOptions.AnalyticsBridgeEnabled)
            return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterAppDbContext>();

        if (!_initialized)
        {
            _watermark = await InitializeWatermarkAsync(db, cancellationToken);
            _initialized = true;
            _logger.LogInformation("MetaSignalAnalyticsBridge starting at analytics watermark {Watermark}", _watermark);
        }

        var batchSize = Math.Clamp(bridgeOptions.AnalyticsBridgeBatchSize, 10, 500);
        var analyticsEvents = await db.AnalyticsEvents
            .AsNoTracking()
            .Where(x => x.Id > _watermark && SourceEventTypes.Contains(x.EventType))
            .OrderBy(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (analyticsEvents.Count == 0)
            return false;

        foreach (var analyticsEvent in analyticsEvents)
        {
            try
            {
                await PersistAsync(db, analyticsEvent, cancellationToken, bridgeOptions.Weights);
                _watermark = analyticsEvent.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MetaSignalAnalyticsBridge failed sourceAnalyticsEventId={AnalyticsId} sourceEventType={EventType}",
                    analyticsEvent.Id,
                    analyticsEvent.EventType);
                // The scoped context is disposed before the next poll. Keep the
                // watermark behind the failed row so its stable event ID is retried.
                return false;
            }
        }

        return analyticsEvents.Count == batchSize;
    }

    /// <summary>Derives and persists a signal from a durably accepted canonical analytics event.</summary>
    public static async Task<bool> PersistAsync(MasterAppDbContext db, AnalyticsEvent source,
        CancellationToken cancellationToken = default, MetaSignalScoreWeights? weights = null)
    {
        var persisted = source.Id <= 0 ? null : await db.AnalyticsEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == source.Id && x.EventId == source.EventId, cancellationToken);
        if (persisted is null)
            throw new InvalidOperationException("Meta derivation requires a persisted analytics event.");
        var row = await TryBuildBridgeRowAsync(db, persisted, cancellationToken, weights);
        if (row is null || await AlreadyDerivedAsync(db, row, cancellationToken)) return false;
        UnifiedMetaSignalWriter.Write(db, row);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsDuplicateMetaSignalEvent(ex))
        {
            db.Entry(row).State = EntityState.Detached;
            if (!await AlreadyDerivedAsync(db, row, cancellationToken)) throw;
            return false;
        }
        return true;
    }

    private async Task<long> InitializeWatermarkAsync(MasterAppDbContext db, CancellationToken cancellationToken)
    {
        // Restart from the configured lookback floor, not from the highest
        // globally bridged event. A global high-water mark can skip an older eligible
        // row from another Founder/agent/business scope forever. Replaying the bounded
        // window is safe because bridge derivation is idempotent and duplicate-protected.
        var lookbackUtc = DateTime.UtcNow.AddHours(-Math.Clamp(_options.Value.AnalyticsBridgeStartupLookbackHours, 1, 168));
        var floor = await db.AnalyticsEvents
            .AsNoTracking()
            .Where(x => SourceEventTypes.Contains(x.EventType) && x.ReceivedUtc < lookbackUtc)
            .OrderByDescending(x => x.Id)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return floor ?? 0;
    }

    private static async Task<MetaSignalEvent?> TryBuildBridgeRowAsync(
        MasterAppDbContext db,
        AnalyticsEvent analyticsEvent,
        CancellationToken cancellationToken, MetaSignalScoreWeights? weights = null)
    {
        if (!TryResolveMapping(analyticsEvent, out var mapping, weights))
            return null;

        if (analyticsEvent.IsInternal ||
            (MetaSignalEventCatalog.IsServerAuthorityEvent(mapping.MetaEventName) &&
             !CanonicalAdvertisingEventProjection.CanProjectServer(analyticsEvent)) ||
            !MetaSignalSingleTruthPolicy.CanBridgeToServerAuthority(mapping.MetaEventName, analyticsEvent.MetadataJson))
            return null;

        var eventUtc = analyticsEvent.EventUtc == default ? analyticsEvent.ReceivedUtc : analyticsEvent.EventUtc;
        var pageVariant = ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "PageVariant")
            ?? ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "pageVariant");
        var pageMode = ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "PageMode")
            ?? ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "pageMode");

        var resolvedLead = await ResolveLeadAsync(db, analyticsEvent, eventUtc, cancellationToken);
        var leadId = resolvedLead?.LeadId;
        var trafficType = ClassifyTrafficType(
            analyticsEvent.UtmSource,
            analyticsEvent.UtmMedium,
            analyticsEvent.UtmCampaign,
            analyticsEvent.Fbclid,
            analyticsEvent.MetaCampaignId,
            analyticsEvent.MetaAdSetId,
            analyticsEvent.MetaAdId);

        var deduplicationKey = BuildDeduplicationKey(
            mapping.MetaEventName,
            leadId,
            analyticsEvent.SessionId,
            analyticsEvent.VisitorId,
            eventUtc);
        var upstreamMetaEventId = ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "UpstreamMetaEventId")
            ?? ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "upstreamMetaEventId");
        var leadDispatchState = string.Equals(mapping.MetaEventName, "Lead", StringComparison.OrdinalIgnoreCase)
            ? await ResolveLeadDispatchStateAsync(db, analyticsEvent, resolvedLead, leadId, eventUtc, cancellationToken)
            : null;

        var scopedEventId = ScopeEventId(
            analyticsEvent.CommerceBusinessId,
            !string.IsNullOrWhiteSpace(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "canonicalOutcomeEventId"))
                ? ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "canonicalOutcomeEventId")!
                : !string.IsNullOrWhiteSpace(leadDispatchState?.MetaEventId)
                ? leadDispatchState.MetaEventId!
                : !string.IsNullOrWhiteSpace(upstreamMetaEventId)
                    ? upstreamMetaEventId!
                    : analyticsEvent.EventId == Guid.Empty
                        ? $"analytics_bridge_{analyticsEvent.Id}"
                        : analyticsEvent.EventId.ToString("N"));
        var canonicalBrowserIdentity = analyticsEvent.ClientEventId == analyticsEvent.EventId &&
            ReadAnalyticsMetadataBoolean(analyticsEvent.MetadataJson, "isBrowserSignal") == true;
        var canonicalSourceIdentity = canonicalBrowserIdentity ||
            !string.IsNullOrWhiteSpace(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "canonicalOutcomeEventId"));
        if (canonicalBrowserIdentity)
        {
            // Globally unique accepted envelope IDs are shared verbatim by browser providers.
            scopedEventId = analyticsEvent.EventId.ToString("D");
            deduplicationKey = $"analytics:{analyticsEvent.EventId:N}:{mapping.MetaEventName}";
        }
        var stableServerOutcome = IsStableServerOutcome(analyticsEvent);
        if (stableServerOutcome)
        {
            deduplicationKey = ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "metaDeduplicationKey")
                ?? throw new InvalidOperationException("Server outcome requires stable deduplication identity.");
            scopedEventId = ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "upstreamMetaEventId")
                ?? throw new InvalidOperationException("Server outcome requires stable destination identity.");
            trafficType = analyticsEvent.TrackingVersion == "commerce-server-authority-v1" ? "ecommerce" : "crm";
        }
        var effectivePageKey = Normalize(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "EffectivePageKey"))
            ?? Normalize(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "effectivePageKey"))
            ?? Normalize(analyticsEvent.PageKey);
        var fbc = Normalize(resolvedLead?.Fbc)
            ?? Normalize(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "Fbc"))
            ?? Normalize(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "fbc"));
        var fbp = Normalize(resolvedLead?.Fbp)
            ?? Normalize(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "Fbp"))
            ?? Normalize(ReadAnalyticsMetadataString(analyticsEvent.MetadataJson, "fbp"));
        var agentId = analyticsEvent.CommerceBusinessId.HasValue
            ? null
            : analyticsEvent.AgentTrackingProfileId ?? resolvedLead?.AgentTrackingProfileId;
        var agentSlug = analyticsEvent.CommerceBusinessId.HasValue
            ? null
            : Normalize(analyticsEvent.AgentSlug) ?? Normalize(resolvedLead?.AgentSlug);
        var isServerAuthority = MetaSignalEventCatalog.IsServerAuthorityEvent(mapping.MetaEventName);

        return UnifiedMetaSignalWriter.Create(new UnifiedEventContext
        {
            EventId = scopedEventId,
            EventName = mapping.MetaEventName,
            EventCategory = mapping.EventCategory,
            EventUtc = eventUtc,
            SessionId = Normalize(analyticsEvent.SessionId),
            VisitorId = Normalize(analyticsEvent.VisitorId),
            QuoteType = Normalize(resolvedLead?.InterestType) ?? Normalize(analyticsEvent.QuoteType),
            PageKey = Normalize(analyticsEvent.PageKey),
            EffectivePageKey = effectivePageKey,
            PageVariant = Normalize(pageVariant),
            PageMode = Normalize(pageMode),
            UtmSource = Normalize(analyticsEvent.UtmSource),
            UtmMedium = Normalize(analyticsEvent.UtmMedium),
            UtmCampaign = Normalize(analyticsEvent.UtmCampaign),
            UtmId = Normalize(analyticsEvent.UtmId),
            UtmContent = Normalize(analyticsEvent.UtmContent),
            Fbclid = Normalize(analyticsEvent.Fbclid),
            Oppref = OpenAiClickReference.Normalize(analyticsEvent.Oppref),
            Fbc = fbc,
            Fbp = fbp,
            Referrer = Normalize(analyticsEvent.Referrer),
            DeviceType = Normalize(analyticsEvent.DeviceType),
            Browser = Normalize(analyticsEvent.Browser),
            OperatingSystem = Normalize(analyticsEvent.OperatingSystem),
            UserAgent = Normalize(analyticsEvent.UserAgent),
            ViewportWidth = analyticsEvent.ViewportWidth,
            ViewportHeight = analyticsEvent.ViewportHeight,
            ScreenWidth = analyticsEvent.ScreenWidth,
            ScreenHeight = analyticsEvent.ScreenHeight,
            WebDriver = analyticsEvent.WebDriver,
            IsHeadless = analyticsEvent.IsHeadless,
            MouseMoveCount = analyticsEvent.MouseMoveCount,
            HumanInteractionCount = analyticsEvent.HumanInteractionCount,
            VisibilityChangeCount = analyticsEvent.VisibilityChangeCount,
            Language = Normalize(analyticsEvent.Language),
            TimeZone = Normalize(analyticsEvent.TimeZone),
            AgentTrackingProfileId = agentId,
            AgentSlug = agentSlug,
            CommerceBusinessId = analyticsEvent.CommerceBusinessId,
            WebsiteContentVersionId = analyticsEvent.WebsiteContentVersionId,
            WebsiteBindingId = Normalize(analyticsEvent.WebsiteBindingId),
            Environment = Normalize(analyticsEvent.Environment),
            Host = Normalize(analyticsEvent.Host),
            IsBrowserSignal = false,
            IsServerAuthority = isServerAuthority,
            MetaServerAuthorityEligible = isServerAuthority
        }, row =>
        {
            row.LeadId = leadId;
            row.TrafficType = trafficType;
            row.FunnelStep = mapping.FunnelStep;
            row.StepName = mapping.StepName;
            row.IntentScore = mapping.IntentScore;
            row.EngagementScore = mapping.EngagementScore;
            row.QualificationScore = mapping.QualificationScore;
            row.FrictionScore = mapping.FrictionScore;
            row.TotalSignalScore = mapping.TotalSignalScore
                ?? Math.Max(0, mapping.IntentScore + mapping.EngagementScore + mapping.QualificationScore + mapping.FrictionScore);
            row.ScoreTier = mapping.ScoreTier;
            row.MetaBrowserSent = ReadAnalyticsMetadataBoolean(analyticsEvent.MetadataJson, "BrowserEventSent") ?? false;
            row.MetaServerSent = leadDispatchState?.MetaServerSent ?? false;
            row.MetaDeduplicationKey = deduplicationKey;
            row.UserAgentHash = SafeHash(Normalize(analyticsEvent.UserAgent) ?? Normalize(resolvedLead?.ClientUserAgent));
            row.IpHash = SafeHash(Normalize(analyticsEvent.IpAddress) ?? Normalize(resolvedLead?.ClientIpAddress));
            row.MetadataJson = MetaSignalAnalyticsBridgeMetadata.Build(
                analyticsEvent,
                mapping.MetaEventName,
                deduplicationKey,
                trafficType,
                leadId,
                pageVariant,
                pageMode,
                leadDispatchState?.MetaEventId,
                leadDispatchState?.MetaServerStatus,
                leadDispatchState?.MetaServerNote);
            if (canonicalSourceIdentity)
            {
                var envelope = System.Text.Json.Nodes.JsonNode.Parse(row.MetadataJson)!.AsObject();
                envelope["canonicalSourceIdentity"] = true;
                row.MetadataJson = envelope.ToJsonString();
            }
            if (stableServerOutcome)
            {
                var payload = System.Text.Json.Nodes.JsonNode.Parse(analyticsEvent.MetadataJson!)!.AsObject();
                var canonical = System.Text.Json.Nodes.JsonNode.Parse(row.MetadataJson)!.AsObject();
                foreach (var property in canonical)
                    payload[property.Key] = property.Value?.DeepClone();
                payload["serverOutcomeStableIdentity"] = true;
                payload["metaPipelineOrigin"] = trafficType == "ecommerce"
                    ? "analytics_events>CommercePurchaseBridge" : "analytics_events>crm_outcome_service";
                row.MetadataJson = payload.ToJsonString();
            }
        });

    }

    private static async Task<LeadDispatchState?> ResolveLeadDispatchStateAsync(
        MasterAppDbContext db,
        AnalyticsEvent analyticsEvent,
        WebsiteLead? resolvedLead,
        Guid? leadId,
        DateTime eventUtc,
        CancellationToken cancellationToken)
    {
        var windowStartUtc = eventUtc.AddMinutes(-15);
        var windowEndUtc = eventUtc.AddMinutes(15);

        var query = db.AnalyticsEvents
            .AsNoTracking()
            .Where(x =>
                x.ReceivedUtc >= windowStartUtc &&
                x.ReceivedUtc <= windowEndUtc &&
                (x.EventType == "capi_event_success" || x.EventType == "capi_event_failure"));

        if (analyticsEvent.CommerceBusinessId.HasValue)
            query = query.Where(x => x.CommerceBusinessId == analyticsEvent.CommerceBusinessId);
        else if (analyticsEvent.AgentTrackingProfileId.HasValue)
            query = query.Where(x => x.CommerceBusinessId == null && x.AgentTrackingProfileId == analyticsEvent.AgentTrackingProfileId);

        if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId))
        {
            query = query.Where(x => x.SessionId == analyticsEvent.SessionId);
        }
        else if (!string.IsNullOrWhiteSpace(analyticsEvent.VisitorId))
        {
            query = query.Where(x => x.VisitorId == analyticsEvent.VisitorId);
        }

        if (!string.IsNullOrWhiteSpace(analyticsEvent.PageKey))
        {
            query = query.Where(x => x.PageKey == analyticsEvent.PageKey);
        }

        var candidates = await query
            .OrderByDescending(x => x.Id)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (leadId.HasValue &&
                MetaSignalAnalyticsBridgeMetadata.TryReadGuid(candidate.MetadataJson, "LeadId", out var candidateLeadId) &&
                candidateLeadId != leadId.Value)
            {
                continue;
            }

            return new LeadDispatchState(
                MetaEventId: MetaSignalAnalyticsBridgeMetadata.ReadString(candidate.MetadataJson, "EventId"),
                MetaServerSent: string.Equals(candidate.EventType, "capi_event_success", StringComparison.OrdinalIgnoreCase),
                MetaServerStatus: MetaSignalAnalyticsBridgeMetadata.ReadString(candidate.MetadataJson, "Status"),
                MetaServerNote: MetaSignalAnalyticsBridgeMetadata.ReadString(candidate.MetadataJson, "Note"));
        }

        var leadMetaTracking = MetaLeadTrackingJson.Read(resolvedLead?.MetadataJson);
        if (!string.IsNullOrWhiteSpace(leadMetaTracking?.EventId) ||
            !string.IsNullOrWhiteSpace(leadMetaTracking?.ServerCapiStatus) ||
            !string.IsNullOrWhiteSpace(leadMetaTracking?.ServerCapiNote))
        {
            return new LeadDispatchState(
                MetaEventId: leadMetaTracking?.EventId,
                MetaServerSent: string.Equals(leadMetaTracking?.ServerCapiStatus, "sent", StringComparison.OrdinalIgnoreCase),
                MetaServerStatus: leadMetaTracking?.ServerCapiStatus,
                MetaServerNote: leadMetaTracking?.ServerCapiNote);
        }

        return null;
    }

    private static async Task<WebsiteLead?> ResolveLeadAsync(
        MasterAppDbContext db,
        AnalyticsEvent analyticsEvent,
        DateTime eventUtc,
        CancellationToken cancellationToken)
    {
        var metadataLeadId = ReadLeadIdFromAnalytics(analyticsEvent.MetadataJson);
        if (metadataLeadId.HasValue)
        {
            var directLead = await db.WebsiteLeads
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.LeadId == metadataLeadId.Value, cancellationToken);

            if (directLead != null &&
                directLead.CommerceBusinessId == analyticsEvent.CommerceBusinessId &&
                (!analyticsEvent.CommerceBusinessId.HasValue ||
                 (!directLead.AgentTrackingProfileId.HasValue && string.IsNullOrWhiteSpace(directLead.AgentSlug))))
                return directLead;
        }

        if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId) || !string.IsNullOrWhiteSpace(analyticsEvent.VisitorId))
        {
            var windowStartUtc = eventUtc.AddDays(-7);
            var windowEndUtc = eventUtc.AddDays(2);

            var query = db.WebsiteLeads
                .AsNoTracking()
                .Where(x => x.CreatedUtc >= windowStartUtc && x.CreatedUtc <= windowEndUtc &&
                            x.CommerceBusinessId == analyticsEvent.CommerceBusinessId);

            if (analyticsEvent.CommerceBusinessId.HasValue)
                query = query.Where(x => x.AgentTrackingProfileId == null && (x.AgentSlug == null || x.AgentSlug == ""));
            else if (analyticsEvent.AgentTrackingProfileId.HasValue)
                query = query.Where(x => x.AgentTrackingProfileId == analyticsEvent.AgentTrackingProfileId);

            if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId))
            {
                query = query.Where(x => x.SessionId == analyticsEvent.SessionId);
            }
            else
            {
                query = query.Where(x => x.VisitorId == analyticsEvent.VisitorId);
            }

            if (!string.IsNullOrWhiteSpace(analyticsEvent.AgentSlug))
            {
                query = query.Where(x => x.AgentSlug == analyticsEvent.AgentSlug);
            }

            var bySession = await query
                .OrderByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (bySession != null)
                return bySession;
        }

        return null;
    }

    private static async Task<bool> AlreadyDerivedAsync(
        MasterAppDbContext db,
        MetaSignalEvent candidate,
        CancellationToken cancellationToken)
    {
        if (await db.MetaSignalEvents.AsNoTracking().AnyAsync(x =>
                x.EventId == candidate.EventId &&
                x.CommerceBusinessId == candidate.CommerceBusinessId &&
                x.AgentTrackingProfileId == candidate.AgentTrackingProfileId, cancellationToken))
            return true;

        if (ReadAnalyticsMetadataBoolean(candidate.MetadataJson, "serverOutcomeStableIdentity") == true)
            return await db.MetaSignalEvents.AsNoTracking().AnyAsync(x =>
                x.MetaDeduplicationKey == candidate.MetaDeduplicationKey &&
                x.CommerceBusinessId == candidate.CommerceBusinessId &&
                x.AgentTrackingProfileId == candidate.AgentTrackingProfileId, cancellationToken);

        if (ReadAnalyticsMetadataBoolean(candidate.MetadataJson, "canonicalSourceIdentity") == true)
            return false;

        // Required historical adapter only: old independently generated browser/provider
        // records did not share an action identity. Never apply this heuristic to new facts.
        var roundedMinute = RoundToNearestMinute(candidate.CreatedUtc);
        var windowStart = roundedMinute.AddMinutes(-1);
        var windowEnd = roundedMinute.AddMinutes(1);

        var query = db.MetaSignalEvents
            .AsNoTracking()
            .Where(x =>
                x.EventName == candidate.EventName &&
                x.CreatedUtc >= windowStart &&
                x.CreatedUtc < windowEnd &&
                x.CommerceBusinessId == candidate.CommerceBusinessId);

        if (candidate.CommerceBusinessId.HasValue)
            query = query.Where(x => x.AgentTrackingProfileId == null && (x.AgentSlug == null || x.AgentSlug == ""));
        else if (candidate.AgentTrackingProfileId.HasValue)
            query = query.Where(x => x.AgentTrackingProfileId == candidate.AgentTrackingProfileId);
        else if (!string.IsNullOrWhiteSpace(candidate.AgentSlug))
            query = query.Where(x => x.AgentSlug == candidate.AgentSlug);

        if (candidate.LeadId.HasValue)
        {
            query = query.Where(x => x.LeadId == candidate.LeadId);
        }
        else if (!string.IsNullOrWhiteSpace(candidate.SessionId))
        {
            query = query.Where(x => x.SessionId == candidate.SessionId);
        }
        else if (!string.IsNullOrWhiteSpace(candidate.VisitorId))
        {
            query = query.Where(x => x.VisitorId == candidate.VisitorId);
        }

        return await query.AnyAsync(cancellationToken);
    }

    private static bool IsStableServerOutcome(AnalyticsEvent source) =>
        source.TrackingVersion is "commerce-server-authority-v1" or "crm-production-authority-v1" &&
        ReadAnalyticsMetadataBoolean(source.MetadataJson, "isServerAuthority") == true &&
        ReadAnalyticsMetadataBoolean(source.MetadataJson, "isBrowserSignal") != true &&
        MetaSignalSingleTruthPolicy.CanBridgeToServerAuthority(source.EventType, source.MetadataJson) &&
        MetaSignalEventCatalog.IsServerAuthorityEvent(source.EventType) &&
        !string.IsNullOrWhiteSpace(ReadAnalyticsMetadataString(source.MetadataJson, "upstreamMetaEventId")) &&
        !string.IsNullOrWhiteSpace(ReadAnalyticsMetadataString(source.MetadataJson, "metaDeduplicationKey"));

    private static bool TryResolveMapping(AnalyticsEvent analyticsEvent, out BridgeMapping mapping, MetaSignalScoreWeights? weights = null)
    {
        mapping = null!;
        var normalized = Normalize(analyticsEvent.EventType);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (IsStableServerOutcome(analyticsEvent) && MetaSignalEventCatalog.TryGet(normalized, out var serverDefinition))
        {
            mapping = BuildMetaSignalSourceMapping(analyticsEvent, serverDefinition, weights);
            return true;
        }

        if (MetaSignalAnalyticsAliasCatalog.TryGet(normalized, out var aliasDefinition))
        {
            return TryResolveAnalyticsAliasMapping(analyticsEvent, aliasDefinition, out mapping, weights);
        }

        if (MetaSignalEventCatalog.TryGet(normalized, out var metaSignalDefinition))
        {
            mapping = BuildMetaSignalSourceMapping(analyticsEvent, metaSignalDefinition, weights);
            return true;
        }

        return false;
    }

    private static string[] BuildSourceEventTypes()
    {
        return MetaSignalAnalyticsAliasCatalog.AnalyticsEventNames.ToArray();
    }

    private static Guid? ReadLeadIdFromAnalytics(string? metadataJson)
    {
        if (MetaSignalAnalyticsBridgeMetadata.TryReadGuid(metadataJson, "LeadId", out var pascal))
            return pascal;

        if (MetaSignalAnalyticsBridgeMetadata.TryReadGuid(metadataJson, "leadId", out var camel))
            return camel;

        if (MetaSignalAnalyticsBridgeMetadata.TryReadGuid(metadataJson, "WebsiteLeadId", out var websiteLeadId))
            return websiteLeadId;

        if (MetaSignalAnalyticsBridgeMetadata.TryReadGuid(metadataJson, "websiteLeadId", out var websiteLeadIdCamel))
            return websiteLeadIdCamel;

        return null;
    }

    private static string? ReadAnalyticsMetadataString(string? metadataJson, string propertyName) =>
        CanonicalAdvertisingEventProjection.ReadString(metadataJson, propertyName);

    private static int? ReadAnalyticsMetadataInt32(string? metadataJson, string propertyName)
    {
        var raw = ReadAnalyticsMetadataString(metadataJson, propertyName);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? ReadAnalyticsMetadataBoolean(string? metadataJson, string propertyName)
    {
        var raw = ReadAnalyticsMetadataString(metadataJson, propertyName);
        return bool.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static bool TryResolveAnalyticsAliasMapping(
        AnalyticsEvent analyticsEvent,
        MetaSignalAnalyticsAliasDefinition aliasDefinition,
        out BridgeMapping mapping, MetaSignalScoreWeights? weights = null)
    {
        mapping = null!;
        if (!MetaSignalAnalyticsAliasCatalog.IsBridgeEligibleAnalyticsSource(
                aliasDefinition.AnalyticsEventName,
                analyticsEvent.ScrollPercent,
                analyticsEvent.DwellMilliseconds,
                analyticsEvent.EngagedMilliseconds,
                analyticsEvent.IsBounceCandidate))
        {
            return false;
        }

        if (!MetaSignalEventCatalog.TryGet(aliasDefinition.MetaSignalEventName, out var definition))
            return false;

        mapping = BuildMetaSignalSourceMapping(analyticsEvent, definition, weights);
        return true;
    }

    private static BridgeMapping BuildMetaSignalSourceMapping(AnalyticsEvent analyticsEvent, MetaSignalEventDefinition definition, MetaSignalScoreWeights? weights = null)
    {
        var metadataJson = analyticsEvent.MetadataJson;
        var defaults = ResolveDefaultBridgeMapping(definition, weights);
        var stepName = ReadAnalyticsMetadataString(metadataJson, "StepName")
            ?? ReadAnalyticsMetadataString(metadataJson, "stepName")
            ?? defaults.StepName;
        var scoreTier = ReadAnalyticsMetadataString(metadataJson, "ScoreTier")
            ?? ReadAnalyticsMetadataString(metadataJson, "scoreTier")
            ?? defaults.ScoreTier;

        return new BridgeMapping(
            MetaEventName: definition.Name,
            EventCategory: ReadAnalyticsMetadataString(metadataJson, "EventCategory")
                ?? ReadAnalyticsMetadataString(metadataJson, "eventCategory")
                ?? defaults.EventCategory,
            FunnelStep: ReadAnalyticsMetadataInt32(metadataJson, "StepNumber")
                ?? ReadAnalyticsMetadataInt32(metadataJson, "stepNumber")
                ?? defaults.FunnelStep,
            StepName: stepName,
            IntentScore: ReadAnalyticsMetadataInt32(metadataJson, "IntentScore")
                ?? ReadAnalyticsMetadataInt32(metadataJson, "intentScore")
                ?? defaults.IntentScore,
            EngagementScore: ReadAnalyticsMetadataInt32(metadataJson, "EngagementScore")
                ?? ReadAnalyticsMetadataInt32(metadataJson, "engagementScore")
                ?? defaults.EngagementScore,
            QualificationScore: ReadAnalyticsMetadataInt32(metadataJson, "QualificationScore")
                ?? ReadAnalyticsMetadataInt32(metadataJson, "qualificationScore")
                ?? defaults.QualificationScore,
            FrictionScore: ReadAnalyticsMetadataInt32(metadataJson, "FrictionScore")
                ?? ReadAnalyticsMetadataInt32(metadataJson, "frictionScore")
                ?? defaults.FrictionScore,
            ScoreTier: scoreTier,
            TotalSignalScore: ReadAnalyticsMetadataInt32(metadataJson, "TotalSignalScore")
                ?? ReadAnalyticsMetadataInt32(metadataJson, "totalSignalScore"));
    }

    private static BridgeMapping ResolveDefaultBridgeMapping(MetaSignalEventDefinition definition, MetaSignalScoreWeights? configuredWeights = null)
    {
        var weights = configuredWeights ?? new MetaSignalScoreWeights();
        return definition.Name switch
        {
            "ViewContent" => ViewContentMapping,
            "Lead" => LeadMapping,
            "QualifiedLead" => QualifiedLeadMapping,
            "AppointmentBooked" => AppointmentBookedMapping,
            "AppointmentCompleted" => AppointmentCompletedMapping,
            "ApplicationSubmitted" => ApplicationSubmittedMapping,
            "PolicyIssued" => PolicyIssuedMapping,
            "PolicyPaid" => PolicyPaidMapping,
            "SessionEngaged5s" => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: 1,
                StepName: "session_engaged_5s",
                IntentScore: 0,
                EngagementScore: weights.Stay5Seconds,
                QualificationScore: 0,
                FrictionScore: 0,
                ScoreTier: definition.Name),
            "SessionEngaged15s" => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: 1,
                StepName: "session_engaged_15s",
                IntentScore: 0,
                EngagementScore: weights.Stay15Seconds,
                QualificationScore: 0,
                FrictionScore: 0,
                ScoreTier: definition.Name),
            "MeaningfulScroll" => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: 1,
                StepName: "meaningful_scroll",
                IntentScore: 0,
                EngagementScore: weights.MeaningfulScroll,
                QualificationScore: 0,
                FrictionScore: 0,
                ScoreTier: definition.Name),
            "RapidBounce" => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: 1,
                StepName: "rapid_bounce",
                IntentScore: 0,
                EngagementScore: 0,
                QualificationScore: 0,
                FrictionScore: weights.RapidBounce,
                ScoreTier: definition.Name),
            "DeadClick" => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: 1,
                StepName: "dead_click",
                IntentScore: 0,
                EngagementScore: 0,
                QualificationScore: 0,
                FrictionScore: weights.DeadClick,
                ScoreTier: definition.Name),
            "RageClick" => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: 1,
                StepName: "rage_click",
                IntentScore: 0,
                EngagementScore: 0,
                QualificationScore: 0,
                FrictionScore: weights.RageClick,
                ScoreTier: definition.Name),
            _ => new BridgeMapping(
                MetaEventName: definition.Name,
                EventCategory: definition.Category,
                FunnelStep: ResolveFallbackFunnelStep(definition.Name),
                StepName: ResolveFallbackStepName(definition.Name),
                IntentScore: 0,
                EngagementScore: 0,
                QualificationScore: 0,
                FrictionScore: 0,
                ScoreTier: definition.Name)
        };
    }

    private static int ResolveFallbackFunnelStep(string eventName) =>
        eventName switch
        {
            "ViewContent" => 1,
            "LeadFormStart" => 2,
            "DiscoveryComplete" => 2,
            "FunnelStepComplete" => 2,
            "RecommendationViewed" => 2,
            "ContactStepReached" => 2,
            "ContactInputStarted" => 2,
            "PhoneFieldCompleted" => 2,
            "RequiredContactFieldsCompleted" => 2,
            "SubmitAttempt" => 2,
            "HighIntentLeadSignal" => 2,
            "LeadReadySignal" => 2,
            "Lead" => 3,
            "QualifiedLead" => 3,
            "AppointmentBooked" => 4,
            "AppointmentCompleted" => 5,
            "ApplicationSubmitted" => 6,
            "PolicyIssued" => 7,
            "PolicyPaid" => 8,
            _ => 0
        };

    private static string ResolveFallbackStepName(string eventName) =>
        eventName switch
        {
            "ViewContent" => "view_content",
            "LeadFormStart" => "lead_form_start",
            "DiscoveryComplete" => "discovery_complete",
            "FunnelStepComplete" => "funnel_step_complete",
            "RecommendationViewed" => "recommendation_viewed",
            "ContactStepReached" => "contact_step_reached",
            "ContactInputStarted" => "contact_input_started",
            "PhoneFieldCompleted" => "phone_field_completed",
            "RequiredContactFieldsCompleted" => "required_contact_fields_completed",
            "SubmitAttempt" => "submit_attempt",
            "HighIntentLeadSignal" => "high_intent_lead_signal",
            "LeadReadySignal" => "lead_ready_signal",
            "AbandonedHighIntentLead" => "abandoned_high_intent_lead",
            "FieldError" => "field_error",
            "Backtrack" => "backtrack",
            "DeadClick" => "dead_click",
            "RageClick" => "rage_click",
            "RapidBounce" => "rapid_bounce",
            _ => Normalize(eventName)?.ToLowerInvariant() ?? "event"
        };

    private static string BuildDeduplicationKey(
        string eventName,
        Guid? leadId,
        string? sessionId,
        string? visitorId,
        DateTime eventUtc)
    {
        var minuteBucket = RoundToNearestMinute(eventUtc).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var identityKey = leadId?.ToString("N")
            ?? Normalize(sessionId)
            ?? Normalize(visitorId)
            ?? "anonymous";

        return $"{eventName}:{identityKey}:{minuteBucket}";
    }

    private static DateTime RoundToNearestMinute(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        var rounded = utc.AddSeconds(30);
        return new DateTime(
            rounded.Year,
            rounded.Month,
            rounded.Day,
            rounded.Hour,
            rounded.Minute,
            0,
            DateTimeKind.Utc);
    }

    private TimeSpan GetPollInterval()
        => TimeSpan.FromSeconds(Math.Clamp(_options.Value.AnalyticsBridgePollSeconds, 30, 60));

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

    private static string ScopeEventId(Guid? commerceBusinessId, string eventId) =>
        CanonicalAdvertisingEventProjection.ScopeEventId(commerceBusinessId, eventId);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? SafeHash(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsDuplicateMetaSignalEvent(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("MetaSignalEvents", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("EventId", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("2601", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("2627", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("2067", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record BridgeMapping(
        string MetaEventName,
        string EventCategory,
        int FunnelStep,
        string StepName,
        int IntentScore,
        int EngagementScore,
        int QualificationScore,
        int FrictionScore,
        string ScoreTier,
        int? TotalSignalScore = null);

    private sealed record LeadDispatchState(
        string? MetaEventId,
        bool MetaServerSent,
        string? MetaServerStatus,
        string? MetaServerNote);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Analytics;

namespace ProtectWebsite.Services.MetaSignal;

public sealed class MetaSignalAnalyticsBridge : BackgroundService
{
    private static readonly string[] ExplicitSourceEventTypes =
    [
        "qualified_lead",
        AppointmentAnalyticsEventCatalog.Booked,
        "application_submitted",
        "policy_issued",
        "policy_paid",
        "purchase"
    ];

    private static readonly string[] SourceEventTypes = BuildSourceEventTypes();

    private static readonly BridgeMapping ViewContentMapping =
        new("ViewContent", "page", FunnelStep: 1, StepName: "view_content", IntentScore: 5, EngagementScore: 5, QualificationScore: 0, FrictionScore: 0, ScoreTier: "ViewContent");

    private static readonly BridgeMapping LeadMapping =
        new("Lead", "conversion", FunnelStep: 3, StepName: "lead_submitted", IntentScore: 100, EngagementScore: 100, QualificationScore: 100, FrictionScore: 0, ScoreTier: "SubmittedLead");

    private static readonly BridgeMapping QualifiedLeadMapping =
        new("QualifiedLead", "conversion", FunnelStep: 3, StepName: "qualified_lead", IntentScore: 120, EngagementScore: 120, QualificationScore: 120, FrictionScore: 0, ScoreTier: "QualifiedLead");

    private static readonly BridgeMapping AppointmentBookedMapping =
        new("AppointmentBooked", "conversion", FunnelStep: 4, StepName: "appointment_booked", IntentScore: 120, EngagementScore: 120, QualificationScore: 120, FrictionScore: 0, ScoreTier: "AppointmentBooked");

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
                var bridgeRow = await TryBuildBridgeRowAsync(db, analyticsEvent, cancellationToken);
                _watermark = analyticsEvent.Id;

                if (bridgeRow == null)
                    continue;

                if (await AlreadyDerivedAsync(db, bridgeRow, cancellationToken))
                    continue;

                db.MetaSignalEvents.Add(bridgeRow);

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (IsDuplicateMetaSignalEvent(ex))
                {
                    var entry = db.Entry(bridgeRow);
                    if (entry.State != EntityState.Detached)
                        entry.State = EntityState.Detached;

                    _logger.LogDebug(
                        ex,
                        "MetaSignalAnalyticsBridge ignored duplicate derived row sourceAnalyticsEventId={AnalyticsId} eventName={EventName}",
                        analyticsEvent.Id,
                        bridgeRow.EventName);
                    continue;
                }

                _logger.LogInformation(
                    "MetaSignalBridge processed event {EventType} for Lead {LeadId}",
                    bridgeRow.EventName,
                    bridgeRow.LeadId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MetaSignalAnalyticsBridge failed sourceAnalyticsEventId={AnalyticsId} sourceEventType={EventType}",
                    analyticsEvent.Id,
                    analyticsEvent.EventType);
                _watermark = analyticsEvent.Id;
            }
        }

        return analyticsEvents.Count == batchSize;
    }

    private async Task<long> InitializeWatermarkAsync(MasterAppDbContext db, CancellationToken cancellationToken)
    {
        var recentBridgeRows = await db.MetaSignalEvents
            .AsNoTracking()
            .Where(x => x.MetadataJson != null && x.MetadataJson.Contains(MetaSignalAnalyticsBridgeMetadata.BridgeSourceMarker))
            .OrderByDescending(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        var bridgeWatermark = recentBridgeRows
            .Select(x => MetaSignalAnalyticsBridgeMetadata.ReadInt64(x.MetadataJson, "sourceAnalyticsEventId"))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (bridgeWatermark > 0)
            return bridgeWatermark;

        var lookbackUtc = DateTime.UtcNow.AddHours(-Math.Clamp(_options.Value.AnalyticsBridgeStartupLookbackHours, 1, 168));
        var floor = await db.AnalyticsEvents
            .AsNoTracking()
            .Where(x => SourceEventTypes.Contains(x.EventType) && x.ReceivedUtc < lookbackUtc)
            .OrderByDescending(x => x.Id)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return floor ?? 0;
    }

    private async Task<MetaSignalEvent?> TryBuildBridgeRowAsync(
        MasterAppDbContext db,
        AnalyticsEvent analyticsEvent,
        CancellationToken cancellationToken)
    {
        if (!TryResolveMapping(analyticsEvent.EventType, out var mapping))
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
        var leadDispatchState = string.Equals(mapping.MetaEventName, "Lead", StringComparison.OrdinalIgnoreCase)
            ? await ResolveLeadDispatchStateAsync(db, analyticsEvent, leadId, eventUtc, cancellationToken)
            : null;

        return new MetaSignalEvent
        {
            CreatedUtc = eventUtc,
            EventId = !string.IsNullOrWhiteSpace(leadDispatchState?.MetaEventId)
                ? leadDispatchState.MetaEventId!
                : analyticsEvent.EventId == Guid.Empty
                    ? $"analytics_bridge_{analyticsEvent.Id}"
                    : analyticsEvent.EventId.ToString("N"),
            EventName = mapping.MetaEventName,
            EventCategory = mapping.EventCategory,
            LeadId = leadId,
            SessionId = Normalize(analyticsEvent.SessionId),
            VisitorId = Normalize(analyticsEvent.VisitorId),
            QuoteType = Normalize(resolvedLead?.InterestType) ?? Normalize(analyticsEvent.QuoteType),
            PageKey = Normalize(analyticsEvent.PageKey),
            EffectivePageKey = Normalize(analyticsEvent.PageKey),
            PageVariant = Normalize(pageVariant),
            PageMode = Normalize(pageMode),
            TrafficType = trafficType,
            FunnelStep = mapping.FunnelStep,
            StepName = mapping.StepName,
            IntentScore = mapping.IntentScore,
            EngagementScore = mapping.EngagementScore,
            QualificationScore = mapping.QualificationScore,
            FrictionScore = mapping.FrictionScore,
            TotalSignalScore = Math.Max(0, mapping.IntentScore + mapping.EngagementScore + mapping.QualificationScore + mapping.FrictionScore),
            ScoreTier = mapping.ScoreTier,
            MetaBrowserSent = false,
            MetaServerSent = leadDispatchState?.MetaServerSent ?? false,
            MetaDeduplicationKey = deduplicationKey,
            UtmSource = Normalize(analyticsEvent.UtmSource),
            UtmMedium = Normalize(analyticsEvent.UtmMedium),
            UtmCampaign = Normalize(analyticsEvent.UtmCampaign),
            UtmId = Normalize(analyticsEvent.UtmId),
            UtmContent = Normalize(analyticsEvent.UtmContent),
            FbclidPresent = !string.IsNullOrWhiteSpace(analyticsEvent.Fbclid),
            FbcPresent = !string.IsNullOrWhiteSpace(resolvedLead?.Fbc),
            FbpPresent = !string.IsNullOrWhiteSpace(resolvedLead?.Fbp),
            Referrer = Normalize(analyticsEvent.Referrer),
            UserAgentHash = SafeHash(Normalize(analyticsEvent.UserAgent) ?? Normalize(resolvedLead?.ClientUserAgent)),
            IpHash = SafeHash(Normalize(analyticsEvent.IpAddress) ?? Normalize(resolvedLead?.ClientIpAddress)),
            AgentTrackingProfileId = analyticsEvent.AgentTrackingProfileId ?? resolvedLead?.AgentTrackingProfileId,
            AgentSlug = Normalize(analyticsEvent.AgentSlug) ?? Normalize(resolvedLead?.AgentSlug),
            Environment = Normalize(analyticsEvent.Environment),
            Host = Normalize(analyticsEvent.Host),
            MetadataJson = MetaSignalAnalyticsBridgeMetadata.Build(
                analyticsEvent,
                mapping.MetaEventName,
                deduplicationKey,
                trafficType,
                leadId,
                pageVariant,
                pageMode,
                leadDispatchState?.MetaEventId,
                leadDispatchState?.MetaServerStatus,
                leadDispatchState?.MetaServerNote),
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
            TimeZone = Normalize(analyticsEvent.TimeZone)
        };
    }

    private async Task<LeadDispatchState?> ResolveLeadDispatchStateAsync(
        MasterAppDbContext db,
        AnalyticsEvent analyticsEvent,
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

        return null;
    }

    private async Task<WebsiteLead?> ResolveLeadAsync(
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

            if (directLead != null)
                return directLead;
        }

        if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId) || !string.IsNullOrWhiteSpace(analyticsEvent.VisitorId))
        {
            var windowStartUtc = eventUtc.AddDays(-7);
            var windowEndUtc = eventUtc.AddDays(2);

            var query = db.WebsiteLeads
                .AsNoTracking()
                .Where(x => x.CreatedUtc >= windowStartUtc && x.CreatedUtc <= windowEndUtc);

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

    private async Task<bool> AlreadyDerivedAsync(
        MasterAppDbContext db,
        MetaSignalEvent candidate,
        CancellationToken cancellationToken)
    {
        if (await db.MetaSignalEvents.AsNoTracking().AnyAsync(x => x.EventId == candidate.EventId, cancellationToken))
            return true;

        var roundedMinute = RoundToNearestMinute(candidate.CreatedUtc);
        var windowStart = roundedMinute.AddMinutes(-1);
        var windowEnd = roundedMinute.AddMinutes(1);

        var query = db.MetaSignalEvents
            .AsNoTracking()
            .Where(x =>
                x.EventName == candidate.EventName &&
                x.CreatedUtc >= windowStart &&
                x.CreatedUtc < windowEnd);

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

    private static bool TryResolveMapping(string? analyticsEventType, out BridgeMapping mapping)
    {
        mapping = null!;
        var normalized = Normalize(analyticsEventType);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (string.Equals(normalized, "qualified_lead", StringComparison.OrdinalIgnoreCase))
        {
            mapping = QualifiedLeadMapping;
            return true;
        }

        if (string.Equals(normalized, AppointmentAnalyticsEventCatalog.Booked, StringComparison.OrdinalIgnoreCase))
        {
            mapping = AppointmentBookedMapping;
            return true;
        }

        if (string.Equals(normalized, "application_submitted", StringComparison.OrdinalIgnoreCase))
        {
            mapping = ApplicationSubmittedMapping;
            return true;
        }

        if (string.Equals(normalized, "policy_issued", StringComparison.OrdinalIgnoreCase))
        {
            mapping = PolicyIssuedMapping;
            return true;
        }

        if (string.Equals(normalized, "policy_paid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "purchase", StringComparison.OrdinalIgnoreCase))
        {
            mapping = PolicyPaidMapping;
            return true;
        }

        if (AnalyticsEventCatalog.TryGet(normalized, out var definition))
        {
            if (definition.CountsAsConfirmedLead)
            {
                mapping = LeadMapping;
                return true;
            }

            if (definition.EligibleForMetaSignal && definition.CountsAsLandingView)
            {
                mapping = ViewContentMapping;
                return true;
            }
        }

        return false;
    }

    private static string[] BuildSourceEventTypes()
    {
        var leadAndViewContentSources = AnalyticsEventCatalog.Definitions
            .Where(x => x.CountsAsConfirmedLead || (x.EligibleForMetaSignal && x.CountsAsLandingView))
            .Select(x => x.Name);

        return leadAndViewContentSources
            .Concat(ExplicitSourceEventTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        MetaSignalAnalyticsBridgeMetadata.ReadString(metadataJson, propertyName);

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
        string ScoreTier);

    private sealed record LeadDispatchState(
        string? MetaEventId,
        bool MetaServerSent,
        string? MetaServerStatus,
        string? MetaServerNote);
}

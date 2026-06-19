using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IMetaSignalAnalyticsService
{
    Task<MetaSignalDashboardDto> GetDashboardAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        string? quoteType = null,
        string? campaign = null,
        string? pageMode = null,
        string? scoreTier = null,
        CancellationToken ct = default);

    Task<MetaSignalAiSummaryDto> GetAiSummaryAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        CancellationToken ct = default);

    Task<MetaSignalHealthDashboardDto> GetHealthDashboardAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        CancellationToken ct = default);
}

public sealed class MetaSignalAnalyticsService : IMetaSignalAnalyticsService
{
    private const string LearningScopeNoteText = "Meta Paid Signal Intelligence only evaluates paid Meta-attributed traffic. Non-paid/manual tests may appear in Quote Funnel and Conversion Center but are excluded from Meta learning readiness.";
    private const int DispatcherGraceMinutes = 10;
    private static readonly string[] ExplicitBridgeSourceEventTypes =
    [
        "qualified_lead",
        AppointmentAnalyticsEventCatalog.Booked,
        "application_submitted",
        "policy_issued",
        "policy_paid",
        "purchase"
    ];
    private static readonly HashSet<string> BridgeSourceEventTypes = BuildBridgeSourceEventTypes();
    private static readonly HashSet<string> BrowserPixelEventNames = new(
        MetaSignalEventCatalog.BrowserPixelEventNames,
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ServerForwardEventNames = new(
        MetaSignalEventCatalog.ServerForwardEventNames,
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConversionEventNames = new(
        MetaSignalEventCatalog.Definitions
            .Where(x => string.Equals(x.Category, "conversion", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name),
        StringComparer.OrdinalIgnoreCase);
    private readonly MasterAppDbContext _db;
    private readonly IAnalyticsQueryService _analytics;

    public MetaSignalAnalyticsService(
        MasterAppDbContext db,
        IAnalyticsQueryService analytics)
    {
        _db = db;
        _analytics = analytics;
    }

    public async Task<MetaSignalDashboardDto> GetDashboardAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        string? quoteType = null,
        string? campaign = null,
        string? pageMode = null,
        string? scoreTier = null,
        CancellationToken ct = default)
    {
        var baseQuery = _db.MetaSignalEvents.AsNoTracking()
            .Where(x => x.CreatedUtc >= range.FromUtc && x.CreatedUtc <= range.ToUtc);

        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope, ct);
        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            if (scopedAgentIds is { Length: > 0 })
            {
                baseQuery = baseQuery.Where(x =>
                    x.AgentTrackingProfileId.HasValue &&
                    scopedAgentIds.Contains(x.AgentTrackingProfileId.Value));
            }
            else
            {
                var agentId = scope.AgentTrackingProfileId.Value;
                baseQuery = baseQuery.Where(x => x.AgentTrackingProfileId == agentId);
            }
        }

        baseQuery = ApplyTrafficFilter(baseQuery, trafficType);
        baseQuery = await ApplyQualityFilterAsync(baseQuery, range, scope, scopedAgentIds, ct);

        var baseRows = await baseQuery
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(ct);

        var availableQuoteTypes = baseRows
            .Select(x => Normalize(x.QuoteType))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var availableCampaigns = baseRows
            .Select(ResolveCampaignLabel)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var availablePageModes = baseRows
            .Select(x => Normalize(x.PageMode))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var structurallyFilteredRows = baseRows
            .Where(x => string.IsNullOrWhiteSpace(quoteType) || string.Equals(x.QuoteType, quoteType.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(campaign) || string.Equals(ResolveCampaignLabel(x), campaign.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(pageMode) || string.Equals(x.PageMode, pageMode.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var visitorSummariesBeforeScoreFilter = BuildVisitorSummaries(structurallyFilteredRows.Where(IsMetaLearningEligible));
        var availableScoreTiers = visitorSummariesBeforeScoreFilter
            .Select(x => x.ScoreTier)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ScoreTierOrder)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filteredVisitorKeys = string.IsNullOrWhiteSpace(scoreTier)
            ? null
            : visitorSummariesBeforeScoreFilter
                .Where(x => string.Equals(x.ScoreTier, scoreTier.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(x => x.VisitorKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = filteredVisitorKeys == null
            ? structurallyFilteredRows
            : structurallyFilteredRows
                .Where(x =>
                {
                    var visitorKey = GetVisitorKey(x);
                    return !string.IsNullOrWhiteSpace(visitorKey) && filteredVisitorKeys.Contains(visitorKey);
                })
                .ToList();

        return BuildDashboard(
            rows,
            range,
            BuildTrafficFilterLabel(trafficType),
            availableQuoteTypes,
            availableCampaigns,
            availablePageModes,
            availableScoreTiers);
    }

    public async Task<MetaSignalAiSummaryDto> GetAiSummaryAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        TrafficType trafficType,
        CancellationToken ct = default)
    {
        var dashboard = await GetDashboardAsync(range, scope, trafficType, ct: ct);
        return new MetaSignalAiSummaryDto
        {
            LearningScopeNote = dashboard.LearningScopeNote,
            TotalSignalEvents = dashboard.TotalSignalEvents,
            TotalVisitors = dashboard.TotalVisitors,
            HighIntentVisitors = dashboard.HighIntentVisitors,
            LeadReadyVisitors = dashboard.LeadReadyVisitors,
            SubmittedLeads = dashboard.SubmittedLeads,
            SubmitAttemptsWithoutLead = dashboard.SubmitAttemptsWithoutLead,
            HighIntentAbandons = dashboard.HighIntentAbandons,
            ContactStepAbandons = dashboard.ContactStepAbandons,
            SignalToLeadConversionRate = dashboard.SignalToLeadConversionRate,
            RecommendedOptimizationEvent = dashboard.RecommendedOptimizationEvent,
            BestPerformingLandingPageVersion = dashboard.BestPerformingLandingPageVersion,
            WorstFrictionStep = dashboard.WorstFrictionStep,
            VisitorsByScoreTier = dashboard.VisitorsByScoreTier,
            AverageScoreByCampaign = dashboard.AverageScoreByCampaign.Take(5).ToList(),
            AverageScoreByPageVariant = dashboard.AverageScoreByPageVariant.Take(5).ToList(),
            EventLadder = dashboard.EventLadder
        };
    }

    public async Task<MetaSignalHealthDashboardDto> GetHealthDashboardAsync(
        TimeRangeRequest range,
        ScopeContext scope,
        CancellationToken ct = default)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope, ct);
        var analyticsRows = await _analytics
            .ScopedEvents(range, scope, scopedAgentIds)
            .Select(x => new HealthAnalyticsEventRow
            {
                Id = x.Id,
                EventType = x.EventType,
                SessionId = x.SessionId,
                VisitorId = x.VisitorId,
                EventUtc = x.EventUtc,
                ScrollPercent = x.ScrollPercent,
                DwellMilliseconds = x.DwellMilliseconds,
                EngagedMilliseconds = x.EngagedMilliseconds,
                IsBounceCandidate = x.IsBounceCandidate
            })
            .ToListAsync(ct);

        var metaRows = await BuildHealthMetaQuery(range, scope, scopedAgentIds, analyticsRows)
            .Select(x => new HealthMetaSignalRow
            {
                Id = x.Id,
                CreatedUtc = x.CreatedUtc,
                EventId = x.EventId,
                EventName = x.EventName,
                EventCategory = x.EventCategory,
                SessionId = x.SessionId,
                VisitorId = x.VisitorId,
                LeadId = x.LeadId,
                FunnelStep = x.FunnelStep,
                StepName = x.StepName,
                MetaBrowserSent = x.MetaBrowserSent,
                MetaServerSent = x.MetaServerSent,
                MetaDeduplicationKey = x.MetaDeduplicationKey,
                MetadataJson = x.MetadataJson
            })
            .ToListAsync(ct);

        var leadsCount = await BuildHealthLeadQuery(range, scope, scopedAgentIds)
            .CountAsync(ct);

        var bridgeEligibleAnalytics = analyticsRows
            .Where(IsBridgeEligibleAnalyticsEvent)
            .ToList();
        var bridgeEligibleAnalyticsIds = bridgeEligibleAnalytics
            .Select(x => x.Id)
            .Distinct()
            .ToHashSet();

        var metaContexts = metaRows
            .Select(CreateHealthMetaContext)
            .ToList();

        var bridgeOwnedRows = metaContexts
            .Where(x => x.IsBridgeOwned)
            .ToList();

        var bridgeOwnedAnalyticsIds = bridgeOwnedRows
            .Where(x => x.SourceAnalyticsEventId.HasValue)
            .Select(x => x.SourceAnalyticsEventId!.Value)
            .Distinct()
            .ToHashSet();

        var identityReadyBridgeRows = bridgeOwnedRows
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Row.EventId) &&
                !string.IsNullOrWhiteSpace(x.Row.MetaDeduplicationKey) &&
                (x.Row.LeadId.HasValue && x.Row.LeadId.Value != Guid.Empty ||
                 !string.IsNullOrWhiteSpace(x.Row.SessionId) ||
                 !string.IsNullOrWhiteSpace(x.Row.VisitorId)))
            .ToList();

        var nowUtc = DateTime.UtcNow;
        var dispatcherThresholdUtc = nowUtc.AddMinutes(-DispatcherGraceMinutes);
        var dispatcherEligibleRows = metaContexts
            .Where(x => RequiresDispatcher(x.Row))
            .ToList();
        var dispatcherDueRows = dispatcherEligibleRows
            .Where(x => x.Row.CreatedUtc <= dispatcherThresholdUtc)
            .ToList();
        var dispatcherTouchedRows = dispatcherEligibleRows
            .Where(HasDispatcherActivity)
            .ToList();
        var dispatcherTouchedDueRows = dispatcherDueRows
            .Where(HasDispatcherActivity)
            .ToList();

        var authorityBlockedRows = dispatcherTouchedRows
            .Where(x => string.Equals(x.MetaServerStatus, "blocked_by_authority", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var authorityAllowedRows = dispatcherTouchedRows
            .Where(x => !string.Equals(x.MetaServerStatus, "blocked_by_authority", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missingBridgeCount = bridgeEligibleAnalyticsIds.Count == 0
            ? 0
            : bridgeEligibleAnalyticsIds.Except(bridgeOwnedAnalyticsIds).Count();

        var conversionRowsMissingLead = metaContexts
            .Where(x => ConversionEventNames.Contains(x.Row.EventName))
            .Count(x => !x.Row.LeadId.HasValue || x.Row.LeadId.Value == Guid.Empty);
        var bridgeRowsMissingSession = bridgeOwnedRows
            .Count(x => string.IsNullOrWhiteSpace(x.Row.SessionId));
        var browserEligibleRows = metaContexts
            .Where(x => BrowserPixelEventNames.Contains(x.Row.EventName))
            .ToList();
        var browserStuckCount = browserEligibleRows.Count(x => !x.Row.MetaBrowserSent);
        var dispatcherMissingCount = dispatcherDueRows.Count(x => !HasDispatcherActivity(x));

        var failureDetection = new List<MetaSignalHealthIssueDto>
        {
            BuildIssue(
                "missing_bridge_rows",
                "Events Missing MetaSignalEvents",
                missingBridgeCount,
                bridgeEligibleAnalyticsIds.Count,
                missingBridgeCount == 0
                    ? "Every bridge-eligible analytics event in the selected diagnostic range produced a derived signal row."
                    : $"{missingBridgeCount} of {bridgeEligibleAnalyticsIds.Count} bridge-eligible analytics events do not have a matching bridge-owned MetaSignal row."),
            BuildIssue(
                "missing_identity",
                "Meta Rows Missing LeadId or SessionId",
                conversionRowsMissingLead + bridgeRowsMissingSession,
                Math.Max(1, metaContexts.Count),
                $"Missing lead on {conversionRowsMissingLead} conversion rows and missing session on {bridgeRowsMissingSession} bridge rows."),
            BuildIssue(
                "browser_pending",
                "Events Stuck at MetaBrowserSent = 0",
                browserStuckCount,
                browserEligibleRows.Count,
                browserEligibleRows.Count == 0
                    ? "No browser-eligible Meta Signal rows were produced in the selected diagnostic range."
                    : $"{browserStuckCount} of {browserEligibleRows.Count} browser-eligible rows never marked browser send success."),
            BuildIssue(
                "dispatcher_pending",
                "Events Never Reaching Dispatcher",
                dispatcherMissingCount,
                dispatcherDueRows.Count,
                dispatcherDueRows.Count == 0
                    ? $"No dispatcher-eligible rows are older than the {DispatcherGraceMinutes}-minute grace window."
                    : $"{dispatcherMissingCount} of {dispatcherDueRows.Count} dispatcher-eligible rows show no dispatch metadata after {DispatcherGraceMinutes} minutes.")
        };

        var result = new MetaSignalHealthDashboardDto
        {
            RangeLabel = range.Label,
            LastUpdatedUtc = nowUtc,
            DispatcherGraceMinutes = DispatcherGraceMinutes,
            PipelineHealth = new MetaSignalHealthPipelineSummaryDto
            {
                AnalyticsEventsLast24Hours = analyticsRows.Count,
                BridgeEligibleAnalyticsEventsLast24Hours = bridgeEligibleAnalyticsIds.Count,
                BridgeOwnedMetaSignalEventsLast24Hours = bridgeOwnedRows.Count,
                MetaSignalEventsLast24Hours = metaContexts.Count,
                WebsiteLeadsLast24Hours = leadsCount,
                MetaServerSentCount = metaContexts.Count(x => x.Row.MetaServerSent),
                MetaBrowserSentCount = metaContexts.Count(x => x.Row.MetaBrowserSent)
            },
            FlowIntegrity = new List<MetaSignalHealthMetricDto>
            {
                BuildMetric(
                    "analytics_bridge_coverage",
                    "Analytics → Bridge Coverage",
                    bridgeOwnedAnalyticsIds.Count,
                    bridgeEligibleAnalyticsIds.Count,
                    "Bridge-owned MetaSignal rows linked back to bridge-eligible analytics events."),
                BuildMetric(
                    "bridge_meta_signal_coverage",
                    "Bridge → MetaSignal Coverage",
                    identityReadyBridgeRows.Count,
                    bridgeOwnedRows.Count,
                    "Bridge rows carrying deduplication and visitor identity needed for downstream Meta handling."),
                BuildMetric(
                    "dispatcher_execution_rate",
                    "Dispatcher Execution Rate",
                    dispatcherTouchedDueRows.Count,
                    dispatcherDueRows.Count,
                    $"Rows older than {DispatcherGraceMinutes} minutes that already show dispatch metadata."),
                BuildMetric(
                    "authority_allow_rate",
                    "MetaSendAuthority Allowed Ratio",
                    authorityAllowedRows.Count,
                    dispatcherTouchedRows.Count,
                    dispatcherTouchedRows.Count == 0
                        ? "No dispatcher decisions have been recorded in the selected diagnostic range."
                        : $"{authorityAllowedRows.Count} allowed vs {authorityBlockedRows.Count} blocked by MetaSendAuthority.")
            },
            FailureDetection = failureDetection,
            RecentEvents = metaContexts
                .OrderByDescending(x => x.Row.CreatedUtc)
                .Take(20)
                .Select(x => new MetaSignalHealthRecentEventRowDto
                {
                    CreatedUtc = x.Row.CreatedUtc,
                    EventType = x.SourceAnalyticsEventType ?? x.Row.EventCategory ?? "MetaSignal",
                    EventName = x.Row.EventName,
                    SourceLabel = x.IsBridgeOwned ? "Analytics Bridge" : "Direct Meta Signal",
                    SessionId = Normalize(x.Row.SessionId),
                    LeadId = x.Row.LeadId,
                    FunnelStep = BuildFunnelStepLabel(x.Row),
                    MetaBrowserSent = x.Row.MetaBrowserSent,
                    MetaServerSent = x.Row.MetaServerSent,
                    DispatcherStatus = ResolveDispatcherStatus(x, dispatcherThresholdUtc),
                    AuthorityStatus = ResolveAuthorityStatus(x),
                    MetaServerStatus = x.MetaServerStatus ?? string.Empty
                })
                .ToList()
        };

        return result;
    }

    private MetaSignalDashboardDto BuildDashboard(
        IReadOnlyCollection<MetaSignalEvent> rows,
        TimeRangeRequest range,
        string trafficFilterLabel,
        List<string> availableQuoteTypes,
        List<string> availableCampaigns,
        List<string> availablePageModes,
        List<string> availableScoreTiers)
    {
        var eligibleRows = rows.Where(IsMetaLearningEligible).ToList();
        var excludedRows = rows.Where(x => !IsMetaLearningEligible(x)).ToList();
        var visitorSummaries = BuildVisitorSummaries(eligibleRows);
        var highIntentVisitors = visitorSummaries.Count(x => x.IsHighIntent);
        var leadReadyVisitors = visitorSummaries.Count(x => x.IsLeadReady);
        var submittedVisitors = visitorSummaries.Count(x => x.LeadSubmitted);
        var submitAttemptsWithoutLead = visitorSummaries.Count(x => x.SubmitAttempted && !x.LeadSubmitted);
        var highIntentAbandons = visitorSummaries.Count(x => x.HighIntentAbandon && !x.LeadSubmitted);
        var contactStepAbandons = visitorSummaries.Count(x => x.ContactStepAbandon && !x.LeadSubmitted);

        var eventsByQuoteType = eligibleRows
            .GroupBy(x => Normalize(x.QuoteType) ?? "unknown")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalValueRowDto
            {
                Label = g.Key,
                Value = g.Count()
            })
            .ToList();

        var eventsByCampaign = eligibleRows
            .GroupBy(x => ResolveCampaignLabel(x) ?? "Unattributed")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalValueRowDto
            {
                Label = g.Key,
                Value = g.Count()
            })
            .ToList();

        var visitorsByScoreTier = visitorSummaries
            .GroupBy(x => x.ScoreTier)
            .OrderBy(g => ScoreTierOrder(g.Key))
            .Select(g => new MetaSignalTierRowDto
            {
                ScoreTier = g.Key,
                Visitors = g.Count()
            })
            .ToList();

        var averageScoreByCampaign = eligibleRows
            .GroupBy(x => ResolveCampaignLabel(x) ?? "Unattributed")
            .OrderByDescending(g => g.Average(x => x.TotalSignalScore))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalAverageRowDto
            {
                Label = g.Key,
                AverageScore = Math.Round((decimal)g.Average(x => x.TotalSignalScore), 1)
            })
            .ToList();

        var averageScoreByPageVariant = eligibleRows
            .GroupBy(x => Normalize(x.PageVariant) ?? Normalize(x.EffectivePageKey) ?? Normalize(x.PageKey) ?? "default")
            .OrderByDescending(g => g.Average(x => x.TotalSignalScore))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalAverageRowDto
            {
                Label = g.Key,
                AverageScore = Math.Round((decimal)g.Average(x => x.TotalSignalScore), 1)
            })
            .ToList();

        var ladder = eligibleRows.Count == 0 ? new List<MetaSignalLadderRowDto>() : BuildEventLadder(eligibleRows);
        var frictionHotspots = eligibleRows.Count == 0 ? new List<MetaSignalFrictionRowDto>() : BuildFrictionHotspots(eligibleRows);
        var bestVariant = eligibleRows.Count == 0 ? "—" : ResolveBestVariant(eligibleRows);
        var recommendedOptimizationEvent = eligibleRows.Count == 0
            ? "No paid Meta-attributed funnel progression"
            : ResolveRecommendedOptimizationEvent(ladder, leadReadyVisitors, submittedVisitors, highIntentVisitors);
        var worstFrictionStep = eligibleRows.Count == 0 ? "—" : ResolveWorstFrictionStep(ladder, frictionHotspots);

        return new MetaSignalDashboardDto
        {
            RangeLabel = range.Label,
            TrafficFilterLabel = trafficFilterLabel,
            LearningScopeNote = LearningScopeNoteText,
            HasEligiblePaidMetaTraffic = eligibleRows.Count > 0,
            TotalSignalEvents = eligibleRows.Count,
            TotalVisitors = visitorSummaries.Count,
            HighIntentVisitors = highIntentVisitors,
            LeadReadyVisitors = leadReadyVisitors,
            SubmittedLeads = submittedVisitors,
            SubmitAttemptsWithoutLead = submitAttemptsWithoutLead,
            HighIntentAbandons = highIntentAbandons,
            ContactStepAbandons = contactStepAbandons,
            ExcludedSignalEvents = excludedRows.Count,
            ExcludedSignalVisitors = CountDistinctVisitors(excludedRows),
            SignalToLeadConversionRate = visitorSummaries.Count == 0 ? 0 : Math.Round((submittedVisitors * 100m) / visitorSummaries.Count, 2),
            RecommendedOptimizationEvent = recommendedOptimizationEvent,
            BestPerformingLandingPageVersion = bestVariant,
            WorstFrictionStep = worstFrictionStep,
            AvailableQuoteTypes = availableQuoteTypes,
            AvailableCampaigns = availableCampaigns,
            AvailablePageModes = availablePageModes,
            AvailableScoreTiers = availableScoreTiers,
            EventsByQuoteType = eventsByQuoteType,
            EventsByCampaign = eventsByCampaign,
            VisitorsByScoreTier = visitorsByScoreTier,
            AverageScoreByCampaign = averageScoreByCampaign,
            AverageScoreByPageVariant = averageScoreByPageVariant,
            EventLadder = ladder,
            FrictionHotspots = frictionHotspots,
            RecentDiagnostics = BuildRecentDiagnostics(rows)
        };
    }

    private static List<MetaSignalLadderRowDto> BuildEventLadder(IEnumerable<MetaSignalEvent> rows)
    {
        var visitorSummaries = BuildVisitorSummaries(rows);
        var stageCounts = new List<(string Key, string Label, int Count)>
        {
            (
                "view_content",
                "View Content",
                rows
                    .Where(x => string.Equals(x.EventName, "ViewContent", StringComparison.OrdinalIgnoreCase))
                    .Select(GetVisitorKey)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
            ),
            (
                "lead_form_start",
                "Lead Form Start",
                visitorSummaries.Count(x => x.FunnelStarted)
            ),
            (
                "discovery_complete",
                "Discovery Complete",
                visitorSummaries.Count(x => x.DiscoveryComplete)
            ),
            (
                "recommendation_viewed",
                "Recommendation Viewed",
                visitorSummaries.Count(x => x.RecommendationViewed)
            ),
            (
                "contact_step_reached",
                "Contact Details Signal",
                visitorSummaries.Count(x => x.ContactStepReached)
            ),
            (
                "submit_attempt",
                "Submit Attempt",
                visitorSummaries.Count(x => x.SubmitAttempted)
            ),
            (
                "lead",
                "Submitted Lead",
                visitorSummaries.Count(x => x.LeadSubmitted)
            )
        };

        var result = new List<MetaSignalLadderRowDto>();
        int? priorCount = null;
        foreach (var stage in stageCounts)
        {
            result.Add(new MetaSignalLadderRowDto
            {
                StepKey = stage.Key,
                StepLabel = stage.Label,
                Visitors = stage.Count,
                ProgressionRate = priorCount.HasValue && priorCount.Value > 0
                    ? Math.Round((stage.Count * 100m) / priorCount.Value, 2)
                    : (decimal?)null
            });

            priorCount = stage.Count;
        }

        return result;
    }

    private static List<MetaSignalFrictionRowDto> BuildFrictionHotspots(IEnumerable<MetaSignalEvent> rows)
    {
        return rows
            .Where(x =>
                string.Equals(x.EventName, "FieldError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.EventName, "Backtrack", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.EventName, "DeadClick", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.EventName, "RageClick", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.EventName, "RapidBounce", StringComparison.OrdinalIgnoreCase))
            .GroupBy(ResolveFrictionLabel)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalFrictionRowDto
            {
                Label = g.Key,
                Count = g.Count()
            })
            .ToList();
    }

    private static string ResolveBestVariant(IEnumerable<MetaSignalEvent> rows)
    {
        var winner = rows
            .GroupBy(x => Normalize(x.PageVariant) ?? Normalize(x.EffectivePageKey) ?? Normalize(x.PageKey) ?? "default")
            .Select(g =>
            {
                var visitors = g
                    .Where(x => string.Equals(x.EventName, "ViewContent", StringComparison.OrdinalIgnoreCase))
                    .Select(GetVisitorKey)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                var leads = g
                    .Where(x => string.Equals(x.EventName, "Lead", StringComparison.OrdinalIgnoreCase))
                    .Select(GetVisitorKey)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                var rate = visitors == 0 ? 0m : (leads * 100m) / visitors;
                return new
                {
                    Variant = g.Key,
                    Rate = Math.Round(rate, 2),
                    Leads = leads
                };
            })
            .OrderByDescending(x => x.Rate)
            .ThenByDescending(x => x.Leads)
            .FirstOrDefault();

        return winner?.Variant ?? "—";
    }

    private static string ResolveRecommendedOptimizationEvent(
        IReadOnlyCollection<MetaSignalLadderRowDto> ladder,
        int leadReadyVisitors,
        int submittedVisitors,
        int highIntentVisitors)
    {
        var leadFormStarts = ladder.FirstOrDefault(x => x.StepKey == "lead_form_start")?.Visitors ?? 0;
        var recommendationVisitors = ladder.FirstOrDefault(x => x.StepKey == "recommendation_viewed")?.Visitors ?? 0;
        var contactStepVisitors = ladder.FirstOrDefault(x => x.StepKey == "contact_step_reached")?.Visitors ?? 0;

        if (submittedVisitors >= 25) return "Lead";
        if (leadReadyVisitors >= 20) return "LeadReadySignal";
        if (contactStepVisitors >= 20) return "ContactStepReached";
        if (highIntentVisitors >= 15) return "HighIntentLeadSignal";
        if (recommendationVisitors >= 15) return "RecommendationViewed";
        if (leadFormStarts >= 15) return "LeadFormStart";
        return "LeadFormStart";
    }

    private static string ResolveWorstFrictionStep(
        IReadOnlyCollection<MetaSignalLadderRowDto> ladder,
        IReadOnlyCollection<MetaSignalFrictionRowDto> frictionHotspots)
    {
        var worstStage = ladder
            .Where(x => x.ProgressionRate.HasValue)
            .OrderBy(x => x.ProgressionRate!.Value)
            .ThenBy(x => x.Visitors)
            .FirstOrDefault();

        if (worstStage != null)
            return worstStage.StepLabel;

        return frictionHotspots.FirstOrDefault()?.Label ?? "—";
    }


    private async Task<IQueryable<MetaSignalEvent>> ApplyQualityFilterAsync(
        IQueryable<MetaSignalEvent> query,
        TimeRangeRequest range,
        ScopeContext scope,
        Guid[]? scopedAgentIds,
        CancellationToken ct)
    {
        if (range.QualityMode == TrafficQualityMode.AllTraffic)
            return query;

        var qualityEvents = await _analytics
            .ScopedEvents(range, scope, scopedAgentIds)
            .Select(e => new { e.VisitorId, e.SessionId })
            .ToListAsync(ct);

        var visitorIds = qualityEvents
            .Select(x => x.VisitorId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sessionIds = qualityEvents
            .Select(x => x.SessionId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visitorIds.Count == 0 && sessionIds.Count == 0)
            return query.Where(x => false);

        return query.Where(x =>
            (!string.IsNullOrWhiteSpace(x.VisitorId) && visitorIds.Contains(x.VisitorId!)) ||
            (!string.IsNullOrWhiteSpace(x.SessionId) && sessionIds.Contains(x.SessionId!)));
    }

    private IQueryable<MetaSignalEvent> BuildHealthMetaQuery(
        TimeRangeRequest range,
        ScopeContext scope,
        Guid[]? scopedAgentIds,
        IReadOnlyCollection<HealthAnalyticsEventRow> analyticsRows)
    {
        var query = _db.MetaSignalEvents.AsNoTracking()
            .Where(x => x.CreatedUtc >= range.FromUtc && x.CreatedUtc <= range.ToUtc);

        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            if (scopedAgentIds is { Length: > 0 })
            {
                query = query.Where(x =>
                    x.AgentTrackingProfileId.HasValue &&
                    scopedAgentIds.Contains(x.AgentTrackingProfileId.Value));
            }
            else
            {
                var agentId = scope.AgentTrackingProfileId.Value;
                query = query.Where(x => x.AgentTrackingProfileId == agentId);
            }
        }

        return ApplyHealthMetaQualityFilter(query, range, analyticsRows);
    }

    private IQueryable<MetaSignalEvent> ApplyHealthMetaQualityFilter(
        IQueryable<MetaSignalEvent> query,
        TimeRangeRequest range,
        IReadOnlyCollection<HealthAnalyticsEventRow> analyticsRows)
    {
        if (range.QualityMode == TrafficQualityMode.AllTraffic)
            return query;

        var visitorIds = analyticsRows
            .Select(x => Normalize(x.VisitorId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sessionIds = analyticsRows
            .Select(x => Normalize(x.SessionId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visitorIds.Count == 0 && sessionIds.Count == 0)
            return query.Where(x => false);

        return query.Where(x =>
            (!string.IsNullOrWhiteSpace(x.VisitorId) && visitorIds.Contains(x.VisitorId!)) ||
            (!string.IsNullOrWhiteSpace(x.SessionId) && sessionIds.Contains(x.SessionId!)));
    }

    private IQueryable<WebsiteLead> BuildHealthLeadQuery(
        TimeRangeRequest range,
        ScopeContext scope,
        Guid[]? scopedAgentIds)
    {
        var query = _db.WebsiteLeads.AsNoTracking()
            .Where(x => !x.IsDeleted)
            .Where(x => x.CreatedUtc >= range.FromUtc && x.CreatedUtc <= range.ToUtc);

        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            if (scopedAgentIds is { Length: > 0 })
            {
                query = query.Where(x =>
                    x.AgentTrackingProfileId.HasValue &&
                    scopedAgentIds.Contains(x.AgentTrackingProfileId.Value));
            }
            else
            {
                var agentId = scope.AgentTrackingProfileId.Value;
                query = query.Where(x => x.AgentTrackingProfileId == agentId);
            }
        }

        return ApplyHealthLeadQualityFilter(query, range.QualityMode);
    }

    private static IQueryable<WebsiteLead> ApplyHealthLeadQualityFilter(
        IQueryable<WebsiteLead> query,
        TrafficQualityMode qualityMode)
    {
        return query.Where(TrafficQualityBucketFilters.BuildLeadPredicate(qualityMode));
    }

    private static IQueryable<MetaSignalEvent> ApplyTrafficFilter(IQueryable<MetaSignalEvent> query, TrafficType trafficType)
    {
        return trafficType switch
        {
            TrafficType.All => query,
            TrafficType.PaidAds => query.Where(x => x.TrafficType == nameof(TrafficType.PaidAds)),
            TrafficType.NonPaid => query.Where(x =>
                x.TrafficType == nameof(TrafficType.Organic) ||
                x.TrafficType == nameof(TrafficType.Direct) ||
                x.TrafficType == nameof(TrafficType.Referral)),
            TrafficType.Organic => query.Where(x => x.TrafficType == nameof(TrafficType.Organic)),
            TrafficType.Direct => query.Where(x => x.TrafficType == nameof(TrafficType.Direct)),
            TrafficType.Referral => query.Where(x => x.TrafficType == nameof(TrafficType.Referral)),
            TrafficType.Internal => query.Where(x => x.TrafficType == nameof(TrafficType.Internal)),
            TrafficType.Test => query.Where(x => x.TrafficType == nameof(TrafficType.Test)),
            TrafficType.BotSuspicious => query.Where(x => x.TrafficType == nameof(TrafficType.BotSuspicious)),
            TrafficType.Unknown => query.Where(x => x.TrafficType == nameof(TrafficType.Unknown)),
            _ => query
        };
    }

    private static string BuildTrafficFilterLabel(TrafficType trafficType) =>
        TrafficAttribution.BucketLabel(trafficType);

    private static string ResolveFrictionLabel(MetaSignalEvent row)
    {
        if (string.Equals(row.EventName, "FieldError", StringComparison.OrdinalIgnoreCase))
        {
            var fieldName = ReadStringMetadata(row.MetadataJson, "fieldName");
            return !string.IsNullOrWhiteSpace(fieldName) ? $"Field Error: {fieldName}" : "Field Error";
        }

        if (string.Equals(row.EventName, "Backtrack", StringComparison.OrdinalIgnoreCase))
        {
            var fromStep = ReadStringMetadata(row.MetadataJson, "fromStep");
            return !string.IsNullOrWhiteSpace(fromStep) ? $"Backtrack: {fromStep}" : "Backtrack";
        }

        return row.EventName;
    }

    private static List<VisitorSignalSummary> BuildVisitorSummaries(IEnumerable<MetaSignalEvent> rows)
    {
        return rows
            .GroupBy(GetVisitorKey)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g =>
            {
                var summary = new VisitorSignalSummary
                {
                    VisitorKey = g.Key!,
                    MaxTotalSignalScore = g.Max(x => x.TotalSignalScore)
                };

                foreach (var row in g.OrderBy(x => x.CreatedUtc))
                {
                    ApplyRowToVisitorSummary(summary, row);
                }

                summary.ScoreTier = ResolveVisitorScoreTier(summary);
                return summary;
            })
            .ToList();
    }

    private static void ApplyRowToVisitorSummary(VisitorSignalSummary summary, MetaSignalEvent row)
    {
        var eventUtc = row.CreatedUtc;

        if (ReadBoolMetadata(row.MetadataJson, "contactStepReached"))
            summary.ContactStepReached = true;
        if (ReadBoolMetadata(row.MetadataJson, "contactInputStarted"))
            summary.ContactInputStarted = true;
        if (ReadBoolMetadata(row.MetadataJson, "phoneCompleted"))
            summary.PhoneCompleted = true;
        if (ReadBoolMetadata(row.MetadataJson, "requiredContactFieldsComplete"))
            summary.RequiredContactFieldsCompleted = true;
        if (ReadBoolMetadata(row.MetadataJson, "contactStepAbandon"))
        {
            summary.ContactStepAbandon = true;
            summary.LastAbandonUtc = eventUtc;
        }

        switch (row.EventName)
        {
            case "SessionEngaged5s":
            case "SessionEngaged15s":
            case "MeaningfulScroll":
                summary.Engaged = true;
                break;
            case "LeadFormStart":
                summary.FunnelStarted = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "DiscoveryComplete":
                summary.FunnelStarted = true;
                summary.DiscoveryComplete = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "FunnelStepComplete":
                summary.FunnelStarted = true;
                if (row.FunnelStep >= 1)
                {
                    summary.DiscoveryComplete = true;
                }
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "RecommendationViewed":
                summary.RecommendationViewed = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "ContactStepReached":
                summary.ContactStepReached = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "ContactInputStarted":
                summary.ContactInputStarted = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "PhoneFieldCompleted":
                summary.PhoneCompleted = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "RequiredContactFieldsCompleted":
                summary.RequiredContactFieldsCompleted = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "SubmitAttempt":
                summary.SubmitAttempted = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "HighIntentLeadSignal":
                summary.HighIntentSignal = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "LeadReadySignal":
                summary.LeadReadySignal = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
            case "AbandonedHighIntentLead":
                summary.HighIntentAbandon = true;
                summary.LastAbandonUtc = eventUtc;
                break;
            case "Lead":
            case "QualifiedLead":
                summary.LeadSubmitted = true;
                summary.LastMeaningfulProgressUtc = eventUtc;
                break;
        }

        BackfillVisitorSummaryProgress(summary);
        ReconcileAbandonState(summary);
    }

    private static void BackfillVisitorSummaryProgress(VisitorSignalSummary summary)
    {
        if (summary.RecommendationViewed ||
            summary.ContactStepReached ||
            summary.ContactInputStarted ||
            summary.PhoneCompleted ||
            summary.RequiredContactFieldsCompleted ||
            summary.SubmitAttempted ||
            summary.LeadSubmitted ||
            summary.HighIntentSignal ||
            summary.LeadReadySignal)
        {
            summary.FunnelStarted = true;
        }

        if (summary.RecommendationViewed ||
            summary.ContactStepReached ||
            summary.ContactInputStarted ||
            summary.PhoneCompleted ||
            summary.RequiredContactFieldsCompleted ||
            summary.SubmitAttempted ||
            summary.LeadSubmitted ||
            summary.LeadReadySignal)
        {
            summary.DiscoveryComplete = true;
        }

        if (summary.ContactInputStarted ||
            summary.PhoneCompleted ||
            summary.RequiredContactFieldsCompleted ||
            summary.SubmitAttempted ||
            summary.LeadSubmitted ||
            summary.LeadReadySignal)
        {
            summary.ContactStepReached = true;
        }

        if (summary.LeadSubmitted)
        {
            summary.SubmitAttempted = true;
        }
    }

    private static void ReconcileAbandonState(VisitorSignalSummary summary)
    {
        if (!summary.LastAbandonUtc.HasValue || !summary.LastMeaningfulProgressUtc.HasValue)
            return;

        if (summary.LastMeaningfulProgressUtc.Value <= summary.LastAbandonUtc.Value)
            return;

        summary.HighIntentAbandon = false;
        summary.ContactStepAbandon = false;
    }

    private static string ResolveVisitorScoreTier(VisitorSignalSummary summary)
    {
        if (summary.LeadSubmitted)
            return "SubmittedLead";
        if (summary.SubmitAttempted || summary.RequiredContactFieldsCompleted || summary.LeadReadySignal)
            return "SubmitAttempter";
        if (summary.ContactStepReached || summary.ContactInputStarted || summary.PhoneCompleted)
            return "ContactStepViewer";
        if (summary.RecommendationViewed || summary.HighIntentSignal)
            return "RecommendationViewer";
        if (summary.FunnelStarted)
            return "FunnelStarter";
        if (summary.Engaged)
            return "EngagedVisitor";
        return "ColdVisitor";
    }

    private static int ScoreTierOrder(string? scoreTier) => Normalize(scoreTier) switch
    {
        "ColdVisitor" => 1,
        "EngagedVisitor" => 2,
        "FunnelStarter" => 3,
        "RecommendationViewer" => 4,
        "ContactStepViewer" => 5,
        "SubmitAttempter" => 6,
        "SubmittedLead" => 7,
        _ => 99
    };

    private static string? ResolveCampaignLabel(MetaSignalEvent row)
    {
        return Normalize(row.UtmCampaign) ??
               ReadResolvedAttributionString(row.MetadataJson, "utmCampaign") ??
               ReadResolvedAttributionString(row.MetadataJson, "metaCampaignId") ??
               Normalize(row.UtmId);
    }

    private static List<MetaSignalDiagnosticEventRowDto> BuildRecentDiagnostics(IReadOnlyCollection<MetaSignalEvent> rows)
    {
        return rows
            .OrderByDescending(x => x.CreatedUtc)
            .Take(30)
            .Select(row =>
            {
                var attribution = ResolveAttributionSnapshot(row);
                var isMetaAttributedPaid = IsMetaAttributedPaid(attribution);
                var excluded = !IsMetaLearningEligible(row);
                var trafficType = Normalize(row.TrafficType) ?? "Unknown";
                return new MetaSignalDiagnosticEventRowDto
                {
                    CreatedUtc = row.CreatedUtc,
                    EventName = row.EventName,
                    QuoteType = Normalize(row.QuoteType) ?? "unknown",
                    CampaignLabel = ResolveCampaignLabel(row),
                    TrafficType = trafficType,
                    IsPaidMetaAttributed = isMetaAttributedPaid,
                    IsNonPaidOrManual = !isMetaAttributedPaid && trafficType is nameof(TrafficType.Direct) or nameof(TrafficType.Organic) or nameof(TrafficType.Referral),
                    ExcludedFromMetaLearningReadiness = excluded,
                    BrowserPixelSent = row.MetaBrowserSent,
                    ServerCapiSent = row.MetaServerSent,
                    DeduplicationEventIdPresent = !string.IsNullOrWhiteSpace(row.EventId),
                    MetaServerStatus = ReadStringMetadata(row.MetadataJson, "metaServerStatus"),
                    LearningReason = ResolveLearningReason(row, attribution)
                };
            })
            .ToList();
    }

    private static int CountDistinctVisitors(IEnumerable<MetaSignalEvent> rows) =>
        rows.Select(GetVisitorKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static MetaSignalAttributionSnapshot ResolveAttributionSnapshot(MetaSignalEvent row)
    {
        return new MetaSignalAttributionSnapshot(
            Normalize(row.UtmSource) ?? ReadResolvedAttributionString(row.MetadataJson, "utmSource"),
            Normalize(row.UtmMedium) ?? ReadResolvedAttributionString(row.MetadataJson, "utmMedium"),
            Normalize(row.UtmCampaign) ?? ReadResolvedAttributionString(row.MetadataJson, "utmCampaign"),
            Normalize(row.UtmId) ?? ReadResolvedAttributionString(row.MetadataJson, "utmId"),
            ReadResolvedAttributionString(row.MetadataJson, "metaCampaignId"),
            ReadResolvedAttributionString(row.MetadataJson, "metaAdSetId"),
            ReadResolvedAttributionString(row.MetadataJson, "metaAdId"),
            row.FbclidPresent ? "present" : null);
    }

    private static bool IsMetaAttributedPaid(MetaSignalAttributionSnapshot attribution) =>
        TrafficAttribution.IsMetaAttributedPaid(
            attribution.UtmSource,
            attribution.UtmMedium,
            attribution.UtmCampaign,
            attribution.Fbclid,
            attribution.MetaCampaignId,
            attribution.MetaAdSetId,
            attribution.MetaAdId);

    private static bool IsLocalhostOrInternalTest(MetaSignalEvent row)
    {
        var referrer = Normalize(row.Referrer);
        var host = Normalize(row.Host);
        var environment = Normalize(row.Environment);
        var trafficType = Normalize(row.TrafficType);

        return (referrer is not null && referrer.Contains("localhost", StringComparison.OrdinalIgnoreCase)) ||
               (host is not null && (host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                                      host.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                      host.StartsWith("::1", StringComparison.OrdinalIgnoreCase))) ||
               string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase) ||
               trafficType is nameof(TrafficType.Internal) or nameof(TrafficType.Test);
    }

    private static bool IsMetaLearningEligible(MetaSignalEvent row)
    {
        if (IsLocalhostOrInternalTest(row))
            return false;

        return row.MetaServerSent || row.MetaBrowserSent;
    }

    private static string ResolveLearningReason(MetaSignalEvent row, MetaSignalAttributionSnapshot attribution)
    {
        if (IsLocalhostOrInternalTest(row))
            return "Excluded: localhost/internal QA traffic.";

        if ((row.MetaServerSent || row.MetaBrowserSent) && IsMetaAttributedPaid(attribution))
            return "Included: sent to Meta and paid Meta-attributed.";

        if (row.MetaServerSent || row.MetaBrowserSent)
            return "Included: sent to Meta; not paid Meta-attributed.";

        return Normalize(row.TrafficType) switch
        {
            nameof(TrafficType.PaidAds) => "Excluded: paid traffic, but not Meta-attributed.",
            nameof(TrafficType.Direct) => "Excluded: direct/manual traffic.",
            nameof(TrafficType.Organic) => "Excluded: organic traffic.",
            nameof(TrafficType.Referral) => "Excluded: referral/social traffic.",
            nameof(TrafficType.Internal) => "Excluded: internal navigation or preview traffic.",
            nameof(TrafficType.Test) => "Excluded: test or QA traffic.",
            nameof(TrafficType.BotSuspicious) => "Excluded: bot or suspicious traffic.",
            nameof(TrafficType.Unknown) => "Excluded: unattributed traffic.",
            _ => "Excluded from Meta learning readiness."
        };
    }

    private static string? ReadResolvedAttributionString(string? metadataJson, string propertyName)
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

    private static string? GetVisitorKey(MetaSignalEvent row)
    {
        if (!string.IsNullOrWhiteSpace(row.SessionId)) return row.SessionId.Trim();
        if (!string.IsNullOrWhiteSpace(row.VisitorId)) return row.VisitorId.Trim();
        if (row.LeadId.HasValue && row.LeadId.Value != Guid.Empty) return row.LeadId.Value.ToString("D");
        return null;
    }

    private static MetaSignalHealthMetricDto BuildMetric(
        string key,
        string label,
        int numerator,
        int denominator,
        string detail)
    {
        if (denominator <= 0)
        {
            return new MetaSignalHealthMetricDto
            {
                Key = key,
                Label = label,
                Numerator = numerator,
                Denominator = denominator,
                Rate = 0,
                Status = "NoData",
                Detail = detail
            };
        }

        var rate = Math.Round((numerator * 100m) / denominator, 1);
        return new MetaSignalHealthMetricDto
        {
            Key = key,
            Label = label,
            Numerator = numerator,
            Denominator = denominator,
            Rate = rate,
            Status = ResolveMetricStatus(rate),
            Detail = detail
        };
    }

    private static MetaSignalHealthIssueDto BuildIssue(
        string key,
        string label,
        int count,
        int baseline,
        string detail)
    {
        var ratio = baseline <= 0 ? 0m : (decimal)count / baseline;
        return new MetaSignalHealthIssueDto
        {
            Key = key,
            Label = label,
            Count = count,
            Status = ResolveIssueStatus(count, ratio),
            Detail = detail
        };
    }

    private static string ResolveMetricStatus(decimal rate)
    {
        if (rate >= 98m) return "Healthy";
        if (rate >= 90m) return "Watch";
        if (rate >= 75m) return "Risk";
        return "Critical";
    }

    private static string ResolveIssueStatus(int count, decimal ratio)
    {
        if (count <= 0) return "Healthy";
        if (ratio <= 0.05m) return "Watch";
        if (ratio <= 0.15m) return "Risk";
        return "Critical";
    }

    private static bool IsBridgeEligibleAnalyticsEvent(HealthAnalyticsEventRow row)
    {
        if (!BridgeSourceEventTypes.Contains(row.EventType))
            return false;

        return !MetaSignalAnalyticsAliasCatalog.TryGet(row.EventType, out _)
            || MetaSignalAnalyticsAliasCatalog.IsBridgeEligibleAnalyticsSource(
                row.EventType,
                row.ScrollPercent,
                row.DwellMilliseconds,
                row.EngagedMilliseconds,
                row.IsBounceCandidate);
    }

    private static HashSet<string> BuildBridgeSourceEventTypes()
    {
        var leadAndViewContentSources = AnalyticsEventCatalog.Definitions
            .Where(x => x.CountsAsConfirmedLead || (x.EligibleForMetaSignal && x.CountsAsLandingView))
            .Select(x => x.Name);

        return new HashSet<string>(
            leadAndViewContentSources
                .Concat(ExplicitBridgeSourceEventTypes)
                .Concat(MetaSignalAnalyticsAliasCatalog.AnalyticsEventNames)
                .Concat(MetaSignalEventCatalog.Definitions.Select(x => x.Name)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static HealthMetaSignalContext CreateHealthMetaContext(HealthMetaSignalRow row)
    {
        return new HealthMetaSignalContext
        {
            Row = row,
            IsBridgeOwned = ReadStringMetadata(row.MetadataJson, "bridgeSource") == "analytics_events",
            SourceAnalyticsEventId = ReadInt64Metadata(row.MetadataJson, "sourceAnalyticsEventId"),
            SourceAnalyticsEventType = ReadStringMetadata(row.MetadataJson, "sourceAnalyticsEventType"),
            MetaServerStatus = ReadStringMetadata(row.MetadataJson, "metaServerStatus"),
            MetaServerAttempted = ReadBoolMetadataNullable(row.MetadataJson, "metaServerAttempted"),
            MetaServerSentMetadata = ReadBoolMetadataNullable(row.MetadataJson, "metaServerSent"),
            MetaServerDispatchedUtc = ReadDateTimeMetadata(row.MetadataJson, "metaServerDispatchedUtc")
        };
    }

    private static bool RequiresDispatcher(HealthMetaSignalRow row) =>
        ServerForwardEventNames.Contains(row.EventName);

    private static bool HasDispatcherActivity(HealthMetaSignalContext row) =>
        row.MetaServerDispatchedUtc.HasValue ||
        row.MetaServerAttempted.HasValue ||
        row.MetaServerSentMetadata.HasValue ||
        !string.IsNullOrWhiteSpace(row.MetaServerStatus);

    private static string ResolveDispatcherStatus(HealthMetaSignalContext row, DateTime dispatcherThresholdUtc)
    {
        if (!RequiresDispatcher(row.Row))
            return "Not Required";

        if (HasDispatcherActivity(row))
            return row.Row.MetaServerSent || string.Equals(row.MetaServerStatus, "sent", StringComparison.OrdinalIgnoreCase)
                ? "Sent"
                : "Processed";

        return row.Row.CreatedUtc <= dispatcherThresholdUtc
            ? "Pending"
            : "Queued";
    }

    private static string ResolveAuthorityStatus(HealthMetaSignalContext row)
    {
        if (!RequiresDispatcher(row.Row))
            return "N/A";

        if (!HasDispatcherActivity(row))
            return "Pending";

        return string.Equals(row.MetaServerStatus, "blocked_by_authority", StringComparison.OrdinalIgnoreCase)
            ? "Blocked"
            : "Allowed";
    }

    private static string BuildFunnelStepLabel(HealthMetaSignalRow row)
    {
        var stepName = Normalize(row.StepName);
        if (row.FunnelStep.HasValue && !string.IsNullOrWhiteSpace(stepName))
            return $"{row.FunnelStep.Value} · {stepName}";
        if (row.FunnelStep.HasValue)
            return row.FunnelStep.Value.ToString();
        return stepName ?? "—";
    }

    /// <summary>
    /// Expands an agent scope to all tracking profile IDs owned by the same AgentUpn.
    /// This is the true agent boundary: same authenticated agent account, not slug guessing.
    /// </summary>
    private async Task<Guid[]?> ResolveScopedAgentIdsAsync(ScopeContext scope, CancellationToken ct)
    {
        if (scope.ScopeType != ScopeType.Agent || !scope.AgentTrackingProfileId.HasValue)
            return null;

        var selectedId = scope.AgentTrackingProfileId.Value;

        var upn = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.Id == selectedId)
            .Select(p => p.AgentUpn)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(upn))
            return new[] { selectedId };

        var ids = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.AgentUpn == upn)
            .Select(p => p.Id)
            .Distinct()
            .ToListAsync(ct);

        if (!ids.Contains(selectedId))
            ids.Add(selectedId);

        return ids.ToArray();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ReadBoolMetadata(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property))
                return false;
            return property.ValueKind == JsonValueKind.True || (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed) && parsed);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadStringMetadata(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                return null;
            return Normalize(property.GetString());
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBoolMetadataNullable(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property))
                return null;

            if (property.ValueKind == JsonValueKind.True)
                return true;
            if (property.ValueKind == JsonValueKind.False)
                return false;
            if (property.ValueKind == JsonValueKind.String &&
                bool.TryParse(property.GetString(), out var parsed))
                return parsed;
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static long? ReadInt64Metadata(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty(propertyName, out var property))
                return null;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numeric))
                return numeric;
            if (property.ValueKind == JsonValueKind.String &&
                long.TryParse(property.GetString(), out var parsed))
                return parsed;
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static DateTime? ReadDateTimeMetadata(string? metadataJson, string propertyName)
    {
        var raw = ReadStringMetadata(metadataJson, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(raw, out var parsed)
            ? parsed
            : null;
    }

    private sealed class VisitorSignalSummary
    {
        public string VisitorKey { get; init; } = string.Empty;
        public int MaxTotalSignalScore { get; init; }
        public bool Engaged { get; set; }
        public bool FunnelStarted { get; set; }
        public bool DiscoveryComplete { get; set; }
        public bool RecommendationViewed { get; set; }
        public bool ContactStepReached { get; set; }
        public bool ContactInputStarted { get; set; }
        public bool PhoneCompleted { get; set; }
        public bool RequiredContactFieldsCompleted { get; set; }
        public bool SubmitAttempted { get; set; }
        public bool LeadSubmitted { get; set; }
        public bool HighIntentSignal { get; set; }
        public bool LeadReadySignal { get; set; }
        public bool HighIntentAbandon { get; set; }
        public bool ContactStepAbandon { get; set; }
        public DateTime? LastMeaningfulProgressUtc { get; set; }
        public DateTime? LastAbandonUtc { get; set; }
        public string ScoreTier { get; set; } = "ColdVisitor";

        public bool IsHighIntent =>
            RecommendationViewed ||
            ContactStepReached ||
            ContactInputStarted ||
            PhoneCompleted ||
            RequiredContactFieldsCompleted ||
            SubmitAttempted ||
            LeadSubmitted ||
            HighIntentSignal;

        public bool IsLeadReady =>
            RequiredContactFieldsCompleted ||
            SubmitAttempted ||
            LeadSubmitted ||
            LeadReadySignal;
    }

    private sealed class HealthAnalyticsEventRow
    {
        public long Id { get; init; }
        public string EventType { get; init; } = string.Empty;
        public string? SessionId { get; init; }
        public string? VisitorId { get; init; }
        public DateTime EventUtc { get; init; }
        public int? ScrollPercent { get; init; }
        public long? DwellMilliseconds { get; init; }
        public long? EngagedMilliseconds { get; init; }
        public bool? IsBounceCandidate { get; init; }
    }

    private sealed class HealthMetaSignalRow
    {
        public long Id { get; init; }
        public DateTime CreatedUtc { get; init; }
        public string EventId { get; init; } = string.Empty;
        public string EventName { get; init; } = string.Empty;
        public string? EventCategory { get; init; }
        public string? SessionId { get; init; }
        public string? VisitorId { get; init; }
        public Guid? LeadId { get; init; }
        public int? FunnelStep { get; init; }
        public string? StepName { get; init; }
        public bool MetaBrowserSent { get; init; }
        public bool MetaServerSent { get; init; }
        public string? MetaDeduplicationKey { get; init; }
        public string? MetadataJson { get; init; }
    }

    private sealed class HealthMetaSignalContext
    {
        public HealthMetaSignalRow Row { get; init; } = new();
        public bool IsBridgeOwned { get; init; }
        public long? SourceAnalyticsEventId { get; init; }
        public string? SourceAnalyticsEventType { get; init; }
        public string? MetaServerStatus { get; init; }
        public bool? MetaServerAttempted { get; init; }
        public bool? MetaServerSentMetadata { get; init; }
        public DateTime? MetaServerDispatchedUtc { get; init; }
    }

    private sealed record MetaSignalAttributionSnapshot(
        string? UtmSource,
        string? UtmMedium,
        string? UtmCampaign,
        string? UtmId,
        string? MetaCampaignId,
        string? MetaAdSetId,
        string? MetaAdId,
        string? Fbclid);
}

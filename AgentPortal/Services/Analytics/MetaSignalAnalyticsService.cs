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
}

public sealed class MetaSignalAnalyticsService : IMetaSignalAnalyticsService
{
    private const string LearningScopeNoteText = "Meta Paid Signal Intelligence only evaluates paid Meta-attributed traffic. Non-paid/manual tests may appear in Quote Funnel and Conversion Center but are excluded from Meta learning readiness.";
    private readonly MasterAppDbContext _db;

    public MetaSignalAnalyticsService(MasterAppDbContext db)
    {
        _db = db;
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

        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            var agentId = scope.AgentTrackingProfileId.Value;
            baseQuery = baseQuery.Where(x => x.AgentTrackingProfileId == agentId);
        }

        baseQuery = ApplyTrafficFilter(baseQuery, trafficType);

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
                "Contact Step Reached",
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

    private static IQueryable<MetaSignalEvent> ApplyTrafficFilter(IQueryable<MetaSignalEvent> query, TrafficType trafficType)
    {
        return trafficType switch
        {
            TrafficType.All => query,
            TrafficType.PaidAds => query.Where(x => x.TrafficType == nameof(TrafficType.PaidAds)),
            TrafficType.NonPaid => query.Where(x =>
                x.TrafficType == nameof(TrafficType.Organic) ||
                x.TrafficType == nameof(TrafficType.Direct) ||
                x.TrafficType == nameof(TrafficType.Referral) ||
                x.TrafficType == nameof(TrafficType.Unknown)),
            TrafficType.Organic => query.Where(x => x.TrafficType == nameof(TrafficType.Organic)),
            TrafficType.Direct => query.Where(x => x.TrafficType == nameof(TrafficType.Direct)),
            TrafficType.Referral => query.Where(x => x.TrafficType == nameof(TrafficType.Referral)),
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

                foreach (var row in g)
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
        if (ReadBoolMetadata(row.MetadataJson, "contactStepReached"))
            summary.ContactStepReached = true;
        if (ReadBoolMetadata(row.MetadataJson, "contactInputStarted"))
            summary.ContactInputStarted = true;
        if (ReadBoolMetadata(row.MetadataJson, "phoneCompleted"))
            summary.PhoneCompleted = true;
        if (ReadBoolMetadata(row.MetadataJson, "requiredContactFieldsComplete"))
            summary.RequiredContactFieldsCompleted = true;
        if (ReadBoolMetadata(row.MetadataJson, "contactStepAbandon"))
            summary.ContactStepAbandon = true;

        switch (row.EventName)
        {
            case "SessionEngaged5s":
            case "SessionEngaged15s":
            case "MeaningfulScroll":
                summary.Engaged = true;
                break;
            case "LeadFormStart":
                summary.FunnelStarted = true;
                break;
            case "DiscoveryComplete":
                summary.FunnelStarted = true;
                summary.DiscoveryComplete = true;
                break;
            case "FunnelStepComplete":
                if (row.FunnelStep == 1)
                {
                    summary.FunnelStarted = true;
                    summary.DiscoveryComplete = true;
                }
                break;
            case "RecommendationViewed":
                summary.RecommendationViewed = true;
                break;
            case "ContactStepReached":
                summary.ContactStepReached = true;
                break;
            case "ContactInputStarted":
                summary.ContactInputStarted = true;
                break;
            case "PhoneFieldCompleted":
                summary.PhoneCompleted = true;
                break;
            case "RequiredContactFieldsCompleted":
                summary.RequiredContactFieldsCompleted = true;
                break;
            case "SubmitAttempt":
                summary.SubmitAttempted = true;
                break;
            case "HighIntentLeadSignal":
                summary.HighIntentSignal = true;
                break;
            case "LeadReadySignal":
                summary.LeadReadySignal = true;
                break;
            case "AbandonedHighIntentLead":
                summary.HighIntentAbandon = true;
                break;
            case "Lead":
            case "QualifiedLead":
                summary.LeadSubmitted = true;
                break;
        }

        BackfillVisitorSummaryProgress(summary);
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
                    IsNonPaidOrManual = !isMetaAttributedPaid && trafficType is nameof(TrafficType.Direct) or nameof(TrafficType.Organic) or nameof(TrafficType.Referral) or nameof(TrafficType.Unknown),
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

    private static bool IsMetaLearningEligible(MetaSignalEvent row)
    {
        if (!string.Equals(Normalize(row.PageMode), "paid_landing", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsMetaAttributedPaid(ResolveAttributionSnapshot(row));
    }

    private static string ResolveLearningReason(MetaSignalEvent row, MetaSignalAttributionSnapshot attribution)
    {
        if (!string.Equals(Normalize(row.PageMode), "paid_landing", StringComparison.OrdinalIgnoreCase))
            return "Excluded: not a paid landing experience.";

        if (IsMetaAttributedPaid(attribution))
            return "Included: paid Meta-attributed traffic.";

        return Normalize(row.TrafficType) switch
        {
            nameof(TrafficType.PaidAds) => "Excluded: paid traffic, but not Meta-attributed.",
            nameof(TrafficType.Direct) => "Excluded: direct/manual traffic.",
            nameof(TrafficType.Organic) => "Excluded: organic traffic.",
            nameof(TrafficType.Referral) => "Excluded: referral/social traffic.",
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

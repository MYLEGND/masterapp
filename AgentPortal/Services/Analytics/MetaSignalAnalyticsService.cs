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
    private readonly MasterAppDbContext _db;
    private readonly ILogger<MetaSignalAnalyticsService> _logger;

    public MetaSignalAnalyticsService(
        MasterAppDbContext db,
        ILogger<MetaSignalAnalyticsService> logger)
    {
        _db = db;
        _logger = logger;
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
            .Select(x => Normalize(x.UtmCampaign))
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

        var availableScoreTiers = baseRows
            .Select(x => Normalize(x.ScoreTier))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => ScoreTierOrder(x))
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = baseRows
            .Where(x => string.IsNullOrWhiteSpace(quoteType) || string.Equals(x.QuoteType, quoteType.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(campaign) || string.Equals(x.UtmCampaign, campaign.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(pageMode) || string.Equals(x.PageMode, pageMode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(scoreTier) || string.Equals(x.ScoreTier, scoreTier.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        return BuildDashboard(rows, range, trafficType, availableQuoteTypes, availableCampaigns, availablePageModes, availableScoreTiers);
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
            TotalSignalEvents = dashboard.TotalSignalEvents,
            TotalVisitors = dashboard.TotalVisitors,
            HighIntentVisitors = dashboard.HighIntentVisitors,
            LeadReadyVisitors = dashboard.LeadReadyVisitors,
            SubmittedLeads = dashboard.SubmittedLeads,
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
        TrafficType trafficType,
        List<string> availableQuoteTypes,
        List<string> availableCampaigns,
        List<string> availablePageModes,
        List<string> availableScoreTiers)
    {
        var visitorGroups = rows
            .GroupBy(GetVisitorKey)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToList();

        var visitorTierRows = visitorGroups
            .Select(g =>
            {
                var maxScore = g.Max(x => x.TotalSignalScore);
                return new
                {
                    VisitorKey = g.Key,
                    Score = maxScore,
                    ScoreTier = ResolveScoreTier(maxScore)
                };
            })
            .ToList();

        var highIntentVisitors = visitorTierRows.Count(x => string.Equals(x.ScoreTier, "HighIntentVisitor", StringComparison.OrdinalIgnoreCase));
        var leadReadyVisitors = visitorTierRows.Count(x => string.Equals(x.ScoreTier, "LeadReadyVisitor", StringComparison.OrdinalIgnoreCase));
        var submittedVisitors = visitorTierRows.Count(x => string.Equals(x.ScoreTier, "SubmittedLead", StringComparison.OrdinalIgnoreCase));

        var highIntentAbandons = rows
            .Where(x => string.Equals(x.EventName, "AbandonedHighIntentLead", StringComparison.OrdinalIgnoreCase))
            .Select(GetVisitorKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var contactStepAbandons = rows
            .Where(x => string.Equals(x.EventName, "AbandonedHighIntentLead", StringComparison.OrdinalIgnoreCase) && ReadBoolMetadata(x.MetadataJson, "contactStepAbandon"))
            .Select(GetVisitorKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var eventsByQuoteType = rows
            .GroupBy(x => Normalize(x.QuoteType) ?? "unknown")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalValueRowDto
            {
                Label = g.Key,
                Value = g.Count()
            })
            .ToList();

        var eventsByCampaign = rows
            .GroupBy(x => Normalize(x.UtmCampaign) ?? "Unattributed")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalValueRowDto
            {
                Label = g.Key,
                Value = g.Count()
            })
            .ToList();

        var visitorsByScoreTier = visitorTierRows
            .GroupBy(x => x.ScoreTier)
            .OrderBy(g => ScoreTierOrder(g.Key))
            .Select(g => new MetaSignalTierRowDto
            {
                ScoreTier = g.Key,
                Visitors = g.Count()
            })
            .ToList();

        var averageScoreByCampaign = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.UtmCampaign))
            .GroupBy(x => x.UtmCampaign!)
            .OrderByDescending(g => g.Average(x => x.TotalSignalScore))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalAverageRowDto
            {
                Label = g.Key,
                AverageScore = Math.Round((decimal)g.Average(x => x.TotalSignalScore), 1)
            })
            .ToList();

        var averageScoreByPageVariant = rows
            .GroupBy(x => Normalize(x.PageVariant) ?? Normalize(x.EffectivePageKey) ?? Normalize(x.PageKey) ?? "default")
            .OrderByDescending(g => g.Average(x => x.TotalSignalScore))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetaSignalAverageRowDto
            {
                Label = g.Key,
                AverageScore = Math.Round((decimal)g.Average(x => x.TotalSignalScore), 1)
            })
            .ToList();

        var ladder = BuildEventLadder(rows);
        var frictionHotspots = BuildFrictionHotspots(rows);
        var bestVariant = ResolveBestVariant(rows);
        var recommendedOptimizationEvent = ResolveRecommendedOptimizationEvent(ladder, leadReadyVisitors, submittedVisitors, highIntentVisitors);

        return new MetaSignalDashboardDto
        {
            RangeLabel = range.Label,
            TrafficFilterLabel = TrafficAttribution.BucketLabel(trafficType),
            TotalSignalEvents = rows.Count,
            TotalVisitors = visitorGroups.Count,
            HighIntentVisitors = highIntentVisitors,
            LeadReadyVisitors = leadReadyVisitors,
            SubmittedLeads = submittedVisitors,
            HighIntentAbandons = highIntentAbandons,
            ContactStepAbandons = contactStepAbandons,
            SignalToLeadConversionRate = visitorGroups.Count == 0 ? 0 : Math.Round((submittedVisitors * 100m) / visitorGroups.Count, 2),
            RecommendedOptimizationEvent = recommendedOptimizationEvent,
            BestPerformingLandingPageVersion = bestVariant,
            WorstFrictionStep = frictionHotspots.FirstOrDefault()?.Label ?? "—",
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
            FrictionHotspots = frictionHotspots
        };
    }

    private static List<MetaSignalLadderRowDto> BuildEventLadder(IEnumerable<MetaSignalEvent> rows)
    {
        var stages = new (string Key, string Label, Func<MetaSignalEvent, bool> Match)[]
        {
            ("view_content", "View Content", x => string.Equals(x.EventName, "ViewContent", StringComparison.OrdinalIgnoreCase)),
            ("lead_form_start", "Lead Form Start", x => string.Equals(x.EventName, "LeadFormStart", StringComparison.OrdinalIgnoreCase)),
            ("step_1_complete", "Step 1 Complete", x => string.Equals(x.EventName, "FunnelStepComplete", StringComparison.OrdinalIgnoreCase) && x.FunnelStep == 1),
            ("recommendation_viewed", "Recommendation Viewed", x => string.Equals(x.EventName, "RecommendationViewed", StringComparison.OrdinalIgnoreCase)),
            ("contact_step_reached", "Contact Step Reached", x => string.Equals(x.EventName, "ContactStepReached", StringComparison.OrdinalIgnoreCase)),
            ("high_intent", "High Intent", x => string.Equals(x.EventName, "HighIntentLeadSignal", StringComparison.OrdinalIgnoreCase)),
            ("lead_ready", "Lead Ready", x => string.Equals(x.EventName, "LeadReadySignal", StringComparison.OrdinalIgnoreCase)),
            ("lead", "Submitted Lead", x => string.Equals(x.EventName, "Lead", StringComparison.OrdinalIgnoreCase))
        };

        var result = new List<MetaSignalLadderRowDto>();
        int? priorCount = null;
        foreach (var stage in stages)
        {
            var count = rows
                .Where(stage.Match)
                .Select(GetVisitorKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            result.Add(new MetaSignalLadderRowDto
            {
                StepKey = stage.Key,
                StepLabel = stage.Label,
                Visitors = count,
                ProgressionRate = priorCount.HasValue && priorCount.Value > 0
                    ? Math.Round((count * 100m) / priorCount.Value, 2)
                    : (decimal?)null
            });

            priorCount = count;
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
            .GroupBy(x => ResolveFrictionLabel(x))
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
        var contactStepVisitors = ladder.FirstOrDefault(x => x.StepKey == "contact_step_reached")?.Visitors ?? 0;
        if (submittedVisitors >= 25) return "Lead";
        if (leadReadyVisitors >= 35) return "LeadReadySignal";
        if (contactStepVisitors >= 40) return "ContactStepReached";
        if (highIntentVisitors >= 25) return "HighIntentLeadSignal";
        return "LeadFormStart";
    }

    private static IQueryable<MetaSignalEvent> ApplyTrafficFilter(IQueryable<MetaSignalEvent> query, TrafficType trafficType)
    {
        return trafficType switch
        {
            TrafficType.All => query,
            TrafficType.PaidAds => query.Where(x => x.TrafficType == nameof(TrafficType.PaidAds)),
            TrafficType.NonPaid => query.Where(x => x.TrafficType != nameof(TrafficType.PaidAds)),
            TrafficType.Organic => query.Where(x => x.TrafficType == nameof(TrafficType.Organic)),
            TrafficType.Direct => query.Where(x => x.TrafficType == nameof(TrafficType.Direct)),
            TrafficType.Referral => query.Where(x => x.TrafficType == nameof(TrafficType.Referral)),
            TrafficType.Unknown => query.Where(x => x.TrafficType == nameof(TrafficType.Unknown)),
            _ => query
        };
    }

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

    private static string ResolveScoreTier(int score) => score switch
    {
        < 20 => "ColdVisitor",
        < 40 => "EngagedVisitor",
        < 60 => "FunnelStarter",
        < 80 => "HighIntentVisitor",
        < 100 => "LeadReadyVisitor",
        _ => "SubmittedLead"
    };

    private static int ScoreTierOrder(string? scoreTier) => Normalize(scoreTier) switch
    {
        "ColdVisitor" => 1,
        "EngagedVisitor" => 2,
        "FunnelStarter" => 3,
        "HighIntentVisitor" => 4,
        "LeadReadyVisitor" => 5,
        "SubmittedLead" => 6,
        _ => 99
    };

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
}

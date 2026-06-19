using System.Net;
using System.Text.Json;
using AgentPortal.Models.Analytics;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;

namespace AgentPortal.Services.Analytics;

public interface IAnalyticsIncidentQueryService
{
    Task<AnalyticsIncidentMonitorDto> GetSystemMonitorAsync(CancellationToken ct = default);
    Task<int> RefreshSystemIncidentsAsync(CancellationToken ct = default);
}

public sealed class AnalyticsIncidentQueryService : IAnalyticsIncidentQueryService
{
    private const int MonitorWindowHours = 24;
    private const int TimelineWindowHours = 24;
    private const string GlobalScopeKey = "global";

    private static readonly string[] LandingEventNames = AnalyticsEventCatalog.Definitions
        .Where(definition => definition.CountsAsLandingView)
        .Select(definition => definition.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] FunnelStartEventNames = AnalyticsEventCatalog.Definitions
        .Where(definition => definition.CountsAsFunnelStart)
        .Select(definition => definition.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] FormStartEventNames = AnalyticsEventCatalog.Definitions
        .Where(definition => definition.CountsAsFormStart)
        .Select(definition => definition.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] PurchaseEventNames =
    [
        "AppointmentBooked",
        "AppointmentCompleted",
        "ApplicationSubmitted",
        "PolicyIssued",
        "PolicyPaid"
    ];

    private static readonly string[] AttributionEligibleMetaEvents =
    [
        "LeadFormStart",
        "HighIntentLeadSignal",
        "LeadReadySignal",
        "Lead",
        "QualifiedLead",
        "AppointmentBooked",
        "AppointmentCompleted",
        "ApplicationSubmitted",
        "PolicyIssued",
        "PolicyPaid"
    ];

    private readonly MasterAppDbContext _db;
    private readonly IAnalyticsQueryService _analytics;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<AnalyticsIncidentQueryService> _logger;
    private readonly string? _envFilter;
    private readonly bool _excludeLocalHosts;

    public AnalyticsIncidentQueryService(
        MasterAppDbContext db,
        IAnalyticsQueryService analytics,
        IEmailSender emailSender,
        IConfiguration config,
        ILogger<AnalyticsIncidentQueryService> logger)
    {
        _db = db;
        _analytics = analytics;
        _emailSender = emailSender;
        _logger = logger;

        var configuredFilter = NormalizeEnv(config["Analytics:EnvironmentFilter"] ?? config["Analytics__EnvironmentFilter"]);
        var runtimeEnvironment = NormalizeEnv(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        _envFilter = configuredFilter ?? (runtimeEnvironment == "prod" ? "prod" : null);
        _excludeLocalHosts = ParseBool(config["Analytics:ExcludeLocalHosts"] ?? config["Analytics__ExcludeLocalHosts"]);
    }

    public async Task<AnalyticsIncidentMonitorDto> GetSystemMonitorAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var metrics = await BuildSystemMetricsAsync(nowUtc, ct);

        List<AnalyticsDriftAlert> activeAlerts;
        List<AnalyticsDriftAlert> timelineAlerts;

        try
        {
            activeAlerts = await _db.AnalyticsDriftAlerts.AsNoTracking()
                .Where(x => x.ScopeKey == GlobalScopeKey && x.IsActive)
                .OrderByDescending(x => SeverityRank(x.Severity))
                .ThenByDescending(x => x.ObservedUtc)
                .ToListAsync(ct);

            var timelineCutoffUtc = nowUtc.AddHours(-TimelineWindowHours);
            timelineAlerts = await _db.AnalyticsDriftAlerts.AsNoTracking()
                .Where(x => x.ScopeKey == GlobalScopeKey)
                .Where(x => x.ObservedUtc >= timelineCutoffUtc || (x.ResolvedUtc.HasValue && x.ResolvedUtc.Value >= timelineCutoffUtc))
                .OrderByDescending(x => x.ObservedUtc)
                .Take(40)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incident monitor fallback: AnalyticsDriftAlerts table not yet available or query failed.");
            activeAlerts = [];
            timelineAlerts = [];
        }

        return new AnalyticsIncidentMonitorDto
        {
            ScopeLabel = "System-wide",
            RangeLabel = "Last 24 Hours",
            LastUpdatedUtc = nowUtc,
            ActiveIncidentCount = activeAlerts.Count,
            ActiveCriticalCount = activeAlerts.Count(x => SeverityRank(x.Severity) >= SeverityRank("Critical")),
            ActiveHighCount = activeAlerts.Count(x => SeverityRank(x.Severity) == SeverityRank("High")),
            ActiveIncidents = activeAlerts.Select(MapAlert).ToList(),
            Timeline = timelineAlerts.Select(MapTimeline).ToList(),
            FocusMetrics = metrics.FocusMetrics,
            AttributionHealth = metrics.AttributionHealth,
            FunnelHealth = metrics.FunnelHealth
        };
    }

    public async Task<int> RefreshSystemIncidentsAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var metrics = await BuildSystemMetricsAsync(nowUtc, ct);
        var observations = metrics.Observations
            .Where(observation => SeverityRank(observation.Severity) > 0)
            .ToList();

        List<AnalyticsDriftAlert> activeAlerts;
        try
        {
            activeAlerts = await _db.AnalyticsDriftAlerts
                .Where(x => x.ScopeKey == GlobalScopeKey && x.IsActive)
                .OrderByDescending(x => x.LastDetectedUtc)
                .ToListAsync(ct);
        }
        catch (Exception ex) when (IsDriftAlertsStoreUnavailable(ex))
        {
            _logger.LogWarning(ex, "Incident monitor refresh skipped because AnalyticsDriftAlerts is not yet available.");
            return 0;
        }

        var emailQueue = new List<AnalyticsDriftAlert>();
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var observation in observations)
        {
            activeKeys.Add(observation.IncidentKey);

            var matchingAlerts = activeAlerts
                .Where(x => string.Equals(x.IncidentKey, observation.IncidentKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastDetectedUtc)
                .ToList();

            var activeAlert = matchingAlerts.FirstOrDefault();
            var previousRank = activeAlert == null ? 0 : SeverityRank(activeAlert.Severity);

            if (activeAlert == null)
            {
                activeAlert = new AnalyticsDriftAlert
                {
                    IncidentKey = observation.IncidentKey,
                    MetricKey = observation.MetricKey,
                    EventType = observation.EventType,
                    Category = observation.Category,
                    MetricUnit = observation.MetricUnit,
                    ScopeKey = GlobalScopeKey,
                    FirstDetectedUtc = nowUtc,
                    IsActive = true
                };

                _db.AnalyticsDriftAlerts.Add(activeAlert);
                activeAlerts.Add(activeAlert);
            }

            foreach (var duplicate in matchingAlerts.Skip(1))
            {
                duplicate.IsActive = false;
                duplicate.ResolvedUtc = nowUtc;
                duplicate.LastDetectedUtc = nowUtc;
                duplicate.ObservedUtc = nowUtc;
            }

            activeAlert.EventType = observation.EventType;
            activeAlert.Category = observation.Category;
            activeAlert.Severity = observation.Severity;
            activeAlert.MetricUnit = observation.MetricUnit;
            activeAlert.CurrentValue = observation.CurrentValue;
            activeAlert.BaselineValue = observation.BaselineValue;
            activeAlert.DeviationPercent = observation.DeviationPercent;
            activeAlert.Summary = observation.Summary;
            activeAlert.WindowStartUtc = observation.WindowStartUtc;
            activeAlert.WindowEndUtc = observation.WindowEndUtc;
            activeAlert.LastDetectedUtc = nowUtc;
            activeAlert.ObservedUtc = nowUtc;
            activeAlert.ResolvedUtc = null;
            activeAlert.IsActive = true;
            activeAlert.DetailsJson = JsonSerializer.Serialize(new
            {
                observation.PreviousWindowStartUtc,
                observation.PreviousWindowEndUtc
            });

            var newRank = SeverityRank(activeAlert.Severity);
            if (newRank >= SeverityRank("High") &&
                (activeAlert.LastNotifiedUtc == null || previousRank < newRank))
            {
                emailQueue.Add(activeAlert);
            }
        }

        foreach (var alert in activeAlerts.Where(x => x.IsActive && !activeKeys.Contains(x.IncidentKey)))
        {
            alert.IsActive = false;
            alert.ResolvedUtc = nowUtc;
            alert.ObservedUtc = nowUtc;
            alert.LastDetectedUtc = nowUtc;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsDriftAlertsStoreUnavailable(ex))
        {
            _logger.LogWarning(ex, "Incident monitor refresh skipped while saving because AnalyticsDriftAlerts is not yet available.");
            return 0;
        }

        foreach (var alert in emailQueue)
        {
            var sent = await SendIncidentEmailAsync(alert);
            if (!sent)
            {
                continue;
            }

            alert.LastNotifiedUtc = DateTime.UtcNow;
        }

        if (emailQueue.Count > 0)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsDriftAlertsStoreUnavailable(ex))
            {
                _logger.LogWarning(ex, "Incident monitor notification state could not be saved because AnalyticsDriftAlerts is not yet available.");
                return 0;
            }
        }

        return observations.Count;
    }

    private static bool IsDriftAlertsStoreUnavailable(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message ?? string.Empty;
            if (!message.Contains("AnalyticsDriftAlerts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<SystemMetricsSnapshot> BuildSystemMetricsAsync(DateTime nowUtc, CancellationToken ct)
    {
        var currentWindowStartUtc = nowUtc.AddHours(-MonitorWindowHours);
        var previousWindowStartUtc = currentWindowStartUtc.AddHours(-MonitorWindowHours);

        var currentRange = BuildRange(currentWindowStartUtc, nowUtc);
        var previousRange = BuildRange(previousWindowStartUtc, currentWindowStartUtc);

        var resolvedScope = await ResolveScopeAsync(null, false);
        var currentEvents = await _analytics.ScopedEvents(currentRange, resolvedScope).ToListAsync(ct);
        var previousEvents = await _analytics.ScopedEvents(previousRange, resolvedScope).ToListAsync(ct);

        var currentLeads = await QueryWebsiteLeads(currentWindowStartUtc, nowUtc).CountAsync(ct);
        var previousLeads = await QueryWebsiteLeads(previousWindowStartUtc, currentWindowStartUtc).CountAsync(ct);

        var currentPurchases = await QueryMetaSignalEvents(currentWindowStartUtc, nowUtc)
            .Where(x => PurchaseEventNames.Contains(x.EventName))
            .CountAsync(ct);

        var previousPurchases = await QueryMetaSignalEvents(previousWindowStartUtc, currentWindowStartUtc)
            .Where(x => PurchaseEventNames.Contains(x.EventName))
            .CountAsync(ct);

        var currentPageViews = CountDistinctUnits(currentEvents, LandingEventNames);
        var previousPageViews = CountDistinctUnits(previousEvents, LandingEventNames);

        var currentFormStarts = CountDistinctUnits(currentEvents, FormStartEventNames);
        var previousFormStarts = CountDistinctUnits(previousEvents, FormStartEventNames);

        var currentQuoteStarts = CountDistinctUnits(currentEvents, FunnelStartEventNames);
        var previousQuoteStarts = CountDistinctUnits(previousEvents, FunnelStartEventNames);

        var currentAttribution = await BuildAttributionHealthAsync(currentWindowStartUtc, nowUtc, ct);
        var previousAttribution = await BuildAttributionHealthAsync(previousWindowStartUtc, currentWindowStartUtc, ct);

        var currentLeadCaptureRate = Percent(currentLeads, currentFormStarts);
        var previousLeadCaptureRate = Percent(previousLeads, previousFormStarts);
        var currentLeadStartRate = Percent(currentFormStarts, currentPageViews);
        var previousLeadStartRate = Percent(previousFormStarts, previousPageViews);
        var currentLeadToPurchaseRate = Percent(currentPurchases, currentLeads);
        var previousLeadToPurchaseRate = Percent(previousPurchases, previousLeads);

        var observations = new List<IncidentObservation>();

        AddObservation(EvaluateCountMetric(
            metricKey: "lead_events",
            eventType: "Lead events",
            category: "volume",
            currentValue: currentLeads,
            baselineValue: previousLeads,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc));

        AddObservation(EvaluateCountMetric(
            metricKey: "purchase_events",
            eventType: "Purchase events",
            category: "volume",
            currentValue: currentPurchases,
            baselineValue: previousPurchases,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc));

        AddObservation(EvaluateCountMetric(
            metricKey: "quote_start_events",
            eventType: "Quote start events",
            category: "volume",
            currentValue: currentQuoteStarts,
            baselineValue: previousQuoteStarts,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc));

        AddObservation(EvaluateInverseRateMetric(
            metricKey: "browser_server_match_rate",
            eventType: "Browser/server attribution match",
            category: "attribution",
            currentValue: currentAttribution.ServerBrowserMatchRate,
            baselineValue: previousAttribution.ServerBrowserMatchRate,
            metricUnit: "percent",
            minimumSample: 20,
            sampleSize: currentAttribution.EligibleEvents,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc,
            lowThreshold: 80m,
            mediumThreshold: 65m,
            highThreshold: 50m,
            criticalThreshold: 35m));

        AddObservation(EvaluateDirectRateMetric(
            metricKey: "missing_attribution_rate",
            eventType: "Missing FBC / FBCLID attribution",
            category: "attribution",
            currentValue: currentAttribution.MissingAttributionRate,
            baselineValue: previousAttribution.MissingAttributionRate,
            metricUnit: "percent",
            minimumSample: 20,
            sampleSize: currentAttribution.EligibleEvents,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc,
            lowThreshold: 15m,
            mediumThreshold: 25m,
            highThreshold: 40m,
            criticalThreshold: 60m));

        AddObservation(EvaluateInverseRateMetric(
            metricKey: "page_view_to_lead_start_rate",
            eventType: "Page view to lead form start conversion",
            category: "funnel",
            currentValue: currentLeadStartRate,
            baselineValue: previousLeadStartRate,
            metricUnit: "percent",
            minimumSample: 10,
            sampleSize: currentPageViews,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc,
            lowThreshold: baselineThreshold(previousLeadStartRate, 0.85m),
            mediumThreshold: baselineThreshold(previousLeadStartRate, 0.75m),
            highThreshold: baselineThreshold(previousLeadStartRate, 0.60m),
            criticalThreshold: baselineThreshold(previousLeadStartRate, 0.40m)));

        AddObservation(EvaluateInverseRateMetric(
            metricKey: "lead_capture_rate",
            eventType: "Lead form start to lead conversion",
            category: "funnel",
            currentValue: currentLeadCaptureRate,
            baselineValue: previousLeadCaptureRate,
            metricUnit: "percent",
            minimumSample: 10,
            sampleSize: currentFormStarts,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc,
            lowThreshold: baselineThreshold(previousLeadCaptureRate, 0.85m),
            mediumThreshold: baselineThreshold(previousLeadCaptureRate, 0.75m),
            highThreshold: baselineThreshold(previousLeadCaptureRate, 0.60m),
            criticalThreshold: baselineThreshold(previousLeadCaptureRate, 0.40m)));

        AddObservation(EvaluateInverseRateMetric(
            metricKey: "lead_to_purchase_rate",
            eventType: "Lead to purchase conversion",
            category: "funnel",
            currentValue: currentLeadToPurchaseRate,
            baselineValue: previousLeadToPurchaseRate,
            metricUnit: "percent",
            minimumSample: 10,
            sampleSize: currentLeads,
            windowStartUtc: currentWindowStartUtc,
            windowEndUtc: nowUtc,
            previousWindowStartUtc: previousWindowStartUtc,
            previousWindowEndUtc: currentWindowStartUtc,
            lowThreshold: baselineThreshold(previousLeadToPurchaseRate, 0.85m),
            mediumThreshold: baselineThreshold(previousLeadToPurchaseRate, 0.75m),
            highThreshold: baselineThreshold(previousLeadToPurchaseRate, 0.60m),
            criticalThreshold: baselineThreshold(previousLeadToPurchaseRate, 0.40m)));

        return new SystemMetricsSnapshot(
            FocusMetrics:
            [
                BuildFocusMetric("lead_events", "Lead events", currentLeads, previousLeads, "count"),
                BuildFocusMetric("purchase_events", "Purchase events", currentPurchases, previousPurchases, "count"),
                BuildFocusMetric("quote_start_events", "Quote start events", currentQuoteStarts, previousQuoteStarts, "count")
            ],
            AttributionHealth: currentAttribution,
            FunnelHealth: new AnalyticsIncidentFunnelHealthDto
            {
                PageViews = currentPageViews,
                LeadFormStarts = currentFormStarts,
                Leads = currentLeads,
                Purchases = currentPurchases,
                PageViewToLeadStartRate = RoundPercent(currentLeadStartRate),
                LeadStartToLeadRate = RoundPercent(currentLeadCaptureRate),
                LeadToPurchaseRate = RoundPercent(currentLeadToPurchaseRate)
            },
            Observations: observations);

        void AddObservation(IncidentObservation? observation)
        {
            if (observation != null)
            {
                observations.Add(observation);
            }
        }

        static decimal baselineThreshold(decimal baseline, decimal multiplier)
        {
            if (baseline <= 0m)
            {
                return 0m;
            }

            return Math.Max(0m, baseline * multiplier);
        }
    }

    private async Task<AnalyticsIncidentAttributionHealthDto> BuildAttributionHealthAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var rows = await QueryMetaSignalEvents(fromUtc, toUtc)
            .Where(x => AttributionEligibleMetaEvents.Contains(x.EventName))
            .Select(x => new
            {
                x.MetaBrowserSent,
                x.MetaServerSent,
                x.FbcPresent,
                x.FbclidPresent
            })
            .ToListAsync(ct);

        var eligibleEvents = rows.Count;
        var browserSentEvents = rows.Count(x => x.MetaBrowserSent);
        var serverSentEvents = rows.Count(x => x.MetaServerSent);
        var matchedEvents = rows.Count(x => x.MetaBrowserSent && x.MetaServerSent);
        var missingAttributionEvents = rows.Count(x => !x.FbcPresent && !x.FbclidPresent);

        return new AnalyticsIncidentAttributionHealthDto
        {
            EligibleEvents = eligibleEvents,
            BrowserSentEvents = browserSentEvents,
            ServerSentEvents = serverSentEvents,
            MatchedEvents = matchedEvents,
            ServerBrowserMatchRate = RoundPercent(Percent(matchedEvents, eligibleEvents)),
            MissingAttributionEvents = missingAttributionEvents,
            MissingAttributionRate = RoundPercent(Percent(missingAttributionEvents, eligibleEvents))
        };
    }

    private IQueryable<WebsiteLead> QueryWebsiteLeads(DateTime fromUtc, DateTime toUtc)
    {
        var query = _db.WebsiteLeads.AsNoTracking()
            .Where(x => !x.IsDeleted)
            .Where(x => !x.IsInternal)
            .Where(x => x.CreatedUtc >= fromUtc && x.CreatedUtc <= toUtc);

        query = ApplyEnvironmentFilter(query);
        query = ApplyHostFilter(query);
        return query;
    }

    private static ValueTask<ScopeContext> ResolveScopeAsync(Guid? requestedAgentId, bool team)
    {
        _ = requestedAgentId;
        _ = team;

        return ValueTask.FromResult(new ScopeContext
        {
            ScopeType = ScopeType.Global
        });
    }

    private IQueryable<MetaSignalEvent> QueryMetaSignalEvents(DateTime fromUtc, DateTime toUtc)
    {
        var query = _db.MetaSignalEvents.AsNoTracking()
            .Where(x => x.CreatedUtc >= fromUtc && x.CreatedUtc <= toUtc)
            .Where(x =>
                x.TrafficType == null ||
                x.TrafficType == "" ||
                (x.TrafficType != "internal" &&
                 x.TrafficType != "test" &&
                 x.TrafficType != "bot_suspicious"));

        query = ApplyEnvironmentFilter(query);
        query = ApplyHostFilter(query);
        return query;
    }

    private IQueryable<WebsiteLead> ApplyEnvironmentFilter(IQueryable<WebsiteLead> query)
    {
        if (_envFilter == "prod")
        {
            return query.Where(x => x.Environment == "prod" || x.Environment == "production" || x.Environment == "Prod" || x.Environment == "Production");
        }

        if (_envFilter == "dev")
        {
            return query.Where(x => x.Environment == "dev" || x.Environment == "development" || x.Environment == "Dev" || x.Environment == "Development");
        }

        return query.Where(x =>
            x.Environment == null || x.Environment == "" ||
            x.Environment == "prod" || x.Environment == "production" || x.Environment == "Prod" || x.Environment == "Production" ||
            x.Environment == "dev" || x.Environment == "development" || x.Environment == "Dev" || x.Environment == "Development");
    }

    private IQueryable<MetaSignalEvent> ApplyEnvironmentFilter(IQueryable<MetaSignalEvent> query)
    {
        if (_envFilter == "prod")
        {
            return query.Where(x => x.Environment == "prod" || x.Environment == "production" || x.Environment == "Prod" || x.Environment == "Production");
        }

        if (_envFilter == "dev")
        {
            return query.Where(x => x.Environment == "dev" || x.Environment == "development" || x.Environment == "Dev" || x.Environment == "Development");
        }

        return query.Where(x =>
            x.Environment == null || x.Environment == "" ||
            x.Environment == "prod" || x.Environment == "production" || x.Environment == "Prod" || x.Environment == "Production" ||
            x.Environment == "dev" || x.Environment == "development" || x.Environment == "Dev" || x.Environment == "Development");
    }

    private IQueryable<WebsiteLead> ApplyHostFilter(IQueryable<WebsiteLead> query)
    {
        if (!_excludeLocalHosts)
        {
            return query;
        }

        return query.Where(x =>
            x.Host == null || x.Host == "" ||
            (!x.Host.StartsWith("localhost") &&
             !x.Host.StartsWith("127.0.0.1") &&
             !x.Host.StartsWith("::1") &&
             !x.Host.StartsWith("[::1]")));
    }

    private IQueryable<MetaSignalEvent> ApplyHostFilter(IQueryable<MetaSignalEvent> query)
    {
        if (!_excludeLocalHosts)
        {
            return query;
        }

        return query.Where(x =>
            x.Host == null || x.Host == "" ||
            (!x.Host.StartsWith("localhost") &&
             !x.Host.StartsWith("127.0.0.1") &&
             !x.Host.StartsWith("::1") &&
             !x.Host.StartsWith("[::1]")));
    }

    private static TimeRangeRequest BuildRange(DateTime fromUtc, DateTime toUtc)
    {
        return new TimeRangeRequest
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Grouping = TimeGrouping.Day,
            Label = "Last 24 Hours",
            Preset = "system_monitor_24h",
            ViewerTimeZone = TimeZoneInfo.Utc,
            QualityMode = TrafficQualityMode.RealHumanTraffic
        };
    }

    private static AnalyticsIncidentFocusMetricDto BuildFocusMetric(
        string key,
        string label,
        int currentValue,
        int previousValue,
        string metricUnit)
    {
        return new AnalyticsIncidentFocusMetricDto
        {
            Key = key,
            Label = label,
            CurrentValue = currentValue,
            PreviousValue = previousValue,
            DeltaPercent = RoundPercent(ComputeDeviationPercent(currentValue, previousValue)),
            MetricUnit = metricUnit
        };
    }

    private static IncidentObservation? EvaluateCountMetric(
        string metricKey,
        string eventType,
        string category,
        int currentValue,
        int baselineValue,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        DateTime previousWindowStartUtc,
        DateTime previousWindowEndUtc)
    {
        if (baselineValue < 5 && currentValue < 5)
        {
            return null;
        }

        var deviationPercent = ComputeDeviationPercent(currentValue, baselineValue);
        var severity = baselineValue >= 10 && currentValue == 0
            ? "Critical"
            : deviationPercent <= -85m
                ? "Critical"
                : deviationPercent <= -60m
                    ? "High"
                    : deviationPercent <= -35m || deviationPercent >= 175m
                        ? "Medium"
                        : deviationPercent <= -20m || deviationPercent >= 100m
                            ? "Low"
                            : string.Empty;

        if (string.IsNullOrWhiteSpace(severity))
        {
            return null;
        }

        var summary = deviationPercent >= 0m
            ? $"{eventType} rose {Math.Abs(RoundPercent(deviationPercent))}% versus the previous 24-hour window ({currentValue} vs {baselineValue})."
            : $"{eventType} fell {Math.Abs(RoundPercent(deviationPercent))}% versus the previous 24-hour window ({currentValue} vs {baselineValue}).";

        return new IncidentObservation(
            IncidentKey: $"system:{metricKey}",
            MetricKey: metricKey,
            EventType: eventType,
            Category: category,
            Severity: severity,
            MetricUnit: "count",
            CurrentValue: currentValue,
            BaselineValue: baselineValue,
            DeviationPercent: RoundPercent(deviationPercent),
            Summary: summary,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            PreviousWindowStartUtc: previousWindowStartUtc,
            PreviousWindowEndUtc: previousWindowEndUtc);
    }

    private static IncidentObservation? EvaluateInverseRateMetric(
        string metricKey,
        string eventType,
        string category,
        decimal currentValue,
        decimal baselineValue,
        string metricUnit,
        int minimumSample,
        int sampleSize,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        DateTime previousWindowStartUtc,
        DateTime previousWindowEndUtc,
        decimal lowThreshold,
        decimal mediumThreshold,
        decimal highThreshold,
        decimal criticalThreshold)
    {
        if (sampleSize < minimumSample)
        {
            return null;
        }

        var severity =
            currentValue <= criticalThreshold && criticalThreshold > 0m ? "Critical" :
            currentValue <= highThreshold && highThreshold > 0m ? "High" :
            currentValue <= mediumThreshold && mediumThreshold > 0m ? "Medium" :
            currentValue <= lowThreshold && lowThreshold > 0m ? "Low" :
            string.Empty;

        if (string.IsNullOrWhiteSpace(severity))
        {
            return null;
        }

        var deviationPercent = ComputeDeviationPercent(currentValue, baselineValue);
        var summary = $"{eventType} is down to {RoundPercent(currentValue)}% versus {RoundPercent(baselineValue)}% in the prior 24-hour window.";

        return new IncidentObservation(
            IncidentKey: $"system:{metricKey}",
            MetricKey: metricKey,
            EventType: eventType,
            Category: category,
            Severity: severity,
            MetricUnit: metricUnit,
            CurrentValue: RoundPercent(currentValue),
            BaselineValue: RoundPercent(baselineValue),
            DeviationPercent: RoundPercent(deviationPercent),
            Summary: summary,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            PreviousWindowStartUtc: previousWindowStartUtc,
            PreviousWindowEndUtc: previousWindowEndUtc);
    }

    private static IncidentObservation? EvaluateDirectRateMetric(
        string metricKey,
        string eventType,
        string category,
        decimal currentValue,
        decimal baselineValue,
        string metricUnit,
        int minimumSample,
        int sampleSize,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        DateTime previousWindowStartUtc,
        DateTime previousWindowEndUtc,
        decimal lowThreshold,
        decimal mediumThreshold,
        decimal highThreshold,
        decimal criticalThreshold)
    {
        if (sampleSize < minimumSample)
        {
            return null;
        }

        var severity =
            currentValue >= criticalThreshold ? "Critical" :
            currentValue >= highThreshold ? "High" :
            currentValue >= mediumThreshold ? "Medium" :
            currentValue >= lowThreshold ? "Low" :
            string.Empty;

        if (string.IsNullOrWhiteSpace(severity))
        {
            return null;
        }

        var deviationPercent = ComputeDeviationPercent(currentValue, baselineValue);
        var summary = $"{eventType} climbed to {RoundPercent(currentValue)}% versus {RoundPercent(baselineValue)}% in the prior 24-hour window.";

        return new IncidentObservation(
            IncidentKey: $"system:{metricKey}",
            MetricKey: metricKey,
            EventType: eventType,
            Category: category,
            Severity: severity,
            MetricUnit: metricUnit,
            CurrentValue: RoundPercent(currentValue),
            BaselineValue: RoundPercent(baselineValue),
            DeviationPercent: RoundPercent(deviationPercent),
            Summary: summary,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            PreviousWindowStartUtc: previousWindowStartUtc,
            PreviousWindowEndUtc: previousWindowEndUtc);
    }

    private async Task<bool> SendIncidentEmailAsync(AnalyticsDriftAlert alert)
    {
        if (SeverityRank(alert.Severity) < SeverityRank("High"))
        {
            return false;
        }

        var recipient = FounderGuard.FounderEmail;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return false;
        }

        var safeSummary = WebUtility.HtmlEncode(alert.Summary ?? string.Empty);
        var safeEventType = WebUtility.HtmlEncode(alert.EventType);
        var safeSeverity = WebUtility.HtmlEncode(alert.Severity);
        var safeCurrent = WebUtility.HtmlEncode(FormatMetricValue(alert.CurrentValue, alert.MetricUnit));
        var safeBaseline = WebUtility.HtmlEncode(FormatMetricValue(alert.BaselineValue, alert.MetricUnit));
        var safeDeviation = WebUtility.HtmlEncode($"{RoundPercent(alert.DeviationPercent)}%");
        var safeWindow = WebUtility.HtmlEncode($"{alert.WindowStartUtc:u} to {alert.WindowEndUtc:u}");

        var subject = $"[Website Analytics Incident][{alert.Severity}] {alert.EventType}";
        var htmlBody = $"""
            <div style="font-family:Arial,sans-serif;color:#0f172a;line-height:1.5;">
              <h2 style="margin:0 0 12px;color:#9f1239;">Website Analytics Incident</h2>
              <p style="margin:0 0 16px;"><strong>{safeSeverity}</strong> severity drift detected for <strong>{safeEventType}</strong>.</p>
              <table style="border-collapse:collapse;margin:0 0 16px;">
                <tr><td style="padding:4px 12px 4px 0;"><strong>Current</strong></td><td style="padding:4px 0;">{safeCurrent}</td></tr>
                <tr><td style="padding:4px 12px 4px 0;"><strong>Baseline</strong></td><td style="padding:4px 0;">{safeBaseline}</td></tr>
                <tr><td style="padding:4px 12px 4px 0;"><strong>Deviation</strong></td><td style="padding:4px 0;">{safeDeviation}</td></tr>
                <tr><td style="padding:4px 12px 4px 0;"><strong>Window</strong></td><td style="padding:4px 0;">{safeWindow}</td></tr>
              </table>
              <p style="margin:0 0 12px;">{safeSummary}</p>
              <p style="margin:0;">Review the founder-only Incident Monitor in AgentPortal Website Analytics for live details.</p>
            </div>
            """;

        var textBody = $"{alert.Severity} incident: {alert.EventType}\nCurrent: {FormatMetricValue(alert.CurrentValue, alert.MetricUnit)}\nBaseline: {FormatMetricValue(alert.BaselineValue, alert.MetricUnit)}\nDeviation: {RoundPercent(alert.DeviationPercent)}%\nWindow: {alert.WindowStartUtc:u} to {alert.WindowEndUtc:u}\n{alert.Summary}";

        var sent = await _emailSender.TrySendAsync(recipient, subject, htmlBody, textBody);
        if (!sent)
        {
            _logger.LogWarning("Incident alert email failed for {EventType} severity={Severity}", alert.EventType, alert.Severity);
        }

        return sent;
    }

    private static AnalyticsIncidentAlertDto MapAlert(AnalyticsDriftAlert alert)
    {
        return new AnalyticsIncidentAlertDto
        {
            EventType = alert.EventType,
            Severity = alert.Severity,
            CurrentValue = alert.CurrentValue,
            BaselineValue = alert.BaselineValue,
            DeviationPercent = alert.DeviationPercent,
            MetricUnit = alert.MetricUnit,
            Category = alert.Category,
            Summary = alert.Summary,
            TimestampUtc = alert.ObservedUtc,
            IsActive = alert.IsActive
        };
    }

    private static AnalyticsIncidentTimelineItemDto MapTimeline(AnalyticsDriftAlert alert)
    {
        var timestampUtc = alert.IsActive ? alert.ObservedUtc : alert.ResolvedUtc ?? alert.ObservedUtc;
        return new AnalyticsIncidentTimelineItemDto
        {
            EventType = alert.EventType,
            Severity = alert.Severity,
            StatusLabel = alert.IsActive ? "Active" : "Resolved",
            CurrentValue = alert.CurrentValue,
            BaselineValue = alert.BaselineValue,
            DeviationPercent = alert.DeviationPercent,
            MetricUnit = alert.MetricUnit,
            Summary = alert.Summary,
            TimestampUtc = timestampUtc
        };
    }

    private static int CountDistinctUnits(IEnumerable<AnalyticsEvent> events, IReadOnlyCollection<string> eventNames)
    {
        if (events == null)
        {
            return 0;
        }

        return events
            .Where(x => eventNames.Contains(x.EventType, StringComparer.OrdinalIgnoreCase))
            .Select(BuildInteractionUnitKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string BuildInteractionUnitKey(AnalyticsEvent analyticsEvent)
    {
        if (!string.IsNullOrWhiteSpace(analyticsEvent.SessionId))
        {
            return $"sid:{analyticsEvent.SessionId.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(analyticsEvent.VisitorId))
        {
            return $"vid:{analyticsEvent.VisitorId.Trim()}";
        }

        return $"evt:{analyticsEvent.EventId}";
    }

    private static decimal Percent(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return 0m;
        }

        return (decimal)numerator / denominator * 100m;
    }

    private static decimal RoundPercent(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static decimal ComputeDeviationPercent(decimal currentValue, decimal baselineValue)
    {
        if (baselineValue <= 0m)
        {
            return currentValue <= 0m ? 0m : 100m;
        }

        return ((currentValue - baselineValue) / baselineValue) * 100m;
    }

    private static string FormatMetricValue(decimal value, string metricUnit)
    {
        return string.Equals(metricUnit, "percent", StringComparison.OrdinalIgnoreCase)
            ? $"{RoundPercent(value)}%"
            : Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0");
    }

    private static int SeverityRank(string? severity)
    {
        return severity?.Trim().ToLowerInvariant() switch
        {
            "critical" => 4,
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };
    }

    private static string? NormalizeEnv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "production" => "prod",
            "prod" => "prod",
            "development" => "dev",
            "dev" => "dev",
            _ => value.Trim().ToLowerInvariant()
        };
    }

    private static bool ParseBool(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SystemMetricsSnapshot(
        List<AnalyticsIncidentFocusMetricDto> FocusMetrics,
        AnalyticsIncidentAttributionHealthDto AttributionHealth,
        AnalyticsIncidentFunnelHealthDto FunnelHealth,
        List<IncidentObservation> Observations);

    private sealed record IncidentObservation(
        string IncidentKey,
        string MetricKey,
        string EventType,
        string Category,
        string Severity,
        string MetricUnit,
        decimal CurrentValue,
        decimal BaselineValue,
        decimal DeviationPercent,
        string Summary,
        DateTime WindowStartUtc,
        DateTime WindowEndUtc,
        DateTime PreviousWindowStartUtc,
        DateTime PreviousWindowEndUtc);
}

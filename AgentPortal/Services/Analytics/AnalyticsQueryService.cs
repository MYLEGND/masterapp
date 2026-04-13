using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentPortal.Models.Analytics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using System.Linq.Expressions;

namespace AgentPortal.Services.Analytics;

public sealed class AnalyticsQueryService : IAnalyticsQueryService
{
    private const int LowSampleThreshold = 20;
    private const double PageDwellHardCapMs = 30 * 60 * 1000;       // 30 minutes
    private const double SessionDurationHardCapMs = 2 * 60 * 60 * 1000; // 2 hours
    private const double TrimFractionPerSide = 0.025;               // 2.5% each tail
    private const int TrimMinimumSampleSize = 20;
    private readonly string? _envFilter; // normalized ("prod","dev") or null for legacy fallback
    private readonly bool _excludeLocalHosts;
    private static readonly HashSet<string> AllowedEnvironmentsFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        "prod","production","dev","development", ""
    };
    private readonly AgentPortal.Services.Tracking.AgentTrackingResolver _resolver;

    private readonly MasterAppDbContext _db;

    public AnalyticsQueryService(MasterAppDbContext db, IConfiguration config, AgentPortal.Services.Tracking.AgentTrackingResolver resolver)
    {
        _db = db;
        var configuredFilter = NormalizeEnv(config["Analytics:EnvironmentFilter"] ?? config["Analytics__EnvironmentFilter"]);
        var runtimeEnvironment = NormalizeEnv(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        // In production, default to strict production filtering if no explicit filter is configured.
        _envFilter = configuredFilter ?? (runtimeEnvironment == "prod" ? "prod" : null);
        _excludeLocalHosts = ParseBool(config["Analytics:ExcludeLocalHosts"] ?? config["Analytics__ExcludeLocalHosts"]);
        _resolver = resolver;
    }

    private IQueryable<AnalyticsEvent> BaseEvents(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.AnalyticsEvents.AsNoTracking()
            .Where(e => !e.IsInternal)
            .Where(e => e.EventUtc >= range.FromUtc && e.EventUtc <= range.ToUtc)
            .Where(EnvPredicateEvents())
            .Where(HostPredicateEvents())
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

    private IQueryable<WebsiteLead> BaseLeads(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsInternal)
            .Where(l => l.CreatedUtc >= range.FromUtc && l.CreatedUtc <= range.ToUtc)
            .Where(EnvPredicateLeads())
            .Where(HostPredicateLeads())
            .Where(ScopePredicateLeads(scope, scopedAgentIds));

    private IQueryable<AnalyticsEvent> EventsInRange(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.AnalyticsEvents.AsNoTracking()
            .Where(e => !e.IsInternal)
            .Where(e => e.EventUtc >= from && e.EventUtc <= to)
            .Where(EnvPredicateEvents())
            .Where(HostPredicateEvents())
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

    private IQueryable<WebsiteLead> LeadsInRange(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsInternal)
            .Where(l => l.CreatedUtc >= from && l.CreatedUtc <= to)
            .Where(EnvPredicateLeads())
            .Where(HostPredicateLeads())
            .Where(ScopePredicateLeads(scope, scopedAgentIds));

    private bool EnvIncluded(string? env)
    {
        var norm = NormalizeEnv(env);
        if (_envFilter != null)
        {
            // strict mode: only matching envs, exclude null/legacy
            return norm != null && norm == _envFilter;
        }
        // legacy fallback: allow prod/dev + null
        return AllowedEnvironmentsFallback.Contains(env ?? string.Empty) || norm != null;
    }

    private Expression<Func<AnalyticsEvent, bool>> EnvPredicateEvents()
    {
        if (_envFilter == "prod")
            return e => e.Environment == "prod" || e.Environment == "production" || e.Environment == "Prod" || e.Environment == "Production";
        if (_envFilter == "dev")
            return e => e.Environment == "dev" || e.Environment == "development" || e.Environment == "Dev" || e.Environment == "Development";
        // legacy fallback
        return e =>
            e.Environment == null || e.Environment == "" ||
            e.Environment == "prod" || e.Environment == "production" || e.Environment == "Prod" || e.Environment == "Production" ||
            e.Environment == "dev" || e.Environment == "development" || e.Environment == "Dev" || e.Environment == "Development";
    }

    private Expression<Func<WebsiteLead, bool>> EnvPredicateLeads()
    {
        if (_envFilter == "prod")
            return l => l.Environment == "prod" || l.Environment == "production" || l.Environment == "Prod" || l.Environment == "Production";
        if (_envFilter == "dev")
            return l => l.Environment == "dev" || l.Environment == "development" || l.Environment == "Dev" || l.Environment == "Development";
        // legacy fallback
        return l =>
            l.Environment == null || l.Environment == "" ||
            l.Environment == "prod" || l.Environment == "production" || l.Environment == "Prod" || l.Environment == "Production" ||
            l.Environment == "dev" || l.Environment == "development" || l.Environment == "Dev" || l.Environment == "Development";
    }

    private Expression<Func<AnalyticsEvent, bool>> HostPredicateEvents()
    {
        if (!_excludeLocalHosts) return e => true;
        return e =>
            e.Host == null || e.Host == "" ||
            (!e.Host.StartsWith("localhost") &&
             !e.Host.StartsWith("127.0.0.1") &&
             !e.Host.StartsWith("::1") &&
             !e.Host.StartsWith("[::1]"));
    }

    private Expression<Func<WebsiteLead, bool>> HostPredicateLeads()
    {
        if (!_excludeLocalHosts) return l => true;
        return l =>
            l.Host == null || l.Host == "" ||
            (!l.Host.StartsWith("localhost") &&
             !l.Host.StartsWith("127.0.0.1") &&
             !l.Host.StartsWith("::1") &&
             !l.Host.StartsWith("[::1]"));
    }

    private static Expression<Func<AnalyticsEvent, bool>> ScopePredicateEvents(ScopeContext scope, Guid[]? scopedAgentIds)
    {
        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            if (scopedAgentIds != null && scopedAgentIds.Length > 0)
            {
                return e => e.AgentTrackingProfileId.HasValue && scopedAgentIds.Contains(e.AgentTrackingProfileId.Value);
            }
            var agentId = scope.AgentTrackingProfileId.Value;
            return e => e.AgentTrackingProfileId == agentId;
        }
        // founder/global: include all, including null/unattributed
        return e => true;
    }

    private static Expression<Func<WebsiteLead, bool>> ScopePredicateLeads(ScopeContext scope, Guid[]? scopedAgentIds)
    {
        if (scope.ScopeType == ScopeType.Agent && scope.AgentTrackingProfileId.HasValue)
        {
            if (scopedAgentIds != null && scopedAgentIds.Length > 0)
            {
                return l => l.AgentTrackingProfileId.HasValue && scopedAgentIds.Contains(l.AgentTrackingProfileId.Value);
            }
            var agentId = scope.AgentTrackingProfileId.Value;
            return l => l.AgentTrackingProfileId == agentId;
        }
        return l => true;
    }

    /// <summary>
    /// Expands an agent scope to all tracking profile IDs sharing the same UPN.
    /// This prevents analytics drop-offs when duplicate profile rows exist for one user.
    /// </summary>
    private async Task<Guid[]?> ResolveScopedAgentIdsAsync(ScopeContext scope)
    {
        if (scope.ScopeType != ScopeType.Agent || !scope.AgentTrackingProfileId.HasValue)
            return null;

        var selectedId = scope.AgentTrackingProfileId.Value;
        var upn = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.Id == selectedId)
            .Select(p => p.AgentUpn)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(upn))
            return new[] { selectedId };

        var ids = await _db.AgentTrackingProfiles.AsNoTracking()
            .Where(p => p.AgentUpn == upn)
            .Select(p => p.Id)
            .Distinct()
            .ToListAsync();

        if (!ids.Contains(selectedId))
            ids.Add(selectedId);

        return ids.ToArray();
    }

    private static string? NormalizeEnv(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return null;
        var v = env.Trim().ToLowerInvariant();
        if (v.StartsWith("prod")) return "prod";
        if (v.StartsWith("dev")) return "dev";
        return null;
    }

    private static bool ParseBool(string? value) =>
        !string.IsNullOrWhiteSpace(value) && bool.TryParse(value, out var parsed) && parsed;

    private static string BucketLabel(DateTime dt, TimeGrouping g) =>
        g switch
        {
            TimeGrouping.Day => dt.ToString("yyyy-MM-dd"),
            TimeGrouping.Week => $"{dt:yyyy}-W{ISOWeek.GetWeekOfYear(dt):00}",
            TimeGrouping.Month => dt.ToString("yyyy-MM"),
            TimeGrouping.Year => dt.ToString("yyyy"),
            _ => dt.ToString("yyyy-MM-dd")
        };

    private static DateTime BucketStart(DateTime dt, TimeGrouping g)
    {
        return g switch
        {
            TimeGrouping.Day => dt.Date,
            TimeGrouping.Week => dt.Date.AddDays(-(int)dt.Date.DayOfWeek + (int)DayOfWeek.Monday),
            TimeGrouping.Month => new DateTime(dt.Year, dt.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeGrouping.Year => new DateTime(dt.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => dt.Date
        };
    }

    private static List<TrendPointDto> TrendFromEvents(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, DateTime> selector, TimeGrouping grouping)
    {
        return events
            .GroupBy(e => BucketStart(selector(e), grouping))
            .OrderBy(g => g.Key)
            .Select(g => new TrendPointDto { Label = BucketLabel(g.Key, grouping), Value = g.Count() })
            .ToList();
    }

    private static List<TrendPointDto> TrendDistinct(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, string?> keySelector, TimeGrouping grouping, Func<AnalyticsEvent, DateTime> timeSelector)
    {
        return events
            .Where(e => !string.IsNullOrWhiteSpace(keySelector(e)))
            .GroupBy(e => new { Bucket = BucketStart(timeSelector(e), grouping), Key = keySelector(e)! })
            .GroupBy(g => g.Key.Bucket)
            .OrderBy(g => g.Key)
            .Select(g => new TrendPointDto { Label = BucketLabel(g.Key, grouping), Value = g.SelectMany(x => x).Select(x => keySelector(x)).Distinct().Count() })
            .ToList();
    }

    private static bool IsQuoteFormKey(string? formKey) =>
        !string.IsNullOrWhiteSpace(formKey) &&
        formKey.Contains("quote_", StringComparison.OrdinalIgnoreCase);

    private static bool IsQuoteSubmitSuccess(AnalyticsEvent e) =>
        e.EventType == "form_submit" &&
        IsQuoteFormKey(e.FormKey) &&
        string.Equals(e.SubmitOutcome, "success", StringComparison.OrdinalIgnoreCase);

    private static string BuildInteractionUnitKey(AnalyticsEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.SessionId))
            return $"sid:{e.SessionId}";
        if (!string.IsNullOrWhiteSpace(e.VisitorId))
            return $"vid:{e.VisitorId}";
        if (e.ClientEventId.HasValue)
            return $"cid:{e.ClientEventId.Value:D}";
        return $"eid:{e.EventId:D}";
    }

    private static int CountDistinctUnits(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, bool> predicate)
    {
        return events
            .Where(predicate)
            .Select(BuildInteractionUnitKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static int CountQuoteIntentStarts(IEnumerable<AnalyticsEvent> events)
    {
        return CountDistinctUnits(events, e =>
            e.EventType == "quote_click" ||
            (e.EventType == "form_start" && IsQuoteFormKey(e.FormKey)));
    }

    private sealed record EventAttributionSnapshot(
        string? UtmSource,
        string? UtmMedium,
        string? UtmCampaign,
        string? Fbclid);

    private sealed record AttributedEventRow(
        AnalyticsEvent Event,
        EventAttributionSnapshot Attribution,
        TrafficType TrafficType);

    private static string? NormalizeAttributionToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static EventAttributionSnapshot SnapshotFromEvent(AnalyticsEvent e) =>
        new(
            NormalizeAttributionToken(e.UtmSource),
            NormalizeAttributionToken(e.UtmMedium),
            NormalizeAttributionToken(e.UtmCampaign),
            NormalizeAttributionToken(e.Fbclid));

    private static bool HasAttributionSignal(EventAttributionSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.UtmSource) ||
        !string.IsNullOrWhiteSpace(snapshot.UtmMedium) ||
        !string.IsNullOrWhiteSpace(snapshot.UtmCampaign) ||
        !string.IsNullOrWhiteSpace(snapshot.Fbclid);

    private static TrafficType Classify(EventAttributionSnapshot snapshot) =>
        TrafficAttribution.Classify(snapshot.UtmSource, snapshot.UtmMedium, snapshot.UtmCampaign, snapshot.Fbclid);

    private static Dictionary<string, EventAttributionSnapshot> BuildSessionAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected =
                group.Where(e => e.EventType == "page_view")
                    .OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal)
                ?? group.OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal);

            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }

    private static Dictionary<string, EventAttributionSnapshot> BuildVisitorAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
            .GroupBy(e => e.VisitorId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected =
                group.Where(e => e.EventType == "page_view")
                    .OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal)
                ?? group.OrderBy(e => e.EventUtc)
                    .Select(SnapshotFromEvent)
                    .FirstOrDefault(HasAttributionSignal);

            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }

    private static EventAttributionSnapshot ResolveAttribution(
        AnalyticsEvent e,
        IReadOnlyDictionary<string, EventAttributionSnapshot> sessionMap,
        IReadOnlyDictionary<string, EventAttributionSnapshot> visitorMap)
    {
        var direct = SnapshotFromEvent(e);
        if (HasAttributionSignal(direct))
            return direct;

        if (!string.IsNullOrWhiteSpace(e.SessionId) &&
            sessionMap.TryGetValue(e.SessionId!, out var sessionAttribution) &&
            HasAttributionSignal(sessionAttribution))
        {
            return sessionAttribution;
        }

        if (!string.IsNullOrWhiteSpace(e.VisitorId) &&
            visitorMap.TryGetValue(e.VisitorId!, out var visitorAttribution) &&
            HasAttributionSignal(visitorAttribution))
        {
            return visitorAttribution;
        }

        return direct;
    }

    private static List<AttributedEventRow> BuildAttributedEventRows(IEnumerable<AnalyticsEvent> events)
    {
        var list = events.ToList();
        if (list.Count == 0)
            return new List<AttributedEventRow>();

        var sessionMap = BuildSessionAttributionMap(list);
        var visitorMap = BuildVisitorAttributionMap(list);

        return list
            .Select(e =>
            {
                var attribution = ResolveAttribution(e, sessionMap, visitorMap);
                return new AttributedEventRow(e, attribution, Classify(attribution));
            })
            .ToList();
    }

    private static List<AttributedEventRow> FilterAttributedRowsByTraffic(List<AttributedEventRow> rows, TrafficType trafficType)
    {
        if (trafficType == TrafficType.All)
            return rows;
        return rows.Where(r => TrafficAttribution.MatchesFilter(r.TrafficType, trafficType)).ToList();
    }

    private static List<AttributedEventRow> BuildSessionAttributionRows(List<AttributedEventRow> rows)
    {
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Event.SessionId))
            .GroupBy(r => r.Event.SessionId!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
                g.Where(r => r.Event.EventType == "page_view" && HasAttributionSignal(r.Attribution))
                    .OrderBy(r => r.Event.EventUtc)
                    .FirstOrDefault()
                ?? g.Where(r => HasAttributionSignal(r.Attribution))
                    .OrderBy(r => r.Event.EventUtc)
                    .FirstOrDefault())
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    private static decimal ClampPercent(decimal value) => Math.Min(100m, Math.Max(0m, value));

    public async Task<SummaryKpiDto> GetSummaryAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();

        // previous period for deltas
        var span = range.ToUtc - range.FromUtc;
        var prevFrom = range.FromUtc - span;
        var prevTo = range.ToUtc - span;
        var prevEvents = await EventsInRange(prevFrom, prevTo, scope, scopedAgentIds).ToListAsync();
        var prevLeads = await LeadsInRange(prevFrom, prevTo, scope, scopedAgentIds).ToListAsync();

        int pageViews = events.Count(e => e.EventType == "page_view");
        int sessions = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId)).Select(e => e.SessionId!).Distinct().Count();
        int visitors = events.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId!).Distinct().Count();
        int verifiedLeads = leads.Count;
        int quoteFormStarts = CountDistinctUnits(events, e => e.EventType == "form_start" && IsQuoteFormKey(e.FormKey));
        int quoteFormSubmits = CountDistinctUnits(events, IsQuoteSubmitSuccess);
        int quoteStarts = CountQuoteIntentStarts(events);

        int prevPageViews = prevEvents.Count(e => e.EventType == "page_view");
        int prevSessions = prevEvents.Where(e => !string.IsNullOrWhiteSpace(e.SessionId)).Select(e => e.SessionId!).Distinct().Count();
        int prevVisitors = prevEvents.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId!).Distinct().Count();
        int prevVerifiedLeads = prevLeads.Count;

        var topPage = events.Where(e => e.EventType == "page_view")
            .GroupBy(e => e.PageKey ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var topCta = events.Where(e => e.EventType == "cta_click")
            .GroupBy(e => e.ElementKey ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var attributedRows = BuildAttributedEventRows(events);
        var sessionAttributionRows = BuildSessionAttributionRows(attributedRows);
        var topAttributionRows = sessionAttributionRows.Count > 0 ? sessionAttributionRows : attributedRows;

        var topSource = topAttributionRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Attribution.UtmSource))
            .GroupBy(r => r.Attribution.UtmSource!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var topCampaign = topAttributionRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Attribution.UtmCampaign))
            .GroupBy(r => r.Attribution.UtmCampaign!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        decimal sessionConversionRate = sessions > 0 ? Math.Round((decimal)verifiedLeads / sessions * 100, 2) : 0;
        bool sessionLow = sessions > 0 && sessions < LowSampleThreshold;

        int intentDenom = quoteStarts;
        string intentLabel = "Quote Submits / Quote Starts";
        bool intentAvailable = intentDenom > 0 && quoteFormSubmits > 0;
        decimal intentConversionRate = intentAvailable
            ? Math.Min(100, Math.Round((decimal)quoteFormSubmits / intentDenom * 100, 2))
            : 0;
        bool intentLow = intentAvailable && intentDenom < LowSampleThreshold;
        var envLabel = _envFilter switch
        {
            "prod" => "Environment: Production",
            "dev" => "Environment: Development",
            _ => "Environment: Mixed/Legacy"
        };

        return new SummaryKpiDto
        {
            PageViews = pageViews,
            Sessions = sessions,
            UniqueVisitors = visitors,
            VerifiedLeads = verifiedLeads,
            EnvironmentLabel = envLabel,
            SessionConversionRate = sessionConversionRate,
            SessionLowSample = sessionLow,
            IntentConversionRate = intentConversionRate,
            IntentAvailable = intentAvailable,
            IntentDenominatorLabel = intentLabel,
            IntentLowSample = intentLow,
            PrevPageViews = prevPageViews,
            PrevSessions = prevSessions,
            PrevUniqueVisitors = prevVisitors,
            PrevVerifiedLeads = prevVerifiedLeads,
            PageViewTrend = TrendFromEvents(events.Where(e => e.EventType == "page_view"), e => e.EventUtc, range.Grouping),
            TopPage = topPage,
            TopCta = topCta,
            TopSource = topSource,
            TopCampaign = topCampaign,
            RangeLabel = range.Label
        };
    }

    public async Task<TrafficOverviewDto> GetTrafficAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var attributedRows = BuildAttributedEventRows(allEvents);
        attributedRows = FilterAttributedRowsByTraffic(attributedRows, trafficType);
        var events = attributedRows.Select(r => r.Event).ToList();
        var sessionAttributionRows = BuildSessionAttributionRows(attributedRows);
        var topAttributionRows = sessionAttributionRows.Count > 0 ? sessionAttributionRows : attributedRows;

        var traffic = new TrafficOverviewDto
        {
            PageViewTrend = TrendFromEvents(events.Where(e => e.EventType == "page_view"), e => e.EventUtc, range.Grouping),
            SessionTrend = TrendDistinct(events, e => e.SessionId, range.Grouping, e => e.EventUtc),
            VisitorTrend = TrendDistinct(events, e => e.VisitorId, range.Grouping, e => e.EventUtc),
            TopPages = events.Where(e => e.EventType == "page_view")
                .GroupBy(e => e.PageKey ?? "unknown")
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            TopCtas = events.Where(e => e.EventType == "cta_click")
                .GroupBy(e => e.ElementKey ?? "unknown")
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            RangeLabel = range.Label,
            TopSources = topAttributionRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Attribution.UtmSource))
                .GroupBy(r => r.Attribution.UtmSource!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            TopCampaigns = topAttributionRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Attribution.UtmCampaign))
                .GroupBy(r => r.Attribution.UtmCampaign!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList()
        };

        // Entry pages: first page_view per session within range
        var firstPerSession = events
            .Where(e => e.EventType == "page_view" && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .Select(g => g.OrderBy(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
            .ToList();
        traffic.EntryPages = firstPerSession;

        traffic.RecentActivity = events
            .OrderByDescending(e => e.EventUtc)
            .Take(25)
            .Select(e => new ActivityItemDto
            {
                EventUtc = e.EventUtc,
                EventType = e.EventType,
                PageKey = e.PageKey,
                ElementKey = e.ElementKey
            }).ToList();

        return traffic;
    }

    public async Task<PagePerformanceDto> GetPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        if (trafficType != TrafficType.All)
        {
            events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(events), trafficType)
                .Select(r => r.Event)
                .ToList();
            leads = leads.Where(l => TrafficAttribution.MatchesFilter(
                TrafficAttribution.Classify(l.UtmSource, l.UtmMedium, l.UtmCampaign, l.Fbclid), trafficType)).ToList();
        }

        var pageViews = events.Where(e => e.EventType == "page_view")
            .GroupBy(e => e.PageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var ctas = events.Where(e => e.EventType == "cta_click")
            .GroupBy(e => e.PageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var leadsByPage = leads
            .GroupBy(l => l.SourcePageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var pages = pageViews.Keys.Union(ctas.Keys).Union(leadsByPage.Keys).Distinct();

        var rows = pages.Select(p =>
        {
            var views = pageViews.TryGetValue(p, out var pv) ? pv : 0;
            var clicks = ctas.TryGetValue(p, out var ct) ? ct : 0;
            var l = leadsByPage.TryGetValue(p, out var ld) ? ld : 0;
            var conv = views > 0 ? Math.Round((decimal)l / views * 100, 2) : 0;
            return new PagePerformanceRow
            {
                PageKey = p,
                Views = views,
                CtaClicks = clicks,
                Leads = l,
                ConversionRate = conv
            };
        }).OrderByDescending(r => r.Views).ToList();

        return new PagePerformanceDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<CtaPerformanceDto> GetCtaPerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "cta_click")
            .ToListAsync();
        if (trafficType != TrafficType.All)
        {
            events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(events), trafficType)
                .Select(r => r.Event)
                .ToList();
        }

        var rows = events
            .GroupBy(e => new { Page = e.PageKey ?? "unknown", Cta = e.ElementKey ?? "unknown" })
            .OrderByDescending(g => g.Count())
            .Select(g => new CtaPerformanceRow
            {
                PageKey = g.Key.Page,
                ElementKey = g.Key.Cta,
                Clicks = g.Count()
            })
            .ToList();

        return new CtaPerformanceDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<QuoteFunnelDto> GetQuoteFunnelAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var attributedRows = BuildAttributedEventRows(allEvents);
        attributedRows = FilterAttributedRowsByTraffic(attributedRows, trafficType);
        var events = attributedRows.Select(r => r.Event).ToList();

        int starts = CountQuoteIntentStarts(events);
        int formStarts = CountDistinctUnits(events, e => e.EventType == "form_start" && IsQuoteFormKey(e.FormKey));
        int formSubmits = CountDistinctUnits(events, IsQuoteSubmitSuccess);

        var byType = events
            .Where(e => e.EventType == "quote_click" && !string.IsNullOrWhiteSpace(e.QuoteType))
            .GroupBy(e => e.QuoteType!)
            .Select(g => new KeyCountDto
            {
                Key = g.Key,
                Count = g.Select(BuildInteractionUnitKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        return new QuoteFunnelDto
        {
            QuoteStarts = starts,
            QuoteFormStarts = formStarts,
            QuoteFormSubmits = formSubmits,
            ByQuoteType = byType,
            RangeLabel = range.Label,
            TrafficType = trafficType,
            DropOffStartsToFormStarts = starts > 0
                ? Math.Round(ClampPercent((decimal)(starts - formStarts) / starts * 100), 2)
                : null,
            DropOffFormStartsToSubmits = formStarts > 0
                ? Math.Round(ClampPercent((decimal)(formStarts - formSubmits) / formStarts * 100), 2)
                : null
        };
    }

    public async Task<ConversionCenterDto> GetConversionsAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var conversionEvents = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "lead_form_submit_success" ||
                        (e.EventType == "form_submit" && (e.SubmitOutcome == null || e.SubmitOutcome == "success")))
            .ToListAsync();

        var filteredConversionEvents = FilterAttributedRowsByTraffic(
                BuildAttributedEventRows(conversionEvents),
                trafficType)
            .Select(r => r.Event)
            .ToList();

        var totalConversions = filteredConversionEvents.Count;

        var recentEvents = filteredConversionEvents
            .OrderByDescending(e => e.EventUtc)
            .Take(100)
            .ToList();

        var dto = new ConversionCenterDto
        {
            TotalConversions = totalConversions,
            Recent = recentEvents.Select(e => new ConversionRow
            {
                EventType = e.EventType,
                PageKey = e.PageKey,
                SourceCta = e.ElementKey,
                EventUtc = e.EventUtc
            }).ToList(),
            RangeLabel = range.Label
        };
        return dto;
    }

    public async Task<LeadSnapshotDto> GetLeadsAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All, int take = 200)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var leadsQuery = BaseLeads(range, scope, scopedAgentIds);
        if (trafficType != TrafficType.All)
        {
            leadsQuery = leadsQuery.Where(l => TrafficAttribution.MatchesFilter(
                TrafficAttribution.Classify(l.UtmSource, l.UtmMedium, l.UtmCampaign, l.Fbclid), trafficType));
        }
        var leads = await leadsQuery.OrderByDescending(l => l.CreatedUtc).Take(take).ToListAsync();
        var total = await leadsQuery.CountAsync();

        var dto = new LeadSnapshotDto
        {
            Total = total,
            RangeLabel = range.Label,
            TrafficType = trafficType,
            Leads = leads.Select(l =>
            {
                var classified = TrafficAttribution.Classify(l.UtmSource, l.UtmMedium, l.UtmCampaign, l.Fbclid);
                return new LeadSnapshotRow
                {
                    CreatedUtc  = l.CreatedUtc,
                    Name        = $"{l.FirstName} {l.LastName}".Trim(),
                    Email       = l.Email,
                    Phone       = l.Phone,
                    Interest    = l.InterestType,
                    Source      = $"{l.SourcePageKey}/{l.SourceCtaKey}".Trim('/'),
                    UtmSource   = l.UtmSource,
                    UtmMedium   = l.UtmMedium,
                    UtmCampaign = l.UtmCampaign,
                    Fbclid      = l.Fbclid,
                    SourcePage  = l.SourcePageKey,
                    TrafficType = classified,
                    Attribution = new LeadAttributionDto
                    {
                        IsPaid    = classified == TrafficType.PaidAds,
                        IsNonPaid = classified != TrafficType.PaidAds && classified != TrafficType.Unknown,
                        TrafficType = classified
                    }
                };
            }).ToList()
        };
        return dto;
    }

    public async Task<AgentPerformanceDto> GetAgentPerformanceAsync(TimeRangeRequest range, ScopeContext scope, AnalyticsQueryOptions? options = null)
    {
        // Only allow founder/global rollup to compare agents
        if (scope.ScopeType != ScopeType.Global)
        {
            return new AgentPerformanceDto { RangeLabel = range.Label };
        }

        options ??= new AnalyticsQueryOptions();
        var order = options.OrderBy?.ToLowerInvariant() ?? "leads";
        var take = options.Take.HasValue && options.Take.Value > 0 ? options.Take.Value : 50;
        var skip = options.Skip.HasValue && options.Skip.Value >= 0 ? options.Skip.Value : 0;

        var events = await BaseEvents(range, ScopeContext.Global).ToListAsync();
        var leads = await BaseLeads(range, ScopeContext.Global).ToListAsync();

        var sessionsByAgent = events
            .Where(e => e.AgentTrackingProfileId != null && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.AgentTrackingProfileId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.SessionId!).Distinct().Count());

        var intentsByAgent = events
            .Where(e => e.AgentTrackingProfileId != null && e.EventType == "form_start" && e.FormKey != null && e.FormKey.Contains("quote_"))
            .GroupBy(e => e.AgentTrackingProfileId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var leadsByAgent = leads
            .Where(l => l.AgentTrackingProfileId != null)
            .GroupBy(l => l.AgentTrackingProfileId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var conversionsByAgent = events
            .Where(e => e.AgentTrackingProfileId != null &&
                        (e.EventType == "lead_form_submit_success" ||
                         (e.EventType == "form_submit" && (e.SubmitOutcome == null || e.SubmitOutcome == "success"))))
            .GroupBy(e => e.AgentTrackingProfileId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var topSourceByAgent = events
            .Where(e => e.AgentTrackingProfileId != null && !string.IsNullOrWhiteSpace(e.UtmSource))
            .GroupBy(e => new { e.AgentTrackingProfileId, e.UtmSource })
            .Select(g => new { AgentId = g.Key.AgentTrackingProfileId!.Value, Source = g.Key.UtmSource!, Count = g.Count() })
            .GroupBy(x => x.AgentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Count).First().Source);

        var profiles = await _db.AgentTrackingProfiles.AsNoTracking().ToListAsync();

        var rows = new List<AgentPerformanceRow>();
        foreach (var profile in profiles)
        {
            var id = profile.Id;
            var leadsCount = leadsByAgent.TryGetValue(id, out var lc) ? lc : 0;
            var convCount = conversionsByAgent.TryGetValue(id, out var cc) ? cc : 0;
            var sessions = sessionsByAgent.TryGetValue(id, out var sc) ? sc : 0;
            var intents = intentsByAgent.TryGetValue(id, out var ic) ? ic : 0;

            var sessionConv = sessions > 0 ? Math.Round((decimal)convCount / sessions * 100, 2) : 0;
            var intentConv = intents > 0 ? Math.Round((decimal)convCount / intents * 100, 2) : 0;
            var lowSample = (sessions > 0 && sessions < LowSampleThreshold) || (intents > 0 && intents < LowSampleThreshold);
            rows.Add(new AgentPerformanceRow
            {
                AgentTrackingProfileId = id,
                AgentName = profile.DisplayName ?? profile.AgentUpn ?? profile.Slug,
                Slug = profile.Slug,
                Leads = leadsCount,
                Conversions = convCount,
                Sessions = sessions,
                SessionConversionRate = sessionConv,
                IntentConversionRate = intentConv,
                TopSource = topSourceByAgent.TryGetValue(id, out var ts) ? ts : null,
                LowSample = lowSample
            });
        }

        rows = rows.OrderByDescending(r => r.Leads).ToList();

        rows = order switch
        {
            "conversions" => (options.Desc ? rows.OrderByDescending(r => r.Conversions) : rows.OrderBy(r => r.Conversions)).ToList(),
            "session" => (options.Desc ? rows.OrderByDescending(r => r.SessionConversionRate) : rows.OrderBy(r => r.SessionConversionRate)).ToList(),
            "intent" => (options.Desc ? rows.OrderByDescending(r => r.IntentConversionRate) : rows.OrderBy(r => r.IntentConversionRate)).ToList(),
            _ => (options.Desc ? rows.OrderByDescending(r => r.Leads) : rows.OrderBy(r => r.Leads)).ToList()
        };

        rows = rows.Skip(skip).Take(take).ToList();

        return new AgentPerformanceDto
        {
            Rows = rows,
            RangeLabel = range.Label
        };
    }

    // ── Behavior Intelligence Engine ──────────────────────────────────────────

    public async Task<EngagementSummaryDto> GetEngagementSummaryAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        if (trafficType != TrafficType.All)
        {
            events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(events), trafficType)
                .Select(r => r.Event)
                .ToList();
        }

        var pvs = events.Where(e => e.EventType == "page_view").ToList();
        // page_exit events carry the real per-page dwell (elapsed ms at departure).
        // page_view.DwellMilliseconds is always 0 (captured at load time), so we use page_exit for all dwell metrics.
        var exitEvents = events.Where(e => e.EventType == "page_exit" && e.DwellMilliseconds.HasValue).ToList();
        var dwellSamples = exitEvents.Select(e => (double)e.DwellMilliseconds!.Value).ToList();
        var avgTimeOnPage = RobustAverage(dwellSamples, PageDwellHardCapMs);
        var medianTimeOnPage = Median(CapValues(dwellSamples, PageDwellHardCapMs));

        // Session duration: elapsed time from first to last event in each session.
        // Using Max(EventUtc) - Min(EventUtc) is correct for multi-page sessions and works
        // immediately from page_view events — no dependency on page_exit being present.
        // Sum(DwellMilliseconds) was wrong: it summed per-page dwell across pages, which
        // double-counts time and returns 0 when page_exit has not been emitted yet.
        var sessionDurationsRaw = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .Where(g => g.Count() > 1) // need at least 2 events for a meaningful duration
            .Select(g => (g.Max(x => x.EventUtc) - g.Min(x => x.EventUtc)).TotalMilliseconds)
            .Where(ms => ms > 0)
            .ToList();
        var avgSessionDuration = RobustAverage(sessionDurationsRaw, SessionDurationHardCapMs);
        var medianSessionDuration = Median(CapValues(sessionDurationsRaw, SessionDurationHardCapMs));

        // Session map: distinct sessions and their max accumulated engaged ms.
        var sessionEngagedMap = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .ToDictionary(g => g.Key, g => g.Max(x => x.EngagedMilliseconds ?? 0));

        // Quick exits: sessions whose final page_exit had dwell < 10 s or was a bounce candidate.
        // Use page_exit only so Summary and Exit Analysis tabs use the same quick-exit methodology.
        var lastExitPerSession = exitEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .Select(g => g.OrderByDescending(x => x.EventUtc).First())
            .ToList();
        int quickExits = lastExitPerSession.Count(e =>
            e.IsBounceCandidate == true ||
            (e.DwellMilliseconds.HasValue && e.DwellMilliseconds.Value < 10_000));
        // Return null (not 0) when no page_exit data exists at all.
        // A 0% quick exit rate is only meaningful when exit beacons are being received;
        // showing 0.00% without data is misleading — the UI renders null as "—".
        decimal? quickExitRate = lastExitPerSession.Count > 0
            ? Math.Round((decimal)quickExits / lastExitPerSession.Count * 100, 2)
            : (decimal?)null;

        // Engaged sessions: session has any page_engaged_30s/page_engaged_60s checkpoint,
        // OR EngagedMilliseconds >= 30 s as a fallback for sessions where the event fired but
        // the engagement-event record is absent (e.g. beacon loss on slow connections).
        // Threshold is 30s to match the EngagementSummaryDto documentation and the UI label.
        var sessionsWithEngagementEvent = new HashSet<string>(
            events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId) &&
                               (e.EventType == "page_engaged_30s" ||
                                e.EventType == "page_engaged_60s"))
                  .Select(e => e.SessionId!));
        int engagedSessions = sessionEngagedMap.Count(kv =>
            sessionsWithEngagementEvent.Contains(kv.Key) || kv.Value >= 30_000);
        // Return null when neither engagement events nor EngagedMilliseconds data exists.
        // This distinguishes "genuinely 0% engaged" from "instrumentation not yet producing data".
        bool hasEngagementData = sessionsWithEngagementEvent.Count > 0 ||
                                 sessionEngagedMap.Values.Any(v => v > 0);
        decimal? engagedSessionRate = (hasEngagementData && sessionEngagedMap.Count > 0)
            ? Math.Round((decimal)engagedSessions / sessionEngagedMap.Count * 100, 2)
            : (decimal?)null;

        // Top exit page: last page_exit per session (which page did visitors actually leave from),
        // falling back to last page_view per session when no exit beacons exist.
        // Using last-per-session prevents high-traffic mid-session pages from dominating.
        var topExitPage = (exitEvents.Count > 0
            ? exitEvents
                .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
                .GroupBy(e => e.SessionId!)
                .Select(g => g.OrderByDescending(x => x.EventUtc).First())
                .GroupBy(e => e.PageKey ?? "unknown").OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            : null)
            ?? pvs.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
                .GroupBy(e => e.SessionId!).Select(g => g.OrderByDescending(x => x.EventUtc).First())
                .GroupBy(e => e.PageKey ?? "unknown").OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        // Top long-dwell page: from page_exit.DwellMilliseconds (real elapsed time per page).
        var topLongDwellPage = exitEvents
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g => new
            {
                Page = g.Key,
                Avg = RobustAverage(g.Select(x => (double)x.DwellMilliseconds!.Value).ToList(), PageDwellHardCapMs)
            })
            .OrderByDescending(x => x.Avg).Select(x => x.Page).FirstOrDefault();

        // Highest scroll completion: from page_exit.ScrollPercent (scroll at moment of exit).
        var topScrollPage = events
            .Where(e => e.EventType == "page_exit" && e.ScrollPercent.HasValue)
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g => new { Page = g.Key, Avg = g.Average(x => (double)x.ScrollPercent!.Value) })
            .OrderByDescending(x => x.Avg).Select(x => x.Page).FirstOrDefault();

        return new EngagementSummaryDto
        {
            AvgSessionDurationMs = Math.Round(avgSessionDuration, 0),
            MedianSessionDurationMs = Math.Round(medianSessionDuration, 0),
            AvgTimeOnPageMs = Math.Round(avgTimeOnPage, 0),
            MedianTimeOnPageMs = Math.Round(medianTimeOnPage, 0),
            QuickExitRate = quickExitRate,
            EngagedSessionRate = engagedSessionRate,
            TopExitPage = topExitPage,
            TopLongDwellPage = topLongDwellPage,
            HighestScrollCompletionPage = topScrollPage,
            TotalPageViews = pvs.Count,
            TotalSessions = sessionEngagedMap.Count,
            RangeLabel = range.Label
        };
    }

    public async Task<PageEngagementDto> GetPageEngagementAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();

        var pvList = events.Where(e => e.EventType == "page_view").ToList();
        var pageKeys = pvList.Select(e => e.PageKey ?? "unknown").Distinct().ToList();

        var ctaByPage = events.Where(e => e.EventType == "cta_click")
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var fsByPage = events.Where(e => e.EventType == "form_start")
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var leadsByPage = leads.GroupBy(l => l.SourcePageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        // Use page_exit for exit counts (IsExitPage is reliably set on page_exit, not page_view).
        var exitByPage = events.Where(e => e.EventType == "page_exit")
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var s50ByPage = events.Where(e => e.EventType == "scroll_depth_50")
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var s90ByPage = events.Where(e => e.EventType == "scroll_depth_90")
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        // page_exit.DwellMilliseconds = real elapsed time at departure (page_view is always 0 at load).
        var exitDwellByPage = events.Where(e => e.EventType == "page_exit" && e.DwellMilliseconds.HasValue)
            .GroupBy(e => e.PageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Select(x => (double)x.DwellMilliseconds!.Value).ToList());

        var rows = pageKeys.Select(p =>
        {
            var pv = pvList.Where(e => (e.PageKey ?? "unknown") == p).ToList();
            var views = pv.Count;
            var uv = pv.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId!).Distinct().Count();
            var dl = exitDwellByPage.TryGetValue(p, out var exitDwells) ? exitDwells : new List<double>();
            return new PageEngagementRow
            {
                PageKey = p, Views = views, UniqueVisitors = uv,
                AvgTimeMs = Math.Round(RobustAverage(dl, PageDwellHardCapMs), 0),
                MedianTimeMs = Math.Round(Median(CapValues(dl, PageDwellHardCapMs)), 0),
                Scroll50Rate = views > 0 ? Math.Round((decimal)(s50ByPage.TryGetValue(p, out var a) ? a : 0) / views * 100, 2) : 0,
                Scroll90Rate = views > 0 ? Math.Round((decimal)(s90ByPage.TryGetValue(p, out var b) ? b : 0) / views * 100, 2) : 0,
                ExitRate = views > 0 ? Math.Round((decimal)(exitByPage.TryGetValue(p, out var c) ? c : 0) / views * 100, 2) : 0,
                CtaClickRate = views > 0 ? Math.Round((decimal)(ctaByPage.TryGetValue(p, out var d) ? d : 0) / views * 100, 2) : 0,
                FormStartRate = views > 0 ? Math.Round((decimal)(fsByPage.TryGetValue(p, out var e2) ? e2 : 0) / views * 100, 2) : 0,
                LeadRate = views > 0 ? Math.Round((decimal)(leadsByPage.TryGetValue(p, out var f) ? f : 0) / views * 100, 2) : 0,
                LastActivity = pv.Max(e => (DateTime?)e.EventUtc)
            };
        }).OrderByDescending(r => r.Views).ToList();
        return new PageEngagementDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<TimeOnPageDto> GetTimeOnPageAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        // page_exit carries the real elapsed dwell per page visit; page_view.DwellMilliseconds is always 0 at load.
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_exit" && e.DwellMilliseconds != null).ToListAsync();
        // Views count = distinct page_view events per page (for denominator context).
        var pvCounts = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_view")
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g => new { PageKey = g.Key, Count = g.Count() })
            .ToListAsync();
        var pvCountMap = pvCounts.ToDictionary(x => x.PageKey, x => x.Count);
        var totalPageViews = pvCounts.Sum(x => x.Count);
        var totalTimingSamples = events.Count;
        var byPage = events.GroupBy(e => e.PageKey ?? "unknown").Select(g =>
        {
            var d = g.Select(x => (double)x.DwellMilliseconds!.Value).ToList();
            var viewCount = pvCountMap.TryGetValue(g.Key, out var vc) ? vc : g.Count();
            return new DwellPageRow
            {
                PageKey = g.Key,
                Views = viewCount,
                TimingSamples = d.Count,
                AvgDwellMs = Math.Round(RobustAverage(d, PageDwellHardCapMs), 0),
                MedianDwellMs = Math.Round(Median(CapValues(d, PageDwellHardCapMs)), 0)
            };
        }).ToList();
        return new TimeOnPageDto
        {
            LongestAvgDwell = byPage.OrderByDescending(p => p.AvgDwellMs).Take(10).ToList(),
            LongestMedianDwell = byPage.OrderByDescending(p => p.MedianDwellMs).Take(10).ToList(),
            ShortVisitProblemPages = byPage.Where(p => p.Views >= 5 && p.AvgDwellMs < 8_000).OrderBy(p => p.AvgDwellMs).Take(10).ToList(),
            TotalPageViews = totalPageViews,
            TotalTimingSamples = totalTimingSamples,
            RangeLabel = range.Label
        };
    }

    public async Task<ExitAnalysisDto> GetExitAnalysisAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        // Load page_view for view counts and page_exit for exit/dwell signals.
        var pageViewEvents = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_view").ToListAsync();
        var pageExitEvents = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_exit").ToListAsync();
        var viewsByPage = pageViewEvents.GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var lastExitPerSession = pageExitEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .Select(g => g.OrderByDescending(x => x.EventUtc).First())
            .ToList();
        // Use last page_exit per session as the exit signal — consistent across all pages.
        // Previously mixed explicit-per-event and inferred-per-session on a per-page basis,
        // making exit rates incomparable row-to-row. Now all rows use the same methodology.
        var lastExitPerSessionByPage = lastExitPerSession
            .GroupBy(e => e.PageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());
        var topExitRows = viewsByPage.Keys.Select(p =>
        {
            var ex = lastExitPerSessionByPage.TryGetValue(p, out var ee) ? ee : 0;
            var v = viewsByPage[p];
            return new ExitPageRow { PageKey = p, Views = v, Exits = ex, ExitRate = v > 0 ? Math.Round((decimal)ex / v * 100, 2) : 0 };
        }).OrderByDescending(r => r.Exits).Take(15).ToList();
        // Quick exits use the same population as TopExitPages: final page_exit per session.
        var quickExits = lastExitPerSession
            .Where(e => e.IsBounceCandidate == true || (e.DwellMilliseconds.HasValue && e.DwellMilliseconds.Value < 10_000))
            .GroupBy(e => e.PageKey ?? "unknown").Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
            .OrderByDescending(k => k.Count).Take(10).ToList();
        return new ExitAnalysisDto { TopExitPages = topExitRows, QuickExitPages = quickExits, RangeLabel = range.Label };
    }

    public async Task<ScrollAnalysisDto> GetScrollAnalysisAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var scrollTypes = new[] { "page_view", "scroll_depth_25", "scroll_depth_50", "scroll_depth_75", "scroll_depth_90", "scroll_depth_100" };
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => scrollTypes.Contains(e.EventType)).ToListAsync();
        var pvByPage = events.Where(e => e.EventType == "page_view")
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        int SC(string t, string p) => events.Count(e => e.EventType == t && (e.PageKey ?? "unknown") == p);
        var rows = pvByPage.Keys.Select(p =>
        {
            var v = pvByPage[p];
            return new ScrollPageRow
            {
                PageKey = p, Views = v,
                Scroll25Rate = v > 0 ? Math.Round((decimal)SC("scroll_depth_25", p) / v * 100, 2) : 0,
                Scroll50Rate = v > 0 ? Math.Round((decimal)SC("scroll_depth_50", p) / v * 100, 2) : 0,
                Scroll75Rate = v > 0 ? Math.Round((decimal)SC("scroll_depth_75", p) / v * 100, 2) : 0,
                Scroll90Rate = v > 0 ? Math.Round((decimal)SC("scroll_depth_90", p) / v * 100, 2) : 0,
                Scroll100Rate = v > 0 ? Math.Round((decimal)SC("scroll_depth_100", p) / v * 100, 2) : 0
            };
        }).OrderByDescending(r => r.Views).ToList();
        return new ScrollAnalysisDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<JourneyAnalysisDto> GetJourneyAnalysisAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_view").ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var topLanding = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!).Select(g => g.OrderBy(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown").OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() }).ToList();
        // SourcePageKey = page where the lead form was submitted (the conversion page).
        // This is shown in the UI as "Lead Conversion Pages" — the page the visitor was on when they converted.
        var pagesBeforeLead = leads.Where(l => !string.IsNullOrWhiteSpace(l.SourcePageKey))
            .GroupBy(l => l.SourcePageKey!).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() }).ToList();
        var convertedSids = new HashSet<string>(leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId)).Select(l => l.SessionId!));
        var dropOff = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && !convertedSids.Contains(e.SessionId!))
            .GroupBy(e => e.SessionId!).Select(g => g.OrderByDescending(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown").OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() }).ToList();
        return new JourneyAnalysisDto { TopLandingPages = topLanding, PagesBeforeLead = pagesBeforeLead, CommonDropOffPages = dropOff, RangeLabel = range.Label };
    }

    public async Task<SourcePerformanceDto> GetSourcePerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var pageViewEvents = await BaseEvents(range, scope, scopedAgentIds).Where(e => e.EventType == "page_view").ToListAsync();
        if (trafficType != TrafficType.All)
        {
            pageViewEvents = FilterAttributedRowsByTraffic(BuildAttributedEventRows(pageViewEvents), trafficType)
                .Select(r => r.Event)
                .ToList();
        }
        var engagementSignals = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId) &&
                        (e.EventType == "page_engaged_30s" ||
                         e.EventType == "page_engaged_60s" ||
                         e.EngagedMilliseconds.HasValue))
            .Select(e => new { e.SessionId, e.EventType, e.EngagedMilliseconds })
            .ToListAsync();
        // page_exit carries real elapsed dwell per page; page_view.DwellMilliseconds is always 0 at load time.
        var exitDwellEvents = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_exit" && e.DwellMilliseconds != null).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var leadsBySid = leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId))
            .GroupBy(l => l.SessionId!).ToDictionary(g => g.Key, g => g.Count());
        var engagedBySid = engagementSignals
            .GroupBy(e => e.SessionId!)
            .ToDictionary(
                g => g.Key,
                g => g.Any(x => x.EventType == "page_engaged_30s" || x.EventType == "page_engaged_60s") ||
                     g.Max(x => x.EngagedMilliseconds ?? 0) >= 30_000);
        // Sum page_exit dwell times per session = total time on site for that session.
        var totalDwellBySid = exitDwellEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .ToDictionary(g => g.Key, g => (double)g.Sum(x => x.DwellMilliseconds!.Value));
        var sessionFirst = pageViewEvents.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!).Select(g => g.OrderBy(x => x.EventUtc).First()).ToList();
        var rows = sessionFirst
            .GroupBy(e => new
            {
                Source = string.IsNullOrWhiteSpace(e.UtmSource) ? "unattributed" : e.UtmSource.Trim(),
                Medium = e.UtmMedium?.Trim() ?? (string?)null,
                Campaign = e.UtmCampaign?.Trim() ?? (string?)null,
                LandingPage = e.PageKey ?? "unknown"
            })
            .Select(g =>
            {
                var sids = g.Select(e => e.SessionId!).ToList();
                var sessions = sids.Count;
                var engaged = sids.Count(sid => engagedBySid.TryGetValue(sid, out var v) && v);
                var lCount = sids.Sum(sid => leadsBySid.TryGetValue(sid, out var c) ? c : 0);
                var dwellSamples = sids
                    .Select(sid => totalDwellBySid.TryGetValue(sid, out var d) ? d : 0)
                    .ToList();
                var avgDwell = RobustAverage(dwellSamples, SessionDurationHardCapMs);
                var lpLeads = leads.Count(l => l.SourcePageKey == g.Key.LandingPage && !string.IsNullOrWhiteSpace(l.SessionId) && sids.Contains(l.SessionId!));
                return new SourcePerformanceRow
                {
                    Source = g.Key.Source, Medium = g.Key.Medium, Campaign = g.Key.Campaign, LandingPage = g.Key.LandingPage,
                    Sessions = sessions, EngagedSessions = engaged, VerifiedLeads = lCount,
                    SessionConversionRate = sessions > 0 ? Math.Round((decimal)lCount / sessions * 100, 2) : 0,
                    LandingPageConversionRate = sessions > 0 ? Math.Round((decimal)lpLeads / sessions * 100, 2) : 0,
                    AvgDwellMs = Math.Round(avgDwell, 0)
                };
            }).OrderByDescending(r => r.Sessions).Take(50).ToList();
        return new SourcePerformanceDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<LandingPagePerformanceDto> GetLandingPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var pageViewEvents = await BaseEvents(range, scope, scopedAgentIds).Where(e => e.EventType == "page_view").ToListAsync();
        var engagementSignals = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId) &&
                        (e.EventType == "page_engaged_30s" ||
                         e.EventType == "page_engaged_60s" ||
                         e.EngagedMilliseconds.HasValue))
            .Select(e => new { e.SessionId, e.EventType, e.EngagedMilliseconds })
            .ToListAsync();
        // page_exit carries real elapsed dwell; page_view.DwellMilliseconds is always 0 at load time.
        var exitDwellEvents = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "page_exit" && e.DwellMilliseconds != null).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var leadsBySid = leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId))
            .GroupBy(l => l.SessionId!).ToDictionary(g => g.Key, g => g.Count());
        var engagedBySid = engagementSignals
            .GroupBy(e => e.SessionId!)
            .ToDictionary(
                g => g.Key,
                g => g.Any(x => x.EventType == "page_engaged_30s" || x.EventType == "page_engaged_60s") ||
                     g.Max(x => x.EngagedMilliseconds ?? 0) >= 30_000);
        // Sum page_exit dwell times per session = total time on site for that session.
        var totalDwellBySid = exitDwellEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .ToDictionary(g => g.Key, g => (double)g.Sum(x => x.DwellMilliseconds!.Value));
        var rows = pageViewEvents.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!).Select(g => g.OrderBy(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g =>
            {
                var sids = g.Select(e => e.SessionId!).ToList();
                var sessions = sids.Count;
                var engaged = sids.Count(sid => engagedBySid.TryGetValue(sid, out var v) && v);
                var lCount = sids.Sum(sid => leadsBySid.TryGetValue(sid, out var c) ? c : 0);
                var dwellSamples = sids
                    .Select(sid => totalDwellBySid.TryGetValue(sid, out var d) ? d : 0)
                    .ToList();
                var avgDwell = RobustAverage(dwellSamples, SessionDurationHardCapMs);
                return new LandingPagePerformanceRow
                {
                    PageKey = g.Key, Sessions = sessions, EngagedSessions = engaged, AvgDwellMs = Math.Round(avgDwell, 0),
                    VerifiedLeads = lCount, ConversionRate = sessions > 0 ? Math.Round((decimal)lCount / sessions * 100, 2) : 0,
                    TopSource = g.Where(e => !string.IsNullOrWhiteSpace(e.UtmSource)).GroupBy(e => e.UtmSource!).OrderByDescending(sg => sg.Count()).Select(sg => sg.Key).FirstOrDefault(),
                    TopCampaign = g.Where(e => !string.IsNullOrWhiteSpace(e.UtmCampaign)).GroupBy(e => e.UtmCampaign!).OrderByDescending(sg => sg.Count()).Select(sg => sg.Key).FirstOrDefault()
                };
            }).OrderByDescending(r => r.Sessions).ToList();
        return new LandingPagePerformanceDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<FormFrictionDto> GetFormFrictionAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "form_start" || e.EventType == "form_submit" ||
                        e.EventType == "form_field_focus" || e.EventType == "form_field_complete" ||
                        e.EventType == "form_field_error").ToListAsync();
        Func<AnalyticsEvent, string> fKey = e => e.FormKey ?? e.FormId ?? "unknown";
        var starts = events.Where(e => e.EventType == "form_start").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var submits = events.Where(e => e.EventType == "form_submit").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var ffFocus = events.Where(e => e.EventType == "form_field_focus").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var ffComplete = events.Where(e => e.EventType == "form_field_complete").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var ffError = events.Where(e => e.EventType == "form_field_error").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var rows = starts.Keys.Select(fk =>
        {
            var startSids = events.Where(e => e.EventType == "form_start" && fKey(e) == fk && !string.IsNullOrWhiteSpace(e.SessionId))
                .Select(e => e.SessionId!).Distinct().ToHashSet();
            var submitSids = events.Where(e => e.EventType == "form_submit" && fKey(e) == fk && !string.IsNullOrWhiteSpace(e.SessionId))
                .Select(e => e.SessionId!).Distinct().ToHashSet();
            var s = starts.TryGetValue(fk, out var sc) ? sc : 0;
            var sub = submits.TryGetValue(fk, out var sbc) ? sbc : 0;
            return new FormFrictionRow
            {
                FormKey = fk, Starts = s, Submits = sub,
                Abandons = startSids.Count(sid => !submitSids.Contains(sid)),
                CompletionRate = s > 0 ? Math.Round((decimal)sub / s * 100, 2) : 0,
                FieldFocuses = ffFocus.TryGetValue(fk, out var ff) ? ff : 0,
                FieldCompletes = ffComplete.TryGetValue(fk, out var fc) ? fc : 0,
                FieldErrors = ffError.TryGetValue(fk, out var fe) ? fe : 0
            };
        }).OrderByDescending(r => r.Starts).ToList();
        var topErrorFields = events.Where(e => e.EventType == "form_field_error" && !string.IsNullOrWhiteSpace(e.FieldName))
            .GroupBy(e => e.FieldName!).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() }).ToList();
        return new FormFrictionDto { Rows = rows, TopErrorFields = topErrorFields, RangeLabel = range.Label };
    }

    public async Task<FormAbandonmentDto> GetFormAbandonmentAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);

        // Fetch form_abandon and form_start for denominator, plus form_field_error for validation friction.
        // Include submit-success events so true submitters are never counted as abandons.
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "form_abandon" ||
                        e.EventType == "form_start" ||
                        e.EventType == "form_field_error" ||
                        e.EventType == "lead_form_submit_success" ||
                        e.EventType == "form_submit")
            .ToListAsync();
        if (trafficType != TrafficType.All)
        {
            events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(events), trafficType)
                .Select(r => r.Event)
                .ToList();
        }

        var submitSuccessEvents = events
            .Where(e => e.EventType == "lead_form_submit_success" ||
                        (e.EventType == "form_submit" &&
                         string.Equals(e.SubmitOutcome, "success", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        static string? BuildSessionFormKey(string? sessionId, string? formKey, string? quoteType)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            if (!string.IsNullOrWhiteSpace(formKey)) return $"{sessionId}::{formKey}";
            if (!string.IsNullOrWhiteSpace(quoteType)) return $"{sessionId}::qt:{quoteType}";
            return null;
        }

        var successfulSubmitKeys = submitSuccessEvents
            .Select(e => BuildSessionFormKey(e.SessionId, e.FormKey ?? e.FormId, e.QuoteType))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var abandonEvents = events
            .Where(e => e.EventType == "form_abandon")
            .Where(e =>
            {
                var key = BuildSessionFormKey(e.SessionId, e.FormKey ?? e.FormId, e.QuoteType);
                return key == null || !successfulSubmitKeys.Contains(key);
            })
            .ToList();
        var startEvents = events.Where(e => e.EventType == "form_start").ToList();
        var errorEvents = events.Where(e => e.EventType == "form_field_error" && !string.IsNullOrWhiteSpace(e.FieldName)).ToList();

        // Parse MetadataJson from form_abandon events
        var parsed = abandonEvents
            .Select(e =>
            {
                string? lastFocused = null, lastCompleted = null, quoteType = e.QuoteType;
                bool submitAttempted = false, consentInteracted = false;
                int completedCount = 0, errorCount = 0;
                double timeOnFormMs = 0;

                if (!string.IsNullOrWhiteSpace(e.MetadataJson))
                {
                    try
                    {
                        var doc = System.Text.Json.JsonDocument.Parse(e.MetadataJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("lastFocusedField", out var lff) && lff.ValueKind == System.Text.Json.JsonValueKind.String)
                            lastFocused = lff.GetString();
                        if (root.TryGetProperty("lastCompletedField", out var lcf) && lcf.ValueKind == System.Text.Json.JsonValueKind.String)
                            lastCompleted = lcf.GetString();
                        if (root.TryGetProperty("quoteType", out var qt) && qt.ValueKind == System.Text.Json.JsonValueKind.String)
                            quoteType = qt.GetString() ?? quoteType;
                        if (root.TryGetProperty("submitAttempted", out var sa))
                            submitAttempted = sa.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (root.TryGetProperty("consentInteracted", out var ci))
                            consentInteracted = ci.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (root.TryGetProperty("completedFieldCount", out var cfc) && cfc.TryGetInt32(out var cfcVal))
                            completedCount = cfcVal;
                        if (root.TryGetProperty("errorCount", out var ec) && ec.TryGetInt32(out var ecVal))
                            errorCount = ecVal;
                        if (root.TryGetProperty("timeOnFormMs", out var tfm) && tfm.TryGetDouble(out var tfmVal))
                            timeOnFormMs = tfmVal;
                    }
                    catch { /* malformed JSON — skip */ }
                }

                return new
                {
                    Event = e,
                    LastFocused = lastFocused,
                    LastCompleted = lastCompleted,
                    QuoteType = quoteType ?? "unknown",
                    SubmitAttempted = submitAttempted,
                    ConsentInteracted = consentInteracted,
                    CompletedCount = completedCount,
                    ErrorCount = errorCount,
                    TimeOnFormMs = timeOnFormMs
                };
            })
            .ToList();

        // Summary per quote type
        var startsByType = startEvents
            .GroupBy(e => e.QuoteType ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var summary = parsed
            .GroupBy(p => p.QuoteType)
            .Select(g =>
            {
                var abandons = g.Count();
                var starts = startsByType.TryGetValue(g.Key, out var s) ? s : 0;
                var startSignalMissing = starts == 0 && abandons > 0;
                return new FormAbandonSummaryRow
                {
                    QuoteType = g.Key,
                    Abandons = abandons,
                    Starts = starts,
                    AbandonRate = starts > 0 ? Math.Round((decimal)abandons / starts * 100, 2) : null,
                    StartSignalMissing = startSignalMissing,
                    AvgCompletedFields = abandons > 0 ? Math.Round(g.Average(p => (double)p.CompletedCount), 1) : 0,
                    SubmitAttemptedAbandonCount = g.Count(p => p.SubmitAttempted)
                };
            })
            .OrderByDescending(r => r.Abandons)
            .ToList();
        var startSignalGapQuoteTypeCount = summary.Count(r => r.StartSignalMissing);
        var dataQualityNote = startSignalGapQuoteTypeCount > 0
            ? "Some quote types have form_abandon events but no form_start denominator in-range; abandon rate is shown as — for those rows."
            : null;

        // Top last-focused fields (where drop-off happens)
        var topAbandoned = parsed
            .Where(p => !string.IsNullOrWhiteSpace(p.LastFocused))
            .GroupBy(p => new { Field = p.LastFocused!, p.QuoteType })
            .Select(g => new TopAbandonedFieldRow { FieldName = g.Key.Field, QuoteType = g.Key.QuoteType, AbandonCount = g.Count() })
            .OrderByDescending(r => r.AbandonCount)
            .Take(15)
            .ToList();

        // Top last-completed fields (furthest progress before abandon)
        var topLastCompleted = parsed
            .Where(p => !string.IsNullOrWhiteSpace(p.LastCompleted))
            .GroupBy(p => new { Field = p.LastCompleted!, p.QuoteType })
            .Select(g => new LastCompletedFieldRow { FieldName = g.Key.Field, QuoteType = g.Key.QuoteType, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .Take(15)
            .ToList();

        // Validation friction from form_field_error events
        var validationFriction = errorEvents
            .GroupBy(e => new { FieldName = e.FieldName!, QuoteType = e.QuoteType ?? "unknown" })
            .Select(g => new ValidationFrictionRow { FieldName = g.Key.FieldName, QuoteType = g.Key.QuoteType, ErrorCount = g.Count() })
            .OrderByDescending(r => r.ErrorCount)
            .Take(15)
            .ToList();

        // Consent friction: abandons where consentInteracted = false and submitAttempted = true
        var consentFriction = parsed.Count(p => p.SubmitAttempted && !p.ConsentInteracted);

        return new FormAbandonmentDto
        {
            Summary = summary,
            TopAbandonedFields = topAbandoned,
            TopLastCompletedFields = topLastCompleted,
            ValidationFriction = validationFriction,
            ConsentFrictionCount = consentFriction,
            StartSignalGapQuoteTypeCount = startSignalGapQuoteTypeCount,
            DataQualityNote = dataQualityNote,
            RangeLabel = range.Label
        };
    }

    private static List<double> CapValues(IEnumerable<double> values, double hardCapMs)
    {
        return values
            .Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0)
            .Select(v => Math.Min(v, hardCapMs))
            .ToList();
    }

    private static List<double> TrimValues(List<double> values, double fractionPerSide, int minSampleSize)
    {
        if (values.Count < minSampleSize) return values;
        var sorted = values.OrderBy(v => v).ToList();
        var trimCount = (int)Math.Floor(sorted.Count * fractionPerSide);
        if (trimCount <= 0 || (trimCount * 2) >= sorted.Count) return sorted;
        return sorted.Skip(trimCount).Take(sorted.Count - (trimCount * 2)).ToList();
    }

    private static double RobustAverage(IEnumerable<double> values, double hardCapMs)
    {
        var capped = CapValues(values, hardCapMs);
        if (capped.Count == 0) return 0;
        var trimmed = TrimValues(capped, TrimFractionPerSide, TrimMinimumSampleSize);
        return trimmed.Count == 0 ? 0 : trimmed.Average();
    }

    /// <summary>Computes the median of a list of doubles. Returns 0 for empty list.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}

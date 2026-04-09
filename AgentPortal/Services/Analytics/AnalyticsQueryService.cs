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
    private readonly string? _envFilter; // normalized ("prod","dev") or null for legacy fallback
    private static readonly HashSet<string> AllowedEnvironmentsFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        "prod","production","dev","development", ""
    };
    private readonly AgentPortal.Services.Tracking.AgentTrackingResolver _resolver;

    private readonly MasterAppDbContext _db;

    public AnalyticsQueryService(MasterAppDbContext db, IConfiguration config, AgentPortal.Services.Tracking.AgentTrackingResolver resolver)
    {
        _db = db;
        _envFilter = NormalizeEnv(config["Analytics:EnvironmentFilter"] ?? config["Analytics__EnvironmentFilter"]);
        _resolver = resolver;
    }

    private IQueryable<AnalyticsEvent> BaseEvents(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.AnalyticsEvents.AsNoTracking()
            .Where(e => !e.IsInternal)
            .Where(e => e.EventUtc >= range.FromUtc && e.EventUtc <= range.ToUtc)
            .Where(EnvPredicateEvents())
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

    private IQueryable<WebsiteLead> BaseLeads(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsInternal)
            .Where(l => l.CreatedUtc >= range.FromUtc && l.CreatedUtc <= range.ToUtc)
            .Where(EnvPredicateLeads())
            .Where(ScopePredicateLeads(scope, scopedAgentIds));

    private IQueryable<AnalyticsEvent> EventsInRange(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.AnalyticsEvents.AsNoTracking()
            .Where(e => !e.IsInternal)
            .Where(e => e.EventUtc >= from && e.EventUtc <= to)
            .Where(EnvPredicateEvents())
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

    private IQueryable<WebsiteLead> LeadsInRange(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsInternal)
            .Where(l => l.CreatedUtc >= from && l.CreatedUtc <= to)
            .Where(EnvPredicateLeads())
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
        int quoteFormStarts = events.Count(e => e.EventType == "form_start" && e.FormKey != null && e.FormKey.Contains("quote_", StringComparison.OrdinalIgnoreCase));
        int quoteFormSubmits = events.Count(e => e.EventType == "form_submit" && e.FormKey != null && e.FormKey.Contains("quote_", StringComparison.OrdinalIgnoreCase));
        int quoteStarts = events.Count(e => e.EventType == "quote_start");
        int formStarts = events.Count(e => e.EventType == "form_start");

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

        var topSource = events.Where(e => !string.IsNullOrWhiteSpace(e.UtmSource))
            .GroupBy(e => e.UtmSource!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var topCampaign = events.Where(e => !string.IsNullOrWhiteSpace(e.UtmCampaign))
            .GroupBy(e => e.UtmCampaign!)
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

    public async Task<TrafficOverviewDto> GetTrafficAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();

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
            RangeLabel = range.Label,
            TopSources = events.Where(e => !string.IsNullOrWhiteSpace(e.UtmSource))
                .GroupBy(e => e.UtmSource!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            TopCampaigns = events.Where(e => !string.IsNullOrWhiteSpace(e.UtmCampaign))
                .GroupBy(e => e.UtmCampaign!)
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

    public async Task<PagePerformanceDto> GetPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();

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

    public async Task<CtaPerformanceDto> GetCtaPerformanceAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "cta_click")
            .ToListAsync();

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

    public async Task<QuoteFunnelDto> GetQuoteFunnelAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();

        int starts = events.Count(e => e.EventType == "quote_click");
        int formStarts = events.Count(e => e.EventType == "form_start" && e.FormKey != null && e.FormKey.Contains("quote_"));
        int formSubmits = events.Count(e => e.EventType == "form_submit" && e.FormKey != null && e.FormKey.Contains("quote_"));

        var byType = events
            .Where(e => e.EventType == "quote_click" && !string.IsNullOrWhiteSpace(e.QuoteType))
            .GroupBy(e => e.QuoteType!)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        return new QuoteFunnelDto
        {
            QuoteStarts = starts,
            QuoteFormStarts = formStarts,
            QuoteFormSubmits = formSubmits,
            ByQuoteType = byType,
            RangeLabel = range.Label,
            DropOffStartsToFormStarts = starts > 0 ? Math.Round((decimal)(starts - formStarts) / starts * 100, 2) : null,
            DropOffFormStartsToSubmits = formStarts > 0 ? Math.Round((decimal)(formStarts - formSubmits) / formStarts * 100, 2) : null
        };
    }

    public async Task<ConversionCenterDto> GetConversionsAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "lead_form_submit_success" ||
                        (e.EventType == "form_submit" && (e.SubmitOutcome == null || e.SubmitOutcome == "success")))
            .OrderByDescending(e => e.EventUtc)
            .Take(100)
            .ToListAsync();

        var dto = new ConversionCenterDto
        {
            TotalConversions = events.Count,
            Recent = events.Select(e => new ConversionRow
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

    public async Task<LeadSnapshotDto> GetLeadsAsync(TimeRangeRequest range, ScopeContext scope, int take = 200)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var leadsQuery = BaseLeads(range, scope, scopedAgentIds)
            .OrderByDescending(l => l.CreatedUtc)
            .Take(take);

        var leads = await leadsQuery.ToListAsync();
        var total = await BaseLeads(range, scope, scopedAgentIds).CountAsync();

        var dto = new LeadSnapshotDto
        {
            Total = total,
            RangeLabel = range.Label,
            Leads = leads.Select(l => new LeadSnapshotRow
            {
                CreatedUtc = l.CreatedUtc,
                Name = $"{l.FirstName} {l.LastName}".Trim(),
                Email = l.Email,
                Phone = l.Phone,
                Interest = l.InterestType,
                Source = $"{l.SourcePageKey}/{l.SourceCtaKey}".Trim('/')
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

    public async Task<EngagementSummaryDto> GetEngagementSummaryAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();

        var pvs = events.Where(e => e.EventType == "page_view").ToList();
        // page_exit events carry the real per-page dwell (elapsed ms at departure).
        // page_view.DwellMilliseconds is always 0 (captured at load time), so we use page_exit for all dwell metrics.
        var exitEvents = events.Where(e => e.EventType == "page_exit" && e.DwellMilliseconds.HasValue).ToList();
        var dwells = exitEvents.Select(e => (double)e.DwellMilliseconds!.Value).ToList();
        var avgTimeOnPage = dwells.Count > 0 ? dwells.Average() : 0;
        var medianTimeOnPage = Median(dwells);

        // Session duration: max dwell per session, sourced from page_exit / session_end events.
        var sessionDurations = events
            .Where(e => (e.EventType == "page_exit" || e.EventType == "session_end") && !string.IsNullOrWhiteSpace(e.SessionId) && e.DwellMilliseconds.HasValue)
            .GroupBy(e => e.SessionId!)
            .Select(g => (double)g.Max(x => x.DwellMilliseconds!.Value))
            .ToList();
        var avgSessionDuration = sessionDurations.Count > 0 ? sessionDurations.Average() : 0;
        var medianSessionDuration = Median(sessionDurations);

        // Quick exits: sessions whose final page_exit had dwell < 10 s or was a bounce candidate.
        var sessionEngagedMap = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .ToDictionary(g => g.Key, g => g.Max(x => x.EngagedMilliseconds ?? 0));
        var lastExitPerSession = events
            .Where(e => (e.EventType == "page_exit" || e.EventType == "session_end") && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .Select(g => g.OrderByDescending(x => x.EventUtc).First())
            .ToList();
        int quickExits = lastExitPerSession.Count(e =>
            e.IsBounceCandidate == true ||
            (e.DwellMilliseconds.HasValue && e.DwellMilliseconds.Value < 10_000));
        var quickExitRate = sessionEngagedMap.Count > 0 ? Math.Round((decimal)quickExits / sessionEngagedMap.Count * 100, 2) : 0;

        int engagedSessions = sessionEngagedMap.Count(kv => kv.Value >= 30_000);
        var engagedSessionRate = sessionEngagedMap.Count > 0 ? Math.Round((decimal)engagedSessions / sessionEngagedMap.Count * 100, 2) : 0;

        // Top exit page: from explicit page_exit events, falling back to last page_view per session.
        var topExitPage = exitEvents
            .GroupBy(e => e.PageKey ?? "unknown").OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? pvs.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
                .GroupBy(e => e.SessionId!).Select(g => g.OrderByDescending(x => x.EventUtc).First())
                .GroupBy(e => e.PageKey ?? "unknown").OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        // Top long-dwell page: from page_exit.DwellMilliseconds (real elapsed time per page).
        var topLongDwellPage = exitEvents
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g => new { Page = g.Key, Avg = g.Average(x => (double)x.DwellMilliseconds!.Value) })
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
                AvgTimeMs = Math.Round(dl.Count > 0 ? dl.Average() : 0, 0),
                MedianTimeMs = Math.Round(Median(dl), 0),
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
        var byPage = events.GroupBy(e => e.PageKey ?? "unknown").Select(g =>
        {
            var d = g.Select(x => (double)x.DwellMilliseconds!.Value).ToList();
            var viewCount = pvCountMap.TryGetValue(g.Key, out var vc) ? vc : g.Count();
            return new DwellPageRow { PageKey = g.Key, Views = viewCount, AvgDwellMs = Math.Round(d.Average(), 0), MedianDwellMs = Math.Round(Median(d), 0) };
        }).ToList();
        return new TimeOnPageDto
        {
            LongestAvgDwell = byPage.OrderByDescending(p => p.AvgDwellMs).Take(10).ToList(),
            LongestMedianDwell = byPage.OrderByDescending(p => p.MedianDwellMs).Take(10).ToList(),
            ShortVisitProblemPages = byPage.Where(p => p.Views >= 5 && p.AvgDwellMs < 8_000).OrderBy(p => p.AvgDwellMs).Take(10).ToList(),
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
        // Explicit exits from page_exit events; infer from last page_view per session as fallback.
        var explicitExits = pageExitEvents
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var inferredExits = pageViewEvents.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!).Select(g => g.OrderByDescending(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var topExitRows = viewsByPage.Keys.Select(p =>
        {
            var ex = explicitExits.TryGetValue(p, out var ee) ? ee : inferredExits.TryGetValue(p, out var ie) ? ie : 0;
            var v = viewsByPage[p];
            return new ExitPageRow { PageKey = p, Views = v, Exits = ex, ExitRate = v > 0 ? Math.Round((decimal)ex / v * 100, 2) : 0 };
        }).OrderByDescending(r => r.Exits).Take(15).ToList();
        // Quick exits: page_exit events with real dwell < 10 s or bounce candidate flag.
        var quickExits = pageExitEvents
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

    public async Task<SourcePerformanceDto> GetSourcePerformanceAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds).Where(e => e.EventType == "page_view").ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var leadsBySid = leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId))
            .GroupBy(l => l.SessionId!).ToDictionary(g => g.Key, g => g.Count());
        var engagedBySid = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && e.EngagedMilliseconds.HasValue)
            .GroupBy(e => e.SessionId!).ToDictionary(g => g.Key, g => g.Max(x => x.EngagedMilliseconds!.Value) >= 30_000);
        var sessionFirst = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
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
                var avgDwell = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && sids.Contains(e.SessionId!) && e.DwellMilliseconds.HasValue)
                    .GroupBy(e => e.SessionId!).Select(sg => (double)sg.Max(x => x.DwellMilliseconds!.Value)).DefaultIfEmpty(0).Average();
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
        var events = await BaseEvents(range, scope, scopedAgentIds).Where(e => e.EventType == "page_view").ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var leadsBySid = leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId))
            .GroupBy(l => l.SessionId!).ToDictionary(g => g.Key, g => g.Count());
        var engagedBySid = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && e.EngagedMilliseconds.HasValue)
            .GroupBy(e => e.SessionId!).ToDictionary(g => g.Key, g => g.Max(x => x.EngagedMilliseconds!.Value) >= 30_000);
        var rows = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!).Select(g => g.OrderBy(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g =>
            {
                var sids = g.Select(e => e.SessionId!).ToList();
                var sessions = sids.Count;
                var engaged = sids.Count(sid => engagedBySid.TryGetValue(sid, out var v) && v);
                var lCount = sids.Sum(sid => leadsBySid.TryGetValue(sid, out var c) ? c : 0);
                var avgDwell = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && sids.Contains(e.SessionId!) && e.DwellMilliseconds.HasValue)
                    .GroupBy(e => e.SessionId!).Select(sg => (double)sg.Max(x => x.DwellMilliseconds!.Value)).DefaultIfEmpty(0).Average();
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
                        e.EventType == "form_field_abandon").ToListAsync();
        Func<AnalyticsEvent, string> fKey = e => e.FormKey ?? e.FormId ?? "unknown";
        var starts = events.Where(e => e.EventType == "form_start").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var submits = events.Where(e => e.EventType == "form_submit").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var ffFocus = events.Where(e => e.EventType == "form_field_focus").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var ffAbandon = events.Where(e => e.EventType == "form_field_abandon").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
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
                FieldAbandons = ffAbandon.TryGetValue(fk, out var fa) ? fa : 0
            };
        }).OrderByDescending(r => r.Starts).ToList();
        var topAbandonFields = events.Where(e => e.EventType == "form_field_abandon" && !string.IsNullOrWhiteSpace(e.FieldName))
            .GroupBy(e => e.FieldName!).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() }).ToList();
        return new FormFrictionDto { Rows = rows, TopAbandonFields = topAbandonFields, RangeLabel = range.Label };
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

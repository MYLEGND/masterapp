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
using Shared.Meta;
using Shared.Analytics;

namespace AgentPortal.Services.Analytics;

public sealed class AnalyticsQueryService : IAnalyticsQueryService
{
    private const int LowSampleThreshold = 20;
    private const int MarketingHealthRecentTrackingErrorLimit = 10;
    private const double PageDwellHardCapMs = 30 * 60 * 1000;       // 30 minutes
    private const double SessionDurationHardCapMs = 2 * 60 * 60 * 1000; // 2 hours
    private const double TrimFractionPerSide = 0.025;               // 2.5% each tail
    private const int TrimMinimumSampleSize = 20;
    private readonly string? _envFilter; // normalized ("prod","dev") or null for legacy fallback
    private readonly AgentPortal.Services.Tracking.AgentTrackingResolver _resolver;

    private readonly MasterAppDbContext _db;

    public AnalyticsQueryService(MasterAppDbContext db, IConfiguration config, AgentPortal.Services.Tracking.AgentTrackingResolver resolver)
    {
        _db = db;
        var configuredFilter = NormalizeEnv(config["Analytics:EnvironmentFilter"] ?? config["Analytics__EnvironmentFilter"]);
        var runtimeEnvironment = NormalizeEnv(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        // In production, default to strict production filtering if no explicit filter is configured.
        _envFilter = configuredFilter ?? (runtimeEnvironment == "prod" ? "prod" : null);
        _resolver = resolver;
    }

    private IQueryable<AnalyticsEvent> BaseEvents(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null)
    {
        var query = _db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.EventUtc >= range.FromUtc && e.EventUtc <= range.ToUtc)
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

        return ApplyQualityFilterEvents(query, range.QualityMode);
    }

    private IQueryable<AnalyticsEvent> BaseEventsWithoutQualityFilter(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.EventUtc >= range.FromUtc && e.EventUtc <= range.ToUtc)
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

    private IQueryable<AnalyticsEvent> EventsInRangeWithoutQualityFilter(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.EventUtc >= from && e.EventUtc <= to)
            .Where(ScopePredicateEvents(scope, scopedAgentIds));


    public IQueryable<AnalyticsEvent> ScopedEvents(
        TimeRangeRequest range,
        ScopeContext scope,
        Guid[]? scopedAgentIds = null)
        => BaseEvents(range, scope, scopedAgentIds);

    private IQueryable<WebsiteLead> BaseLeads(TimeRangeRequest range, ScopeContext scope, Guid[]? scopedAgentIds = null) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsDeleted)
            .Where(l => l.CreatedUtc >= range.FromUtc && l.CreatedUtc <= range.ToUtc)
            .Where(ScopePredicateLeads(scope, scopedAgentIds))
            .Where(QualityPredicateLeads(range.QualityMode));

    private IQueryable<AnalyticsEvent> EventsInRange(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null, TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic)
    {
        var query = _db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.EventUtc >= from && e.EventUtc <= to)
            .Where(ScopePredicateEvents(scope, scopedAgentIds));

        return ApplyQualityFilterEvents(query, qualityMode);
    }

    private IQueryable<WebsiteLead> LeadsInRange(DateTime from, DateTime to, ScopeContext scope, Guid[]? scopedAgentIds = null, TrafficQualityMode qualityMode = TrafficQualityMode.RealHumanTraffic) =>
        _db.WebsiteLeads.AsNoTracking()
            .Where(l => !l.IsDeleted)
            .Where(l => l.CreatedUtc >= from && l.CreatedUtc <= to)
            .Where(ScopePredicateLeads(scope, scopedAgentIds))
            .Where(QualityPredicateLeads(qualityMode));


    private static Expression<Func<AnalyticsEvent, bool>> QualityPredicateEvents(TrafficQualityMode mode) =>
        TrafficQualityBucketFilters.BuildEventPredicate(mode);

    private static Expression<Func<WebsiteLead, bool>> QualityPredicateLeads(TrafficQualityMode mode) =>
        TrafficQualityBucketFilters.BuildLeadPredicate(mode);

    private sealed class EventBucketMembership
    {
        public EventBucketMembership(
            IQueryable<string> sessionIds,
            IQueryable<string> visitorIds,
            IQueryable<Guid> eventIds)
        {
            SessionIds = sessionIds;
            VisitorIds = visitorIds;
            EventIds = eventIds;
        }

        public IQueryable<string> SessionIds { get; }
        public IQueryable<string> VisitorIds { get; }
        public IQueryable<Guid> EventIds { get; }
    }

    // Traffic buckets are assigned at the session/visitor identity level so
    // supporting rows like page_view inherit the session's final quality bucket.
    private static IQueryable<AnalyticsEvent> ApplyQualityFilterEvents(IQueryable<AnalyticsEvent> query, TrafficQualityMode mode)
    {
        if (mode == TrafficQualityMode.AllTraffic)
            return query;

        var internalQaBucket = BuildEventBucketMembership(query, QualityPredicateEvents(TrafficQualityMode.InternalQa));
        if (mode == TrafficQualityMode.InternalQa)
            return ApplyEventBucketMembership(query, internalQaBucket);

        var botBucket = BuildEventBucketMembership(
            query,
            QualityPredicateEvents(TrafficQualityMode.LikelyBotsAutomation),
            internalQaBucket);
        if (mode == TrafficQualityMode.LikelyBotsAutomation)
            return ApplyEventBucketMembership(query, botBucket);

        var suspiciousBucket = BuildEventBucketMembership(
            query,
            QualityPredicateEvents(TrafficQualityMode.SuspiciousActivity),
            internalQaBucket,
            botBucket);
        if (mode == TrafficQualityMode.SuspiciousActivity)
            return ApplyEventBucketMembership(query, suspiciousBucket);

        if (mode == TrafficQualityMode.RealHumanTraffic)
        {
            // RealHumanTraffic is already a complete row-level predicate:
            // it excludes internal/non-prod/local, automation, bot user agents,
            // bounce-only/suspicious rows, and requires session + visitor + human signal.
            //
            // Avoid building identity bucket membership for this hot path. The previous
            // implementation created several nested Contains() subqueries over session,
            // visitor, and event IDs, which caused production analytics modal timeouts.
            return query.Where(QualityPredicateEvents(TrafficQualityMode.RealHumanTraffic));
        }

        var realHumanBucket = BuildEventBucketMembership(
            query,
            QualityPredicateEvents(TrafficQualityMode.RealHumanTraffic),
            internalQaBucket,
            botBucket,
            suspiciousBucket);

        var likelyHumanBucket = BuildEventBucketMembership(
            query,
            QualityPredicateEvents(TrafficQualityMode.LikelyHuman),
            internalQaBucket,
            botBucket,
            suspiciousBucket,
            realHumanBucket);
        if (mode == TrafficQualityMode.LikelyHuman)
            return ApplyEventBucketMembership(query, likelyHumanBucket);

        var reviewedNeededBucket = BuildRemainingEventBucketMembership(
            query,
            internalQaBucket,
            botBucket,
            suspiciousBucket,
            realHumanBucket,
            likelyHumanBucket);

        return ApplyEventBucketMembership(query, reviewedNeededBucket);
    }

    private static EventBucketMembership BuildEventBucketMembership(
        IQueryable<AnalyticsEvent> query,
        Expression<Func<AnalyticsEvent, bool>> bucketPredicate,
        params EventBucketMembership[] excludedBuckets)
    {
        var candidates = query.Where(bucketPredicate);
        return BuildEventBucketMembership(ExcludeEventBucketMembership(candidates, excludedBuckets));
    }

    private static EventBucketMembership BuildRemainingEventBucketMembership(
        IQueryable<AnalyticsEvent> query,
        params EventBucketMembership[] excludedBuckets)
        => BuildEventBucketMembership(ExcludeEventBucketMembership(query, excludedBuckets));

    private static EventBucketMembership BuildEventBucketMembership(IQueryable<AnalyticsEvent> query)
    {
        var sessionIds = query
            .Where(e => e.SessionId != null && e.SessionId != string.Empty)
            .Select(e => e.SessionId!)
            .Distinct();

        var visitorIds = query
            .Where(e =>
                (e.SessionId == null || e.SessionId == string.Empty) &&
                e.VisitorId != null &&
                e.VisitorId != string.Empty)
            .Select(e => e.VisitorId!)
            .Distinct();

        var eventIds = query
            .Where(e =>
                (e.SessionId == null || e.SessionId == string.Empty) &&
                (e.VisitorId == null || e.VisitorId == string.Empty))
            .Select(e => e.EventId)
            .Distinct();

        return new EventBucketMembership(sessionIds, visitorIds, eventIds);
    }

    private static IQueryable<AnalyticsEvent> ExcludeEventBucketMembership(
        IQueryable<AnalyticsEvent> query,
        params EventBucketMembership[] excludedBuckets)
    {
        foreach (var excludedBucket in excludedBuckets)
        {
            query = query.Where(e =>
                ((e.SessionId != null && e.SessionId != string.Empty) &&
                 !excludedBucket.SessionIds.Contains(e.SessionId!)) ||
                (((e.SessionId == null || e.SessionId == string.Empty) &&
                  e.VisitorId != null &&
                  e.VisitorId != string.Empty) &&
                 !excludedBucket.VisitorIds.Contains(e.VisitorId!)) ||
                ((e.SessionId == null || e.SessionId == string.Empty) &&
                 (e.VisitorId == null || e.VisitorId == string.Empty) &&
                 !excludedBucket.EventIds.Contains(e.EventId)));
        }

        return query;
    }

    private static IQueryable<AnalyticsEvent> ApplyEventBucketMembership(
        IQueryable<AnalyticsEvent> query,
        EventBucketMembership bucket)
        => query.Where(e =>
            ((e.SessionId != null && e.SessionId != string.Empty) &&
             bucket.SessionIds.Contains(e.SessionId!)) ||
            (((e.SessionId == null || e.SessionId == string.Empty) &&
              e.VisitorId != null &&
              e.VisitorId != string.Empty) &&
             bucket.VisitorIds.Contains(e.VisitorId!)) ||
            ((e.SessionId == null || e.SessionId == string.Empty) &&
             (e.VisitorId == null || e.VisitorId == string.Empty) &&
             bucket.EventIds.Contains(e.EventId)));


    private sealed record InMemoryEventBucketMembership(
        HashSet<string> SessionIds,
        HashSet<string> VisitorIds,
        HashSet<Guid> EventIds);

    private static List<AnalyticsEvent> ApplyQualityFilterEventsInMemory(
        List<AnalyticsEvent> events,
        TrafficQualityMode mode)
    {
        if (mode == TrafficQualityMode.AllTraffic || events.Count == 0)
            return events;

        var internalQaBucket = BuildEventBucketMembershipInMemory(
            events.Where(QualityPredicateEvents(TrafficQualityMode.InternalQa).Compile()));
        if (mode == TrafficQualityMode.InternalQa)
            return ApplyEventBucketMembershipInMemory(events, internalQaBucket);

        var botCandidates = ExcludeEventBucketMembershipInMemory(
            events.Where(QualityPredicateEvents(TrafficQualityMode.LikelyBotsAutomation).Compile()),
            internalQaBucket);
        var botBucket = BuildEventBucketMembershipInMemory(botCandidates);
        if (mode == TrafficQualityMode.LikelyBotsAutomation)
            return ApplyEventBucketMembershipInMemory(events, botBucket);

        var suspiciousCandidates = ExcludeEventBucketMembershipInMemory(
            events.Where(QualityPredicateEvents(TrafficQualityMode.SuspiciousActivity).Compile()),
            internalQaBucket,
            botBucket);
        var suspiciousBucket = BuildEventBucketMembershipInMemory(suspiciousCandidates);
        if (mode == TrafficQualityMode.SuspiciousActivity)
            return ApplyEventBucketMembershipInMemory(events, suspiciousBucket);

        var realHumanCandidates = ExcludeEventBucketMembershipInMemory(
            events.Where(QualityPredicateEvents(TrafficQualityMode.RealHumanTraffic).Compile()),
            internalQaBucket,
            botBucket,
            suspiciousBucket);
        var realHumanBucket = BuildEventBucketMembershipInMemory(realHumanCandidates);
        if (mode == TrafficQualityMode.RealHumanTraffic)
            return ApplyEventBucketMembershipInMemory(events, realHumanBucket);

        var likelyHumanCandidates = ExcludeEventBucketMembershipInMemory(
            events.Where(QualityPredicateEvents(TrafficQualityMode.LikelyHuman).Compile()),
            internalQaBucket,
            botBucket,
            suspiciousBucket,
            realHumanBucket);
        var likelyHumanBucket = BuildEventBucketMembershipInMemory(likelyHumanCandidates);
        if (mode == TrafficQualityMode.LikelyHuman)
            return ApplyEventBucketMembershipInMemory(events, likelyHumanBucket);

        var reviewedNeededCandidates = ExcludeEventBucketMembershipInMemory(
            events,
            internalQaBucket,
            botBucket,
            suspiciousBucket,
            realHumanBucket,
            likelyHumanBucket);

        return ApplyEventBucketMembershipInMemory(
            events,
            BuildEventBucketMembershipInMemory(reviewedNeededCandidates));
    }

    private static InMemoryEventBucketMembership BuildEventBucketMembershipInMemory(IEnumerable<AnalyticsEvent> events)
    {
        var sessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eventIds = new HashSet<Guid>();

        foreach (var e in events)
        {
            if (!string.IsNullOrWhiteSpace(e.SessionId))
                sessionIds.Add(e.SessionId!);
            else if (!string.IsNullOrWhiteSpace(e.VisitorId))
                visitorIds.Add(e.VisitorId!);
            else
                eventIds.Add(e.EventId);
        }

        return new InMemoryEventBucketMembership(sessionIds, visitorIds, eventIds);
    }

    private static IEnumerable<AnalyticsEvent> ExcludeEventBucketMembershipInMemory(
        IEnumerable<AnalyticsEvent> events,
        params InMemoryEventBucketMembership[] excludedBuckets)
    {
        return events.Where(e => !excludedBuckets.Any(bucket => IsEventInBucket(e, bucket)));
    }

    private static List<AnalyticsEvent> ApplyEventBucketMembershipInMemory(
        IEnumerable<AnalyticsEvent> events,
        InMemoryEventBucketMembership bucket)
        => events.Where(e => IsEventInBucket(e, bucket)).ToList();

    private static bool IsEventInBucket(AnalyticsEvent e, InMemoryEventBucketMembership bucket)
    {
        if (!string.IsNullOrWhiteSpace(e.SessionId))
            return bucket.SessionIds.Contains(e.SessionId!);

        if (!string.IsNullOrWhiteSpace(e.VisitorId))
            return bucket.VisitorIds.Contains(e.VisitorId!);

        return bucket.EventIds.Contains(e.EventId);
    }

    // SCOPE SAFETY RULE:
    // Global scope must NEVER be passed directly into analytics queries.
    // Always resolve scope through ResolveScopeAsync before query execution.
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

    private string BuildEnvironmentLabel(IReadOnlyCollection<AnalyticsEvent> events)
    {
        if (events.Count == 0)
        {
            return _envFilter switch
            {
                "prod" => "Environment: Production",
                "dev" => "Environment: Development",
                _ => "Environment: Mixed/Legacy"
            };
        }

        var internalOnly = events.All(e => e.IsInternal);
        var localOnly = events.All(e => IsLocalHost(e.Host));
        var environmentKinds = events
            .Select(e => NormalizeEnv(e.Environment))
            .Select(e => e switch
            {
                "prod" => "production",
                "dev" => "development",
                _ => "legacy"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var environmentDescriptor = environmentKinds.Count switch
        {
            0 => "legacy",
            1 => environmentKinds[0],
            _ => "mixed"
        };

        if (internalOnly && localOnly)
            return $"Dataset: Internal QA / localhost / {environmentDescriptor}";

        if (internalOnly)
            return $"Dataset: Internal traffic / {environmentDescriptor}";

        if (localOnly)
            return $"Dataset: localhost traffic / {environmentDescriptor}";

        return environmentDescriptor switch
        {
            "production" => "Environment: Production",
            "development" => "Environment: Development",
            _ => "Environment: Mixed/Legacy"
        };
    }

    private static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var normalized = host.Trim();
        return normalized.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("::1", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime EnsureUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private static DateTime ViewerLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), tz);

    private static string BucketLabel(DateTime dt, TimeGrouping g) =>
        g switch
        {
            TimeGrouping.Day => dt.ToString("yyyy-MM-dd"),
            TimeGrouping.Week => $"{dt:yyyy}-W{ISOWeek.GetWeekOfYear(dt):00}",
            TimeGrouping.Month => dt.ToString("yyyy-MM"),
            TimeGrouping.Year => dt.ToString("yyyy"),
            _ => dt.ToString("yyyy-MM-dd")
        };

    private static DateTime BucketStart(DateTime utc, TimeGrouping g, TimeZoneInfo tz)
    {
        var dt = ViewerLocal(utc, tz);
        var mondayOffset = ((int)dt.Date.DayOfWeek + 6) % 7;
        return g switch
        {
            TimeGrouping.Day => dt.Date,
            TimeGrouping.Week => dt.Date.AddDays(-mondayOffset),
            TimeGrouping.Month => new DateTime(dt.Year, dt.Month, 1),
            TimeGrouping.Year => new DateTime(dt.Year, 1, 1),
            _ => dt.Date
        };
    }

    private static DateTime NextBucket(DateTime localBucketStart, TimeGrouping grouping) =>
        grouping switch
        {
            TimeGrouping.Day => localBucketStart.AddDays(1),
            TimeGrouping.Week => localBucketStart.AddDays(7),
            TimeGrouping.Month => localBucketStart.AddMonths(1),
            TimeGrouping.Year => localBucketStart.AddYears(1),
            _ => localBucketStart.AddDays(1)
        };

    private static IEnumerable<DateTime> EnumerateBuckets(TimeRangeRequest range)
    {
        var start = BucketStart(range.FromUtc, range.Grouping, range.ViewerTimeZone);
        var end = BucketStart(range.ToUtc, range.Grouping, range.ViewerTimeZone);

        for (var cursor = start; cursor <= end; cursor = NextBucket(cursor, range.Grouping))
        {
            yield return cursor;
        }
    }

    private static List<TrendPointDto> TrendFromEvents(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, DateTime> selector, TimeRangeRequest range)
    {
        var grouped = events
            .GroupBy(e => BucketStart(selector(e), range.Grouping, range.ViewerTimeZone))
            .ToDictionary(g => g.Key, g => g.Count());

        return EnumerateBuckets(range)
            .Select(bucket => new TrendPointDto
            {
                Label = BucketLabel(bucket, range.Grouping),
                Value = grouped.TryGetValue(bucket, out var value) ? value : 0
            })
            .ToList();
    }

    private static List<TrendPointDto> TrendDistinct(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, string?> keySelector, TimeRangeRequest range, Func<AnalyticsEvent, DateTime> timeSelector)
    {
        var grouped = events
            .Where(e => !string.IsNullOrWhiteSpace(keySelector(e)))
            .GroupBy(e => new
            {
                Bucket = BucketStart(timeSelector(e), range.Grouping, range.ViewerTimeZone),
                Key = keySelector(e)!
            })
            .GroupBy(g => g.Key.Bucket)
            .ToDictionary(g => g.Key, g => g.Count());

        return EnumerateBuckets(range)
            .Select(bucket => new TrendPointDto
            {
                Label = BucketLabel(bucket, range.Grouping),
                Value = grouped.TryGetValue(bucket, out var value) ? value : 0
            })
            .ToList();
    }

    private static bool IsQuoteFormKey(string? formKey) =>
        !string.IsNullOrWhiteSpace(formKey) &&
        formKey.Contains("quote_", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialLeadSuccessEvent(AnalyticsEvent e)
    {
        return AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "confirmed_lead") &&
               (IsQuoteFormKey(e.FormKey) || IsQuoteFormKey(e.PageKey));
    }

    // Legacy compatibility only.
    // Do NOT use form_submit as a canonical success signal moving forward.
    private static bool IsQuoteFallbackSubmitSuccessEvent(AnalyticsEvent e)
    {
        return IsQuoteScopeEvent(e) &&
               string.Equals(e.EventType, "form_submit", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(e.SubmitOutcome, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuoteSuccessEvent(AnalyticsEvent e) =>
        IsOfficialLeadSuccessEvent(e) || IsQuoteFallbackSubmitSuccessEvent(e);

    private static bool IsSubmitSuccessMetricEvent(AnalyticsEvent e) =>
        AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "submit_success") ||
        AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "confirmed_lead");

    private static bool IsSubmitAttemptMetricEvent(AnalyticsEvent e) =>
        AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "submit_attempt");

    private static bool IsQuoteSubmitSuccess(AnalyticsEvent e) =>
        IsQuoteSuccessEvent(e) &&
        (IsQuoteFormKey(e.FormKey) || IsQuoteFormKey(e.PageKey));

    private static bool IsCtaMetricEvent(AnalyticsEvent e) =>
        AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "cta_click");

    private static bool IsQuoteCtaIntentEvent(AnalyticsEvent e)
    {
        if (!IsQuoteScopeEvent(e))
            return false;

        if (!IsCtaMetricEvent(e) &&
            !string.Equals(e.EventType, "quote_click", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var elementKey = (e.ElementKey ?? string.Empty).Trim();

        return !string.Equals(elementKey, "nav_quote", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuoteSubmitAttempt(AnalyticsEvent e)
    {
        var inQuoteScope =
            IsQuoteFormKey(e.FormKey) ||
            IsQuoteFormKey(e.FormId) ||
            IsQuoteFormKey(e.PageKey) ||
            !string.IsNullOrWhiteSpace(ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey));

        if (!inQuoteScope)
            return false;

        if (AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "submit_attempt") &&
            !string.Equals(e.EventType, "form_submit", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(e.EventType, "form_submit", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(e.SubmitOutcome, "attempt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(e.SubmitOutcome, "success", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(e.SubmitOutcome, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuoteScopeEvent(AnalyticsEvent e)
    {
        return IsQuoteFormKey(e.FormKey) ||
               IsQuoteFormKey(e.FormId) ||
               IsQuoteFormKey(e.PageKey) ||
               !string.IsNullOrWhiteSpace(ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey));
    }

    private static bool IsQuoteFunnelInteractionEvent(AnalyticsEvent e)
    {
        return IsQuoteFunnelStartSignalEvent(e) ||
               e.EventType == "form_field_focus" ||
               e.EventType == "form_field_complete" ||
               e.EventType == "contact_field_focus" ||
               e.EventType == "contact_field_complete" ||
               e.EventType == "contact_progress_snapshot" ||
               e.EventType == "form_field_error" ||
               e.EventType == "form_abandon" ||
               IsQuoteSubmitAttempt(e) ||
               IsQuoteSuccessEvent(e);
    }

    private static bool IsQuoteResumeAfterAbandonEvent(AnalyticsEvent e)
    {
        if (!IsQuoteScopeEvent(e))
            return false;

        return (IsQuoteFunnelInteractionEvent(e) && e.EventType != "form_abandon") ||
               e.EventType == "lead_modal_open" ||
               e.EventType == "lead_modal_close";
    }

    private static bool IsQuoteFirstQuestionAnsweredEvent(AnalyticsEvent e)
    {
        if (!IsQuoteScopeEvent(e))
            return false;

        if (AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "first_question_answered"))
            return true;

        return string.Equals(e.EventType, "quote_step_complete", StringComparison.OrdinalIgnoreCase) &&
               ReadMetadataIntValue(e.MetadataJson, "stepNumber") == 1;
    }

    private static bool IsQuoteEntryViewEvent(AnalyticsEvent e)
    {
        if (!IsQuoteScopeEvent(e))
            return false;

        return AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view") ||
               string.Equals(e.EventType, "quote_landing_view", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuotePrimaryCtaExposureEvent(AnalyticsEvent e)
    {
        return IsQuoteScopeEvent(e) &&
               AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "primary_cta_seen");
    }

    private static bool IsQuoteEarlyDiscoveryInteractionEvent(AnalyticsEvent e)
    {
        return IsQuoteFirstQuestionAnsweredEvent(e) ||
               (AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "quote_step_complete") &&
                (e.EventType == "life_step1_protecting_select" ||
                 e.EventType == "protecting_who_completed" ||
                 e.EventType == "goal_completed" ||
                 e.EventType == "life_step1_coverage_select" ||
                 e.EventType == "life_step1_tobacco_select" ||
                 e.EventType == "tobacco_completed" ||
                 e.EventType == "tobaccouse_completed" ||
                 e.EventType == "age_completed" ||
                 e.EventType == "step1_age_entered"));
    }

    private static bool IsQuoteDiscoveryCompleteEvent(AnalyticsEvent e)
    {
        return AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "discovery_complete") ||
               e.EventType == "age_completed" ||
               e.EventType == "tobacco_completed" ||
               e.EventType == "tobaccouse_completed";
    }

    private static bool IsQuoteRecommendationViewedEvent(AnalyticsEvent e)
    {
        return AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "recommendation_viewed");
    }

    private static bool IsQuoteDiscoveryStepCompleteEvent(AnalyticsEvent e)
    {
        return IsQuoteScopeEvent(e) &&
               AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "quote_step_complete");
    }

    private static bool IsQuoteContactStepReachedEvent(AnalyticsEvent e)
    {
        return AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "contact_step_view") ||
               AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "quote_contact_step_view") ||
               string.Equals(e.EventType, "life_contact_first_start", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuoteExplicitFormStartEvent(AnalyticsEvent e)
    {
        if (!IsQuoteScopeEvent(e))
            return false;

        return AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "form_start") ||
               string.Equals(e.EventType, "lead_form_start", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(e.EventType, "life_contact_first_start", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuoteEntryEngagedSignalEvent(AnalyticsEvent e)
    {
        if (!IsQuoteScopeEvent(e))
            return false;

        return string.Equals(e.EventType, "quote_entry_engaged", StringComparison.OrdinalIgnoreCase) ||
               IsQuoteCtaIntentEvent(e) ||
               IsQuoteExplicitFormStartEvent(e) ||
               IsQuoteDiscoveryStepCompleteEvent(e) ||
               IsQuoteContactStepReachedEvent(e);
    }

    private static bool IsQuoteFunnelStartSignalEvent(AnalyticsEvent e)
    {
        return IsQuoteExplicitFormStartEvent(e);
    }

    private static bool IsQuoteExplicitStartSignalEvent(AnalyticsEvent e)
    {
        return IsQuoteEntryEngagedSignalEvent(e);
    }

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

    private static int CountDistinctUnits(
        IEnumerable<AnalyticsEvent> events,
        Func<AnalyticsEvent, bool> predicate,
        Func<AnalyticsEvent, string>? unitKeySelector = null)
    {
        var selector = unitKeySelector ?? BuildInteractionUnitKey;

        return events
            .Where(predicate)
            .Select(selector)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static HashSet<string> BuildDistinctUnitKeySet(
        IEnumerable<AnalyticsEvent> events,
        Func<AnalyticsEvent, bool> predicate,
        Func<AnalyticsEvent, string>? unitKeySelector = null)
    {
        var selector = unitKeySelector ?? BuildInteractionUnitKey;

        return events
            .Where(predicate)
            .Select(selector)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? InferQuoteTypeFromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim().ToLowerInvariant();

        if (normalized.Contains("quote_mortgage_protection") || normalized.Contains("quote_mortgageprotection")) return "mortgage_protection";
        if (normalized.Contains("quote_final_expense") || normalized.Contains("quote_finalexpense")) return "final_expense";
        if (normalized.Contains("quote_whole_life") || normalized.Contains("quote_wholelife")) return "whole_life";
        if (normalized.Contains("quote_term_life") || normalized.Contains("quote_termlife")) return "term_life";
        if (normalized.Contains("quote_life")) return "life";
        if (normalized.Contains("quote_iul")) return "iul";
        if (normalized.Contains("quote_auto")) return "auto";
        if (normalized.Contains("quote_home")) return "home";
        if (normalized.Contains("quote_commercial")) return "commercial";
        if (normalized.Contains("quote_disability")) return "disability";
        if (normalized.Contains("quote_health")) return "health";
        if (normalized.Contains("quote_dvh") ||
            normalized.Contains("quote_dental_vision_hearing") ||
            normalized.Contains("quote_dentalvisionhearing"))
            return "dvh";

        return null;
    }

    private static string? NormalizeQuoteTypeToken(string? quoteType)
    {
        if (string.IsNullOrWhiteSpace(quoteType)) return null;
        var normalized = quoteType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mortgage" or "mortgageprotection" => "mortgage_protection",
            "finalexpense" => "final_expense",
            "wholelife" => "whole_life",
            "term" => "term_life",
            "mortgage protection" => "mortgage_protection",
            "final expense" => "final_expense",
            "whole life" => "whole_life",
            "term life" => "term_life",
            "mortgage_protection" or "final_expense" or "whole_life" or "term_life" => normalized,
            _ => normalized
        };
    }

    private static string? ResolveQuoteTypeForReporting(string? quoteType, string? formKey = null, string? pageKey = null) =>
        InferQuoteTypeFromKey(formKey) ??
        InferQuoteTypeFromKey(pageKey) ??
        NormalizeQuoteTypeToken(quoteType);

    private static string? BuildQuoteScopeKey(string? formKey, string? pageKey, string? quoteType)
    {
        var normalizedScopeKey = NormalizeSuccessScopeKey(formKey) ?? NormalizeSuccessScopeKey(pageKey);
        if (!string.IsNullOrWhiteSpace(normalizedScopeKey))
            return normalizedScopeKey;

        return ResolveQuoteTypeForReporting(quoteType, formKey, pageKey) switch
        {
            "life" => "quote_life",
            "term_life" => "quote_term_life",
            "whole_life" => "quote_whole_life",
            "final_expense" => "quote_final_expense",
            "mortgage_protection" => "quote_mortgage_protection",
            "iul" => "quote_iul",
            "auto" => "quote_auto",
            "home" => "quote_home",
            "commercial" => "quote_commercial",
            "disability" => "quote_disability",
            "health" => "quote_health",
            var normalizedQuoteType when !string.IsNullOrWhiteSpace(normalizedQuoteType) => $"quote_{normalizedQuoteType}",
            _ => null,
        };
    }

    private static string? BuildSessionFormKey(string? sessionId, string? formKey, string? quoteType, string? pageKey = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var normalizedScopeKey = BuildQuoteScopeKey(formKey, pageKey, quoteType);
        if (!string.IsNullOrWhiteSpace(normalizedScopeKey)) return $"{sessionId}::{normalizedScopeKey}";
        var normalizedQuoteType = ResolveQuoteTypeForReporting(quoteType, formKey, pageKey);
        if (!string.IsNullOrWhiteSpace(normalizedQuoteType)) return $"{sessionId}::qt:{normalizedQuoteType}";
        return null;
    }

    private static string? NormalizeSuccessScopeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim().ToLowerInvariant();
        return normalized.EndsWith("_form", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^5]
            : normalized;
    }

    private static string BuildSuccessUnitKey(AnalyticsEvent e)
    {
        var scopeKey =
            BuildQuoteScopeKey(e.FormKey ?? e.FormId, e.PageKey, e.QuoteType) ??
            ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey) ??
            e.EventType.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(e.SessionId))
            return $"sid:{e.SessionId}::{scopeKey}";
        if (!string.IsNullOrWhiteSpace(e.VisitorId))
            return $"vid:{e.VisitorId}::{scopeKey}";
        if (e.ClientEventId.HasValue)
            return $"cid:{e.ClientEventId.Value:D}::{scopeKey}";
        return $"eid:{e.EventId:D}::{scopeKey}";
    }

    private static string BuildQuoteStageUnitKey(AnalyticsEvent e) => BuildSuccessUnitKey(e);

    private static int SuccessEventPriority(AnalyticsEvent e)
    {
        if (string.Equals(e.EventType, "website_lead_submitted", StringComparison.OrdinalIgnoreCase))
            return 3;

        if (IsSubmitSuccessMetricEvent(e))
            return 2;

        return 0;
    }

    private static List<AnalyticsEvent> SelectCanonicalSuccessEvents(IEnumerable<AnalyticsEvent> events)
    {
        return events
            .Where(IsQuoteSuccessEvent)
            .GroupBy(BuildSuccessUnitKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(SuccessEventPriority)
                .ThenByDescending(e => e.EventUtc)
                .First())
            .ToList();
    }

    private static int CountQuoteIntentStarts(IEnumerable<AnalyticsEvent> events)
    {
        return CountDistinctUnits(events, e =>
            IsQuoteEntryEngagedSignalEvent(e) ||
            IsQuoteSubmitAttempt(e),
            BuildQuoteStageUnitKey);
    }

    private static HashSet<string> UnionUnitKeySets(params IReadOnlyCollection<string>[] sets)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets)
        {
            keys.UnionWith(set);
        }

        return keys;
    }

    private static HashSet<string> BuildQuoteSubmitAttemptUnitKeys(IEnumerable<AnalyticsEvent> events) =>
        BuildDistinctUnitKeySet(events, IsQuoteSubmitAttempt, BuildQuoteStageUnitKey);

    private static HashSet<string> BuildQuoteDiscoveryStepUnitKeys(IEnumerable<AnalyticsEvent> events) =>
        BuildDistinctUnitKeySet(events, IsQuoteDiscoveryStepCompleteEvent, BuildQuoteStageUnitKey);

    private static HashSet<string> BuildQuoteContactStageUnitKeys(IEnumerable<AnalyticsEvent> events)
    {
        return UnionUnitKeySets(
            BuildDistinctUnitKeySet(events, IsQuoteContactStepReachedEvent, BuildQuoteStageUnitKey),
            BuildQuoteSubmitAttemptUnitKeys(events));
    }

    private static HashSet<string> BuildQuoteFormStartedUnitKeys(IEnumerable<AnalyticsEvent> events)
    {
        return UnionUnitKeySets(
            BuildDistinctUnitKeySet(events, IsQuoteExplicitFormStartEvent, BuildQuoteStageUnitKey),
            BuildQuoteDiscoveryStepUnitKeys(events),
            BuildQuoteContactStageUnitKeys(events));
    }

    private static HashSet<string> BuildQuoteEntryEngagedUnitKeys(IEnumerable<AnalyticsEvent> events)
    {
        return UnionUnitKeySets(
            BuildDistinctUnitKeySet(
                events,
                e => IsQuoteScopeEvent(e) && string.Equals(e.EventType, "quote_entry_engaged", StringComparison.OrdinalIgnoreCase),
                BuildQuoteStageUnitKey),
            BuildDistinctUnitKeySet(events, IsQuoteCtaIntentEvent, BuildQuoteStageUnitKey),
            BuildQuoteFormStartedUnitKeys(events));
    }

    private sealed record EventAttributionSnapshot(
        string? UtmSource,
        string? UtmMedium,
        string? UtmCampaign,
        string? UtmId,
        string? Fbclid,
        string? UtmTerm,
        string? UtmContent,
        string? MetaCampaignId,
        string? MetaAdSetId,
        string? MetaAdId,
        string? ReferrerHost,
        bool IsInternal,
        string? Environment,
        string? Host);

    private sealed record AttributedEventRow(
        AnalyticsEvent Event,
        EventAttributionSnapshot Attribution,
        TrafficType TrafficType,
        string ResolutionSource);

    private static int CountByReportingBucket(
        IReadOnlyDictionary<TrafficType, int> counts,
        TrafficType bucket) =>
        counts.TryGetValue(bucket, out var count) ? count : 0;

    private static IReadOnlyDictionary<TrafficType, int> CountDistinctUnitsByReportingBucket(
        IEnumerable<AttributedEventRow> rows,
        Func<AnalyticsEvent, bool> predicate,
        Func<AnalyticsEvent, string>? unitKeySelector = null)
    {
        var selector = unitKeySelector ?? BuildInteractionUnitKey;

        return rows
            .Where(r => predicate(r.Event))
            .GroupBy(r => selector(r.Event), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.Event.EventUtc).First())
            .GroupBy(r => TrafficAttribution.ToReportingBucket(r.TrafficType))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static IReadOnlyDictionary<TrafficType, int> CountSessionsByReportingBucket(IEnumerable<AttributedEventRow> sessionRows)
    {
        return sessionRows
            .GroupBy(r => TrafficAttribution.ToReportingBucket(r.TrafficType))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static string? NormalizeAttributionToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static EventAttributionSnapshot SnapshotFromEvent(AnalyticsEvent e) =>
        new(
            NormalizeAttributionToken(e.UtmSource),
            NormalizeAttributionToken(e.UtmMedium),
            NormalizeAttributionToken(e.UtmCampaign),
            NormalizeAttributionToken(e.UtmId),
            NormalizeAttributionToken(e.Fbclid),
            NormalizeAttributionToken(e.UtmTerm),
            NormalizeAttributionToken(e.UtmContent),
            NormalizeAttributionToken(e.MetaCampaignId),
            NormalizeAttributionToken(e.MetaAdSetId),
            NormalizeAttributionToken(e.MetaAdId),
            NormalizeAttributionToken(e.ReferrerHost),
            e.IsInternal,
            NormalizeAttributionToken(e.Environment),
            NormalizeAttributionToken(e.Host));

    private static bool HasAttributionSignal(EventAttributionSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.UtmSource) ||
        !string.IsNullOrWhiteSpace(snapshot.UtmMedium) ||
        !string.IsNullOrWhiteSpace(snapshot.UtmCampaign) ||
        !string.IsNullOrWhiteSpace(snapshot.UtmId) ||
        !string.IsNullOrWhiteSpace(snapshot.Fbclid) ||
        !string.IsNullOrWhiteSpace(snapshot.MetaCampaignId) ||
        !string.IsNullOrWhiteSpace(snapshot.MetaAdSetId) ||
        !string.IsNullOrWhiteSpace(snapshot.MetaAdId) ||
        Classify(snapshot) is TrafficType.Internal or TrafficType.Test or TrafficType.BotSuspicious;

    private static TrafficType Classify(EventAttributionSnapshot snapshot) =>
        TrafficAttribution.Classify(
            snapshot.UtmSource,
            snapshot.UtmMedium,
            snapshot.UtmCampaign,
            snapshot.Fbclid,
            snapshot.ReferrerHost,
            metaCampaignId: snapshot.MetaCampaignId,
            metaAdSetId: snapshot.MetaAdSetId,
            metaAdId: snapshot.MetaAdId,
            isInternal: snapshot.IsInternal,
            environment: snapshot.Environment,
            host: snapshot.Host);

    private static bool IsMetaAttributedPaid(EventAttributionSnapshot snapshot) =>
        TrafficAttribution.IsMetaAttributedPaid(
            snapshot.UtmSource,
            snapshot.UtmMedium,
            snapshot.UtmCampaign,
            snapshot.Fbclid,
            snapshot.MetaCampaignId,
            snapshot.MetaAdSetId,
            snapshot.MetaAdId,
            isInternal: snapshot.IsInternal,
            environment: snapshot.Environment,
            host: snapshot.Host,
            referrerHost: snapshot.ReferrerHost);


    private static int AttributionStrength(EventAttributionSnapshot snapshot)
    {
        if (!HasAttributionSignal(snapshot))
            return -1;

        if (IsMetaAttributedPaid(snapshot))
            return 500;

        if (!string.IsNullOrWhiteSpace(snapshot.Fbclid) ||
            !string.IsNullOrWhiteSpace(snapshot.MetaCampaignId) ||
            !string.IsNullOrWhiteSpace(snapshot.MetaAdSetId) ||
            !string.IsNullOrWhiteSpace(snapshot.MetaAdId))
            return 450;

        if (!string.IsNullOrWhiteSpace(snapshot.UtmSource) &&
            !string.IsNullOrWhiteSpace(snapshot.UtmCampaign))
            return 400;

        if (!string.IsNullOrWhiteSpace(snapshot.UtmSource))
            return 300;

        if (!string.IsNullOrWhiteSpace(snapshot.ReferrerHost))
            return 200;

        return 100;
    }

    private static EventAttributionSnapshot? SelectStrongestAttribution(IEnumerable<AnalyticsEvent> events)
    {
        return events
            .Select(e => new { e.EventUtc, Snapshot = SnapshotFromEvent(e) })
            .Where(x => HasAttributionSignal(x.Snapshot))
            .OrderByDescending(x => AttributionStrength(x.Snapshot))
            .ThenBy(x => x.EventUtc)
            .Select(x => x.Snapshot)
            .FirstOrDefault();
    }


    private static Dictionary<string, EventAttributionSnapshot> BuildSessionAttributionMap(List<AnalyticsEvent> events)
    {
        var map = new Dictionary<string, EventAttributionSnapshot>(StringComparer.OrdinalIgnoreCase);
        var groups = events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var selected = SelectStrongestAttribution(group);
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
            var selected = SelectStrongestAttribution(group);
            if (selected != null && HasAttributionSignal(selected))
                map[group.Key] = selected;
        }

        return map;
    }

    private static (EventAttributionSnapshot Snapshot, string ResolutionSource) ResolveAttribution(
        AnalyticsEvent e,
        IReadOnlyDictionary<string, EventAttributionSnapshot> sessionMap,
        IReadOnlyDictionary<string, EventAttributionSnapshot> visitorMap,
        bool allowVisitorFallback)
    {
        var direct = SnapshotFromEvent(e);
        if (HasAttributionSignal(direct))
            return (direct, "direct");

        if (!string.IsNullOrWhiteSpace(e.SessionId) &&
            sessionMap.TryGetValue(e.SessionId!, out var sessionAttribution) &&
            HasAttributionSignal(sessionAttribution))
        {
            return (sessionAttribution, "session");
        }

        if (allowVisitorFallback &&
            !string.IsNullOrWhiteSpace(e.VisitorId) &&
            visitorMap.TryGetValue(e.VisitorId!, out var visitorAttribution) &&
            HasAttributionSignal(visitorAttribution))
        {
            return (visitorAttribution, "visitor");
        }

        return (direct, "unknown");
    }

    private static List<AttributedEventRow> BuildAttributedEventRows(IEnumerable<AnalyticsEvent> events, bool allowVisitorFallback = false)
    {
        var list = events.ToList();
        if (list.Count == 0)
            return new List<AttributedEventRow>();

        var sessionMap = BuildSessionAttributionMap(list);
        var visitorMap = BuildVisitorAttributionMap(list);

        return list
            .Select(e =>
            {
                var resolved = ResolveAttribution(e, sessionMap, visitorMap, allowVisitorFallback);
                return new AttributedEventRow(e, resolved.Snapshot, Classify(resolved.Snapshot), resolved.ResolutionSource);
            })
            .ToList();
    }

    private static List<AttributedEventRow> FilterAttributedRowsByTraffic(List<AttributedEventRow> rows, TrafficType trafficType)
    {
        if (trafficType == TrafficType.All)
            return rows;
        return rows.Where(r => TrafficAttribution.MatchesFilter(r.TrafficType, trafficType)).ToList();
    }

    private sealed record LeadMetadataSnapshot(
        string? UtmId,
        string? UtmTerm,
        string? UtmContent,
        string? MetaCampaignId,
        string? MetaAdSetId,
        string? MetaAdId,
        string? PageMode,
        MetaLeadTrackingState? MetaTracking);

    private sealed record LeadBehaviorSnapshot(
        string? DeviceType,
        string? Browser,
        string? OperatingSystem,
        int? ScrollPercent,
        int? HumanInteractionCount);

    private static LeadMetadataSnapshot SnapshotFromLeadMetadata(WebsiteLead lead)
    {
        if (string.IsNullOrWhiteSpace(lead.MetadataJson))
            return new LeadMetadataSnapshot(null, null, null, null, null, null, null, null);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(lead.MetadataJson);
            var root = doc.RootElement;

            string? ReadString(string propertyName) =>
                root.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? NormalizeAttributionToken(value.GetString())
                    : null;

            return new LeadMetadataSnapshot(
                ReadString("UtmId"),
                ReadString("UtmTerm"),
                ReadString("UtmContent"),
                ReadString("MetaCampaignId"),
                ReadString("MetaAdSetId"),
                ReadString("MetaAdId"),
                ReadString("PageMode"),
                MetaLeadTrackingJson.Read(lead.MetadataJson));
        }
        catch
        {
            return new LeadMetadataSnapshot(null, null, null, null, null, null, null, null);
        }
    }

    private static (EventAttributionSnapshot Attribution, string ResolutionSource) ResolveLeadAttribution(
        WebsiteLead lead,
        IReadOnlyDictionary<string, EventAttributionSnapshot> sessionMap,
        IReadOnlyDictionary<string, EventAttributionSnapshot> visitorMap)
    {
        var metadata = SnapshotFromLeadMetadata(lead);
        var direct = new EventAttributionSnapshot(
            NormalizeAttributionToken(lead.UtmSource),
            NormalizeAttributionToken(lead.UtmMedium),
            NormalizeAttributionToken(lead.UtmCampaign),
            NormalizeAttributionToken(lead.UtmId) ?? metadata.UtmId,
            NormalizeAttributionToken(lead.Fbclid),
            metadata.UtmTerm,
            metadata.UtmContent,
            NormalizeAttributionToken(lead.MetaCampaignId) ?? metadata.MetaCampaignId,
            NormalizeAttributionToken(lead.MetaAdSetId) ?? metadata.MetaAdSetId,
            NormalizeAttributionToken(lead.MetaAdId) ?? metadata.MetaAdId,
            null,
            lead.IsInternal,
            NormalizeAttributionToken(lead.Environment),
            NormalizeAttributionToken(lead.Host));

        if (HasAttributionSignal(direct))
            return (direct, "lead");

        if (!string.IsNullOrWhiteSpace(lead.SessionId) &&
            sessionMap.TryGetValue(lead.SessionId!, out var sessionAttribution) &&
            HasAttributionSignal(sessionAttribution))
        {
            return (sessionAttribution, "session");
        }

        if (!string.IsNullOrWhiteSpace(lead.VisitorId) &&
            visitorMap.TryGetValue(lead.VisitorId!, out var visitorAttribution) &&
            HasAttributionSignal(visitorAttribution))
        {
            return (visitorAttribution, "visitor");
        }

        return (direct, "unknown");
    }

    private static Dictionary<string, List<AnalyticsEvent>> BuildSessionEventMap(IEnumerable<AnalyticsEvent> events)
    {
        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.EventUtc).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<AnalyticsEvent>> BuildVisitorEventMap(IEnumerable<AnalyticsEvent> events)
    {
        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.VisitorId))
            .GroupBy(e => e.VisitorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.EventUtc).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeDeviceContextLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";

        var normalized = value.Trim();
        if (normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return "Unknown";

        var lower = normalized.ToLowerInvariant();
        return lower switch
        {
            "ios" => "iOS",
            "macos" => "macOS",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "safari" => "Safari",
            "edge" => "Edge",
            "android" => "Android",
            "windows" => "Windows",
            "desktop" => "Desktop",
            "mobile" => "Mobile",
            "tablet" => "Tablet",
            _ => char.ToUpperInvariant(normalized[0]) + normalized[1..]
        };
    }

    private static string ResolveDeviceTypeContext(AnalyticsEvent e)
    {
        var existing = NormalizeDeviceContextLabel(e.DeviceType);
        if (!existing.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return existing;

        var width = e.ViewportWidth ?? e.ScreenWidth ?? 0;
        if (width <= 0) return "Unknown";
        if (width < 768) return "Mobile";
        if (width < 1024) return "Tablet";
        return "Desktop";
    }

    private static string BucketWidthLabel(int? width)
    {
        if (!width.HasValue || width.Value <= 0) return "Unknown";
        var w = width.Value;
        if (w < 390) return "Small mobile (<390)";
        if (w < 480) return "Large mobile (390-479)";
        if (w < 768) return "Phablet (480-767)";
        if (w < 1024) return "Tablet (768-1023)";
        if (w < 1440) return "Laptop/Desktop (1024-1439)";
        return "Wide desktop (1440+)";
    }

    private static string ResolveLatestContextLabel(IEnumerable<AnalyticsEvent> events, Func<AnalyticsEvent, string> selector)
    {
        var labels = events
            .OrderBy(e => e.EventUtc)
            .Select(selector)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var best = labels.LastOrDefault(x => !x.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(best) ? (labels.LastOrDefault() ?? "Unknown") : best;
    }

    private static string? BuildIdentityProfileKeyOrNull(AnalyticsEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.SessionId))
            return $"sid:{e.SessionId.Trim()}";

        if (!string.IsNullOrWhiteSpace(e.VisitorId))
            return $"vid:{e.VisitorId.Trim()}";

        return null;
    }

    private static bool IsVisitorFallbackIdentityKey(string identityKey) =>
        identityKey.StartsWith("vid:", StringComparison.OrdinalIgnoreCase);

    private static LeadBehaviorSnapshot ResolveLeadBehaviorSnapshot(
        WebsiteLead lead,
        IReadOnlyDictionary<string, List<AnalyticsEvent>> sessionEventMap,
        IReadOnlyDictionary<string, List<AnalyticsEvent>> visitorEventMap)
    {
        var sessionId = FirstNonBlank(lead.SessionId);
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionEventMap.TryGetValue(sessionId, out var sessionEvents))
        {
            return BuildLeadBehaviorSnapshot(sessionEvents);
        }

        var visitorId = FirstNonBlank(lead.VisitorId);
        if (!string.IsNullOrWhiteSpace(visitorId) && visitorEventMap.TryGetValue(visitorId, out var visitorEvents))
        {
            return BuildLeadBehaviorSnapshot(visitorEvents);
        }

        return new LeadBehaviorSnapshot(null, null, null, null, null);
    }

    private static LeadBehaviorSnapshot BuildLeadBehaviorSnapshot(IReadOnlyCollection<AnalyticsEvent> events)
    {
        if (events.Count == 0)
            return new LeadBehaviorSnapshot(null, null, null, null, null);

        var scrollSamples = events
            .Where(e => e.ScrollPercent.HasValue)
            .Select(e => e.ScrollPercent!.Value)
            .ToList();

        var humanInteractionSamples = events
            .Where(e => e.HumanInteractionCount.HasValue)
            .Select(e => e.HumanInteractionCount!.Value)
            .ToList();

        return new LeadBehaviorSnapshot(
            DeviceType: ResolveLatestContextLabel(events, ResolveDeviceTypeContext),
            Browser: ResolveLatestContextLabel(events, e => NormalizeDeviceContextLabel(e.Browser)),
            OperatingSystem: ResolveLatestContextLabel(events, e => NormalizeDeviceContextLabel(e.OperatingSystem)),
            ScrollPercent: scrollSamples.Count == 0 ? null : scrollSamples.Max(),
            HumanInteractionCount: humanInteractionSamples.Count == 0 ? null : humanInteractionSamples.Max());
    }


    private static string NormalizeAnalyticsSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "Unknown";

        var s = source.Trim().ToLowerInvariant();

        return s switch
        {
            "ig" => "Instagram",
            "instagram" => "Instagram",
            "instagram.com" => "Instagram",

            "fb" => "Facebook / Meta",
            "facebook" => "Facebook / Meta",
            "facebook.com" => "Facebook / Meta",
            "meta" => "Facebook / Meta",

            "messenger" => "Messenger",
            "m.me" => "Messenger",

            "google" => "Google",
            "googleads" => "Google Ads",
            "google_ads" => "Google Ads",
            "adwords" => "Google Ads",

            "bing" => "Microsoft Ads",
            "microsoft" => "Microsoft Ads",

            "chatgpt" => "ChatGPT / OpenAI",
            "openai" => "ChatGPT / OpenAI",

            "claude" => "Claude / Anthropic",
            "anthropic" => "Claude / Anthropic",

            "perplexity" => "Perplexity",

            "(direct)" => "Direct",
            "direct" => "Direct",
            "(none)" => "Direct",
            "none" => "Direct",

            _ => char.ToUpperInvariant(s[0]) + s[1..]
        };
    }


    private static string SourceBucketLabel(EventAttributionSnapshot attribution, TrafficType trafficType)
    {
        if (!string.IsNullOrWhiteSpace(attribution.UtmSource))
            return NormalizeAnalyticsSource(attribution.UtmSource);

        if (IsMetaAttributedPaid(attribution))
            return "Meta Ads";

        return trafficType switch
        {
            TrafficType.Direct => "Direct",
            TrafficType.Organic => "Organic",
            TrafficType.Referral => "Referral",
            TrafficType.Internal => "Internal",
            TrafficType.Test => "Test",
            TrafficType.BotSuspicious => "Bot/Suspicious",
            TrafficType.PaidAds => "Paid (Unattributed)",
            _ => "Unknown"
        };
    }

    private static string SourceBucketLabel(AttributedEventRow row) =>
        SourceBucketLabel(row.Attribution, row.TrafficType);

    private static string CampaignBucketLabel(EventAttributionSnapshot attribution) =>
        !string.IsNullOrWhiteSpace(attribution.UtmCampaign)
            ? attribution.UtmCampaign!.Trim()
            : !string.IsNullOrWhiteSpace(attribution.MetaCampaignId)
                ? attribution.MetaCampaignId!.Trim()
                : !string.IsNullOrWhiteSpace(attribution.UtmId)
                    ? attribution.UtmId!.Trim()
                    : "(none)";

    private static string CampaignBucketLabel(AttributedEventRow row) =>
        CampaignBucketLabel(row.Attribution);

    private static string ResolveMetaLearningReason(bool sentToMeta, EventAttributionSnapshot attribution, TrafficType trafficType)
    {
        if (sentToMeta && IsMetaAttributedPaid(attribution))
            return "Included: sent to Meta CAPI and paid Meta-attributed.";

        if (sentToMeta)
            return "Included: sent to Meta CAPI; not paid Meta-attributed.";

        return trafficType switch
        {
            TrafficType.PaidAds => "Excluded: paid traffic, but not Meta-attributed.",
            TrafficType.Direct => "Excluded: direct/manual traffic.",
            TrafficType.Organic => "Excluded: organic traffic.",
            TrafficType.Referral => "Excluded: referral/social traffic.",
            TrafficType.Internal => "Excluded: internal navigation or preview traffic.",
            TrafficType.Test => "Excluded: test or QA traffic.",
            TrafficType.BotSuspicious => "Excluded: bot or suspicious traffic.",
            TrafficType.Unknown => "Excluded: unattributed traffic.",
            _ => "Excluded from Meta learning readiness."
        };
    }

    private static string BuildLeadUnitKey(WebsiteLead lead)
    {
        if (!string.IsNullOrWhiteSpace(lead.SessionId))
            return $"sid:{lead.SessionId}";
        if (!string.IsNullOrWhiteSpace(lead.VisitorId))
            return $"vid:{lead.VisitorId}";
        return $"lid:{lead.LeadId:D}";
    }

    private static string BuildSessionOrVisitorKey(AnalyticsEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.SessionId))
            return $"sid:{e.SessionId}";
        if (!string.IsNullOrWhiteSpace(e.VisitorId))
            return $"vid:{e.VisitorId}";
        return $"eid:{e.EventId:D}";
    }

    private static string BuildPipelineUnitKey(AnalyticsEvent e)
    {
        var leadId = ReadMetadataStringValue(e.MetadataJson, "LeadId");
        if (!string.IsNullOrWhiteSpace(leadId))
            return $"lead:{leadId}";

        return BuildSessionOrVisitorKey(e);
    }

    private sealed record TrackingErrorMetadata(
        string AttemptedEventName,
        int? StatusCode,
        string ErrorMessage,
        int RetryCount,
        string QueueReason,
        string Route,
        string FetchUrl,
        string Method,
        string SessionId,
        string VisitorId,
        string Trigger);

    private static TrackingErrorMetadata ReadTrackingErrorMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new TrackingErrorMetadata(string.Empty, null, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            return new TrackingErrorMetadata(
                AttemptedEventName: ReadJsonString(root, "attemptedEventName") ?? string.Empty,
                StatusCode: ReadJsonInt(root, "statusCode"),
                ErrorMessage: ReadJsonString(root, "errorMessage") ?? string.Empty,
                RetryCount: ReadJsonInt(root, "retryCount") ?? 0,
                QueueReason: ReadJsonString(root, "queueReason") ?? string.Empty,
                Route: ReadJsonString(root, "route") ?? string.Empty,
                FetchUrl: ReadJsonString(root, "fetchUrl") ?? string.Empty,
                Method: ReadJsonString(root, "method") ?? string.Empty,
                SessionId: ReadJsonString(root, "sessionId") ?? string.Empty,
                VisitorId: ReadJsonString(root, "visitorId") ?? string.Empty,
                Trigger: ReadJsonString(root, "trigger") ?? string.Empty);
        }
        catch
        {
            return new TrackingErrorMetadata(string.Empty, null, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private static string? ReadMetadataStringValue(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (root.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetString()?.Trim();
            }
        }
        catch
        {
            // ignored: malformed metadata should not block analytics health checks
        }

        return null;
    }

    private static int? ReadMetadataIntValue(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            return ReadJsonInt(doc.RootElement, propertyName);
        }
        catch
        {
            // ignored: malformed metadata should not block analytics health checks
        }

        return null;
    }

    private static string? ReadJsonString(System.Text.Json.JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString()?.Trim(),
            System.Text.Json.JsonValueKind.Number => value.ToString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? ReadJsonInt(System.Text.Json.JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ShortTrackingId(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    private static string? TrimTrackingUrl(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
        {
            var pathAndQuery = absolute.PathAndQuery;
            return string.IsNullOrWhiteSpace(pathAndQuery) ? absolute.AbsolutePath : pathAndQuery;
        }

        return normalized;
    }

    private static bool MatchesTrackingIdentity(AnalyticsEvent candidate, string? sessionId, string? visitorId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(visitorId) &&
            string.Equals(candidate.VisitorId, visitorId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool? ResolveTrackingErrorRecoveredState(
        string attemptedEventName,
        AnalyticsEvent errorEvent,
        IReadOnlyCollection<AnalyticsEvent> allEvents,
        string? sessionId,
        string? visitorId)
    {
        if (string.IsNullOrWhiteSpace(attemptedEventName) ||
            (!AnalyticsEventCatalog.TryGet(attemptedEventName, out _) &&
             !string.Equals(attemptedEventName, "fetch_non_ok", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(attemptedEventName, "fetch_failed", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (string.Equals(attemptedEventName, "fetch_non_ok", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attemptedEventName, "fetch_failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attemptedEventName, "window_error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attemptedEventName, "unhandled_rejection", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(visitorId))
            return null;

        var recovered = allEvents.Any(candidate =>
            !string.Equals(candidate.EventType, AnalyticsEventCatalog.ClientTrackingErrorEventName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.EventType, attemptedEventName, StringComparison.OrdinalIgnoreCase) &&
            candidate.EventUtc > errorEvent.EventUtc &&
            MatchesTrackingIdentity(candidate, sessionId, visitorId));

        return recovered;
    }

    private static bool IsCoreTrackingFailure(string attemptedEventName)
    {
        if (string.IsNullOrWhiteSpace(attemptedEventName))
            return false;

        return string.Equals(attemptedEventName, "lead_form_start", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(attemptedEventName, "lead_persisted", StringComparison.OrdinalIgnoreCase) ||
               AnalyticsEventCatalog.MatchesDashboardMetric(attemptedEventName, "submit_success") ||
               AnalyticsEventCatalog.MatchesDashboardMetric(attemptedEventName, "confirmed_lead");
    }

    private static string ResolveTrackingErrorSeverity(string attemptedEventName, int? statusCode, bool? recovered)
    {
        var isCriticalEvent = AnalyticsEventCatalog.TryGet(attemptedEventName, out var definition) && definition.IsCritical;
        var isCoreFailure = IsCoreTrackingFailure(attemptedEventName);

        if (recovered == true)
            return isCriticalEvent ? "Medium" : "Low";

        if (recovered == false)
        {
            if (isCoreFailure)
                return "Critical";

            return isCriticalEvent ? "High" : "Medium";
        }

        if (statusCode is >= 500)
            return isCriticalEvent ? "High" : "Medium";

        return isCriticalEvent ? "Medium" : "Low";
    }

    private static string ResolveTrackingErrorSuggestedAction(int? statusCode, string? errorMessage)
    {
        if (statusCode == 400)
            return "Check event catalog / payload schema";
        if (statusCode == 401 || statusCode == 403)
            return "Check auth/anti-forgery/session permissions";
        if (statusCode == 404)
            return "Check endpoint route";
        if (statusCode is >= 500)
            return "Check server logs";

        var message = errorMessage?.Trim() ?? string.Empty;
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed to fetch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("load failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Check connectivity or retry queue";
        }

        return "Inspect browser console and server ingest logs";
    }

    private static string ResolveTrackingAttemptedEndpoint(TrackingErrorMetadata metadata)
    {
        var fetchUrl = TrimTrackingUrl(metadata.FetchUrl);
        if (!string.IsNullOrWhiteSpace(fetchUrl))
            return fetchUrl!;

        return "/api/analytics/ingest";
    }

    private static string BuildTrackingErrorWarningSummary(int count, MarketingHealthTrackingErrorDto? recentError)
    {
        if (recentError == null)
            return $"{count} tracking errors detected.";

        var eventName = string.IsNullOrWhiteSpace(recentError.AttemptedEventName) ? "tracking event" : recentError.AttemptedEventName;
        var page = FirstNonBlank(recentError.PageKey, recentError.PagePath, recentError.QuoteType) ?? "unknown page";
        var status = recentError.StatusCode.HasValue
            ? $"HTTP {recentError.StatusCode.Value}"
            : string.IsNullOrWhiteSpace(recentError.ErrorMessage)
                ? "an unknown error"
                : recentError.ErrorMessage;

        return $"{count} tracking errors detected. Most recent: {eventName} failed on {page} with {status}.";
    }

    private static string DescribeElapsedFromError(DateTime errorUtc, DateTime leadUtc)
    {
        var delta = leadUtc - errorUtc;
        if (delta <= TimeSpan.Zero)
            return "same moment";
        if (delta.TotalSeconds < 60)
            return $"{Math.Max(1, (int)Math.Round(delta.TotalSeconds))}s later";
        if (delta.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))}m later";
        if (delta.TotalHours < 24)
            return $"{Math.Max(1, (int)Math.Round(delta.TotalHours))}h later";
        return $"{Math.Max(1, (int)Math.Round(delta.TotalDays))}d later";
    }

    private static MarketingHealthMatchedLeadDto? ResolveTrackingErrorMatchedLead(
        AnalyticsEvent errorEvent,
        IReadOnlyCollection<WebsiteLead> candidateLeads,
        string? sessionId,
        string? visitorId,
        TimeZoneInfo viewerTimeZone)
    {
        WebsiteLead? matchedLead = null;
        string matchType = "session";

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            matchedLead = candidateLeads
                .Where(lead =>
                    string.Equals(lead.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                    lead.CreatedUtc >= errorEvent.EventUtc)
                .OrderBy(lead => lead.CreatedUtc)
                .FirstOrDefault();
        }

        if (matchedLead == null && !string.IsNullOrWhiteSpace(visitorId))
        {
            var visitorWindowEndUtc = errorEvent.EventUtc.AddHours(24);
            matchedLead = candidateLeads
                .Where(lead =>
                    string.Equals(lead.VisitorId, visitorId, StringComparison.OrdinalIgnoreCase) &&
                    lead.CreatedUtc >= errorEvent.EventUtc &&
                    lead.CreatedUtc <= visitorWindowEndUtc)
                .OrderBy(lead => lead.CreatedUtc)
                .FirstOrDefault();
            matchType = "visitor";
        }

        if (matchedLead == null)
            return null;

        var displayNameParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(matchedLead.FirstName))
            displayNameParts.Add(matchedLead.FirstName.Trim());
        if (!string.IsNullOrWhiteSpace(matchedLead.LastName))
            displayNameParts.Add(matchedLead.LastName.Trim());
        var displayName = string.Join(" ", displayNameParts);

        return new MarketingHealthMatchedLeadDto
        {
            LeadId = matchedLead.LeadId,
            LocalDisplayTime = ViewerLocal(matchedLead.CreatedUtc, viewerTimeZone).ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture),
            Name = string.IsNullOrWhiteSpace(displayName) ? "Submitted lead" : displayName,
            Email = matchedLead.Email,
            Phone = matchedLead.Phone,
            Interest = matchedLead.InterestType,
            SourcePageKey = matchedLead.SourcePageKey,
            MatchType = matchType,
            DelayFromErrorLabel = DescribeElapsedFromError(errorEvent.EventUtc, matchedLead.CreatedUtc)
        };
    }

    private static string BuildTrackingErrorActionWarning(MarketingHealthTrackingErrorDto detail)
    {
        var page = FirstNonBlank(detail.PageKey, detail.PagePath, detail.QuoteType) ?? "this page";
        return $"Tracking errors detected on {page}. {detail.SuggestedAction}.";
    }

    private static MarketingHealthTrackingErrorDto BuildTrackingErrorDetail(
        AnalyticsEvent errorEvent,
        IReadOnlyCollection<AnalyticsEvent> allEvents,
        IReadOnlyCollection<WebsiteLead> candidateLeads,
        TimeZoneInfo viewerTimeZone)
    {
        var metadata = ReadTrackingErrorMetadata(errorEvent.MetadataJson);
        var sessionId = FirstNonBlank(errorEvent.SessionId, metadata.SessionId);
        var visitorId = FirstNonBlank(errorEvent.VisitorId, metadata.VisitorId);
        var attemptedEventName = metadata.AttemptedEventName;
        var recovered = ResolveTrackingErrorRecoveredState(attemptedEventName, errorEvent, allEvents, sessionId, visitorId);
        var severity = ResolveTrackingErrorSeverity(attemptedEventName, metadata.StatusCode, recovered);
        var matchedLead = ResolveTrackingErrorMatchedLead(errorEvent, candidateLeads, sessionId, visitorId, viewerTimeZone);

        return new MarketingHealthTrackingErrorDto
        {
            EventUtc = errorEvent.EventUtc,
            LocalDisplayTime = ViewerLocal(errorEvent.EventUtc, viewerTimeZone).ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture),
            PageKey = errorEvent.PageKey,
            PageUrl = TrimTrackingUrl(errorEvent.Url),
            PagePath = FirstNonBlank(errorEvent.Path, metadata.Route),
            QuoteType = errorEvent.QuoteType,
            AttemptedEventName = attemptedEventName,
            ErrorMessage = FirstNonBlank(metadata.ErrorMessage, "tracking_error") ?? "tracking_error",
            StatusCode = metadata.StatusCode,
            AttemptedEndpoint = ResolveTrackingAttemptedEndpoint(metadata),
            RetryCount = Math.Max(metadata.RetryCount, 0),
            Recovered = recovered,
            SessionId = sessionId,
            SessionIdShort = ShortTrackingId(sessionId),
            VisitorId = visitorId,
            VisitorIdShort = ShortTrackingId(visitorId),
            Browser = errorEvent.Browser,
            DeviceType = errorEvent.DeviceType,
            OperatingSystem = errorEvent.OperatingSystem,
            RequestMethod = FirstNonBlank(metadata.Method, "POST"),
            RequestRoute = FirstNonBlank(metadata.Route, errorEvent.Path),
            RequestTrigger = metadata.Trigger,
            RawFetchUrl = metadata.FetchUrl,
            Source = SourceBucketLabel(SnapshotFromEvent(errorEvent), Classify(SnapshotFromEvent(errorEvent))),
            Campaign = FirstNonBlank(errorEvent.UtmCampaign, errorEvent.MetaCampaignName, errorEvent.MetaCampaignId),
            Severity = severity,
            SuggestedAction = ResolveTrackingErrorSuggestedAction(metadata.StatusCode, metadata.ErrorMessage),
            MatchedLead = matchedLead
        };
    }

    private static int CountConvertedSessions(IEnumerable<WebsiteLead> leads) =>
        leads.Select(BuildLeadUnitKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    /// <summary>
    /// Applies traffic filtering to a list of leads using the same session→visitor attribution
    /// fallback chain used for events. Leads with blank UTM fields are resolved against the
    /// session/visitor attribution maps built from the provided context events before filtering.
    /// This prevents paid sessions from being misclassified as Unknown simply because the lead
    /// record itself did not capture UTM fields directly.
    /// </summary>
    private static List<WebsiteLead> ResolveAndFilterLeads(
        List<WebsiteLead> leads,
        List<AnalyticsEvent> contextEvents,
        TrafficType trafficType)
    {
        if (trafficType == TrafficType.All) return leads;
        if (leads.Count == 0) return leads;

        var sessionMap = BuildSessionAttributionMap(contextEvents);
        var visitorMap = BuildVisitorAttributionMap(contextEvents);

        return leads.Where(l =>
        {
            var resolved = ResolveLeadAttribution(l, sessionMap, visitorMap);
            return TrafficAttribution.MatchesFilter(Classify(resolved.Attribution), trafficType);
        }).ToList();
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
                    .FirstOrDefault()
                ?? g.Where(r => r.Event.EventType == "page_view")
                    .OrderBy(r => r.Event.EventUtc)
                    .FirstOrDefault()
                ?? g.OrderBy(r => r.Event.EventUtc).FirstOrDefault())
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    private static decimal ClampPercent(decimal value) => Math.Min(100m, Math.Max(0m, value));

    public async Task<SummaryKpiDto> GetSummaryAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var rawEvents = await BaseEventsWithoutQualityFilter(range, scope, scopedAgentIds).ToListAsync();
        var allEvents = ApplyQualityFilterEventsInMemory(rawEvents, range.QualityMode);
        var allLeads  = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();

        List<AnalyticsEvent> events;
        List<WebsiteLead>    leads;
        if (trafficType == TrafficType.All)
        {
            events = allEvents;
            leads  = allLeads;
        }
        else
        {
            events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
                         .Select(r => r.Event).ToList();
            leads  = ResolveAndFilterLeads(allLeads, allEvents, trafficType);
        }

        // previous period for deltas (same filter applied)
        var span = range.ToUtc - range.FromUtc;
        var prevFrom = range.FromUtc - span;
        var prevTo   = range.ToUtc - span;
        var rawPrevEvents = await EventsInRangeWithoutQualityFilter(prevFrom, prevTo, scope, scopedAgentIds).ToListAsync();
        var prevAllEvents = ApplyQualityFilterEventsInMemory(rawPrevEvents, range.QualityMode);
        var prevAllLeads  = await LeadsInRange(prevFrom, prevTo, scope, scopedAgentIds, range.QualityMode).ToListAsync();

        List<AnalyticsEvent> prevEvents;
        List<WebsiteLead>    prevLeads;
        if (trafficType == TrafficType.All)
        {
            prevEvents = prevAllEvents;
            prevLeads  = prevAllLeads;
        }
        else
        {
            prevEvents = FilterAttributedRowsByTraffic(BuildAttributedEventRows(prevAllEvents), trafficType)
                             .Select(r => r.Event).ToList();
            prevLeads  = ResolveAndFilterLeads(prevAllLeads, prevAllEvents, trafficType);
        }

        int pageViews = events.Count(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"));
        int sessions = events.Where(e => !string.IsNullOrWhiteSpace(e.SessionId)).Select(e => e.SessionId!).Distinct().Count();
        int visitors = events.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId!).Distinct().Count();
        int verifiedLeads = leads.Count;
        int quoteFormStarts = BuildQuoteFormStartedUnitKeys(events).Count;
        int quoteFormSubmits = SelectCanonicalSuccessEvents(events).Count;
        int quoteStarts = BuildQuoteEntryEngagedUnitKeys(events).Count;
        int convertedSessions = CountConvertedSessions(leads);

        int prevPageViews = prevEvents.Count(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"));
        int prevSessions = prevEvents.Where(e => !string.IsNullOrWhiteSpace(e.SessionId)).Select(e => e.SessionId!).Distinct().Count();
        int prevVisitors = prevEvents.Where(e => !string.IsNullOrWhiteSpace(e.VisitorId)).Select(e => e.VisitorId!).Distinct().Count();
        int prevVerifiedLeads = prevLeads.Count;

        var topPage = events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"))
            .GroupBy(e => e.PageKey ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var topCta = events.Where(IsCtaMetricEvent)
            .GroupBy(e => e.ElementKey ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var attributedRows = BuildAttributedEventRows(events);
        var sessionAttributionRows = BuildSessionAttributionRows(attributedRows);
        var topAttributionRows = sessionAttributionRows.Count > 0 ? sessionAttributionRows : attributedRows;

        var topSource = topAttributionRows
            .GroupBy(SourceBucketLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var topCampaign = topAttributionRows
            .GroupBy(CampaignBucketLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        decimal sessionConversionRate = sessions > 0 ? Math.Round((decimal)convertedSessions / sessions * 100, 2) : 0;
        bool sessionLow = sessions > 0 && sessions < LowSampleThreshold;

        int intentDenom = quoteStarts;
        string intentLabel = "Server-confirmed Leads / Quote Starts";
        bool intentAvailable = intentDenom > 0;
        decimal intentConversionRate = intentDenom > 0
            ? Math.Min(100, Math.Round((decimal)quoteFormSubmits / intentDenom * 100, 2))
            : 0;
        bool intentLow = intentDenom > 0 && intentDenom < LowSampleThreshold;
        var envLabel = BuildEnvironmentLabel(events);

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
            PageViewTrend = TrendFromEvents(events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view")), e => e.EventUtc, range),
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
        var allAttributedRows = BuildAttributedEventRows(allEvents);
        var attributedRows = FilterAttributedRowsByTraffic(allAttributedRows, trafficType);
        var events = attributedRows.Select(r => r.Event).ToList();
        var sessionAttributionRows = BuildSessionAttributionRows(attributedRows);
        var sessionBucketCounts = CountSessionsByReportingBucket(sessionAttributionRows);
        var topAttributionRows = sessionAttributionRows.Count > 0 ? sessionAttributionRows : attributedRows;

        var traffic = new TrafficOverviewDto
        {
            PageViewTrend = TrendFromEvents(events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view")), e => e.EventUtc, range),
            SessionTrend = TrendDistinct(events, e => e.SessionId, range, e => e.EventUtc),
            VisitorTrend = TrendDistinct(events, e => e.VisitorId, range, e => e.EventUtc),
            TopPages = events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"))
                .GroupBy(e => e.PageKey ?? "unknown")
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            TopCtas = events.Where(IsCtaMetricEvent)
                .GroupBy(e => e.ElementKey ?? "unknown")
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            RangeLabel = range.Label,
            TrafficType = trafficType,
            TopSources = topAttributionRows
                .GroupBy(SourceBucketLabel, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            TopCampaigns = topAttributionRows
                .GroupBy(CampaignBucketLabel, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
                .ToList(),
            PaidSessionCount = CountByReportingBucket(sessionBucketCounts, TrafficType.PaidAds),
            NonPaidSessionCount = CountByReportingBucket(sessionBucketCounts, TrafficType.NonPaid),
            UnknownSessionCount = CountByReportingBucket(sessionBucketCounts, TrafficType.Unknown)
        };

        // Entry pages: first page_view per session within range
        var firstPerSession = events
            .Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view") && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .Select(g => g.OrderBy(x => x.EventUtc).First())
            .GroupBy(e => e.PageKey ?? "unknown")
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() })
            .ToList();
        traffic.EntryPages = firstPerSession;

        static int EngagementSeconds(string? eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType)) return 0;
            if (!eventType.StartsWith("page_engaged_", StringComparison.OrdinalIgnoreCase)) return 0;

            var raw = eventType
                .Replace("page_engaged_", "", StringComparison.OrdinalIgnoreCase)
                .Replace("s", "", StringComparison.OrdinalIgnoreCase);

            return int.TryParse(raw, out var seconds) ? seconds : 0;
        }

        static string BuildActivitySummary(List<AnalyticsEvent> sessionEvents)
        {
            var eventTypes = sessionEvents
                .Select(e => e.EventType ?? "")
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasPrimaryCtaSeen = sessionEvents.Any(IsQuotePrimaryCtaExposureEvent);
            var hasQuoteEntryEngaged = BuildQuoteEntryEngagedUnitKeys(sessionEvents).Count > 0;
            var hasFormStarted = BuildQuoteFormStartedUnitKeys(sessionEvents).Count > 0;
            var hasContactStep = BuildQuoteContactStageUnitKeys(sessionEvents).Count > 0;
            var hasSubmitAttempt = sessionEvents.Any(IsQuoteSubmitAttempt);
            var hasLeadConfirmed = SelectCanonicalSuccessEvents(sessionEvents).Count > 0;

            var parts = new List<string>();

            if (eventTypes.Contains("page_view")) parts.Add("viewed");
            if (eventTypes.Contains("quote_landing_view")) parts.Add("quote viewed");
            if (hasPrimaryCtaSeen) parts.Add("CTA seen");
            if (hasQuoteEntryEngaged) parts.Add("quote engaged");
            if (hasFormStarted) parts.Add("form started");
            if (hasContactStep) parts.Add("contact viewed");
            if (hasSubmitAttempt) parts.Add("submit attempted");
            if (hasLeadConfirmed) parts.Add("submitted");

            var maxEngagement = sessionEvents
                .Select(e => EngagementSeconds(e.EventType))
                .DefaultIfEmpty(0)
                .Max();

            if (maxEngagement > 0) parts.Add($"engaged {maxEngagement}s");
            if (eventTypes.Contains("form_abandon")) parts.Add("abandoned");
            if (eventTypes.Contains("page_exit")) parts.Add("exited");

            return parts.Count > 0 ? string.Join(" · ", parts) : "activity recorded";
        }

        static string BuildOutcomeSummary(List<AnalyticsEvent> sessionEvents)
        {
            var eventTypes = sessionEvents
                .Select(e => e.EventType ?? "")
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasPrimaryCtaSeen = sessionEvents.Any(IsQuotePrimaryCtaExposureEvent);
            var hasQuoteEntryEngaged = BuildQuoteEntryEngagedUnitKeys(sessionEvents).Count > 0;
            var hasFormStarted = BuildQuoteFormStartedUnitKeys(sessionEvents).Count > 0;
            var hasContactStep = BuildQuoteContactStageUnitKeys(sessionEvents).Count > 0;
            var hasSubmitAttempt = sessionEvents.Any(IsQuoteSubmitAttempt);
            var hasLeadConfirmed = SelectCanonicalSuccessEvents(sessionEvents).Count > 0;

            if (hasLeadConfirmed) return "Lead confirmed";
            if (hasSubmitAttempt) return "Submit attempted";
            if (hasContactStep) return "Contact step reached";
            if (hasFormStarted) return "Form started";
            if (hasQuoteEntryEngaged) return "Quote engaged";
            if (eventTypes.Contains("form_abandon")) return "Funnel abandoned";
            if (hasPrimaryCtaSeen) return "CTA seen, no engagement";
            if (eventTypes.Contains("page_exit")) return "Exited before engagement";

            var maxEngagement = sessionEvents
                .Select(e => EngagementSeconds(e.EventType))
                .DefaultIfEmpty(0)
                .Max();

            return maxEngagement > 0 ? $"Engaged {maxEngagement}s" : "Viewed";
        }

        var activityEvents = events
            .Where(e =>
                e.EventType != "page_visibility_hidden" &&
                e.EventType != "page_visibility_return" &&
                e.EventType != "scroll_depth_25" &&
                e.EventType != "scroll_depth_50" &&
                e.EventType != "scroll_depth_75" &&
                e.EventType != "scroll_depth_90" &&
                e.EventType != "scroll_depth_100")
            .ToList();

        var sessionActivityRows = new List<ActivityItemDto>();
        var visitInactivityGap = TimeSpan.FromMinutes(15);

        foreach (var group in activityEvents
            .GroupBy(e => new
            {
                SessionKey = !string.IsNullOrWhiteSpace(e.SessionId)
                    ? e.SessionId!
                    : $"event:{e.EventUtc.Ticks}:{e.EventType}:{e.PageKey}:{e.ElementKey}",
                PageKey = e.PageKey ?? "unknown"
            }))
        {
            var orderedEvents = group.OrderBy(e => e.EventUtc).ToList();
            var visitEvents = new List<AnalyticsEvent>();
            DateTime? previousEventUtc = null;

            foreach (var currentEvent in orderedEvents)
            {
                if (previousEventUtc.HasValue &&
                    currentEvent.EventUtc - previousEventUtc.Value > visitInactivityGap &&
                    visitEvents.Count > 0)
                {
                    var first = visitEvents.First();
                    var last = visitEvents.Last();
                    var durationSeconds = Math.Max(0, (int)Math.Round((last.EventUtc - first.EventUtc).TotalSeconds));
                    var sessionId = FirstNonBlank(visitEvents.AsEnumerable().Reverse().Select(x => x.SessionId).ToArray());
                    var visitorId = FirstNonBlank(visitEvents.AsEnumerable().Reverse().Select(x => x.VisitorId).ToArray());

                    sessionActivityRows.Add(new ActivityItemDto
                    {
                        EventUtc = first.EventUtc,
                        EndUtc = last.EventUtc,
                        DurationSeconds = durationSeconds,
                        EventCount = visitEvents.Count,
                        EventType = BuildActivitySummary(visitEvents),
                        ActivitySummary = BuildActivitySummary(visitEvents),
                        PageKey = group.Key.PageKey,
                        ElementKey = BuildOutcomeSummary(visitEvents),
                        OutcomeSummary = BuildOutcomeSummary(visitEvents),
                        SessionId = sessionId,
                        SessionIdShort = ShortTrackingId(sessionId),
                        VisitorId = visitorId,
                        VisitorIdShort = ShortTrackingId(visitorId)
                    });

                    visitEvents = new List<AnalyticsEvent>();
                }

                visitEvents.Add(currentEvent);
                previousEventUtc = currentEvent.EventUtc;
            }

            if (visitEvents.Count > 0)
            {
                var first = visitEvents.First();
                var last = visitEvents.Last();
                var durationSeconds = Math.Max(0, (int)Math.Round((last.EventUtc - first.EventUtc).TotalSeconds));
                var sessionId = FirstNonBlank(visitEvents.AsEnumerable().Reverse().Select(x => x.SessionId).ToArray());
                var visitorId = FirstNonBlank(visitEvents.AsEnumerable().Reverse().Select(x => x.VisitorId).ToArray());

                sessionActivityRows.Add(new ActivityItemDto
                {
                    EventUtc = first.EventUtc,
                    EndUtc = last.EventUtc,
                    DurationSeconds = durationSeconds,
                    EventCount = visitEvents.Count,
                    EventType = BuildActivitySummary(visitEvents),
                    ActivitySummary = BuildActivitySummary(visitEvents),
                    PageKey = group.Key.PageKey,
                    ElementKey = BuildOutcomeSummary(visitEvents),
                    OutcomeSummary = BuildOutcomeSummary(visitEvents),
                    SessionId = sessionId,
                    SessionIdShort = ShortTrackingId(sessionId),
                    VisitorId = visitorId,
                    VisitorIdShort = ShortTrackingId(visitorId)
                });
            }
        }

        traffic.RecentActivity = sessionActivityRows
            .OrderByDescending(a => a.EndUtc ?? a.EventUtc)
            .ToList();

        return traffic;
    }

    public async Task<PagePerformanceDto> GetPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads  = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();

        List<AnalyticsEvent> events;
        List<WebsiteLead>    leads;
        if (trafficType == TrafficType.All)
        {
            events = allEvents;
            leads  = allLeads;
        }
        else
        {
            events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
                         .Select(r => r.Event).ToList();
            // Use session/visitor fallback so leads with missing direct UTMs are resolved correctly.
            leads = ResolveAndFilterLeads(allLeads, allEvents, trafficType);
        }

        var pageViews = events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"))
            .GroupBy(e => e.PageKey ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        var ctas = events.Where(IsCtaMetricEvent)
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

        return new PagePerformanceDto { Rows = rows, RangeLabel = range.Label, TrafficType = trafficType };
    }

    public async Task<CtaPerformanceDto> GetCtaPerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        // Load ALL events for proper session attribution (same reason as GetConversionsAsync).
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var attributedRows = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType);
        var events = attributedRows
            .Where(r => IsCtaMetricEvent(r.Event))
            .Select(r => r.Event)
            .ToList();
        var leads = trafficType == TrafficType.All
            ? allLeads
            : ResolveAndFilterLeads(allLeads, allEvents, trafficType);

        var rows = events
            .GroupBy(e => new { Page = e.PageKey ?? "unknown", Cta = e.ElementKey ?? "unknown" })
            .OrderByDescending(g => g.Count())
            .Select(g => new CtaPerformanceRow
            {
                PageKey = g.Key.Page,
                ElementKey = g.Key.Cta,
                Clicks = g.Count(),
                UniqueClickSessions = g.Where(x => !string.IsNullOrWhiteSpace(x.SessionId))
                    .Select(x => x.SessionId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                VerifiedLeads = leads.Count(l => (l.SourcePageKey ?? "unknown") == g.Key.Page && (l.SourceCtaKey ?? "unknown") == g.Key.Cta),
                ClickToLeadRate = null
            })
            .ToList()
            .Select(row =>
            {
                row.ClickToLeadRate = row.UniqueClickSessions > 0
                    ? Math.Round((decimal)row.VerifiedLeads / row.UniqueClickSessions * 100, 2)
                    : null;
                return row;
            })
            .ToList();

        return new CtaPerformanceDto { Rows = rows, RangeLabel = range.Label, TrafficType = trafficType };
    }

    public async Task<QuoteFunnelDto> GetQuoteFunnelAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var allAttributedRows = BuildAttributedEventRows(allEvents);
        var attributedRows = FilterAttributedRowsByTraffic(allAttributedRows, trafficType);
        var events = attributedRows.Select(r => r.Event).ToList();
        var leads = trafficType == TrafficType.All
            ? allLeads
            : ResolveAndFilterLeads(allLeads, allEvents, trafficType);

        var quoteEntryViewKeys = BuildDistinctUnitKeySet(events, IsQuoteEntryViewEvent, BuildQuoteStageUnitKey);
        var primaryCtaSeenKeys = BuildDistinctUnitKeySet(events, IsQuotePrimaryCtaExposureEvent, BuildQuoteStageUnitKey);
        var ctaStartKeys = BuildDistinctUnitKeySet(events, IsQuoteCtaIntentEvent, BuildQuoteStageUnitKey);
        var explicitEntryEngagedKeys = BuildDistinctUnitKeySet(
            events,
            e => IsQuoteScopeEvent(e) && string.Equals(e.EventType, "quote_entry_engaged", StringComparison.OrdinalIgnoreCase),
            BuildQuoteStageUnitKey);
        var explicitFormStartKeys = BuildDistinctUnitKeySet(events, IsQuoteExplicitFormStartEvent, BuildQuoteStageUnitKey);
        var discoveryStepKeys = BuildDistinctUnitKeySet(events, IsQuoteDiscoveryStepCompleteEvent, BuildQuoteStageUnitKey);
        var explicitContactStepKeys = BuildDistinctUnitKeySet(events, IsQuoteContactStepReachedEvent, BuildQuoteStageUnitKey);
        var submitAttemptKeys = BuildDistinctUnitKeySet(events, IsQuoteSubmitAttempt, BuildQuoteStageUnitKey);
        var leadConfirmedKeys = SelectCanonicalSuccessEvents(events)
            .Select(BuildQuoteStageUnitKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var quoteEntryEngagedKeys = UnionUnitKeySets(
            explicitEntryEngagedKeys,
            ctaStartKeys,
            explicitFormStartKeys,
            discoveryStepKeys,
            explicitContactStepKeys,
            submitAttemptKeys);
        var formStartedKeys = UnionUnitKeySets(
            explicitFormStartKeys,
            discoveryStepKeys,
            explicitContactStepKeys,
            submitAttemptKeys);
        var contactStepKeys = UnionUnitKeySets(
            explicitContactStepKeys,
            submitAttemptKeys);

        var startBucketCounts = CountDistinctUnitsByReportingBucket(
            attributedRows,
            e => IsQuoteEntryEngagedSignalEvent(e) || IsQuoteSubmitAttempt(e),
            BuildQuoteStageUnitKey);
        var directFormStartCount = formStartedKeys.Count - formStartedKeys.Intersect(ctaStartKeys, StringComparer.OrdinalIgnoreCase).Count();

        int starts = quoteEntryEngagedKeys.Count;
        int formStarts = formStartedKeys.Count;
        int submitAttempts = submitAttemptKeys.Count;
        int formSubmits = leadConfirmedKeys.Count;

        var byType = events
            .Select(e => new
            {
                QuoteType = ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey),
                UnitKey = BuildQuoteStageUnitKey(e)
            })
            .Where(x => quoteEntryEngagedKeys.Contains(x.UnitKey) && !string.IsNullOrWhiteSpace(x.QuoteType))
            .GroupBy(x => x.QuoteType!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyCountDto
            {
                Key = g.Key,
                Count = g.Select(x => x.UnitKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var stageMetrics = new List<QuoteStageMetricRow>
        {
            new() { StageKey = "quote_entry_views", Label = "Quote Entry Views", Count = quoteEntryViewKeys.Count },
            new() { StageKey = "primary_cta_seen", Label = "Primary CTA Seen", Count = primaryCtaSeenKeys.Count },
            new() { StageKey = "quote_entry_engaged", Label = "Quote Entry Engaged", Count = starts },
            new() { StageKey = "form_started", Label = "Form Started", Count = formStarts },
            new() { StageKey = "discovery_steps_completed", Label = "Discovery Steps Completed", Count = discoveryStepKeys.Count },
            new() { StageKey = "contact_step_viewed", Label = "Contact Step Viewed", Count = contactStepKeys.Count },
            new() { StageKey = "submit_attempted", Label = "Submit Attempted", Count = submitAttempts },
            new() { StageKey = "lead_confirmed", Label = "Lead Confirmed", Count = formSubmits }
        }.Where(x => x.Count > 0).ToList();

        return new QuoteFunnelDto
        {
            QuoteStarts = starts,
            QuoteFormStarts = formStarts,
            QuoteSubmitAttempts = submitAttempts,
            QuoteFormSubmits = formSubmits,
            CtaStartCount = ctaStartKeys.Count,
            DirectFormStartCount = Math.Max(0, directFormStartCount),
            ByQuoteType = byType,
            StageMetrics = stageMetrics,
            RangeLabel = range.Label,
            TrafficType = trafficType,
            PaidStartCount = CountByReportingBucket(startBucketCounts, TrafficType.PaidAds),
            NonPaidStartCount = CountByReportingBucket(startBucketCounts, TrafficType.NonPaid),
            UnknownStartCount = CountByReportingBucket(startBucketCounts, TrafficType.Unknown),
            DropOffStartsToFormStarts = starts > 0
                ? Math.Round(ClampPercent((decimal)(starts - formStarts) / starts * 100), 2)
                : null,
            DropOffFormStartsToSubmits = formStarts > 0
                ? Math.Round(ClampPercent((decimal)(formStarts - formSubmits) / formStarts * 100), 2)
                : null
        };
    }

    public async Task<MarketingHealthDto> GetMarketingHealthAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var attributedRows = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType);
        var events = attributedRows.Select(r => r.Event).ToList();
        var leads = trafficType == TrafficType.All
            ? allLeads
            : ResolveAndFilterLeads(allLeads, allEvents, trafficType);

        var clientTrackingErrorEvents = events
            .Where(e => string.Equals(e.EventType, "client_tracking_error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var recentTrackingErrorEvents = clientTrackingErrorEvents
            .OrderByDescending(e => e.EventUtc)
            .Take(MarketingHealthRecentTrackingErrorLimit)
            .ToList();

        var recentTrackingSessionIds = recentTrackingErrorEvents
            .Select(e =>
            {
                var metadata = ReadTrackingErrorMetadata(e.MetadataJson);
                return FirstNonBlank(e.SessionId, metadata.SessionId);
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var recentTrackingVisitorIds = recentTrackingErrorEvents
            .Select(e =>
            {
                var metadata = ReadTrackingErrorMetadata(e.MetadataJson);
                return FirstNonBlank(e.VisitorId, metadata.VisitorId);
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<WebsiteLead> recentTrackingLinkedLeads = new();
        if (recentTrackingSessionIds.Length > 0 || recentTrackingVisitorIds.Length > 0)
        {
            recentTrackingLinkedLeads = await _db.WebsiteLeads.AsNoTracking()
                .Where(l => !l.IsDeleted)
                .Where(ScopePredicateLeads(scope, scopedAgentIds))
                .Where(QualityPredicateLeads(range.QualityMode))
                .Where(l =>
                    (!string.IsNullOrWhiteSpace(l.SessionId) && recentTrackingSessionIds.Contains(l.SessionId!)) ||
                    (!string.IsNullOrWhiteSpace(l.VisitorId) && recentTrackingVisitorIds.Contains(l.VisitorId!)))
                .OrderBy(l => l.CreatedUtc)
                .ToListAsync();
        }

        var recentTrackingErrors = recentTrackingErrorEvents
            .GroupBy(e =>
            {
                var metadata = ReadTrackingErrorMetadata(e.MetadataJson);

                var visitorKey = FirstNonBlank(e.VisitorId, metadata.VisitorId);
                var sessionKey = FirstNonBlank(e.SessionId, metadata.SessionId);

                var identityKey = !string.IsNullOrWhiteSpace(visitorKey)
                    ? $"vid:{visitorKey}"
                    : !string.IsNullOrWhiteSpace(sessionKey)
                        ? $"sid:{sessionKey}"
                        : $"eid:{e.EventId:D}";

                var normalizedError =
                    FirstNonBlank(
                        metadata.ErrorMessage,
                        metadata.AttemptedEventName,
                        "tracking_error"
                    )!
                    .Trim()
                    .ToLowerInvariant();

                if (normalizedError.Contains("failed to fetch"))
                    normalizedError = "failed_to_fetch";

                if (normalizedError.Contains("network"))
                    normalizedError = "network_error";

                if (normalizedError.Contains("load failed"))
                    normalizedError = "resource_load_failure";

                return $"{identityKey}|{normalizedError}";
            })
            .Select(g =>
            {
                var newest = g
                    .OrderByDescending(x => x.EventUtc)
                    .First();

                var dto = BuildTrackingErrorDetail(
                    newest,
                    events,
                    recentTrackingLinkedLeads,
                    range.ViewerTimeZone
                );

                if (g.Count() > 1)
                {
                    var pageCount = g
                        .Select(x => FirstNonBlank(x.PageKey, x.Path, "unknown-page"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    var sessionCount = g
                        .Select(x =>
                        {
                            var metadata = ReadTrackingErrorMetadata(x.MetadataJson);
                            return FirstNonBlank(x.SessionId, metadata.SessionId);
                        })
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    dto.ErrorMessage =
                        $"{dto.ErrorMessage} ({g.Count()} related events · {sessionCount} sessions · {pageCount} pages)";
                }

                return dto;
            })
            .OrderByDescending(x => x.EventUtc)
            .ToList();

        var startUnits = events
            .Where(IsQuoteExplicitStartSignalEvent)
            .Select(BuildInteractionUnitKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var laterProgressUnits = events
            .Where(e =>
                IsQuoteRecommendationViewedEvent(e) ||
                IsQuoteContactStepReachedEvent(e) ||
                IsQuoteSubmitAttempt(e) ||
                IsQuoteSuccessEvent(e))
            .Select(BuildInteractionUnitKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inferredStartUnits = laterProgressUnits
            .Where(key => !startUnits.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var leadPersistedEvents = events
            .Where(e =>
                string.Equals(e.EventType, "lead_persisted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.EventType, "website_lead_submitted", StringComparison.OrdinalIgnoreCase) ||
                IsSubmitSuccessMetricEvent(e))
            .GroupBy(e => !string.IsNullOrWhiteSpace(e.SessionId)
                ? $"sid:{e.SessionId}"
                : $"vid:{e.VisitorId}|{e.EventUtc:O}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(e => string.Equals(e.EventType, "lead_persisted", StringComparison.OrdinalIgnoreCase) ? 3 :
                    string.Equals(e.EventType, "website_lead_submitted", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
                .ThenByDescending(e => e.EventUtc)
                .First())
            .ToList();
        var workstationAttemptEvents = events
            .Where(e => string.Equals(e.EventType, "workstation_capture_attempt", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var workstationSuccessEvents = events
            .Where(e => string.Equals(e.EventType, "workstation_capture_success", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var workstationFailureEvents = events
            .Where(e => string.Equals(e.EventType, "workstation_capture_failure", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sessionAttributionMap = BuildSessionAttributionMap(allEvents);
        var visitorAttributionMap = BuildVisitorAttributionMap(allEvents);

        var sessionTrafficRows = attributedRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Event.SessionId))
            .GroupBy(r => r.Event.SessionId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(r => r.Event.EventUtc)
                .First())
            .ToList();

        var unknownAttributedLeads = leads.Count(l =>
        {
            var resolved = ResolveLeadAttribution(
                l,
                sessionAttributionMap,
                visitorAttributionMap);
            return Classify(resolved.Attribution) == TrafficType.Unknown;
        });

        var warnings = new List<string>();
        if (clientTrackingErrorEvents.Count > 0)
        {
            var mostRecentTrackingError = recentTrackingErrors.FirstOrDefault();
            warnings.Add(BuildTrackingErrorWarningSummary(clientTrackingErrorEvents.Count, mostRecentTrackingError));

            var mostActionableTrackingError = recentTrackingErrors
                .OrderByDescending(detail =>
                    string.Equals(detail.Severity, "Critical", StringComparison.OrdinalIgnoreCase) ? 4 :
                    string.Equals(detail.Severity, "High", StringComparison.OrdinalIgnoreCase) ? 3 :
                    string.Equals(detail.Severity, "Medium", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
                .ThenByDescending(detail => detail.EventUtc)
                .FirstOrDefault();

            if (mostActionableTrackingError != null)
                warnings.Add(BuildTrackingErrorActionWarning(mostActionableTrackingError));

            var recoveredCritical = recentTrackingErrors.FirstOrDefault(detail =>
                detail.Recovered == true &&
                (string.Equals(detail.Severity, "Medium", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(detail.Severity, "High", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(detail.Severity, "Critical", StringComparison.OrdinalIgnoreCase)));
            if (recoveredCritical != null &&
                !string.Equals(recoveredCritical.AttemptedEventName, "page_engaged_10s", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Critical event retry recovered successfully for {recoveredCritical.AttemptedEventName}.");
            }

            var unrecoveredCritical = recentTrackingErrors.FirstOrDefault(detail =>
                detail.Recovered == false &&
                (string.Equals(detail.Severity, "High", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(detail.Severity, "Critical", StringComparison.OrdinalIgnoreCase)));
            if (unrecoveredCritical != null)
                warnings.Add($"Critical event failed after retries for {unrecoveredCritical.AttemptedEventName}. Funnel data may be incomplete.");
        }
        if (inferredStartUnits.Count > 0)
            warnings.Add($"Missing funnel start events suspected for {inferredStartUnits.Count} sessions.");
        if (workstationFailureEvents.Count > 0)
            warnings.Add($"Workstation capture failures detected: {workstationFailureEvents.Count}.");
        var noOwnerFailures = workstationFailureEvents.Count(e =>
            string.Equals(ReadMetadataStringValue(e.MetadataJson, "Reason"), "NoAgentOwner", StringComparison.OrdinalIgnoreCase));
        if (noOwnerFailures > 0)
            warnings.Add($"Workstation capture had {noOwnerFailures} no-owner failures.");
        if (unknownAttributedLeads > 0)
            warnings.Add($"Unknown lead attribution remains on {unknownAttributedLeads} leads.");

        return new MarketingHealthDto
        {
            ClientTrackingErrors = clientTrackingErrorEvents.Count,
            ClientTrackingErrorSessions = clientTrackingErrorEvents
                .Select(BuildSessionOrVisitorKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            InferredFormStarts = inferredStartUnits.Count,
            MissingStartEventSessions = inferredStartUnits.Count,
            LeadPersistedEvents = leadPersistedEvents
                .Select(BuildPipelineUnitKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            WorkstationCaptureAttempts = workstationAttemptEvents
                .Select(BuildPipelineUnitKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            WorkstationCaptureSuccesses = workstationSuccessEvents
                .Select(BuildPipelineUnitKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            WorkstationCaptureFailures = workstationFailureEvents
                .Select(BuildPipelineUnitKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            WorkstationNoOwnerFailures = noOwnerFailures,
            UnknownAttributedLeads = unknownAttributedLeads,
            InternalTrafficSessions = sessionTrafficRows.Count(r => r.TrafficType == TrafficType.Internal),
            TestTrafficSessions = sessionTrafficRows.Count(r => r.TrafficType == TrafficType.Test),
            BotSuspiciousSessions = sessionTrafficRows.Count(r => r.TrafficType == TrafficType.BotSuspicious),
            Warnings = warnings,
            RecentTrackingErrors = recentTrackingErrors,
            RangeLabel = range.Label,
            TrafficType = trafficType
        };
    }

    public async Task<ConversionCenterDto> GetConversionsAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All, int recentTake = 100)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var leads = trafficType == TrafficType.All
            ? allLeads
            : ResolveAndFilterLeads(allLeads, allEvents, trafficType);
        var safeRecentTake = recentTake > 0 ? recentTake : 100;

        var sessionMap = BuildSessionAttributionMap(allEvents);
        var visitorMap = BuildVisitorAttributionMap(allEvents);

        var recentEvents = leads
            .OrderByDescending(l => l.CreatedUtc)
            .Take(safeRecentTake)
            .Select(l =>
            {
                var resolved = ResolveLeadAttribution(l, sessionMap, visitorMap);
                var classified = Classify(resolved.Attribution);

                return new ConversionRow
                {
                    EventType = "Server-confirmed lead",
                    PageKey = l.SourcePageKey,
                    SourceCta = l.SourceCtaKey,
                    EventUtc = l.CreatedUtc,
                    QuoteType = NormalizeQuoteTypeToken(l.InterestType),
                    SourceLabel = SourceBucketLabel(resolved.Attribution, classified)
                };
            })
            .ToList();

        var dto = new ConversionCenterDto
        {
            TotalConversions = leads.Count,
            Recent = recentEvents,
            RangeLabel = range.Label,
            TrafficType = trafficType
        };
        return dto;
    }

    public async Task<LeadSnapshotDto> GetLeadsAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All, int take = 200)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allLeads = await BaseLeads(range, scope, scopedAgentIds)
            .OrderByDescending(l => l.CreatedUtc)
            .ToListAsync();
        var contextEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var sessionMap = BuildSessionAttributionMap(contextEvents);
        var visitorMap = BuildVisitorAttributionMap(contextEvents);
        var sessionEventMap = BuildSessionEventMap(contextEvents);
        var visitorEventMap = BuildVisitorEventMap(contextEvents);

        List<WebsiteLead> filteredLeads;
        if (trafficType == TrafficType.All)
        {
            filteredLeads = allLeads;
        }
        else
        {
            filteredLeads = ResolveAndFilterLeads(allLeads, contextEvents, trafficType);
        }

        var total = filteredLeads.Count;
        var leads = filteredLeads.Take(take).ToList();

        var dto = new LeadSnapshotDto
        {
            Total = total,
            ReturnedCount = leads.Count,
            IsTruncated = total > take,
            RangeLabel = range.Label,
            TrafficType = trafficType,
            Leads = leads.Select(l =>
            {
                var resolved = ResolveLeadAttribution(l, sessionMap, visitorMap);
                var classified = Classify(resolved.Attribution);
                var metadata = SnapshotFromLeadMetadata(l);
                var behavior = ResolveLeadBehaviorSnapshot(l, sessionEventMap, visitorEventMap);
                var metaTracking = metadata.MetaTracking;
                var isMetaAttributedPaid = IsMetaAttributedPaid(resolved.Attribution);
                var isPaidLandingExperience = string.Equals(metadata.PageMode, "paid_landing", StringComparison.OrdinalIgnoreCase);
                var browserPixelSent = string.Equals(metaTracking?.BrowserPixelStatus, "sent", StringComparison.OrdinalIgnoreCase);
                var serverCapiSent = string.Equals(metaTracking?.ServerCapiStatus, "sent", StringComparison.OrdinalIgnoreCase);
                var sentToMeta = browserPixelSent || serverCapiSent;
                var metaLearningReason = ResolveMetaLearningReason(sentToMeta, resolved.Attribution, classified);
                var excludedFromMetaLearning = !sentToMeta;
                var resolvedUtmId = resolved.Attribution.UtmId;
                var resolvedMetaCampaignId = resolved.Attribution.MetaCampaignId;
                var resolvedMetaAdSetId = resolved.Attribution.MetaAdSetId;
                var resolvedMetaAdId = resolved.Attribution.MetaAdId;
                var leadUtmId = NormalizeAttributionToken(l.UtmId) ?? metadata.UtmId;
                var leadMetaCampaignId = NormalizeAttributionToken(l.MetaCampaignId) ?? metadata.MetaCampaignId;
                var leadMetaAdSetId = NormalizeAttributionToken(l.MetaAdSetId) ?? metadata.MetaAdSetId;
                var leadMetaAdId = NormalizeAttributionToken(l.MetaAdId) ?? metadata.MetaAdId;
                var leadSessionId = FirstNonBlank(l.SessionId);
                var leadVisitorId = FirstNonBlank(l.VisitorId);
                return new LeadSnapshotRow
                {
                    LeadId      = l.LeadId,
                    CreatedUtc  = l.CreatedUtc,
                    Name        = $"{l.FirstName} {l.LastName}".Trim(),
                    Email       = l.Email,
                    Phone       = l.Phone,
                    Interest    = l.InterestType,
                    LeadSource  = $"{l.SourcePageKey}/{l.SourceCtaKey}".Trim('/'),
                    SessionId   = leadSessionId,
                    VisitorId   = leadVisitorId,
                    DeviceType  = behavior.DeviceType,
                    Browser     = behavior.Browser,
                    OperatingSystem = behavior.OperatingSystem,
                    ScrollPercent = behavior.ScrollPercent,
                    HumanInteractionCount = behavior.HumanInteractionCount,
                    UtmSource   = l.UtmSource,
                    UtmMedium   = l.UtmMedium,
                    UtmCampaign = l.UtmCampaign,
                    UtmId       = leadUtmId,
                    Fbclid      = NormalizeAttributionToken(l.Fbclid),
                    MetaCampaignId = leadMetaCampaignId,
                    MetaAdSetId = leadMetaAdSetId,
                    MetaAdId    = leadMetaAdId,
                    ResolvedSource = SourceBucketLabel(resolved.Attribution, classified),
                    ResolvedMedium = resolved.Attribution.UtmMedium,
                    ResolvedCampaign = CampaignBucketLabel(resolved.Attribution),
                    ResolvedUtmId = resolvedUtmId,
                    ResolvedContent = resolved.Attribution.UtmContent,
                    ResolvedTerm = resolved.Attribution.UtmTerm,
                    ResolvedFbclidPresent = !string.IsNullOrWhiteSpace(resolved.Attribution.Fbclid),
                    ResolvedMetaCampaignId = resolvedMetaCampaignId,
                    ResolvedMetaAdSetId = resolvedMetaAdSetId,
                    ResolvedMetaAdId = resolvedMetaAdId,
                    SourcePage  = l.SourcePageKey,
                    LandingPage = l.SourcePageKey,
                    TrafficType = classified,
                    Attribution = new LeadAttributionDto
                    {
                        IsPaid    = classified == TrafficType.PaidAds,
                        IsNonPaid = classified == TrafficType.Direct || classified == TrafficType.Organic || classified == TrafficType.Referral,
                        TrafficType = classified,
                        ResolutionSource = resolved.ResolutionSource,
                        IsMetaAttributedPaid = isMetaAttributedPaid,
                        ExcludedFromMetaLearningReadiness = excludedFromMetaLearning,
                        MetaLearningReason = metaLearningReason
                    },
                    MetaTracking = metaTracking == null
                        ? null
                        : new MetaLeadTrackingDto
                        {
                            MetaLeadEventId = metaTracking.EventId,
                            ResolvedMetaPixelId = metaTracking.ResolvedMetaPixelId,
                            PixelOwnerType = metaTracking.PixelOwnerType,
                            BrowserPixelStatus = metaTracking.BrowserPixelStatus,
                            ServerCapiStatus = metaTracking.ServerCapiStatus,
                            BrowserPixelSent = browserPixelSent,
                            ServerCapiSent = serverCapiSent,
                            DedupReady = browserPixelSent && serverCapiSent && !string.IsNullOrWhiteSpace(metaTracking.EventId)
                        }
                };
            }).ToList()
        };
        return dto;
    }

    public async Task<AgentPerformanceDto> GetAgentPerformanceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All, AnalyticsQueryOptions? options = null)
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

        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);

        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var attributedRows = BuildAttributedEventRows(allEvents);
        var events = allEvents;
        var leads = allLeads;

        if (trafficType != TrafficType.All)
        {
            attributedRows = FilterAttributedRowsByTraffic(attributedRows, trafficType);
            events = attributedRows.Select(r => r.Event).ToList();
            leads = ResolveAndFilterLeads(allLeads, allEvents, trafficType);
        }

        var attributedSessionRows = BuildSessionAttributionRows(attributedRows);
        var topAttributionRows = attributedSessionRows.Count > 0 ? attributedSessionRows : attributedRows;

        var profiles = await _db.AgentTrackingProfiles.AsNoTracking().ToListAsync();
        var profileToGroupKey = profiles.ToDictionary(
            p => p.Id,
            p =>
            {
                if (!string.IsNullOrWhiteSpace(p.AgentUserId)) return $"oid:{p.AgentUserId.Trim().ToLowerInvariant()}";
                if (!string.IsNullOrWhiteSpace(p.AgentUpn)) return $"upn:{p.AgentUpn.Trim().ToLowerInvariant()}";
                if (!string.IsNullOrWhiteSpace(p.Slug)) return $"slug:{p.Slug.Trim().ToLowerInvariant()}";
                return $"id:{p.Id:D}";
            });
        var profileGroups = profiles
            .GroupBy(p => profileToGroupKey[p.Id], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        string? GroupKey(Guid? profileId) =>
            profileId.HasValue && profileToGroupKey.TryGetValue(profileId.Value, out var key) ? key : null;

        var sessionsByAgent = events
            .Where(e => e.AgentTrackingProfileId != null && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => GroupKey(e.AgentTrackingProfileId), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key!, g => g.Select(x => x.SessionId!).Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);

        var intentsByAgent = events
            .Where(e => e.AgentTrackingProfileId != null)
            .Select(e => new { GroupKey = GroupKey(e.AgentTrackingProfileId), Event = e })
            .Where(x => !string.IsNullOrWhiteSpace(x.GroupKey))
            .GroupBy(x => x.GroupKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => BuildQuoteEntryEngagedUnitKeys(g.Select(x => x.Event)).Count,
                StringComparer.OrdinalIgnoreCase);

        var leadsByAgent = leads
            .Where(l => l.AgentTrackingProfileId != null)
            .GroupBy(l => GroupKey(l.AgentTrackingProfileId), StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key!, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var topSourceByAgent = topAttributionRows
            .Where(r => r.Event.AgentTrackingProfileId != null)
            .Select(r => new { GroupKey = GroupKey(r.Event.AgentTrackingProfileId), Source = SourceBucketLabel(r) })
            .Where(x => !string.IsNullOrWhiteSpace(x.GroupKey))
            .GroupBy(x => new { x.GroupKey, x.Source })
            .Select(g => new { g.Key.GroupKey, g.Key.Source, Count = g.Count() })
            .GroupBy(x => x.GroupKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Count).ThenBy(x => x.Source).First().Source, StringComparer.OrdinalIgnoreCase);

        var rows = new List<AgentPerformanceRow>();
        foreach (var (groupKey, groupedProfiles) in profileGroups)
        {
            var representative = groupedProfiles
                .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.DisplayName))
                .ThenByDescending(p => p.UpdatedUtc)
                .First();

            var leadsCount = leadsByAgent.TryGetValue(groupKey, out var lc) ? lc : 0;
            var convCount = leadsCount;
            var sessions = sessionsByAgent.TryGetValue(groupKey, out var sc) ? sc : 0;
            var intents = intentsByAgent.TryGetValue(groupKey, out var ic) ? ic : 0;

            var sessionConv = sessions > 0 ? Math.Round((decimal)convCount / sessions * 100, 2) : 0;
            var intentConv = intents > 0 ? Math.Round((decimal)convCount / intents * 100, 2) : 0;
            var lowSample = (sessions > 0 && sessions < LowSampleThreshold) || (intents > 0 && intents < LowSampleThreshold);
            rows.Add(new AgentPerformanceRow
            {
                AgentTrackingProfileId = representative.Id,
                AgentName = representative.DisplayName ?? representative.AgentUpn ?? representative.Slug,
                Slug = representative.Slug,
                Leads = leadsCount,
                Conversions = convCount,
                Sessions = sessions,
                SessionConversionRate = sessionConv,
                IntentConversionRate = intentConv,
                TopSource = topSourceByAgent.TryGetValue(groupKey, out var ts) ? ts : null,
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

        var pvs = events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view")).ToList();
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

        var pvList = events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view")).ToList();
        var pageKeys = pvList.Select(e => e.PageKey ?? "unknown").Distinct().ToList();

        var ctaByPage = events.Where(IsCtaMetricEvent)
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

    public async Task<TimeOnPageDto> GetTimeOnPageAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var filteredEvents = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
            .Select(r => r.Event)
            .ToList();
        // page_exit carries the real elapsed dwell per page visit; page_view.DwellMilliseconds is always 0 at load.
        var events = filteredEvents
            .Where(e => e.EventType == "page_exit" && e.DwellMilliseconds != null)
            .ToList();
        // Views count = distinct page_view events per page (for denominator context).
        var pvEvents = filteredEvents
            .Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"))
            .ToList();

        var pvCounts = pvEvents
            .GroupBy(e => e.PageKey ?? "unknown")
            .Select(g => new { PageKey = g.Key, Count = g.Count() })
            .ToList();
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

    public async Task<ExitAnalysisDto> GetExitAnalysisAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var filteredEvents = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
            .Select(r => r.Event)
            .ToList();
        // Load page_view for view counts and page_exit for exit/dwell signals.
        var pageViewEvents = filteredEvents.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view")).ToList();
        var pageExitEvents = filteredEvents.Where(e => e.EventType == "page_exit").ToList();
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
        var pvByPage = events.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view"))
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

    public async Task<JourneyAnalysisDto> GetJourneyAnalysisAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var filteredEvents = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
            .Select(r => r.Event)
            .ToList();
        var events = filteredEvents.Where(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view")).ToList();
        if (trafficType != TrafficType.All)
            leads = ResolveAndFilterLeads(leads, allEvents, trafficType);
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
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var allLeads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var attributedRows = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType);
        var filteredEvents = attributedRows.Select(r => r.Event).ToList();
        var pageViewRows = attributedRows
            .Where(r => r.Event.EventType == "page_view")
            .ToList();
        var leads = trafficType == TrafficType.All
            ? allLeads
            : ResolveAndFilterLeads(allLeads, allEvents, trafficType);
        var leadsBySid = leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId))
            .GroupBy(l => l.SessionId!).ToDictionary(g => g.Key, g => g.Count());
        var engagementSignals = filteredEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId) &&
                        (e.EventType == "page_engaged_30s" ||
                         e.EventType == "page_engaged_60s" ||
                         e.EngagedMilliseconds.HasValue))
            .Select(e => new { e.SessionId, e.EventType, e.EngagedMilliseconds })
            .ToList();
        var engagedBySid = engagementSignals
            .GroupBy(e => e.SessionId!)
            .ToDictionary(
                g => g.Key,
                g => g.Any(x => x.EventType == "page_engaged_30s" || x.EventType == "page_engaged_60s") ||
                     g.Max(x => x.EngagedMilliseconds ?? 0) >= 30_000);
        // Sum page_exit dwell times per session = total time on site for that session.
        var totalDwellBySid = filteredEvents
            .Where(e => e.EventType == "page_exit" && e.DwellMilliseconds != null && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .ToDictionary(g => g.Key, g => (double)g.Sum(x => x.DwellMilliseconds!.Value));
        var sessionFirst = pageViewRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Event.SessionId))
            .GroupBy(r => r.Event.SessionId!)
            .Select(g => g.OrderBy(x => x.Event.EventUtc).First())
            .ToList();
        var rows = sessionFirst
            .GroupBy(r => new
            {
                Source = SourceBucketLabel(r),
                Medium = r.Attribution.UtmMedium?.Trim() ?? (string?)null,
                Campaign = r.Attribution.UtmCampaign?.Trim() ?? (string?)null,
                LandingPage = r.Event.PageKey ?? "unknown"
            })
            .Select(g =>
            {
                var sids = g.Select(r => r.Event.SessionId!).ToList();
                var sessions = sids.Count;
                var engaged = sids.Count(sid => engagedBySid.TryGetValue(sid, out var v) && v);
                var lCount = sids.Sum(sid => leadsBySid.TryGetValue(sid, out var c) ? c : 0);
                var dwellSamples = sids
                    .Where(sid => totalDwellBySid.ContainsKey(sid))
                    .Select(sid => totalDwellBySid[sid])
                    .ToList();
                double? avgDwell = dwellSamples.Count > 0
                    ? Math.Round(RobustAverage(dwellSamples, SessionDurationHardCapMs), 0)
                    : null;
                var lpLeads = leads.Count(l => l.SourcePageKey == g.Key.LandingPage && !string.IsNullOrWhiteSpace(l.SessionId) && sids.Contains(l.SessionId!));
                return new SourcePerformanceRow
                {
                    Source = g.Key.Source, Medium = g.Key.Medium, Campaign = g.Key.Campaign, LandingPage = g.Key.LandingPage,
                    Sessions = sessions, EngagedSessions = engaged, VerifiedLeads = lCount,
                    SessionConversionRate = sessions > 0 ? Math.Round((decimal)lCount / sessions * 100, 2) : 0,
                    LandingPageConversionRate = sessions > 0 ? Math.Round((decimal)lpLeads / sessions * 100, 2) : 0,
                    AvgDwellMs = avgDwell,
                    AvgDwellSampleCount = dwellSamples.Count
                };
            }).OrderByDescending(r => r.Sessions).Take(50).ToList();
        return new SourcePerformanceDto { Rows = rows, RangeLabel = range.Label, TrafficType = trafficType };
    }

    public async Task<LandingPagePerformanceDto> GetLandingPagePerformanceAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var pageViewRows = BuildAttributedEventRows(allEvents)
            .Where(r => r.Event.EventType == "page_view")
            .ToList();
        var leads = await BaseLeads(range, scope, scopedAgentIds).ToListAsync();
        var leadsBySid = leads.Where(l => !string.IsNullOrWhiteSpace(l.SessionId))
            .GroupBy(l => l.SessionId!).ToDictionary(g => g.Key, g => g.Count());
        var engagementSignals = allEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId) &&
                        (e.EventType == "page_engaged_30s" ||
                         e.EventType == "page_engaged_60s" ||
                         e.EngagedMilliseconds.HasValue))
            .Select(e => new { e.SessionId, e.EventType, e.EngagedMilliseconds })
            .ToList();
        var engagedBySid = engagementSignals
            .GroupBy(e => e.SessionId!)
            .ToDictionary(
                g => g.Key,
                g => g.Any(x => x.EventType == "page_engaged_30s" || x.EventType == "page_engaged_60s") ||
                     g.Max(x => x.EngagedMilliseconds ?? 0) >= 30_000);
        // Sum page_exit dwell times per session = total time on site for that session.
        var totalDwellBySid = allEvents
            .Where(e => e.EventType == "page_exit" && e.DwellMilliseconds != null && !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId!)
            .ToDictionary(g => g.Key, g => (double)g.Sum(x => x.DwellMilliseconds!.Value));
        var rows = pageViewRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Event.SessionId))
            .GroupBy(r => r.Event.SessionId!).Select(g => g.OrderBy(x => x.Event.EventUtc).First())
            .GroupBy(r => r.Event.PageKey ?? "unknown")
            .Select(g =>
            {
                var sids = g.Select(r => r.Event.SessionId!).ToList();
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
                    TopSource = g.GroupBy(SourceBucketLabel, StringComparer.OrdinalIgnoreCase).OrderByDescending(sg => sg.Count()).Select(sg => sg.Key).FirstOrDefault(),
                    TopCampaign = g.Where(r => !string.IsNullOrWhiteSpace(r.Attribution.UtmCampaign)).GroupBy(r => r.Attribution.UtmCampaign!).OrderByDescending(sg => sg.Count()).Select(sg => sg.Key).FirstOrDefault()
                };
            }).OrderByDescending(r => r.Sessions).ToList();
        return new LandingPagePerformanceDto { Rows = rows, RangeLabel = range.Label };
    }

    public async Task<FormFrictionDto> GetFormFrictionAsync(TimeRangeRequest range, ScopeContext scope)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var events = await BaseEvents(range, scope, scopedAgentIds)
            .Where(e => e.EventType == "form_start" ||
                        IsSubmitAttemptMetricEvent(e) ||
                        IsSubmitSuccessMetricEvent(e) ||
                        e.EventType == "form_submit" || // legacy read-only compatibility
                        e.EventType == "form_field_focus" ||
                        e.EventType == "form_field_complete" ||
                        e.EventType == "contact_field_focus" ||
                        e.EventType == "contact_field_complete" ||
                        e.EventType == "contact_progress_snapshot" ||
                        e.EventType == "form_field_error").ToListAsync();
        Func<AnalyticsEvent, string> fKey = e => e.FormKey ?? e.FormId ?? "unknown";
        var starts = events.Where(e => e.EventType == "form_start").GroupBy(fKey).ToDictionary(g => g.Key, g => g.Count());
        var submits = events
            .Where(e => IsSubmitSuccessMetricEvent(e))
            .GroupBy(fKey)
            .ToDictionary(g => g.Key, g => g.Count());
        var ffFocus = events.Where(e =>
                e.EventType == "form_field_focus" ||
                e.EventType == "contact_field_focus")
            .GroupBy(fKey)
            .ToDictionary(g => g.Key, g => g.Count());

        var ffComplete = events.Where(e =>
                e.EventType == "form_field_complete" ||
                e.EventType == "contact_field_complete")
            .GroupBy(fKey)
            .ToDictionary(g => g.Key, g => g.Count());

        var ffError = events.Where(e => e.EventType == "form_field_error")
            .GroupBy(fKey)
            .ToDictionary(g => g.Key, g => g.Count());
        var rows = starts.Keys.Select(fk =>
        {
            var startSids = events.Where(e => e.EventType == "form_start" && fKey(e) == fk && !string.IsNullOrWhiteSpace(e.SessionId))
                .Select(e => e.SessionId!).Distinct().ToHashSet();
            var submitSids = events
                .Where(e => IsSubmitSuccessMetricEvent(e) && fKey(e) == fk && !string.IsNullOrWhiteSpace(e.SessionId))
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
        var topCompletedFields = events.Where(e =>
                (e.EventType == "form_field_complete" ||
                 e.EventType == "contact_field_complete") &&
                !string.IsNullOrWhiteSpace(e.FieldName))
            .GroupBy(e => e.FieldName!)
            .Select(g => new
            {
                Field = g.Key,
                Count = g.Select(x => x.SessionId).Distinct().Count()
            })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToList();

        var topErrorFields = events.Where(e => e.EventType == "form_field_error" && !string.IsNullOrWhiteSpace(e.FieldName))
            .GroupBy(e => e.FieldName!).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new KeyCountDto { Key = g.Key, Count = g.Count() }).ToList();
        return new FormFrictionDto { Rows = rows, TopErrorFields = topErrorFields, RangeLabel = range.Label };
    }

    public async Task<FormAbandonmentDto> GetFormAbandonmentAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var events = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
            .Select(r => r.Event)
            .Where(IsQuoteScopeEvent)
            .ToList();

        var successfulSubmitKeys = SelectCanonicalSuccessEvents(events)
            .Select(BuildSuccessUnitKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var eventsBySuccessUnit = events
            .GroupBy(e => BuildSuccessUnitKey(e) ?? $"eid:{e.EventId:D}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.EventUtc).ThenBy(e => e.EventId).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var abandonEvents = events
            .Where(e => e.EventType == "form_abandon")
            .Where(e =>
            {
                var key = BuildSuccessUnitKey(e);
                return string.IsNullOrWhiteSpace(key) || !successfulSubmitKeys.Contains(key);
            })
            .GroupBy(e => BuildSuccessUnitKey(e) ?? $"eid:{e.EventId:D}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.EventUtc).First())
            .Where(e =>
            {
                var key = BuildSuccessUnitKey(e) ?? $"eid:{e.EventId:D}";
                if (!eventsBySuccessUnit.TryGetValue(key, out var groupedEvents))
                    return true;

                return !groupedEvents.Any(other =>
                    other.EventId != e.EventId &&
                    (other.EventUtc > e.EventUtc ||
                     (other.EventUtc == e.EventUtc && other.EventId != e.EventId)) &&
                    IsQuoteResumeAfterAbandonEvent(other));
            })
            .ToList();
        var abandonKeys = abandonEvents
            .Select(BuildSuccessUnitKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var startEvents = events
            .Where(IsQuoteFunnelStartSignalEvent)
            .GroupBy(e => BuildSuccessUnitKey(e) ?? $"eid:{e.EventId:D}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(e => e.EventUtc).First())
            .ToList();
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

                var resolvedQuoteType = ResolveQuoteTypeForReporting(quoteType, e.FormKey ?? e.FormId, e.PageKey) ?? "unknown";

                return new
                {
                    Event = e,
                    LastFocused = lastFocused,
                    LastCompleted = lastCompleted,
                    QuoteType = resolvedQuoteType,
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
            .GroupBy(e => ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey) ?? "unknown")
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
            ? "Some quote types have form_abandon events but no derived funnel-start denominator in-range; abandon rate is shown as — for those rows."
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
            .GroupBy(e => new
            {
                FieldName = e.FieldName!,
                QuoteType = ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey) ?? "unknown"
            })
            .Select(g => new ValidationFrictionRow { FieldName = g.Key.FieldName, QuoteType = g.Key.QuoteType, ErrorCount = g.Count() })
            .OrderByDescending(r => r.ErrorCount)
            .Take(15)
            .ToList();

        // Consent friction: abandons where consentInteracted = false and submitAttempted = true
        var consentFriction = parsed.Count(p => p.SubmitAttempted && !p.ConsentInteracted);

        var unitDiagnostics = events
            .GroupBy(BuildSuccessUnitKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .Select(g =>
            {
                var quoteType = g
                    .Select(e => ResolveQuoteTypeForReporting(e.QuoteType, e.FormKey ?? e.FormId, e.PageKey))
                    .FirstOrDefault(qt => !string.IsNullOrWhiteSpace(qt))
                    ?? "unknown";

                var lastExit = g
                    .Where(e => e.EventType == "page_exit" && IsQuoteFormKey(e.PageKey))
                    .OrderByDescending(e => e.EventUtc)
                    .FirstOrDefault();

                return new
                {
                    QuoteType = quoteType,
                    HasLandingView = g.Any(e => AnalyticsEventCatalog.MatchesDashboardMetric(e.EventType, "page_view") && IsQuoteFormKey(e.PageKey)),
                    HasPageExit = lastExit != null,
                    ExitDwellMs = (double)(lastExit?.DwellMilliseconds ?? 0),
                    ExitEngagedMs = (double)(lastExit?.EngagedMilliseconds ?? 0),
                    HadFunnelInteraction = g.Any(IsQuoteFunnelInteractionEvent),
                    HadFormAbandon = abandonKeys.Contains(g.Key),
                    HadContactStepView = g.Any(IsQuoteContactStepReachedEvent),
                    HadValidationError = g.Any(e => e.EventType == "form_field_error"),
                    HadLeadSuccess = successfulSubmitKeys.Contains(g.Key)
                };
            })
            .ToList();

        var bounceBeforeStartUnits = unitDiagnostics
            .Where(u => u.HasLandingView && u.HasPageExit && !u.HadFunnelInteraction && !u.HadLeadSuccess)
            .ToList();
        var bounceBeforeStartRows = bounceBeforeStartUnits
            .GroupBy(u => u.QuoteType, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BounceBeforeFunnelStartRow
            {
                QuoteType = g.Key,
                ExitCount = g.Count(),
                Engaged5sPlusCount = g.Count(x => (x.ExitEngagedMs > 0 ? x.ExitEngagedMs : x.ExitDwellMs) >= 5000),
                Engaged15sPlusCount = g.Count(x => (x.ExitEngagedMs > 0 ? x.ExitEngagedMs : x.ExitDwellMs) >= 15000),
                AvgDwellMs = Math.Round(g.Average(x => x.ExitDwellMs), 0)
            })
            .OrderByDescending(r => r.ExitCount)
            .ToList();

        var funnelAbandonCount = unitDiagnostics.Count(u => u.HadFormAbandon && !u.HadContactStepView);
        var contactStepAbandonCount = unitDiagnostics.Count(u => u.HadFormAbandon && u.HadContactStepView);
        var validationFrictionAbandonCount = unitDiagnostics.Count(u => u.HadFormAbandon && u.HadValidationError);

        if (summary.Count == 0 && bounceBeforeStartRows.Count > 0)
        {
            const string noAbandonMessage = "No explicit form_abandon events were recorded in this slice. These abandonment tables only populate after a quote funnel starts and the visitor later exits without a confirmed lead.";
            dataQualityNote = string.IsNullOrWhiteSpace(dataQualityNote)
                ? noAbandonMessage
                : $"{dataQualityNote} {noAbandonMessage}";
        }

        if (summary.Count > 0)
        {
            const string resumeSuppressionNote = "Refresh/resume cases that later continued the same quote session are excluded from explicit form_abandon totals.";
            dataQualityNote = string.IsNullOrWhiteSpace(dataQualityNote)
                ? resumeSuppressionNote
                : $"{dataQualityNote} {resumeSuppressionNote}";
        }

        return new FormAbandonmentDto
        {
            Summary = summary,
            BounceBeforeFunnelStart = bounceBeforeStartRows,
            TopAbandonedFields = topAbandoned,
            TopLastCompletedFields = topLastCompleted,
            ValidationFriction = validationFriction,
            BounceBeforeFunnelStartCount = bounceBeforeStartUnits.Count,
            FunnelAbandonCount = funnelAbandonCount,
            ContactStepAbandonCount = contactStepAbandonCount,
            ValidationFrictionAbandonCount = validationFrictionAbandonCount,
            ConsentFrictionCount = consentFriction,
            StartSignalGapQuoteTypeCount = startSignalGapQuoteTypeCount,
            DataQualityNote = dataQualityNote,
            QualificationNote = "Bounce Before Funnel Start counts quote landing exits with no derived funnel start, field interaction, submit attempt, or lead. Funnel Abandon counts explicit form_abandon exits after the funnel starts but before the contact step. Contact-step Abandon counts explicit form_abandon exits after the contact step is viewed. Validation Friction Abandon counts explicit form_abandon exits on sessions that also logged one or more form_field_error events. Resumed quote sessions with later progression are excluded from explicit abandon totals.",
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

    public async Task<DeviceIntelligenceDto> GetDeviceIntelligenceAsync(TimeRangeRequest range, ScopeContext scope, TrafficType trafficType = TrafficType.All)
    {
        var scopedAgentIds = await ResolveScopedAgentIdsAsync(scope);
        var allEvents = await BaseEvents(range, scope, scopedAgentIds).ToListAsync();
        var rows = FilterAttributedRowsByTraffic(BuildAttributedEventRows(allEvents), trafficType)
            .Select(r => r.Event)
            .ToList();

        List<DeviceIntelligenceRowDto> BuildRows(Func<AnalyticsEvent, string> selector)
        {
            var sessionProfiles = rows
                .Select(e => new { Event = e, IdentityKey = BuildIdentityProfileKeyOrNull(e) })
                .Where(x => !string.IsNullOrWhiteSpace(x.IdentityKey))
                .GroupBy(x => x.IdentityKey!, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var events = g.Select(x => x.Event).OrderBy(e => e.EventUtc).ToList();
                    var ctas = events.Count(e => e.EventType == "cta_click" || e.EventType == "quote_click");
                    var starts = BuildQuoteFormStartedUnitKeys(events).Count > 0 ? 1 : 0;
                    var submits = events.Count(e =>
                        IsSubmitAttemptMetricEvent(e) ||
                        (
                            e.EventType == "form_submit" &&
                            (e.SubmitOutcome ?? "").ToLower() == "attempt"
                        ));

                    var leads =
                        events.Any(e => IsSubmitSuccessMetricEvent(e))
                            ? 1
                            : 0;

                    return new
                    {
                        IdentityKey = g.Key,
                        Label = ResolveLatestContextLabel(events, selector),
                        Events = events.Count,
                        CtaClicks = ctas,
                        FormStarts = starts,
                        SubmitAttempts = submits,
                        ConfirmedLeads = leads
                    };
                })
                .ToList();

            return sessionProfiles
                .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var sessions = g.Count();
                    var events = g.Sum(x => x.Events);
                    var starts = g.Sum(x => x.FormStarts);
                    var leads = g.Sum(x => x.ConfirmedLeads);

                    return new DeviceIntelligenceRowDto
                    {
                        Label = string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key,
                        Sessions = sessions,
                        Events = events,
                        CtaClicks = g.Sum(x => x.CtaClicks),
                        FormStarts = starts,
                        SubmitAttempts = g.Sum(x => x.SubmitAttempts),
                        ConfirmedLeads = leads,
                        StartRate = sessions <= 0 ? 0 : Math.Round((decimal)starts / sessions * 100, 1),
                        LeadRate = sessions <= 0 ? 0 : Math.Round((decimal)leads / sessions * 100, 1)
                    };
                })
                .OrderByDescending(x => x.Sessions)
                .ThenBy(x => x.Label)
                .Take(12)
                .ToList();
        }

        var identityProfiles = rows
            .Select(BuildIdentityProfileKeyOrNull)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DeviceIntelligenceDto
        {
            RangeLabel = range.Label,
            TrafficType = trafficType,
            Sessions = rows.Select(e => e.SessionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count(),
            IdentityProfiles = identityProfiles.Count,
            VisitorFallbackProfiles = identityProfiles.Count(identityKey => identityKey is not null && IsVisitorFallbackIdentityKey(identityKey)),
            AnonymousEventsExcluded = rows.Count(e => string.IsNullOrWhiteSpace(BuildIdentityProfileKeyOrNull(e))),
            Events = rows.Count,
            FormStarts = BuildQuoteFormStartedUnitKeys(rows).Count,
            ConfirmedLeads = rows.Count(e =>
                IsSubmitSuccessMetricEvent(e)),
            Devices = BuildRows(ResolveDeviceTypeContext),
            Browsers = BuildRows(e => NormalizeDeviceContextLabel(e.Browser)),
            OperatingSystems = BuildRows(e => NormalizeDeviceContextLabel(e.OperatingSystem)),
            Viewports = BuildRows(e => BucketWidthLabel(e.ViewportWidth)),
            Screens = BuildRows(e => BucketWidthLabel(e.ScreenWidth)),
            TimeZones = BuildRows(e => NormalizeDeviceContextLabel(e.TimeZone)),
            Languages = BuildRows(e => NormalizeDeviceContextLabel(e.Language))
        };
    }

}

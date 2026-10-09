using System.Linq.Expressions;
using Domain.Entities;
using Shared.Analytics;

namespace Infrastructure.Analytics;

/// <summary>One classification policy for reporting, trust summaries and marketing eligibility.
/// Scores describe observed behavior, never verified identity. Attribution and event names add no confidence.</summary>
public static class TrafficQualityBucketFilters
{
    public const string RealHumanTrafficClientValue = "real_human_traffic";
    public const string LikelyHumanClientValue = "likely_human";
    public const string ReviewedNeededClientValue = "reviewed_needed";
    public const string SuspiciousActivityClientValue = "suspicious_activity";
    public const string LikelyBotsAutomationClientValue = "likely_bots_automation";
    public const string InternalQaClientValue = "internal_qa";
    public const string AllTrafficClientValue = "all_traffic";

    private static readonly Expression<Func<AnalyticsEvent, bool>> Internal = e => e.IsInternal ||
        (e.Environment != null && (e.Environment.ToLower().StartsWith("dev") || e.Environment.ToLower().StartsWith("stag") ||
        e.Environment.ToLower().StartsWith("preview") || e.Environment.ToLower().StartsWith("sandbox") ||
        e.Environment.ToLower().StartsWith("qa") || e.Environment.ToLower().StartsWith("test") || e.Environment.ToLower().StartsWith("local"))) ||
        (e.Host != null && (e.Host.ToLower().Contains("localhost") || e.Host.StartsWith("127.0.0.1") || e.Host.StartsWith("::1") || e.Host.StartsWith("[::1]")));
    private static readonly Expression<Func<AnalyticsEvent, bool>> Automated = e => e.WebDriver == true || e.IsHeadless == true ||
        (e.UserAgent ?? "").ToLower().Contains("bot") || (e.UserAgent ?? "").ToLower().Contains("crawler") ||
        (e.UserAgent ?? "").ToLower().Contains("spider") || (e.UserAgent ?? "").ToLower().Contains("headless") ||
        (e.UserAgent ?? "").ToLower().Contains("selenium") || (e.UserAgent ?? "").ToLower().Contains("puppeteer") ||
        (e.UserAgent ?? "").ToLower().Contains("playwright") || (e.UserAgent ?? "").ToLower().Contains("curl") ||
        (e.UserAgent ?? "").ToLower().Contains("wget") || (e.UserAgent ?? "").ToLower().Contains("python-requests") ||
        (e.UserAgent ?? "").ToLower().Contains("httpclient");
    private static readonly Expression<Func<AnalyticsEvent, bool>> Anomalous = e =>
        e.ScrollPercent < 0 || e.ScrollPercent > 100 || e.HumanInteractionCount < 0 || e.MouseMoveCount < 0 ||
        e.EngagedMilliseconds < 0 || e.EngagedMilliseconds > e.DwellMilliseconds;
    private static readonly Expression<Func<AnalyticsEvent, int>> Score = e =>
        e.UserAgent == null || e.UserAgent == "" ? 0 :
        ((e.HumanInteractionCount ?? 0) >= 3 ? 45 : (e.HumanInteractionCount ?? 0) >= 1 ? 35 : 0) +
        ((e.EngagedMilliseconds ?? 0) >= 15000 ? 30 : (e.EngagedMilliseconds ?? 0) >= 5000 ? 20 : 0) +
        ((e.ScrollPercent ?? 0) >= 50 ? 20 : (e.ScrollPercent ?? 0) >= 25 ? 15 : 0) +
        ((e.MouseMoveCount ?? 0) >= 5 ? 5 : 0);
    private static readonly Func<AnalyticsEvent, int> ScoreValue = Score.Compile();
    public static int HumanScore(AnalyticsEvent e) => ScoreValue(e);

    private sealed class Replace(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
    public static Expression<Func<AnalyticsEvent, bool>> BuildEventPredicate(TrafficQualityMode mode)
    {
        var p = Expression.Parameter(typeof(AnalyticsEvent), "e");
        Expression Bind(LambdaExpression x) => new Replace(x.Parameters[0], p).Visit(x.Body)!;
        var internalTraffic = Bind(Internal); var bot = Bind(Automated); var score = Bind(Score);
        var external = Expression.AndAlso(Expression.Not(internalTraffic), Expression.Not(bot));
        var suspicious = Bind(Anomalous);
        var valid = Expression.AndAlso(external, Expression.Not(suspicious));
        var human = Expression.GreaterThanOrEqual(score, Expression.Constant(90));
        var likely = Expression.AndAlso(Expression.GreaterThanOrEqual(score, Expression.Constant(60)), Expression.Not(human));
        Expression body = mode switch
        {
            TrafficQualityMode.AllTraffic => Expression.Constant(true),
            TrafficQualityMode.InternalQa => internalTraffic,
            TrafficQualityMode.LikelyBotsAutomation => Expression.AndAlso(Expression.Not(internalTraffic), bot),
            TrafficQualityMode.RealHumanTraffic => Expression.AndAlso(valid, human),
            TrafficQualityMode.LikelyHuman => Expression.AndAlso(valid, likely),
            // Absence of interaction is unknown, not proof of suspicious activity.
            TrafficQualityMode.SuspiciousActivity => Expression.AndAlso(external, suspicious),
            _ => Expression.AndAlso(valid, Expression.LessThan(score, Expression.Constant(60)))
        };
        return Expression.Lambda<Func<AnalyticsEvent, bool>>(body, p);
    }
    private static readonly TrafficQualityMode[] Precedence = [TrafficQualityMode.InternalQa, TrafficQualityMode.LikelyBotsAutomation,
        TrafficQualityMode.SuspiciousActivity, TrafficQualityMode.RealHumanTraffic, TrafficQualityMode.LikelyHuman];
    private static readonly IReadOnlyDictionary<TrafficQualityMode, Func<AnalyticsEvent, bool>> Predicates =
        Precedence.ToDictionary(x => x, x => BuildEventPredicate(x).Compile());
    public static TrafficQualityMode Classify(IEnumerable<AnalyticsEvent> evidence)
    {
        var rows = evidence.ToArray();
        foreach (var mode in Precedence) if (rows.Any(Predicates[mode])) return mode;
        return TrafficQualityMode.ReviewedNeeded;
    }
    private sealed class BucketSeed
    {
        public AnalyticsEvent Row { get; set; } = null!;
        public int Rank { get; set; }
    }

    public static IQueryable<AnalyticsEvent> ApplyEventBucketMembership(IQueryable<AnalyticsEvent> query, TrafficQualityMode mode, IQueryable<AnalyticsEvent>? evidence = null)
    {
        if (mode == TrafficQualityMode.AllTraffic) return query;
        var parameter = Expression.Parameter(typeof(AnalyticsEvent), "row");
        Expression rank = Expression.Constant(Precedence.Length);
        for (var index = Precedence.Length - 1; index >= 0; index--)
        {
            var predicate = BuildEventPredicate(Precedence[index]);
            rank = Expression.Condition(new Replace(predicate.Parameters[0], parameter).Visit(predicate.Body)!, Expression.Constant(index), rank);
        }
        var projection = Expression.Lambda<Func<AnalyticsEvent, BucketSeed>>(Expression.MemberInit(
            Expression.New(typeof(BucketSeed)),
            Expression.Bind(typeof(BucketSeed).GetProperty(nameof(BucketSeed.Row))!, parameter),
            Expression.Bind(typeof(BucketSeed).GetProperty(nameof(BucketSeed.Rank))!, rank)), parameter);
        var sessionIds = query.Where(e => e.SessionId != null && e.SessionId != "").Select(e => e.SessionId);
        var eventIds = query.Select(e => e.EventId);
        var candidates = (evidence ?? query).Where(e => sessionIds.Contains(e.SessionId) || eventIds.Contains(e.EventId));
        // One set-based aggregation and join, rather than one correlated query per bucket per event.
        var buckets = candidates.Select(projection).GroupBy(s => new {
            s.Row.CommerceBusinessId, s.Row.AgentTrackingProfileId, Host = (s.Row.Host ?? "").ToLower(),
            Session = s.Row.SessionId ?? "", Visitor = s.Row.VisitorId ?? "",
            Singleton = s.Row.SessionId == null || s.Row.SessionId == "" ? s.Row.EventId : Guid.Empty
        }).Select(g => new { g.Key, Rank = g.Min(s => s.Rank) });
        var targetRank = Array.IndexOf(Precedence, mode);
        if (targetRank < 0) targetRank = Precedence.Length;
        return query.Join(buckets.Where(b => b.Rank == targetRank), e => new {
            e.CommerceBusinessId, e.AgentTrackingProfileId, Host = (e.Host ?? "").ToLower(),
            Session = e.SessionId ?? "", Visitor = e.VisitorId ?? "",
            Singleton = e.SessionId == null || e.SessionId == "" ? e.EventId : Guid.Empty
        }, b => b.Key, (e, _) => e);
    }

    private static string Identity(AnalyticsEvent e) => $"{e.CommerceBusinessId}|{e.AgentTrackingProfileId}|{e.Host?.ToLowerInvariant()}|" +
        (!string.IsNullOrWhiteSpace(e.SessionId) ? "s:" + e.SessionId + "|" + e.VisitorId : "e:" + e.EventId);
    public static List<AnalyticsEvent> ApplyEventBucketMembershipInMemory(IEnumerable<AnalyticsEvent> events, TrafficQualityMode mode, IEnumerable<AnalyticsEvent>? evidence = null)
    {
        var rows = events.ToList();
        if (mode == TrafficQualityMode.AllTraffic) return rows;
        var buckets = (evidence ?? rows).Concat(rows).GroupBy(Identity).ToDictionary(g => g.Key, g => Classify(g));
        return rows.Where(e => buckets[Identity(e)] == mode).ToList();
    }
    public static List<WebsiteLead> ApplyLeadBucketMembershipInMemory(IEnumerable<WebsiteLead> leads, IEnumerable<AnalyticsEvent> events, TrafficQualityMode mode)
    {
        if (mode == TrafficQualityMode.AllTraffic) return leads.ToList();
        var groups = events.GroupBy(Identity).ToDictionary(g => g.Key, g => Classify(g));
        return leads.Where(l => {
            var row = UnifiedEventMapper.LeadBehaviorEvidence(l);
            return (groups.TryGetValue(Identity(row), out var bucket) ? bucket : Classify([row])) == mode;
        }).ToList();
    }
    public static Expression<Func<WebsiteLead, bool>> BuildLeadPredicate(TrafficQualityMode mode)
    {
        // Standalone intake is a business fact, not proof of human behavior. Reuse the same policy without invented telemetry.
        var lead = Expression.Parameter(typeof(WebsiteLead), "lead");
        var body = new ProjectLead(lead).Visit(BuildEventPredicate(mode).Body)!;
        return Expression.Lambda<Func<WebsiteLead, bool>>(body, lead);
    }
    private sealed class ProjectLead(ParameterExpression lead) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is ParameterExpression p && p.Type == typeof(AnalyticsEvent))
            {
                var name = node.Member.Name == "UserAgent" ? "ClientUserAgent" : node.Member.Name;
                var property = typeof(WebsiteLead).GetProperty(name);
                return property is null ? Expression.Default(node.Type) : Expression.Property(lead, property);
            }
            return base.VisitMember(node);
        }
    }
    public static string ToClientValue(TrafficQualityMode mode)
    {
        return mode switch
        {
            TrafficQualityMode.LikelyHuman => LikelyHumanClientValue,
            TrafficQualityMode.ReviewedNeeded => ReviewedNeededClientValue,
            TrafficQualityMode.SuspiciousActivity => SuspiciousActivityClientValue,
            TrafficQualityMode.LikelyBotsAutomation => LikelyBotsAutomationClientValue,
            TrafficQualityMode.InternalQa => InternalQaClientValue,
            TrafficQualityMode.AllTraffic => AllTrafficClientValue,
            _ => RealHumanTrafficClientValue
        };
    }

    public static TrafficQualityMode ParseClientOrEnumValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TrafficQualityMode.RealHumanTraffic;

        var normalized = value.Trim();
        if (Enum.TryParse<TrafficQualityMode>(normalized, ignoreCase: true, out var parsed))
            return parsed;

        return normalized.ToLowerInvariant() switch
        {
            RealHumanTrafficClientValue => TrafficQualityMode.RealHumanTraffic,
            LikelyHumanClientValue => TrafficQualityMode.LikelyHuman,
            ReviewedNeededClientValue => TrafficQualityMode.ReviewedNeeded,
            SuspiciousActivityClientValue => TrafficQualityMode.SuspiciousActivity,
            LikelyBotsAutomationClientValue => TrafficQualityMode.LikelyBotsAutomation,
            InternalQaClientValue => TrafficQualityMode.InternalQa,
            AllTrafficClientValue => TrafficQualityMode.AllTraffic,
            "real_human" => TrafficQualityMode.RealHumanTraffic,
            "review" => TrafficQualityMode.ReviewedNeeded,
            "suspicious" => TrafficQualityMode.SuspiciousActivity,
            "likely_bot" => TrafficQualityMode.LikelyBotsAutomation,
            "internal" => TrafficQualityMode.InternalQa,
            "all" => TrafficQualityMode.AllTraffic,
            _ => TrafficQualityMode.RealHumanTraffic
        };
    }
}

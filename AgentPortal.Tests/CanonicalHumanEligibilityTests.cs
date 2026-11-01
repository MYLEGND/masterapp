using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalHumanEligibilityTests
{
    private static AnalyticsEvent Evidence() => new() {
        EventId = Guid.NewGuid(), EventType = "page_view", EventUtc = DateTime.UtcNow,
        MetadataJson = "{\"measurementConsentAllowed\":true}",
        SessionId = "session", VisitorId = "visitor", Host = "example.test", UserAgent = "Mozilla/5.0",
        HumanInteractionCount = 3, EngagedMilliseconds = 15000, DwellMilliseconds = 20000, ScrollPercent = 50
    };

    [Fact]
    public async Task ProviderGateUsesPersistedEvidenceAndRejectsAutomationEvenWithHighScore()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var row = Evidence(); row.WebDriver = true;
        db.AnalyticsEvents.Add(row); await db.SaveChangesAsync();
        var forged = Evidence(); forged.EventId = row.EventId;
        var result = await CanonicalMarketingEligibility.ResolveAsync(db, forged);
        Assert.False(result.Eligible);
        Assert.Equal("likely_bots_automation", result.Bucket);
        Assert.False((await CanonicalMarketingEligibility.ResolveAsync(db, Evidence())).Eligible);
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 15000, 50, false)]
    [InlineData(3, 5000, 25, true)]
    [InlineData(3, 15000, 50, true)]
    public async Task ConversionNamesAndAdReferencesDoNotGrantHumanEligibility(int interactions, int engaged, int scroll, bool expected)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var row = Evidence(); row.EventType = "PolicyPaid"; row.Oppref = "click-reference";
        row.HumanInteractionCount = interactions; row.EngagedMilliseconds = engaged; row.ScrollPercent = scroll;
        db.AnalyticsEvents.Add(row); await db.SaveChangesAsync();
        Assert.Equal(expected, (await CanonicalMarketingEligibility.ResolveAsync(db, row)).Eligible);
    }

    [Fact]
    public async Task SessionPromotionNeverCrossesOwnerOrVisitorAndBucketsAreExclusive()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var human = Evidence(); human.AgentTrackingProfileId = Guid.NewGuid();
        var other = Evidence(); other.AgentTrackingProfileId = Guid.NewGuid(); other.HumanInteractionCount = 0;
        other.EngagedMilliseconds = 0; other.ScrollPercent = 0;
        db.AnalyticsEvents.AddRange(human, other); await db.SaveChangesAsync();
        Assert.False((await CanonicalMarketingEligibility.ResolveAsync(db, other)).Eligible);
        var rows = new[] {human, other};
        foreach (var row in rows)
            Assert.Single(Enum.GetValues<TrafficQualityMode>().Distinct().Where(m => m != TrafficQualityMode.AllTraffic &&
                TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(rows, m).Contains(row)));
        foreach (var mode in Enum.GetValues<TrafficQualityMode>())
            Assert.Equal(TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(rows, mode).Count,
                await TrafficQualityBucketFilters.ApplyEventBucketMembership(db.AnalyticsEvents, mode).CountAsync());
    }

    [Fact]
    public async Task DelayedCrmOutcomeRetainsHumanEvidenceOutsideTheReportWindow()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile { Id = Guid.NewGuid(), AgentUserId = "agent", AgentUpn = "agent@example.test", Slug = "agent", Status = "active" };
        var acquisition = Evidence(); acquisition.AgentTrackingProfileId = profile.Id; acquisition.EventUtc = DateTime.UtcNow.AddDays(-45);
        var outcome = Evidence(); outcome.AgentTrackingProfileId = profile.Id; outcome.EventType = "PolicyPaid";
        outcome.HumanInteractionCount = 0; outcome.EngagedMilliseconds = 0; outcome.ScrollPercent = 0;
        db.AddRange(profile, acquisition, outcome); await db.SaveChangesAsync();
        Assert.True((await CanonicalMarketingEligibility.ResolveAsync(db, outcome)).Eligible);
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-1), ToUtc = DateTime.UtcNow, QualityMode = TrafficQualityMode.RealHumanTraffic };
        var service = new AnalyticsQueryService(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Equal(outcome.EventId, Assert.Single(await service.LoadFilteredEventsAsync(range, ScopeContext.ForAgent(profile.Id))).EventId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task StrongHumanEvidenceCannotBypassMissingOrDeniedConsent(bool? consent)
    {
        await using var db = ControllerTestHelpers.BuildDb(); var row = Evidence();
        row.MetadataJson = consent.HasValue ? "{\"measurementConsentAllowed\":false}" : "{}";
        db.AnalyticsEvents.Add(row); await db.SaveChangesAsync();
        var decision = await CanonicalMarketingEligibility.ResolveAsync(db, row);
        Assert.False(decision.Eligible); Assert.StartsWith("measurement_consent_", decision.Reason);
    }

    [Fact]
    public async Task LaterConsentDenialAcrossSessionsBlocksTheOriginalAcquisition()
    {
        await using var db = ControllerTestHelpers.BuildDb(); var row = Evidence();
        var denial = Evidence(); denial.SessionId = "later-session"; denial.EventType = "measurement_consent_changed";
        denial.MetadataJson = "{\"measurementConsentAllowed\":false}";
        db.AnalyticsEvents.AddRange(row, denial); await db.SaveChangesAsync();
        Assert.Equal("measurement_consent_denied", (await CanonicalMarketingEligibility.ResolveAsync(db, row)).Reason);
    }

    [Theory]
    [InlineData("{}", "measurement_consent_unavailable")]
    [InlineData("{\"measurementConsentAllowed\":false}", "measurement_consent_denied")]
    public async Task LaterGrantDoesNotRetroactivelyAuthorizeAnEarlierFact(string metadata, string reason)
    {
        await using var db = ControllerTestHelpers.BuildDb(); var earlier = Evidence(); earlier.EventUtc = DateTime.UtcNow.AddMinutes(-1);
        earlier.MetadataJson = metadata;
        var grant = Evidence(); grant.EventType = "measurement_consent_changed";
        db.AnalyticsEvents.AddRange(earlier, grant); await db.SaveChangesAsync();
        Assert.Equal(reason, (await CanonicalMarketingEligibility.ResolveAsync(db, earlier)).Reason);
    }

    [Fact]
    public async Task RelationalAndInMemoryBucketsAgreeForMissingAndConflictingEvidence()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var rows = Enumerable.Range(0, 7).Select(i => { var row = Evidence(); row.SessionId = "session-" + i; return row; }).ToArray();
        rows[1].UserAgent = null; rows[2].WebDriver = true; rows[3].IsInternal = true; rows[4].EngagedMilliseconds = -1;
        rows[5].HumanInteractionCount = null; rows[5].EngagedMilliseconds = null; rows[5].ScrollPercent = null;
        rows[6].DwellMilliseconds = null;
        db.AnalyticsEvents.AddRange(rows); await db.SaveChangesAsync();
        foreach (var mode in Enum.GetValues<TrafficQualityMode>().Distinct())
        {
            var expected = TrafficQualityBucketFilters.ApplyEventBucketMembershipInMemory(rows, mode).Select(e => e.EventId).Order().ToArray();
            var actual = await TrafficQualityBucketFilters.ApplyEventBucketMembership(db.AnalyticsEvents, mode).Select(e => e.EventId).ToArrayAsync();
            Assert.Equal(expected, actual.Order().ToArray());
        }
    }

    [Fact]
    public async Task RevenueOperatorRealHumanRangeCannotBeContaminatedByAllTrafficAttributionOrScopedCalibrationReads()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var profile = new AgentTrackingProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = "revenue-operator-agent",
            AgentUpn = "revenue-operator-agent@example.test",
            Slug = "revenue-operator-agent",
            Status = "active"
        };

        var human = Evidence();
        human.AgentTrackingProfileId = profile.Id;
        human.SessionId = "human-session";
        human.VisitorId = "human-visitor";
        human.EventType = "LeadReadySignal";

        var internalTraffic = Evidence();
        internalTraffic.AgentTrackingProfileId = profile.Id;
        internalTraffic.SessionId = "internal-session";
        internalTraffic.VisitorId = "internal-visitor";
        internalTraffic.EventType = "LeadReadySignal";
        internalTraffic.IsInternal = true;

        var automation = Evidence();
        automation.AgentTrackingProfileId = profile.Id;
        automation.SessionId = "automation-session";
        automation.VisitorId = "automation-visitor";
        automation.EventType = "LeadReadySignal";
        automation.WebDriver = true;

        db.AddRange(profile, human, internalTraffic, automation);
        await db.SaveChangesAsync();

        var realHumanRange = new TimeRangeRequest
        {
            FromUtc = DateTime.UtcNow.AddHours(-1),
            ToUtc = DateTime.UtcNow.AddHours(1),
            QualityMode = TrafficQualityMode.RealHumanTraffic,
            Label = "revenue-operator-real-human",
            Preset = "custom"
        };
        var allTrafficRange = new TimeRangeRequest
        {
            FromUtc = realHumanRange.FromUtc,
            ToUtc = realHumanRange.ToUtc,
            QualityMode = TrafficQualityMode.AllTraffic,
            Label = "revenue-operator-all-traffic",
            Preset = "custom"
        };

        var service = new AnalyticsQueryService(
            db,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var scope = ScopeContext.ForAgent(profile.Id);

        // Growth Operator and unified performance deliberately use TrafficType.All
        // to retain every attribution channel. QualityMode must still exclude
        // internal/automation traffic before the attribution filter is applied.
        var attributed = await service.LoadAttributedEventsAsync(
            realHumanRange,
            scope,
            TrafficType.All);
        Assert.Equal(human.EventId, Assert.Single(attributed).EventId);

        // Outcome calibration uses ScopedEvents directly. It must obey the same
        // human-quality boundary rather than widening back to every scoped row.
        var calibrationRows = await service.ScopedEvents(realHumanRange, scope)
            .ToListAsync();
        Assert.Equal(human.EventId, Assert.Single(calibrationRows).EventId);

        var allTraffic = await service.LoadAttributedEventsAsync(
            allTrafficRange,
            scope,
            TrafficType.All);
        Assert.Equal(3, allTraffic.Count);
    }

    [Fact]
    public void SessionClassificationTranslatesToSqlWithoutClientEvaluation()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=invalid.test;Database=translation_only;Integrated Security=true").Options);
        foreach (var mode in Enum.GetValues<TrafficQualityMode>())
            Assert.Contains("SELECT", TrafficQualityBucketFilters.ApplyEventBucketMembership(db.AnalyticsEvents, mode).ToQueryString());
    }

    [Fact]
    public void BucketRankingDoesNotRepeatHigherPriorityPredicatesPerLowerBucket()
    {
        using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer("Server=invalid.test;Database=translation_only;Integrated Security=true").Options);
        var query = TrafficQualityBucketFilters.ApplyEventBucketMembership(db.AnalyticsEvents, TrafficQualityMode.RealHumanTraffic);
        var count = new QualityPredicateCount();
        count.Visit(query.Expression);
        Assert.Equal(1, count.InternalChecks);
        Assert.Equal(1, count.WebDriverChecks);
        Assert.Contains("SELECT", query.ToQueryString()); // Translate only; no connection is opened.
    }

    [Fact]
    public async Task RelationalRankPreservesPrecedenceAcrossNullAndOverlappingSignals()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var rows = (from internalFlag in new[] { false, true }
                    from webDriver in new bool?[] { null, false, true }
                    from agent in new string?[] { null, "", "Mozilla/5.0", "crawler bot" }
                    from engaged in new int?[] { null, -1, 5000, 15000, 25000 }
                    select new { internalFlag, webDriver, agent, engaged }).Select((sample, index) =>
        {
            var row = Evidence();
            row.SessionId = "precedence-" + index;
            row.IsInternal = sample.internalFlag;
            row.WebDriver = sample.webDriver;
            row.UserAgent = sample.agent;
            row.EngagedMilliseconds = sample.engaged;
            return row;
        }).ToArray();
        db.AnalyticsEvents.AddRange(rows);
        var oldBot = Evidence(); oldBot.SessionId = rows[0].SessionId; oldBot.UserAgent = "crawler bot";
        oldBot.EventUtc = DateTime.UtcNow.AddDays(-10);
        var otherVisitor = Evidence(); otherVisitor.SessionId = rows[1].SessionId; otherVisitor.VisitorId = "different";
        otherVisitor.IsInternal = true;
        var otherHost = Evidence(); otherHost.SessionId = rows[2].SessionId; otherHost.Host = "other.test";
        otherHost.IsInternal = true;
        var future = Evidence(); future.SessionId = rows[3].SessionId; future.EventUtc = DateTime.UtcNow.AddDays(1);
        future.IsInternal = true;
        db.AnalyticsEvents.AddRange(oldBot, otherVisitor, otherHost, future);
        foreach (var session in new string?[] { null, "", " ", "Case", "case", "café", "cafe" })
        {
            var signal = Evidence(); signal.SessionId = session; signal.Host = "EXAMPLE.TEST";
            var conflict = Evidence(); conflict.SessionId = session; conflict.IsHeadless = true;
            db.AnalyticsEvents.AddRange(signal, conflict);
        }
        await db.SaveChangesAsync();
        var cutoff = DateTime.UtcNow;
        var from = cutoff.AddHours(-1);
        var query = db.AnalyticsEvents.Where(row => row.EventUtc >= from && row.EventUtc <= cutoff);
        var evidence = db.AnalyticsEvents.Where(row => row.EventUtc <= cutoff);
        foreach (var mode in Enum.GetValues<TrafficQualityMode>().Distinct())
        {
            // Retain the original provider-translated rank as the oracle, including
            // SQL null semantics, evidence outside the date range and scoped keys.
            var expected = await LegacyBucketMembership(query, mode, evidence)
                .Select(row => row.EventId).ToArrayAsync();
            var actual = await TrafficQualityBucketFilters.ApplyEventBucketMembership(query, mode, evidence)
                .Select(row => row.EventId).ToArrayAsync();
            Assert.Equal(expected.Order().ToArray(), actual.Order().ToArray());
        }
    }

    private sealed class LegacyBucketSeed
    {
        public AnalyticsEvent Row { get; set; } = null!;
        public int Rank { get; set; }
    }

    // Frozen rank/query shape from 11ec3932, used only as a regression oracle.
    private static IQueryable<AnalyticsEvent> LegacyBucketMembership(IQueryable<AnalyticsEvent> query,
        TrafficQualityMode mode, IQueryable<AnalyticsEvent> evidence)
    {
        if (mode == TrafficQualityMode.AllTraffic) return query;
        TrafficQualityMode[] precedence = [TrafficQualityMode.InternalQa, TrafficQualityMode.LikelyBotsAutomation,
            TrafficQualityMode.SuspiciousActivity, TrafficQualityMode.RealHumanTraffic, TrafficQualityMode.LikelyHuman];
        var parameter = Expression.Parameter(typeof(AnalyticsEvent), "row");
        Expression rank = Expression.Constant(precedence.Length);
        for (var index = precedence.Length - 1; index >= 0; index--)
        {
            var predicate = TrafficQualityBucketFilters.BuildEventPredicate(precedence[index]);
            rank = Expression.Condition(new ReplaceRankParameter(predicate.Parameters[0], parameter).Visit(predicate.Body)!,
                Expression.Constant(index), rank);
        }
        var projection = Expression.Lambda<Func<AnalyticsEvent, LegacyBucketSeed>>(Expression.MemberInit(
            Expression.New(typeof(LegacyBucketSeed)),
            Expression.Bind(typeof(LegacyBucketSeed).GetProperty(nameof(LegacyBucketSeed.Row))!, parameter),
            Expression.Bind(typeof(LegacyBucketSeed).GetProperty(nameof(LegacyBucketSeed.Rank))!, rank)), parameter);
        var sessions = query.Where(row => row.SessionId != null && row.SessionId != "").Select(row => row.SessionId);
        var ids = query.Select(row => row.EventId);
        var buckets = evidence.Where(row => sessions.Contains(row.SessionId) || ids.Contains(row.EventId))
            .Select(projection).GroupBy(seed => new {
                seed.Row.CommerceBusinessId, seed.Row.AgentTrackingProfileId, Host = (seed.Row.Host ?? "").ToLower(),
                Session = seed.Row.SessionId ?? "", Visitor = seed.Row.VisitorId ?? "",
                Singleton = seed.Row.SessionId == null || seed.Row.SessionId == "" ? seed.Row.EventId : Guid.Empty
            }).Select(group => new { group.Key, Rank = group.Min(seed => seed.Rank) });
        var target = Array.IndexOf(precedence, mode);
        if (target < 0) target = precedence.Length;
        return query.Join(buckets.Where(bucket => bucket.Rank == target), row => new {
            row.CommerceBusinessId, row.AgentTrackingProfileId, Host = (row.Host ?? "").ToLower(),
            Session = row.SessionId ?? "", Visitor = row.VisitorId ?? "",
            Singleton = row.SessionId == null || row.SessionId == "" ? row.EventId : Guid.Empty
        }, bucket => bucket.Key, (row, _) => row);
    }

    private sealed class ReplaceRankParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private sealed class QualityPredicateCount : ExpressionVisitor
    {
        public int InternalChecks { get; private set; }
        public int WebDriverChecks { get; private set; }
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.DeclaringType == typeof(AnalyticsEvent))
            {
                if (node.Member.Name == nameof(AnalyticsEvent.IsInternal)) InternalChecks++;
                if (node.Member.Name == nameof(AnalyticsEvent.WebDriver)) WebDriverChecks++;
            }
            return base.VisitMember(node);
        }
    }
}

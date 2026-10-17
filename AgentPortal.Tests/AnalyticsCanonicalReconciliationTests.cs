using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Data.Common;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Domain.Entities;
using Infrastructure.Analytics;
using Microsoft.Extensions.Configuration;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AnalyticsCanonicalReconciliationTests
{
    [Fact]
    public async Task AdversarialCrossBusinessSessionCollisionsCannotContaminateCounts()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var owners = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var now = DateTime.UtcNow;
        for (var ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++)
            for (var visit = 0; visit <= ownerIndex; visit++)
            {
                var session = "shared-session-" + visit;
                var visitor = "shared-visitor-" + visit;
                db.AnalyticsEvents.AddRange(
                    Event(owners[ownerIndex], now.AddMinutes(-3), "page_view", session, visitor, "/"),
                    Event(owners[ownerIndex], now.AddMinutes(-2), "page_engaged_15s", session, visitor, "/", strongHuman:true),
                    Event(owners[ownerIndex], now.AddMinutes(-1), "cta_click", session, visitor, "/", elementKey:"same-button"));
            }
        await db.SaveChangesAsync();
        var query = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest { FromUtc = now.AddHours(-1), ToUtc = now.AddMinutes(1), QualityMode = TrafficQualityMode.RealHumanTraffic };
        for (var index = 0; index < owners.Length; index++)
        {
            var scope = ScopeContext.ForBusiness(owners[index]);
            var summary = await query.GetSummaryAsync(range, scope);
            Assert.Equal(index + 1, summary.PageViews);
            Assert.Equal(index + 1, summary.Sessions);
            Assert.Equal(index + 1, summary.UniqueVisitors);
            Assert.Equal(index + 1, (await query.GetTrafficAsync(range, scope)).PageViewTrend.Sum(row => row.Value));
            var page = Assert.Single((await query.GetPagePerformanceAsync(range, scope)).Rows);
            Assert.Equal(index + 1, page.Views);
            Assert.Equal(index + 1, page.CtaClicks);
            Assert.Equal(index + 1, Assert.Single((await query.GetCtaPerformanceAsync(range, scope)).Rows).Clicks);
        }
    }

    [Fact]
    public async Task BusinessRealHumanSessionCarriesOneQualityStoryAcrossCoreReporting()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var foreignBusinessId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string sessionId = "canonical-session";
        const string visitorId = "canonical-visitor";

        db.AnalyticsEvents.AddRange(
            Event(businessId, now.AddMinutes(-5), "page_view", sessionId, visitorId, "/", bounce: true),
            Event(businessId, now.AddMinutes(-4), "page_engaged_15s", sessionId, visitorId, "/", strongHuman: true),
            Event(businessId, now.AddMinutes(-3), "cta_click", sessionId, visitorId, "/", elementKey: "contact_primary"),
            Event(foreignBusinessId, now.AddMinutes(-2), "page_engaged_15s", "foreign-session", "foreign-visitor", "/", strongHuman: true));

        db.WebsiteLeads.AddRange(
            new WebsiteLead
            {
                LeadId = Guid.NewGuid(),
                CommerceBusinessId = businessId,
                AgentTrackingProfileId = null,
                SessionId = sessionId,
                VisitorId = visitorId,
                SourcePageKey = "/",
                FirstName = "Canonical",
                Email = "canonical@example.org",
                TermsAccepted = false,
                MarketingEmailConsent = false,
                CallTextConsent = false,
                Environment = "production",
                Host = "business.example.org",
                CreatedUtc = now.AddMinutes(-2)
            },
            new WebsiteLead
            {
                LeadId = Guid.NewGuid(),
                CommerceBusinessId = foreignBusinessId,
                AgentTrackingProfileId = null,
                SessionId = "foreign-session",
                VisitorId = "foreign-visitor",
                SourcePageKey = "/",
                FirstName = "Foreign",
                Email = "foreign@example.org",
                TermsAccepted = true,
                Environment = "production",
                Host = "foreign.example.org",
                CreatedUtc = now.AddMinutes(-1)
            });

        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.RealHumanTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };
        var scope = ScopeContext.ForBusiness(businessId);

        var summary = await analytics.GetSummaryAsync(range, scope);
        var traffic = await analytics.GetTrafficAsync(range, scope);
        var pages = await analytics.GetPagePerformanceAsync(range, scope);
        var ctas = await analytics.GetCtaPerformanceAsync(range, scope);
        var conversions = await analytics.GetConversionsAsync(range, scope);
        var leads = await analytics.GetLeadsAsync(range, scope);
        var health = await analytics.GetMarketingHealthAsync(range, scope);
        var quote = await analytics.GetQuoteFunnelAsync(range, scope);
        var engagement = await analytics.GetEngagementSummaryAsync(range, scope);
        var pageEngagement = await analytics.GetPageEngagementAsync(range, scope);
        var timeOnPage = await analytics.GetTimeOnPageAsync(range, scope);
        var exits = await analytics.GetExitAnalysisAsync(range, scope);
        var scroll = await analytics.GetScrollAnalysisAsync(range, scope);
        var journeys = await analytics.GetJourneyAnalysisAsync(range, scope);
        var sources = await analytics.GetSourcePerformanceAsync(range, scope);
        var landingPages = await analytics.GetLandingPagePerformanceAsync(range, scope);
        var friction = await analytics.GetFormFrictionAsync(range, scope);
        var abandonment = await analytics.GetFormAbandonmentAsync(range, scope);
        var devices = await analytics.GetDeviceIntelligenceAsync(range, scope);

        Assert.NotNull(quote);
        Assert.NotNull(engagement);
        Assert.NotNull(pageEngagement);
        Assert.NotNull(timeOnPage);
        Assert.NotNull(exits);
        Assert.NotNull(scroll);
        Assert.NotNull(journeys);
        Assert.NotNull(sources);
        Assert.NotNull(landingPages);
        Assert.NotNull(friction);
        Assert.NotNull(abandonment);
        Assert.NotNull(devices);

        Assert.Equal(1, summary.PageViews);
        Assert.Equal(1, summary.Sessions);
        Assert.Equal(1, summary.UniqueVisitors);
        Assert.Equal(1, summary.VerifiedLeads);

        Assert.Equal(1, traffic.PageViewTrend.Sum(x => x.Value));
        Assert.Contains(traffic.TopPages, x => x.Key == "/" && x.Count == 1);

        var page = Assert.Single(pages.Rows);
        Assert.Equal("/", page.PageKey);
        Assert.Equal(1, page.Views);
        Assert.Equal(1, page.CtaClicks);
        Assert.Equal(1, page.Leads);

        var cta = Assert.Single(ctas.Rows);
        Assert.Equal("contact_primary", cta.ElementKey);
        Assert.Equal(1, cta.Clicks);

        Assert.Equal(1, conversions.TotalConversions);
        Assert.Single(conversions.Recent);
        Assert.Equal(1, leads.Total);
        Assert.Single(leads.Leads);

        // Marketing health reads the same classified population and must not
        // manufacture a missing lead simply because the lead row itself lacked
        // consent/UTM hints that the browser session did not need for human proof.
        Assert.DoesNotContain(health.Warnings, x =>
            x.Contains("Unknown lead attribution remains on 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FounderPersonalAggregatesProtectAndLegendButExcludesOtherOwners()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founderId = Guid.NewGuid();
        var founderAliasId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var businessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.AgentTrackingProfiles.AddRange(
            new AgentTrackingProfile { Id = founderId, AgentUpn = "founder@example.org", AgentUserId = "founder-1", Slug = "founder" },
            new AgentTrackingProfile { Id = founderAliasId, AgentUpn = "founder@example.org", AgentUserId = "founder-2", Slug = "founder-alias" },
            new AgentTrackingProfile { Id = otherAgentId, AgentUpn = "other@example.org", AgentUserId = "other", Slug = "other" });

        db.AnalyticsEvents.AddRange(
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-5), ReceivedUtc = now.AddMinutes(-5),
                SessionId = "founder-protect", VisitorId = "founder-protect-v", PageKey = "protect_home",
                AgentTrackingProfileId = founderAliasId, CommerceBusinessId = null,
                Environment = "production", Host = "protect.mylegnd.com", MetadataJson = "{\"siteKey\":\"protect\",\"reportingOwner\":\"founder\"}"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-4), ReceivedUtc = now.AddMinutes(-4),
                SessionId = "founder-legend", VisitorId = "founder-legend-v", PageKey = "legend_home",
                AgentTrackingProfileId = null, CommerceBusinessId = null,
                Environment = "production", Host = "mylegnd.com", MetadataJson = "{\"siteKey\":\"legend\",\"reportingOwner\":\"founder\"}"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-3), ReceivedUtc = now.AddMinutes(-3),
                SessionId = "other-agent", VisitorId = "other-agent-v", PageKey = "protect_home",
                AgentTrackingProfileId = otherAgentId, CommerceBusinessId = null,
                Environment = "production", Host = "protect.mylegnd.com", MetadataJson = "{\"siteKey\":\"protect\",\"reportingOwner\":\"agent\"}"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-2), ReceivedUtc = now.AddMinutes(-2),
                SessionId = "business", VisitorId = "business-v", PageKey = "/",
                AgentTrackingProfileId = null, CommerceBusinessId = businessId,
                Environment = "production", Host = "business.example.org", MetadataJson = "{\"siteKey\":\"business\"}"
            });

        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.AllTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };

        var summary = await analytics.GetSummaryAsync(range, ScopeContext.ForFounder(founderId));

        Assert.Equal(2, summary.PageViews);
        Assert.Equal(2, summary.Sessions);
        Assert.Equal(2, summary.UniqueVisitors);
    }

    [Fact]
    public async Task ProductionEnvironmentFilterExcludesDevelopmentRowsFromCanonicalFounderReporting()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var founderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.AgentTrackingProfiles.Add(new AgentTrackingProfile
        {
            Id = founderId,
            AgentUpn = "founder@example.org",
            AgentUserId = "founder-1",
            Slug = "founder"
        });

        db.AnalyticsEvents.AddRange(
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-3), ReceivedUtc = now.AddMinutes(-3),
                SessionId = "prod-session", VisitorId = "prod-visitor", PageKey = "protect_home",
                AgentTrackingProfileId = founderId, Environment = "production", Host = "protect.mylegnd.com"
            },
            new AnalyticsEvent
            {
                EventId = Guid.NewGuid(), ClientEventId = Guid.NewGuid(), EventType = "page_view",
                EventUtc = now.AddMinutes(-2), ReceivedUtc = now.AddMinutes(-2),
                SessionId = "dev-session", VisitorId = "dev-visitor", PageKey = "protect_home",
                AgentTrackingProfileId = founderId, Environment = "development", Host = "localhost"
            });

        db.WebsiteLeads.AddRange(
            new WebsiteLead
            {
                LeadId = Guid.NewGuid(), AgentTrackingProfileId = founderId,
                FirstName = "Prod", Email = "prod@example.org", TermsAccepted = true,
                CreatedUtc = now.AddMinutes(-1), Environment = "production", Host = "protect.mylegnd.com"
            },
            new WebsiteLead
            {
                LeadId = Guid.NewGuid(), AgentTrackingProfileId = founderId,
                FirstName = "Dev", Email = "dev@example.org", TermsAccepted = true,
                CreatedUtc = now.AddMinutes(-1), Environment = "development", Host = "localhost"
            });
        await db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:EnvironmentFilter"] = "production"
            })
            .Build();
        var analytics = new AnalyticsQueryService(db, config);
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddHours(1),
            QualityMode = TrafficQualityMode.AllTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };

        var summary = await analytics.GetSummaryAsync(range, ScopeContext.ForFounder(founderId));

        Assert.Equal(1, summary.PageViews);
        Assert.Equal(1, summary.Sessions);
        Assert.Equal(1, summary.UniqueVisitors);
        Assert.Equal(1, summary.VerifiedLeads);
        Assert.Equal("Environment: Production", summary.EnvironmentLabel);
    }

    [Fact]
    public async Task StandaloneServerLeadDoesNotInventHumanEvidence()
    {
        using var db = ControllerTestHelpers.BuildDb();
        var businessId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            CommerceBusinessId = businessId,
            AgentTrackingProfileId = null,
            SessionId = "server-only-session",
            VisitorId = "server-only-visitor",
            SourcePageKey = "/contact",
            FirstName = "Server",
            Email = "server@example.org",
            TermsAccepted = true,
            Environment = "production",
            Host = "business.example.org",
            CreatedUtc = now
        });
        await db.SaveChangesAsync();

        var analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().Build());
        var range = new TimeRangeRequest
        {
            FromUtc = now.AddMinutes(-5),
            ToUtc = now.AddMinutes(5),
            QualityMode = TrafficQualityMode.RealHumanTraffic,
            Label = "test",
            Preset = "custom",
            ViewerTimeZone = TimeZoneInfo.Utc
        };

        var summary = await analytics.GetSummaryAsync(range, ScopeContext.ForBusiness(businessId));
        var conversions = await analytics.GetConversionsAsync(range, ScopeContext.ForBusiness(businessId));

        Assert.Equal(0, summary.VerifiedLeads);
        Assert.Equal(0, conversions.TotalConversions);
        range = new TimeRangeRequest { FromUtc = range.FromUtc, ToUtc = range.ToUtc, QualityMode = TrafficQualityMode.ReviewedNeeded };
        Assert.Equal(1, (await analytics.GetSummaryAsync(range, ScopeContext.ForBusiness(businessId))).VerifiedLeads);
        Assert.Equal(1, (await analytics.GetConversionsAsync(range, ScopeContext.ForBusiness(businessId))).TotalConversions);
    }


    [Theory]
    [InlineData(TrafficQualityMode.RealHumanTraffic, 0, 1)]
    [InlineData(TrafficQualityMode.LikelyBotsAutomation, 1, 0)]
    [InlineData(TrafficQualityMode.AllTraffic, 1, 1)]
    public async Task SharedHealthPreservesBothPipeContainingEvidenceDomains(
        TrafficQualityMode mode, int healthAttempts, int metaEvents)
    {
        await using var fixture = await HealthReadsFixture.CreateAsync(mode);
        var analytics = fixture.Analytics;
        var meta = new MetaSignalAnalyticsService(fixture.Db, analytics);
        var baselineHealth = await analytics.GetMarketingHealthAsync(fixture.Range, fixture.Scope);
        var baselineMeta = await meta.GetHealthDashboardAsync(fixture.Range, fixture.Scope);
        fixture.Reads.Begin();

        var snapshot = await analytics.GetMarketingHealthSnapshotAsync(fixture.Range, fixture.Scope);
        var sharedMeta = await meta.GetHealthDashboardAsync(snapshot);

        Assert.Equal(healthAttempts, snapshot.Health.WorkstationCaptureAttempts);
        Assert.Equal(metaEvents, sharedMeta.PipelineHealth.AnalyticsEventsLast24Hours);
        Assert.Equal(metaEvents, sharedMeta.PipelineHealth.MetaSignalEventsLast24Hours);
        Assert.Equal(1, sharedMeta.PipelineHealth.WebsiteLeadsLast24Hours);
        Assert.Equal(JsonSerializer.Serialize(baselineHealth), JsonSerializer.Serialize(snapshot.Health));
        Assert.Equal(StableMeta(baselineMeta), StableMeta(sharedMeta));
        AssertHealthReadCounts(fixture.Reads, mode == TrafficQualityMode.AllTraffic ? 4 : 5);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Assert.Equal(new[] { "Health" }, serialized.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task SharedHealthProjectsEventSessionMembershipWithDatabaseCollation(bool noCase, int metaEvents)
    {
        await using var fixture = await HealthReadsFixture.CreateAsync(
            TrafficQualityMode.RealHumanTraffic, noCase: noCase, collationVariant: true);
        var meta = new MetaSignalAnalyticsService(fixture.Db, fixture.Analytics);
        var baseline = await meta.GetHealthDashboardAsync(fixture.Range, fixture.Scope);
        fixture.Reads.Begin();
        var snapshot = await fixture.Analytics.GetMarketingHealthSnapshotAsync(fixture.Range, fixture.Scope);
        var shared = await meta.GetHealthDashboardAsync(snapshot);

        Assert.Equal(0, snapshot.Health.WorkstationCaptureAttempts);
        Assert.Equal(metaEvents, shared.PipelineHealth.AnalyticsEventsLast24Hours);
        Assert.Equal(metaEvents, shared.PipelineHealth.MetaSignalEventsLast24Hours);
        Assert.Equal(1, shared.PipelineHealth.WebsiteLeadsLast24Hours);
        Assert.Equal(StableMeta(baseline), StableMeta(shared));
        AssertHealthReadCounts(fixture.Reads, 5);
        var sql = Assert.Single(fixture.Reads.Commands.Where(s => s.Contains("ForEventSession", StringComparison.Ordinal)));
        var projection = sql[..sql.IndexOf("FROM ", StringComparison.OrdinalIgnoreCase)];
        Assert.Contains(" IN ", projection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SessionId", projection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedHealthRetainsAliasOwnerAndAllSourceMetaSemantics()
    {
        await using var fixture = await HealthReadsFixture.CreateAsync(
            TrafficQualityMode.RealHumanTraffic, agentScope: true);
        var meta = new MetaSignalAnalyticsService(fixture.Db, fixture.Analytics);
        var baseline = await meta.GetHealthDashboardAsync(fixture.Range, fixture.Scope);
        fixture.Reads.Begin();
        var snapshot = await fixture.Analytics.GetMarketingHealthSnapshotAsync(
            fixture.Range, fixture.Scope, TrafficType.PaidAds);
        var shared = await meta.GetHealthDashboardAsync(snapshot);

        Assert.Equal(0, snapshot.Health.WorkstationCaptureAttempts);
        Assert.Equal(1, shared.PipelineHealth.AnalyticsEventsLast24Hours);
        Assert.Equal(1, shared.PipelineHealth.MetaSignalEventsLast24Hours);
        Assert.Equal(0, shared.PipelineHealth.WebsiteLeadsLast24Hours);
        Assert.Equal(StableMeta(baseline), StableMeta(shared));
        Assert.Equal(7, fixture.Reads.Commands.Count);
        Assert.Equal(2, fixture.Reads.Commands.Count(s => s.Contains("FROM \"AgentTrackingProfiles\"", StringComparison.Ordinal)));
        Assert.Same(fixture.Scope, snapshot.Scope);
        Assert.Same(fixture.Range, snapshot.Range);
        Assert.Equal(2, snapshot.ScopedAgentIds!.Length);
    }

    public static IEnumerable<object[]> CanceledAnalyticsReadStages()
    {
        foreach (var (operation, count) in new[] { ("summary", 6), ("health", 3), ("projection", 5), ("attributed", 2) })
        {
            for (var stage = 1; stage <= count; stage++)
                yield return new object[] { operation, false, stage };
            // The same datasets follow the alias resolver. Exercise both actual alias reads.
            yield return new object[] { operation, true, 1 };
            yield return new object[] { operation, true, 2 };
        }
    }

    [Theory]
    [MemberData(nameof(CanceledAnalyticsReadStages))]
    public async Task CallerCancellationStopsAtTheActualReadWithoutLaterQueries(string operation, bool agentScope, int stage)
    {
        await using var fixture = await HealthReadsFixture.CreateAsync(
            TrafficQualityMode.RealHumanTraffic, agentScope: agentScope);
        using var cancellation = new CancellationTokenSource();
        fixture.Reads.Begin(cancellation, stage);
        var meta = new MetaSignalAnalyticsService(fixture.Db, fixture.Analytics);
        Task action = operation switch
        {
            "summary" => fixture.Analytics.GetSummaryAsync(fixture.Range, fixture.Scope, TrafficType.All, cancellation.Token),
            "health" => fixture.Analytics.GetMarketingHealthAsync(fixture.Range, fixture.Scope, TrafficType.All, cancellation.Token),
            "projection" => MarketingHealthProjection.LoadAsync(fixture.Analytics, meta, fixture.Range, fixture.Scope,
                TrafficType.All, NullLogger.Instance, cancellation.Token),
            _ => fixture.Analytics.LoadAttributedEventsAsync(fixture.Range, fixture.Scope, TrafficType.All, cancellation.Token)
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Equal(stage, fixture.Reads.Commands.Count);
        Assert.All(fixture.Reads.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    private static string StableMeta(MetaSignalHealthDashboardDto value)
    {
        var json = JsonSerializer.SerializeToNode(value)!;
        json[nameof(MetaSignalHealthDashboardDto.LastUpdatedUtc)] = null;
        return json.ToJsonString();
    }

    private static void AssertHealthReadCounts(AnalyticsReadRecorder reads, int expected)
    {
        Assert.Equal(expected, reads.Commands.Count);
        Assert.Equal(expected == 4 ? 1 : 2, reads.Commands.Count(s => s.Contains("FROM \"AnalyticsEvents\"", StringComparison.Ordinal)));
        Assert.Equal(2, reads.Commands.Count(s => s.Contains("FROM \"WebsiteLeads\"", StringComparison.Ordinal)));
        Assert.Equal(1, reads.Commands.Count(s => s.Contains("FROM \"MetaSignalEvents\"", StringComparison.Ordinal)));
    }

    private sealed class AnalyticsReadRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        private bool _record;
        private CancellationTokenSource? _cancel;
        private int _cancelAt;

        public void Begin(CancellationTokenSource? cancel = null, int cancelAt = 0)
        {
            Commands.Clear();
            Tokens.Clear();
            _cancel = cancel;
            _cancelAt = cancelAt;
            _record = true;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_record)
            {
                Commands.Add(command.CommandText);
                Tokens.Add(cancellationToken);
                if (Commands.Count == _cancelAt) _cancel!.Cancel();
                // Test the token received by the database command, not merely the caller's CTS.
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class NoCaseAnalyticsDb : MasterAppDbContext
    {
        public NoCaseAnalyticsDb(DbContextOptions<MasterAppDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AnalyticsEvent>().Property(e => e.SessionId).UseCollation("NOCASE");
        }
    }

    private sealed class HealthReadsFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public MasterAppDbContext Db { get; }
        public AnalyticsReadRecorder Reads { get; }
        public AnalyticsQueryService Analytics { get; }
        public TimeRangeRequest Range { get; }
        public ScopeContext Scope { get; }

        private HealthReadsFixture(SqliteConnection connection, MasterAppDbContext db, AnalyticsReadRecorder reads,
            TimeRangeRequest range, ScopeContext scope)
        {
            _connection = connection;
            Db = db;
            Reads = reads;
            Range = range;
            Scope = scope;
            Analytics = new AnalyticsQueryService(db, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Analytics:EnvironmentFilter"] = "production" }).Build());
        }

        public static async Task<HealthReadsFixture> CreateAsync(TrafficQualityMode mode,
            bool noCase = false, bool collationVariant = false, bool agentScope = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var reads = new AnalyticsReadRecorder();
            var options = new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlite(connection).AddInterceptors(reads).Options;
            MasterAppDbContext db = noCase ? new NoCaseAnalyticsDb(options) : new MasterAppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var business = Guid.NewGuid();
            var foreign = Guid.NewGuid();
            db.CommerceBusinesses.AddRange(
                new CommerceBusiness { Id = business, Key = "health-owner", DisplayName = "Health owner" },
                new CommerceBusiness { Id = foreign, Key = "foreign-owner", DisplayName = "Foreign owner" });
            var profile = Guid.NewGuid();
            var alias = Guid.NewGuid();
            if (agentScope)
                db.AgentTrackingProfiles.AddRange(
                    new AgentTrackingProfile { Id = profile, AgentUpn = "health-owner@example.test", AgentUserId = "health-owner", Slug = "health-owner" },
                    new AgentTrackingProfile { Id = alias, AgentUpn = "health-owner@example.test", AgentUserId = "health-alias", Slug = "health-alias" });
            var now = DateTime.UtcNow;
            var range = new TimeRangeRequest
            {
                FromUtc = now.AddHours(-1), ToUtc = now.AddMinutes(-15), QualityMode = mode,
                Label = "collision-fixture", Preset = "custom", ViewerTimeZone = TimeZoneInfo.Utc
            };
            var leadSession = collationVariant ? "checkout|Lead" : "checkout|lead";
            var eventVisitor = collationVariant ? "Lead|visitor" : "lead|visitor";
            var current = Event(business, now.AddMinutes(-30), "workstation_capture_attempt", "checkout", eventVisitor, "/", strongHuman: true);
            var historical = Event(business, now.AddHours(-2), "page_engaged_15s", leadSession, "visitor", "/");
            historical.WebDriver = true;
            var previous = Event(business, now.AddMinutes(-80), "page_view", "previous-session", "previous-visitor", "/", strongHuman: true);
            var development = Event(business, now.AddMinutes(-35), "page_view", "development-session", "development-visitor", "/", strongHuman: true);
            development.Environment = "development";
            var ownedEvents = new List<AnalyticsEvent> { current, historical, previous, development };
            if (collationVariant)
                ownedEvents.Add(Event(business, now.AddMinutes(-35), "page_view", "CHECKOUT|LEAD", "sentinel", "/"));
            if (agentScope)
                foreach (var row in ownedEvents)
                {
                    row.CommerceBusinessId = null;
                    row.AgentTrackingProfileId = row == historical ? alias : profile;
                }
            db.AnalyticsEvents.AddRange(ownedEvents);
            var foreignEvidence = Event(foreign, now.AddMinutes(-31), "page_engaged_15s", "checkout", eventVisitor, "/");
            foreignEvidence.WebDriver = true;
            db.AnalyticsEvents.Add(foreignEvidence);
            WebsiteLead Lead(Guid owner, DateTime utc, string session, string visitor) => new()
            {
                LeadId = Guid.NewGuid(), CommerceBusinessId = owner, FirstName = "Health fixture",
                Email = "health-fixture@example.test", SessionId = session, VisitorId = visitor,
                CreatedUtc = utc, Environment = "production", Host = "business.example.org",
                TermsAccepted = false, MarketingEmailConsent = false, CallTextConsent = false
            };
            var currentLead = Lead(business, now.AddMinutes(-25), leadSession, "visitor");
            var previousLead = Lead(business, now.AddMinutes(-70), "previous-session", "previous-visitor");
            if (agentScope)
                foreach (var lead in new[] { currentLead, previousLead })
                {
                    lead.CommerceBusinessId = null;
                    lead.AgentTrackingProfileId = alias;
                }
            db.WebsiteLeads.AddRange(currentLead, previousLead, Lead(foreign, now.AddMinutes(-25), leadSession, "visitor"));
            MetaSignalEvent Signal(Guid owner) => new()
            {
                EventId = Guid.NewGuid().ToString("N"), EventName = "PageView", EventCategory = "engagement",
                CreatedUtc = now.AddMinutes(-29), CommerceBusinessId = owner, SessionId = "checkout",
                VisitorId = eventVisitor, Environment = "production", Host = "business.example.org"
            };
            var currentSignal = Signal(business);
            if (agentScope)
            {
                currentSignal.CommerceBusinessId = null;
                currentSignal.AgentTrackingProfileId = profile;
            }
            db.MetaSignalEvents.AddRange(currentSignal, Signal(foreign));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new HealthReadsFixture(connection, db, reads, range,
                agentScope ? ScopeContext.ForAgent(profile) : ScopeContext.ForBusiness(business));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static AnalyticsEvent Event(
        Guid businessId,
        DateTime utc,
        string eventType,
        string sessionId,
        string visitorId,
        string pageKey,
        bool bounce = false,
        bool strongHuman = false,
        string? elementKey = null) => new()
    {
        EventId = Guid.NewGuid(),
        ClientEventId = Guid.NewGuid(),
        CommerceBusinessId = businessId,
        AgentTrackingProfileId = null,
        EventType = eventType,
        EventUtc = utc,
        ReceivedUtc = utc,
        SessionId = sessionId,
        VisitorId = visitorId,
        PageKey = pageKey,
        ElementKey = elementKey,
        Environment = "production",
        Host = "business.example.org",
        UserAgent = "Mozilla/5.0",
        WebDriver = false,
        IsHeadless = false,
        IsInternal = false,
        IsBounceCandidate = bounce,
        IsExitPage = false,
        EngagedMilliseconds = strongHuman ? 15000 : 0,
        DwellMilliseconds = strongHuman ? 20000 : 100,
        ScrollPercent = strongHuman ? 80 : 0,
        HumanInteractionCount = strongHuman ? 5 : 0,
        MouseMoveCount = strongHuman ? 20 : 0
    };
}

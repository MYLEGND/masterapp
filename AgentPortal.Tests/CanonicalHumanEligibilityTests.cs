using System;
using System.Linq;
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
}

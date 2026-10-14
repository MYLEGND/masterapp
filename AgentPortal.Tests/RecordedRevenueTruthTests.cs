using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Reflection;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Domain.Entities;
using Infrastructure.Analytics;
using Shared.Analytics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class RecordedRevenueTruthTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("malformed")]
    [InlineData("{\"valueCents\":\"invalid\"}")]
    [InlineData("{\"valueCents\":100,\"currency\":\"EUR\"}")]
    public void UnknownRecordedValuePropagatesThroughTotalsAndEconomics(string metadata)
    {
        var rows = new[] { Purchase(1, "{\"valueCents\":5000}"), Purchase(2, metadata) };
        var totals = CanonicalMarketingOutcomeProjection.Totals(rows);
        Assert.Null(totals.Revenue);
        var owner = MarketingOwnerScope.Founder;
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-1), ToUtc = DateTime.UtcNow };
        var snapshot = new UnifiedChannelPerformanceSnapshot(owner, range.FromUtc, range.ToUtc, DateTime.UtcNow, [], totals,
            [new(MarketingChannels.ChatGptAds, 10, 100, 5, 0, 0, 0, 2, null, null, "observed", "canonical")], []);
        var economics = BlendedGrowthEconomicsService.Calculate(owner, range, snapshot, rows);
        Assert.Null(economics.TotalRevenue); Assert.Null(economics.BlendedRoas);
        Assert.Null(Assert.Single(economics.Channels).Revenue); Assert.Null(economics.Channels[0].Roas);
    }

    [Fact]
    public void EmptyAndExplicitZeroAreKnownWhileDuplicateRecordedValuesCountOnce()
    {
        Assert.Equal(0m, CanonicalMarketingOutcomeProjection.Totals([]).Revenue);
        Assert.Equal(0m, CanonicalMarketingOutcomeProjection.Totals([Purchase(1, "{\"valueCents\":0}")]).Revenue);
        var one = Purchase(1, "{\"valueCents\":1200,\"currency\":\"USD\"}");
        var two = Purchase(2, "{\"amount\":2.5,\"currency\":\"USD\"}");
        Assert.Equal(14.5m, CanonicalMarketingOutcomeProjection.Totals([one, one, two]).Revenue);
    }

    [Fact]
    public void ExistingCrmLatestCorrectionAndDeletionRemainAuthoritative()
    {
        var old = Purchase(1, "{\"valueCents\":1000,\"productionRecordId\":\"record-one\"}");
        var corrected = Purchase(2, "{\"valueCents\":400,\"productionRecordId\":\"record-one\"}");
        old.TrackingVersion = corrected.TrackingVersion = "crm-production-state-v1";
        Assert.Equal(4m, CanonicalMarketingOutcomeProjection.Totals([old, corrected]).Revenue);
        corrected.MetadataJson = "{\"productionRecordId\":\"record-one\",\"productionDeleted\":true}";
        Assert.Equal(0m, CanonicalMarketingOutcomeProjection.Totals([old, corrected]).Revenue);
        Assert.Equal(0m, CanonicalMarketingOutcomeProjection.ReadMoney("{}")); // CRM comparison compatibility.
    }

    [Fact]
    public void AiRedactionPreservesUnknownRecordedRevenueAndExpectedValue()
    {
        var safe = WebsiteAnalyticsAiRedactor.Redact(new AiSafeAnalyticsPayload {
            Channels = [new(MarketingChannels.ChatGptAds, 10, 100, 5, 0, 0, 0, 1, null, null, "unavailable")],
            OutcomeCalibration = new OutcomeCalibrationAiPayload { Signals = [new() { Signal = "LeadReadySignal", ObservedRevenue = null, ExpectedRevenuePerObservedVisitor = null }] }
        });
        Assert.Null(Assert.Single(safe.Channels).Revenue);
        Assert.Null(Assert.Single(safe.OutcomeCalibration!.Signals).ObservedRevenue);
        Assert.Null(safe.OutcomeCalibration.Signals[0].ExpectedRevenuePerObservedVisitor);
    }

    [Fact]
    public async Task ActualAiCalibrationPreservesMissingRecordedValue()
    {
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var purchase = Purchase(2, "{}");
        purchase.VisitorId = "visitor-one";
        var signal = new AnalyticsEvent { Id = 1, EventId = Guid.NewGuid(), EventType = "LeadReadySignal",
            EventUtc = purchase.EventUtc.AddMinutes(-1), VisitorId = "visitor-one" };
        db.AnalyticsEvents.AddRange(signal, purchase);
        await db.SaveChangesAsync();
        var range = new TimeRangeRequest { FromUtc = DateTime.UtcNow.AddDays(-1), ToUtc = DateTime.UtcNow.AddDays(1) };
        var scope = ScopeContext.ForAgent(Guid.NewGuid());
        var analytics = new Mock<IAnalyticsQueryService>();
        analytics.Setup(x => x.ScopedEvents(range, scope)).Returns(db.AnalyticsEvents);
        var builder = new WebsiteAnalyticsAiDataBuilder(analytics.Object, null!, null!,
            NullLogger<WebsiteAnalyticsAiDataBuilder>.Instance, db, null!, null!, null!);
        var task = (Task<OutcomeCalibrationAiPayload>)typeof(WebsiteAnalyticsAiDataBuilder)
            .GetMethod("BuildOutcomeCalibrationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(builder, [range, scope, CancellationToken.None])!;
        var row = (await task).Signals.Single(x => x.Signal == "LeadReadySignal");
        Assert.Equal(1, row.PaidCustomers);
        Assert.Null(row.ObservedRevenue); Assert.Null(row.ExpectedRevenuePerObservedVisitor);
    }

    private static AnalyticsEvent Purchase(long id, string metadata)
    {
        var row = UnifiedEventMapper.ToAnalytics(new UnifiedEventContext {
            EventName = "Purchase", EventUtc = DateTime.UtcNow.AddSeconds(id), IsServerAuthority = true,
            Oppref = "recorded-click", Metadata = new { valueCents = 0 }
        });
        row.Id = id;
        row.TrackingVersion = "crm-production-state-v1";
        row.MetadataJson = metadata;
        return row;
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Analytics;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AgentPortal.Tests;

public sealed class CanonicalProductionTruthTests
{
    private static AgentTrackingProfile Profile() => new() { Id = Guid.NewGuid(), AgentUserId = "agent-test",
        AgentUpn = "agent@example.test", Slug = "test-agent", Status = "active" };
    private static ProductionRecord Record() => new() { AgentUserId = "agent-test", Status = ProductionStatus.Paid,
        Side = ProductionSide.Client, ClientUserId = "client-test", Amount = 20000, PersonalAmount = 1000 };

    [Fact]
    public async Task CorrectionAndDeletionReconcileReportingWithoutCreatingAnotherProviderPurchase()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AgentTrackingProfiles.Add(Profile()); await db.SaveChangesAsync();
        var record = Record(); db.ProductionRecords.Add(record);
        await CanonicalCrmOutcomeService.SaveProductionChangesAsync(db);
        var original = await db.AnalyticsEvents.SingleAsync(e => e.TrackingVersion == "crm-production-authority-v1");
        var identity = original.EventId; var originalJson = original.MetadataJson;
        record.PersonalAmount = 400;
        await CanonicalCrmOutcomeService.SaveProductionChangesAsync(db);
        Assert.Equal("production_outcome_requires_reconciliation", (await CanonicalMarketingEligibility.ResolveAsync(db, original)).Reason);
        var corrected = CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(await db.AnalyticsEvents.ToListAsync());
        Assert.Equal(400m, CanonicalMarketingOutcomeProjection.ReadMoney(Assert.Single(corrected).MetadataJson));
        Assert.Equal(1, CanonicalMarketingOutcomeProjection.CustomerCount(corrected));
        Assert.Single((await db.AnalyticsEvents.ToListAsync()).Where(CanonicalAdvertisingEventProjection.CanProjectServer));
        Assert.Equal(identity, original.EventId); Assert.Equal(originalJson, original.MetadataJson);
        var before = await db.AnalyticsEvents.CountAsync(); record.Notes = "note-only update";
        await CanonicalCrmOutcomeService.SaveProductionChangesAsync(db);
        Assert.Equal(before, await db.AnalyticsEvents.CountAsync());
        db.ProductionRecords.Remove(record);
        await CanonicalCrmOutcomeService.SaveProductionChangesAsync(db);
        Assert.Empty(CanonicalMarketingOutcomeProjection.ConfirmedOutcomes(await db.AnalyticsEvents.ToListAsync()));
        Assert.Single((await db.AnalyticsEvents.ToListAsync()).Where(CanonicalAdvertisingEventProjection.CanProjectServer));
    }

    [Fact]
    public async Task CanonicalOutcomeFailureRollsBackTheProductionMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var failure = new FailAnalyticsWrite();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(failure).Options;
        await using var db = new MasterAppDbContext(options); await db.Database.EnsureCreatedAsync();
        db.AgentTrackingProfiles.Add(Profile()); await db.SaveChangesAsync();
        db.ProductionRecords.Add(Record()); failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CanonicalCrmOutcomeService.SaveProductionChangesAsync(db));
        await using var verification = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).Options);
        Assert.Empty(await verification.ProductionRecords.ToListAsync());
        Assert.Empty(await verification.AnalyticsEvents.ToListAsync());
    }

    [Fact]
    public void SeparateDealsForOneCustomerDoNotCollapsePipelineOrMultiplyCustomers()
    {
        AnalyticsEvent Outcome(string deal, string eventType, int value) => new() {
            EventId = Guid.NewGuid(), EventType = eventType, TrackingVersion = "crm-production-state-v1",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new {productionRecordId=deal,clientUserId="same-client",valueCents=value,currency="USD"})
        };
        var rows = new[] { Outcome("one","PolicyIssued",10000),Outcome("two","PolicyIssued",20000),
            Outcome("three","PolicyPaid",30000),Outcome("four","PolicyPaid",40000) };
        Assert.Equal(300m,CanonicalMarketingOutcomeProjection.PipelineValue(rows));
        Assert.Equal(1,CanonicalMarketingOutcomeProjection.CustomerCount(rows));
    }

    private sealed class FailAnalyticsWrite : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<AnalyticsEvent>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Simulated canonical write failure");
            return ValueTask.FromResult(result);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendTranslationQualitySummaryTests
{
    [Fact]
    public async Task Summary_PreservesReviewEligibilityAndEvidenceCounts_WithoutReadingText()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var observer = new QueryObserver();
        await using var db = CreateDb(connection, observer);
        await db.Database.EnsureCreatedAsync();
        var active = AddObservation(db);
        AddEvidence(db, active, "Supported");
        AddEvidence(db, active, "Supported"); // Count observations, not evidence rows.
        AddObservation(db, humanVerified: true);
        AddObservation(db, eligibleSource: false);
        AddObservation(db, eligibleTarget: false);
        AddObservation(db, supersededAlignment: true);
        AddObservation(db, founderSource: false);
        AddObservation(db, supersededEvidence: true);
        AddObservation(db, closedEvidence: true);
        await db.SaveChangesAsync();
        var intelligence = new LegendConnectTranslationIntelligence(db, new ConfigurationBuilder().Build());
        var full = await intelligence.GetTranslationQualityAsync();
        observer.Commands.Clear();

        var summary = await intelligence.GetTranslationQualitySummaryAsync();

        Assert.Equal(1, summary.NeedsReviewCount);
        Assert.Equal(5, summary.ProviderObservationCount);
        Assert.Equal(1, summary.SupportedObservationCount);
        Assert.Equal(4, summary.ContradictionCount);
        Assert.Equal(1, summary.HumanVerifiedAlignmentCount);
        Assert.Equal(full with { ReviewItems = summary.ReviewItems }, summary);
        Assert.Empty(summary.ReviewItems);
        Assert.Equal(5, observer.Commands.Count);
        Assert.All(observer.Commands, sql =>
        {
            Assert.Contains("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"Text\"", sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Summary_RetainsExistingReviewQueueCap_AndPropagatesCancellation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection, new QueryObserver());
        await db.Database.EnsureCreatedAsync();
        for (var i = 0; i < 251; i++) AddObservation(db);
        await db.SaveChangesAsync();
        var intelligence = new LegendConnectTranslationIntelligence(db, new ConfigurationBuilder().Build());
        var summary = await intelligence.GetTranslationQualitySummaryAsync();
        Assert.Equal(250, summary.NeedsReviewCount);
        Assert.Equal(251, summary.ProviderObservationCount);
        Assert.Equal((await intelligence.GetTranslationQualityAsync()).NeedsReviewCount, summary.NeedsReviewCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            intelligence.GetTranslationQualitySummaryAsync(new CancellationToken(canceled: true)));
    }

    private static MasterAppDbContext CreateDb(SqliteConnection connection, QueryObserver observer) =>
        new(new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(connection).AddInterceptors(observer).Options);

    private static LegendTranslationAlignment AddObservation(MasterAppDbContext db,
        bool humanVerified = false, bool eligibleSource = true, bool eligibleTarget = true,
        bool supersededAlignment = false, bool founderSource = true,
        bool supersededEvidence = false, bool closedEvidence = false)
    {
        LegendLanguageTextUnit Unit(string language, bool eligible, string provenance) => new()
        {
            Id = Guid.NewGuid(), LanguageCode = language, StoragePartition = language,
            Text = "quality summary fixture " + Guid.NewGuid(), NormalizedHash = Guid.NewGuid().ToString("N"),
            Provenance = provenance, IsTrainingEligible = eligible
        };
        var source = Unit("en", eligibleSource, founderSource ? "FounderApproved" : "ProviderDerived");
        var target = Unit("ht", eligibleTarget, "ProviderDerived");
        var alignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(), PairKey = "en|ht", SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
            Provenance = "ProviderDerived", Provider = "AzureTranslator", HumanVerified = humanVerified,
            SupersededUtc = supersededAlignment ? DateTime.UtcNow : null
        };
        db.AddRange(source, target, alignment);
        var evidence = AddEvidence(db, alignment, "Contradictory");
        evidence.SupersededUtc = supersededEvidence ? DateTime.UtcNow : null;
        evidence.ResolutionState = closedEvidence ? "Approved" : "Open";
        return alignment;
    }

    private static LegendTranslationQualityEvidence AddEvidence(MasterAppDbContext db,
        LegendTranslationAlignment alignment, string signal)
    {
        var evidence = new LegendTranslationQualityEvidence
        {
            Id = Guid.NewGuid(), ObservedAlignmentId = alignment.Id, PairKey = alignment.PairKey,
            SourceTextUnitId = alignment.SourceTextUnitId, TargetTextUnitId = alignment.TargetTextUnitId,
            Signal = signal, ReasonCode = "summary_fixture", EvidenceIdentity = Guid.NewGuid().ToString("N")
        };
        db.Add(evidence);
        return evidence;
    }

    private sealed class QueryObserver : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

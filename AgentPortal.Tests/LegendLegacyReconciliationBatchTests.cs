using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendLegacyReconciliationBatchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public async Task HistoricalBatch_RecoversExactSourceArtifactsWithTwoDependentReadsPerTick(int sourceCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new DependentReadCounter();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en", "ht", "es");
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var authority = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var sources = new List<LegendLanguageTextUnit>();
        var retiredCandidateIds = new List<Guid>();
        var retiredEventIds = new List<Guid>();
        for (var index = 0; index < sourceCount; index++)
        {
            var text = $"Retained source {index}. Second sentence.";
            var source = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(), LanguageCode = "en", StoragePartition = "/en",
                Text = text, NormalizedHash = LegendLanguageIdentity.TextHash(text),
                Provenance = "FounderApproved", IsTrainingEligible = false
            };
            sources.Add(source);
            var candidate = Candidate("en", source.NormalizedHash);
            var learningEvent = LearningEvent("en", source.NormalizedHash);
            retiredCandidateIds.Add(candidate.Id);
            retiredEventIds.Add(learningEvent.Id);
            db.AddRange(source, candidate, learningEvent, new LegendFounderTrainingSubmission
            {
                Id = Guid.NewGuid(), SourceLanguageCode = "en", RawText = text,
                RawTextHash = source.NormalizedHash, LegacySourceTextUnitId = source.Id,
                RawCharacterCount = text.Length, AtomicUnitCount = 2, ProcessingState = "Reconciled"
            });
        }
        // Same hash in another source language is a different directional
        // lineage. Another hash in the selected language is unrelated too.
        var unrelatedCandidates = new[]
        {
            Candidate("ht", sources[0].NormalizedHash),
            Candidate("en", LegendLanguageIdentity.TextHash("Unrelated source."))
        };
        var unrelatedEvents = new[]
        {
            LearningEvent("ht", sources[0].NormalizedHash),
            LearningEvent("en", LegendLanguageIdentity.TextHash("Unrelated source."))
        };
        db.AddRange(unrelatedCandidates);
        db.AddRange(unrelatedEvents);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        reads.Reset();
        Assert.True(await ReconcileKnownAsync(authority) > 0);
        Assert.Equal(1, reads.CandidateReads);
        Assert.Equal(1, reads.LearningReads);
        db.ChangeTracker.Clear();
        var candidates = await db.LegendCorpusCandidates.AsNoTracking().ToListAsync();
        var events = await db.LegendTranslationLearningEvents.AsNoTracking().ToListAsync();
        foreach (var candidate in candidates.Where(item => retiredCandidateIds.Contains(item.Id)))
        {
            Assert.False(candidate.IsApproved);
            Assert.Equal("Superseded", candidate.ProcessingState);
            Assert.Equal("legacy_multi_unit_reconciled", candidate.FailureCode);
            Assert.Null(candidate.LeaseExpiresUtc);
        }
        foreach (var learningEvent in events.Where(item => retiredEventIds.Contains(item.Id)))
        {
            Assert.Equal("Superseded", learningEvent.ProcessingState);
            Assert.Equal("Superseded", learningEvent.PromotionOutcome);
            Assert.Equal("legacy_multi_unit_reconciled", learningEvent.FailureCode);
            Assert.Null(learningEvent.LeaseExpiresUtc);
        }
        Assert.All(candidates.Where(item => unrelatedCandidates.Any(other => other.Id == item.Id)),
            item => { Assert.True(item.IsApproved); Assert.Equal("Queued", item.ProcessingState); });
        Assert.All(events.Where(item => unrelatedEvents.Any(other => other.Id == item.Id)),
            item => Assert.Equal("Processed", item.ProcessingState));
        var candidateTimes = candidates.ToDictionary(item => item.Id, item => item.ProcessedUtc);
        var eventTimes = events.ToDictionary(item => item.Id, item => item.ProcessedUtc);

        reads.Reset();
        Assert.Equal(0, await ReconcileKnownAsync(authority));
        Assert.Equal(1, reads.CandidateReads);
        Assert.Equal(1, reads.LearningReads);
        db.ChangeTracker.Clear();
        Assert.All(await db.LegendCorpusCandidates.AsNoTracking().ToListAsync(),
            item => Assert.Equal(candidateTimes[item.Id], item.ProcessedUtc));
        Assert.All(await db.LegendTranslationLearningEvents.AsNoTracking().ToListAsync(),
            item => Assert.Equal(eventTimes[item.Id], item.ProcessedUtc));

        // A later writer can leave artifacts after a completed batch. No
        // completion marker or source-eligibility filter hides that recovery.
        var lateCandidate = Candidate("en", sources[0].NormalizedHash);
        var lateEvent = LearningEvent("en", sources[0].NormalizedHash);
        db.AddRange(lateCandidate, lateEvent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        reads.Reset();
        Assert.True(await ReconcileKnownAsync(authority) > 0);
        Assert.Equal(1, reads.CandidateReads);
        Assert.Equal(1, reads.LearningReads);
        db.ChangeTracker.Clear();
        Assert.Equal("Superseded", (await db.LegendCorpusCandidates.FindAsync(lateCandidate.Id))!.ProcessingState);
        Assert.Equal("Superseded", (await db.LegendTranslationLearningEvents.FindAsync(lateEvent.Id))!.ProcessingState);
    }

    private static Task<int> ReconcileKnownAsync(LegendConnectFounderTrainingIngestionAuthority authority)
    {
        var method = typeof(LegendConnectFounderTrainingIngestionAuthority).GetMethod(
            "ReconcileKnownLegacyDerivedArtifactsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<int>)method.Invoke(authority, [25, CancellationToken.None])!;
    }

    private static LegendCorpusCandidate Candidate(string language, string hash) => new()
    {
        Id = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid().ToString("N"),
        SourceLanguageCode = language, TargetLanguageCode = "es", SourceText = "Bounded legacy fixture.",
        SourceTextHash = hash, Category = "FounderApprovedSeed", Provenance = "FounderApproved",
        IsApproved = true, ProcessingState = "Queued", LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(5)
    };

    private static LegendTranslationLearningEvent LearningEvent(string language, string hash) => new()
    {
        Id = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid().ToString("N"),
        SourceLanguageCode = language, TargetLanguageCode = "es", PairKey = language + ":es",
        SourceTextHash = hash, TargetTextHash = LegendLanguageIdentity.TextHash("Fixture target."),
        SourceText = "Bounded legacy fixture.", TargetText = "Fixture target.", Provider = "AzureTranslator",
        Provenance = "FounderApproved", EligibilityState = "Eligible", ProcessingState = "Processed",
        PromotionOutcome = "Promoted", LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(5)
    };

    private sealed class DependentReadCounter : DbCommandInterceptor
    {
        public int CandidateReads { get; private set; }
        public int LearningReads { get; private set; }
        public void Reset() { CandidateReads = 0; LearningReads = 0; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (command.CommandText.Contains("\"LegendCorpusCandidates\"", StringComparison.Ordinal)) CandidateReads++;
                if (command.CommandText.Contains("\"LegendTranslationLearningEvents\"", StringComparison.Ordinal)) LearningReads++;
            }
            return ValueTask.FromResult(result);
        }
    }
}

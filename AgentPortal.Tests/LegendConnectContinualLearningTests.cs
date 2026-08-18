using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectContinualLearningTests
{
    [Fact]
    public async Task DemandRecorder_AggregatesNeuralOutcomesWithoutDoubleCountingRequests()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var recorder =
            new TranslationDemandRecorder(
                db,
                NullLogger<TranslationDemandRecorder>.Instance);

        await recorder.TryRecordAsync(
            "en:ht",
            0,
            neuralModelServed: true);

        var first =
            await db.Set<LegendTranslationPairDemand>()
                .SingleAsync(
                    item => item.PairKey == "en:ht");

        Assert.Equal(1, first.TranslationRequestCount);
        Assert.Equal(1, first.NeuralModelServeCount);
        Assert.Equal(0, first.NeuralModelFailureCount);
        Assert.Equal(0, first.ProviderObservationReuseCount);
        Assert.Equal(0, first.AzureFallbackCount);

        await recorder.TryRecordAsync(
            "en:ht",
            0,
            azureFallback: true,
            neuralModelFailed: true);

        await recorder.TryRecordAsync(
            "en:ht",
            0,
            translationMemoryHit: true,
            neuralModelFailed: true,
            providerObservationReused: true);

        db.ChangeTracker.Clear();

        var final =
            await db.Set<LegendTranslationPairDemand>()
                .AsNoTracking()
                .SingleAsync(
                    item => item.PairKey == "en:ht");

        Assert.Equal(3, final.TranslationRequestCount);
        Assert.Equal(1, final.NeuralModelServeCount);
        Assert.Equal(2, final.NeuralModelFailureCount);
        Assert.Equal(1, final.ProviderObservationReuseCount);
        Assert.Equal(1, final.AzureFallbackCount);
        Assert.Equal(1, final.TranslationMemoryHitCount);
    }

    [Fact]
    public async Task GapPlanner_PrioritizesExistingApprovedCandidateWithNeuralWeakness()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>())
                .Build();

        var registry =
            new LegendLanguageRegistry(
                db,
                configuration);

        const string olderText =
            "older approved candidate";

        const string weakPairText =
            "neural weakness candidate";

        // These literals are already canonical for this behavioral fixture.
        // The production planner itself remains responsible for canonical
        // normalization; the test does not reach into its internal helper.
        var olderNormalized = olderText;
        var weakNormalized = weakPairText;

        var olderSource =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "/en",
                NormalizedHash =
                    "phase11-older-approved-source",
                Text = olderNormalized,
                Provenance = "FounderApproved",
                IsTrainingEligible = true
            };

        var weakSource =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "/en",
                NormalizedHash =
                    "phase11-neural-weakness-source",
                Text = weakNormalized,
                Provenance = "FounderApproved",
                IsTrainingEligible = true
            };

        var now = DateTime.UtcNow;

        var olderCandidate =
            new LegendCorpusCandidate
            {
                Id = Guid.NewGuid(),
                IdempotencyKey =
                    Guid.NewGuid().ToString("N"),
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                SourceText = olderNormalized,
                SourceTextHash =
                    olderSource.NormalizedHash,
                Category = "EverydayConversation",
                Provenance = "FounderApproved",
                IsApproved = true,
                Priority = 1,
                ProcessingState = "Pending",
                CreatedUtc =
                    now.AddMinutes(-10)
            };

        var weakCandidate =
            new LegendCorpusCandidate
            {
                Id = Guid.NewGuid(),
                IdempotencyKey =
                    Guid.NewGuid().ToString("N"),
                SourceLanguageCode = "en",
                TargetLanguageCode = "fr",
                SourceText = weakNormalized,
                SourceTextHash =
                    weakSource.NormalizedHash,
                Category = "EverydayConversation",
                Provenance = "FounderApproved",
                IsApproved = true,
                Priority = 1,
                ProcessingState = "Pending",
                CreatedUtc = now
            };

        db.AddRange(
            olderSource,
            weakSource,
            olderCandidate,
            weakCandidate);

        db.Add(
            new LegendTranslationPairDemand
            {
                Id = Guid.NewGuid(),
                PairKey = "en:fr",
                TranslationRequestCount = 0,
                NeuralModelFailureCount = 1,
                ProviderObservationReuseCount = 0,
                AzureFallbackCount = 0,
                LastRequestedUtc = now
            });

        await db.SaveChangesAsync();

        var planner =
            new LegendConnectAutonomousGapPlanner(
                db,
                registry);

        var selected =
            await planner.SelectApprovedGapAsync();

        Assert.Equal(
            weakCandidate.Id,
            selected);
    }
}

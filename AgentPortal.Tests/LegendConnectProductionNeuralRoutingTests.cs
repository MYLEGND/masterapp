using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectProductionNeuralRoutingTests
{
    [Fact]
    public async Task ActivePromotedModel_ServesBeforeProviderObservation()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        db.Add(
            new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                IsEnabled = true,
                ActiveModelVersion =
                    "ft:legend:active"
            });

        await db.SaveChangesAsync();

        var inference =
            new LegendConnectActiveModelInference(
                db,
                new FakeTransport(
                    "Neural translation"));

        var result =
            await inference.TryTranslateAsync(
                "en",
                "ht",
                "Hello");

        Assert.True(
            result.Succeeded);

        Assert.Equal(
            "Neural translation",
            result.Text);

        Assert.Equal(
            "ft:legend:active",
            result.ModelVersion);
    }

    [Fact]
    public async Task NoActiveModel_FailsClosedWithoutInventingVersion()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        db.Add(
            new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                IsEnabled = true,
                ActiveModelVersion = null
            });

        await db.SaveChangesAsync();

        var inference =
            new LegendConnectActiveModelInference(
                db,
                new FakeTransport(
                    "should-not-run"));

        var result =
            await inference.TryTranslateAsync(
                "en",
                "ht",
                "Hello");

        Assert.False(
            result.Succeeded);

        Assert.Equal(
            "active_model_unavailable",
            result.ErrorCode);

        Assert.Null(
            result.ModelVersion);
    }

    [Fact]
    public async Task TrustedExactMemory_ExcludesProviderDerivedObservation()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var source =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "en",
                NormalizedHash =
                    LegendLanguageIdentity.TextHash(
                        "Hello"),
                Text = "Hello",
                Provenance =
                    "FounderApproved",
                IsTrainingEligible = true
            };

        var target =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "ht",
                StoragePartition = "ht",
                NormalizedHash =
                    LegendLanguageIdentity.TextHash(
                        "Bonjou"),
                Text = "Bonjou",
                Provenance =
                    "ProviderDerived",
                IsTrainingEligible = true
            };

        db.AddRange(
            source,
            target);

        db.Add(
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = source.Id,
                TargetTextUnitId = target.Id,
                Provider = "AzureTranslator",
                Provenance = "ProviderDerived",
                QualityState = "Observation",
                Confidence = 0.99m,
                HumanVerified = false
            });

        await db.SaveChangesAsync();

        var intelligence =
            new LegendConnectTranslationIntelligence(
                db,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>())
                    .Build());

        var trusted =
            await intelligence
                .TryGetTrustedExactMemoryAsync(
                    "en",
                    "ht",
                    "Hello");

        var provider =
            await intelligence
                .TryGetReusableProviderObservationAsync(
                    "en",
                    "ht",
                    "Hello");

        Assert.Null(
            trusted);

        Assert.NotNull(
            provider);

        Assert.Equal(
            "ProviderDerived",
            provider!.Provenance);
    }

    [Fact]
    public async Task OpenProviderContradiction_BlocksReusableObservation()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var source =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "en",
                NormalizedHash =
                    LegendLanguageIdentity.TextHash(
                        "Hello"),
                Text = "Hello",
                Provenance =
                    "ProviderDerived",
                IsTrainingEligible = true
            };

        var target =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "ht",
                StoragePartition = "ht",
                NormalizedHash =
                    LegendLanguageIdentity.TextHash(
                        "Bonjou"),
                Text = "Bonjou",
                Provenance =
                    "ProviderDerived",
                IsTrainingEligible = true
            };

        var alignment =
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = source.Id,
                TargetTextUnitId = target.Id,
                Provider = "AzureTranslator",
                Provenance = "ProviderDerived",
                QualityState = "Observation",
                Confidence = 0.99m,
                HumanVerified = false
            };

        db.AddRange(
            source,
            target,
            alignment);

        db.Add(
            new LegendTranslationQualityEvidence
            {
                Id = Guid.NewGuid(),
                ObservedAlignmentId =
                    alignment.Id,
                PairKey = "en:ht",
                SourceTextUnitId =
                    source.Id,
                TargetTextUnitId =
                    target.Id,
                Signal =
                    "Contradictory",
                ReasonCode =
                    "phase10_test",
                ResolutionState =
                    "Open",
                EvidenceIdentity =
                    Guid.NewGuid()
                        .ToString("N")
            });

        await db.SaveChangesAsync();

        var intelligence =
            new LegendConnectTranslationIntelligence(
                db,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>())
                    .Build());

        var result =
            await intelligence
                .TryGetReusableProviderObservationAsync(
                    "en",
                    "ht",
                    "Hello");

        Assert.Null(
            result);
    }

    private sealed class FakeTransport
        : ILegendConnectModelInferenceTransport
    {
        private readonly string _text;

        public FakeTransport(
            string text)
        {
            _text = text;
        }

        public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
            string model,
            string sourceLanguageCode,
            string targetLanguageCode,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendModelEvaluationGenerationResult(
                    true,
                    _text));
    }
}

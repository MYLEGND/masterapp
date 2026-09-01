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
    private const string RuntimeProof =
        "evaluated=1;reference=1.000000;blocking=0;protected=0;leakage=0;prompt_set=test-v1;code_sha=0123456789abcdef0123456789abcdef01234567;runtime_mode=LockedHeldOutEvaluation;response_authority=LegendConnectActiveModelInference;settings=responses-v1,store=false,max_output_tokens=1200;criteria=governed-reference-policy-v1,held_out>=0.950000,regression>=1.000000,protected>=0.980000,blocking=0,leakage=0,runtime_model=exact;proof_set=abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789;latency_us=1;cost_micro=1";

    [Fact]
    public async Task ActivePromotedModel_ServesBeforeProviderObservation()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var pair =
            new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                IsEnabled = true,
                ActiveModelVersion =
                    "ft:legend:active"
            };
        var run = new LegendConnectModelTrainingRun
        {
            Id = Guid.NewGuid(),
            RunKey = "production-neural-routing",
            ScopeKey = "Global",
            Generation = 1,
            DatasetIdentity =
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            DatasetEvaluatorVersion = 13,
            TrainingProvider = "OpenAI",
            BaseModel = "base",
            ChallengerModelVersion = "ft:legend:active",
            State = "TrainingCompleted",
            EvaluationState = "Passed",
            PromotionState = "Promoted",
            HeldOutScore = 1m,
            RegressionScore = 1m,
            FailureDetail = RuntimeProof,
            CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
            PromotedUtc = DateTime.UtcNow
        };
        db.AddRange(
            pair,
            run,
            new LegendConnectModelPromotionPair
            {
                Id = Guid.NewGuid(),
                ModelTrainingRunId = run.Id,
                PairKey = pair.PairKey,
                PromotedModelVersion =
                    run.ChallengerModelVersion,
                PromotedUtc = run.PromotedUtc!.Value
            });

        await db.SaveChangesAsync();

        var transport =
            new FakeTransport(
                "Neural translation");
        var inference =
            new LegendConnectActiveModelInference(
                db,
                transport);

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
        Assert.Equal(run.Id, result.ModelTrainingRunId);
        Assert.NotNull(transport.LastTask);
        Assert.Equal(LegendModelCapabilityKeys.Translation, transport.LastTask!.CapabilityKey);
        Assert.Equal("Hello", transport.LastTask.Input);
        Assert.Equal("en", transport.LastTask.SourceLanguageCode);
        Assert.Equal("ht", transport.LastTask.TargetLanguageCode);
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

        public LegendModelTaskRequest? LastTask { get; private set; }

        public FakeTransport(
            string text)
        {
            _text = text;
        }

        public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
            string model,
            LegendModelTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            LastTask = task;
            return Task.FromResult(
                new LegendModelEvaluationGenerationResult(
                    true,
                    _text));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectModelPromotionTests
{
    [Fact]
    public async Task PassedChallenger_PromotesExactDatasetPairsOnly()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            await SeedAsync(
                db,
                currentModel:
                    "ft:previous");

        var service =
            Service(db);

        var promoted =
            await service.PromoteAsync(
                run.Id);

        Assert.True(promoted);

        Assert.Equal(
            "Promoted",
            run.PromotionState);

        Assert.NotNull(
            run.PromotedUtc);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Equal(
            "ft:new-challenger",
            pair.ActiveModelVersion);

        var lineage =
            Assert.Single(
                db.Set<LegendConnectModelPromotionPair>());

        Assert.Equal(
            "ft:previous",
            lineage.PreviousActiveModelVersion);

        Assert.Equal(
            "ft:new-challenger",
            lineage.PromotedModelVersion);
    }

    [Fact]
    public async Task OpenContradiction_BlocksPromotion()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            await SeedAsync(
                db,
                currentModel:
                    "ft:previous",
                contradiction:
                    true);

        var service =
            Service(db);

        var promoted =
            await service.PromoteAsync(
                run.Id);

        Assert.False(promoted);

        Assert.Equal(
            "Rejected",
            run.PromotionState);

        Assert.Equal(
            "model_promotion_blocking_contradiction",
            run.FailureCode);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Equal(
            "ft:previous",
            pair.ActiveModelVersion);

        Assert.Empty(
            db.Set<LegendConnectModelPromotionPair>());
    }

    [Fact]
    public async Task DatasetIdentityChange_BlocksPromotion()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            await SeedAsync(
                db,
                currentModel:
                    null);

        run.DatasetIdentity =
            "stale-dataset";

        await db.SaveChangesAsync();

        var service =
            Service(db);

        var promoted =
            await service.PromoteAsync(
                run.Id);

        Assert.False(promoted);

        Assert.Equal(
            "Rejected",
            run.PromotionState);

        Assert.Equal(
            "model_promotion_dataset_identity_changed",
            run.FailureCode);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Null(
            pair.ActiveModelVersion);
    }

    [Fact]
    public async Task Rollback_RestoresExactPreviousModel()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            await SeedAsync(
                db,
                currentModel:
                    "ft:previous");

        var service =
            Service(db);

        Assert.True(
            await service.PromoteAsync(
                run.Id));

        Assert.True(
            await service.RollbackAsync(
                run.Id));

        Assert.Equal(
            "RolledBack",
            run.PromotionState);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Equal(
            "ft:previous",
            pair.ActiveModelVersion);

        var lineage =
            Assert.Single(
                db.Set<LegendConnectModelPromotionPair>());

        Assert.NotNull(
            lineage.RolledBackUtc);
    }

    [Fact]
    public async Task TrainingOrEvaluationCannotSelfPromote()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            await SeedAsync(
                db,
                currentModel:
                    "ft:previous");

        Assert.Equal(
            "NotEvaluated",
            run.PromotionState);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Equal(
            "ft:previous",
            pair.ActiveModelVersion);
    }

    private static LegendConnectModelPromotionService Service(
        Infrastructure.Data.MasterAppDbContext db)
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["LegendConnect:ModelPromotion:Enabled"] =
                            "true",
                        ["LegendConnect:ModelPromotion:MinimumHeldOutScore"] =
                            "0.95",
                        ["LegendConnect:ModelPromotion:MinimumRegressionScore"] =
                            "1"
                    })
                .Build();

        return new(
            db,
            new LegendConnectTrainingDatasetCompiler(
                db),
            configuration);
    }

    private static async Task<LegendConnectModelTrainingRun> SeedAsync(
        Infrastructure.Data.MasterAppDbContext db,
        string? currentModel,
        bool contradiction = false)
    {
        db.Add(
            new LegendConnectRuntimePolicy
            {
                Id = Guid.NewGuid(),
                ScopeKey = "Global",
                CompletedLanguageIntelligenceEvaluatorVersion =
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                TargetLanguageIntelligenceEvaluatorVersion =
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                LanguageIntelligenceReevaluationPhase =
                    "Complete"
            });

        var pair =
            new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                IsEnabled = true,
                ActiveModelVersion =
                    currentModel
            };

        db.Add(pair);

        var source =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "en",
                NormalizedHash =
                    "phase9-source",
                Text =
                    "I understand.",
                Provenance =
                    "FounderApproved",
                IsTrainingEligible =
                    true
            };

        var target =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "ht",
                StoragePartition = "ht",
                NormalizedHash =
                    "phase9-target",
                Text =
                    "Mwen konprann.",
                Provenance =
                    "FounderApproved",
                IsTrainingEligible =
                    true
            };

        db.AddRange(
            source,
            target);

        var alignment =
            new LegendTranslationAlignment
            {
                Id =
                    Guid.Parse(
                        "00000000-0000-0000-0000-000000000001"),
                PairKey =
                    "en:ht",
                SourceTextUnitId =
                    source.Id,
                TargetTextUnitId =
                    target.Id,
                Provider =
                    "Founder",
                Provenance =
                    "FounderApproved",
                HumanVerified =
                    true,
                QualityState =
                    "Verified"
            };

        db.Add(alignment);

        await db.SaveChangesAsync();

        if (contradiction)
        {
            // The contradiction test must not mutate the governed evidence
            // that defines this run's training dataset. Use a separate
            // provider-derived observation on the same directional pair.
            // Provider-only material remains outside canonical training, while
            // its unresolved contradiction must still block pair promotion.
            var observedSource =
                new LegendLanguageTextUnit
                {
                    Id = Guid.NewGuid(),
                    LanguageCode = "en",
                    StoragePartition = "en",
                    NormalizedHash =
                        "phase9-provider-source",
                    Text =
                        "Provider observation source.",
                    Provenance =
                        "ProviderDerived",
                    IsTrainingEligible =
                        false
                };

            var observedTarget =
                new LegendLanguageTextUnit
                {
                    Id = Guid.NewGuid(),
                    LanguageCode = "ht",
                    StoragePartition = "ht",
                    NormalizedHash =
                        "phase9-provider-target",
                    Text =
                        "Provider observation target.",
                    Provenance =
                        "ProviderDerived",
                    IsTrainingEligible =
                        false
                };

            db.AddRange(
                observedSource,
                observedTarget);

            var observedAlignment =
                new LegendTranslationAlignment
                {
                    Id = Guid.NewGuid(),
                    PairKey =
                        "en:ht",
                    SourceTextUnitId =
                        observedSource.Id,
                    TargetTextUnitId =
                        observedTarget.Id,
                    Provider =
                        "AzureTranslator",
                    Provenance =
                        "ProviderDerived",
                    ProviderModel =
                        "phase9-test-provider",
                    QualityState =
                        "Observation",
                    HumanVerified =
                        false,
                    ObservationCount =
                        1
                };

            db.Add(observedAlignment);

            db.Add(
                new LegendTranslationQualityEvidence
                {
                    Id = Guid.NewGuid(),
                    ObservedAlignmentId =
                        observedAlignment.Id,
                    PairKey =
                        "en:ht",
                    SourceTextUnitId =
                        observedSource.Id,
                    TargetTextUnitId =
                        observedTarget.Id,
                    Signal =
                        "Contradictory",
                    ReasonCode =
                        "phase9_test",
                    ResolutionState =
                        "Open",
                    EvidenceIdentity =
                        "phase9-independent-provider-contradiction"
                });

            await db.SaveChangesAsync();
        }

        var manifest =
            await new LegendConnectTrainingDatasetCompiler(
                    db)
                .CompileAsync();

        Assert.NotEmpty(
            manifest.Training);

        var run =
            new LegendConnectModelTrainingRun
            {
                Id = Guid.NewGuid(),
                RunKey =
                    "phase9-run",
                ScopeKey =
                    "Global",
                Generation =
                    1,
                DatasetIdentity =
                    manifest.DatasetIdentity,
                DatasetEvaluatorVersion =
                    manifest.EvaluatorVersion,
                TrainingProvider =
                    "OpenAI",
                BaseModel =
                    "base",
                ChallengerModelVersion =
                    "ft:new-challenger",
                State =
                    "TrainingCompleted",
                EvaluationState =
                    "Passed",
                PromotionState =
                    "NotEvaluated",
                TrainingExampleCount =
                    manifest.Training.Count,
                ValidationExampleCount =
                    manifest.HeldOut.Count,
                HeldOutScore =
                    1m,
                RegressionScore =
                    1m
            };

        db.Add(run);

        await db.SaveChangesAsync();

        return run;
    }
}

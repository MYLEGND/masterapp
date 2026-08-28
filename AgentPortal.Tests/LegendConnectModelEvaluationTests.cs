using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectModelEvaluationTests
{
    [Fact]
    public async Task PerfectHeldOutChallenger_PassesWithoutPromotion()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            Run();

        db.Add(run);

        db.Add(
            new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                IsEnabled = true
            });

        await db.SaveChangesAsync();

        var service =
            Service(
                db,
                new FakeEvaluationBackend
                {
                    ChallengerText =
                        "Mwen konprann.",
                    Judgement =
                        Perfect()
                },
                new FakeBaseline
                {
                    Text =
                        "Mwen konprann."
                });

        await service.EvaluateManifestAsync(
            run,
            Manifest(
                challengerReference:
                    "Mwen konprann."));

        Assert.Equal(
            "Passed",
            run.EvaluationState);

        Assert.Equal(
            1m,
            run.HeldOutScore);

        Assert.Equal(
            1m,
            run.RegressionScore);

        Assert.Equal(
            "NotEvaluated",
            run.PromotionState);

        Assert.Null(
            run.PromotedUtc);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Null(
            pair.ActiveModelVersion);
    }

    [Fact]
    public async Task FounderProtectedRegression_IsRejectedNotMostlyAccepted()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            Run();

        db.Add(run);
        await db.SaveChangesAsync();

        var service =
            Service(
                db,
                new FakeEvaluationBackend
                {
                    ChallengerText =
                        "Wrong meaning.",
                    Judgement =
                        new(
                            true,
                            0.70m,
                            1m,
                            0.70m,
                            0.60m,
                            0.80m,
                            0.80m,
                            0.75m,
                            0.70m,
                            false,
                            false,
                            true,
                            [
                                "protected_meaning_regression"
                            ])
                },
                new FakeBaseline
                {
                    Text =
                        "Mwen konprann."
                });

        await service.EvaluateManifestAsync(
            run,
            Manifest(
                challengerReference:
                    "Mwen konprann."));

        Assert.Equal(
            "Rejected",
            run.EvaluationState);

        Assert.Equal(
            "model_evaluation_regression",
            run.FailureCode);

        Assert.Equal(
            "NotEvaluated",
            run.PromotionState);

        Assert.Null(
            run.PromotedUtc);
    }

    [Fact]
    public async Task MemorizedUnrelatedTrainingTarget_IsBlockingLeakage()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            Run();

        db.Add(run);
        await db.SaveChangesAsync();

        const string leaked =
            "This is a deliberately unrelated long training target sentence.";

        var manifest =
            new LegendConnectTrainingDatasetManifest(
                "dataset",
                13,
                "Global",
                [
                    new(
                        "training",
                        "en:ht",
                        "en",
                        "ht",
                        "Different source.",
                        leaked,
                        "FounderApproved",
                        4,
                        "different-source",
                        "training-target")
                ],
                [
                    HeldOut(
                        "Expected held-out target.")
                ]);

        var service =
            Service(
                db,
                new FakeEvaluationBackend
                {
                    ChallengerText =
                        leaked,
                    Judgement =
                        Perfect()
                },
                new FakeBaseline
                {
                    Text =
                        "Expected held-out target."
                });

        await service.EvaluateManifestAsync(
            run,
            manifest);

        Assert.Equal(
            "Rejected",
            run.EvaluationState);

        Assert.Equal(
            "model_evaluation_regression",
            run.FailureCode);

        Assert.Equal(
            0m,
            run.RegressionScore);
    }

    [Fact]
    public async Task ChallengerWorseThanProductionBaseline_IsRejected()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        var run =
            Run();

        db.Add(run);
        await db.SaveChangesAsync();

        var judgement =
            Perfect() with
            {
                ChallengerScore = 0.94m,
                BaselineScore = 0.99m,
                TranslationAccuracy = 0.94m,
                SemanticPreservation = 0.94m,
                BlockingRegression = true,
                ReasonCodes =
                    [
                        "baseline_outperforms_challenger"
                    ]
            };

        var service =
            Service(
                db,
                new FakeEvaluationBackend
                {
                    ChallengerText =
                        "Almost correct.",
                    Judgement =
                        judgement
                },
                new FakeBaseline
                {
                    Text =
                        "Mwen konprann."
                });

        await service.EvaluateManifestAsync(
            run,
            Manifest(
                challengerReference:
                    "Mwen konprann."));

        Assert.Equal(
            "Rejected",
            run.EvaluationState);

        Assert.Equal(
            "NotEvaluated",
            run.PromotionState);
    }

    [Fact]
    public async Task CapabilityWithoutRegisteredEvaluator_IsRejectedBeforeModelOrBaselineExecution()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var backend = new FakeEvaluationBackend { ChallengerText = "must-not-run" };
        var service = Service(db, backend, new FakeBaseline { Text = "must-not-run" });
        var heldOut = HeldOut("Resolved governed state") with
        {
            CapabilityKey = "governed.semantic_transition",
            Instructions = "Apply only the governed transition.",
            OutputContract = "governed_state_only"
        };

        await service.EvaluateManifestAsync(
            run,
            new LegendConnectTrainingDatasetManifest(
                "dataset",
                13,
                "Global",
                [],
                [heldOut]));

        Assert.Equal("Rejected", run.EvaluationState);
        Assert.Equal("model_evaluation_capability_evaluator_unavailable", run.FailureCode);
        Assert.Equal(0, backend.GenerateCalls);
    }

    private static LegendConnectModelEvaluationService Service(
        Infrastructure.Data.MasterAppDbContext db,
        ILegendConnectModelEvaluationBackend backend,
        ILegendConnectCurrentProductionBaseline baseline)
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["LegendConnect:ModelEvaluation:Enabled"] =
                            "true",
                        ["LegendConnect:ModelEvaluation:MinimumHeldOutScore"] =
                            "0.95",
                        ["LegendConnect:ModelEvaluation:MinimumRegressionScore"] =
                            "1",
                        ["LegendConnect:ModelEvaluation:ProtectedMinimumScore"] =
                            "0.98",
                        ["LegendConnect:ModelEvaluation:MaximumExamples"] =
                            "128",
                        ["LegendConnect:ModelEvaluation:MaximumAttempts"] =
                            "4"
                    })
                .Build();

        return new(
            db,
            new LegendConnectTrainingDatasetCompiler(
                db),
            backend,
            baseline,
            configuration);
    }

    private static LegendConnectModelTrainingRun Run() =>
        new()
        {
            Id = Guid.NewGuid(),
            RunKey = "phase8-run",
            ScopeKey = "Global",
            Generation = 1,
            DatasetIdentity = "dataset",
            DatasetEvaluatorVersion = 13,
            TrainingProvider = "OpenAI",
            BaseModel = "base",
            ChallengerModelVersion =
                "ft:test:challenger",
            State = "TrainingCompleted",
            EvaluationState = "NotStarted",
            PromotionState = "NotEvaluated",
            TrainingExampleCount = 1,
            ValidationExampleCount = 1
        };

    private static LegendConnectTrainingDatasetManifest Manifest(
        string challengerReference) =>
        new(
            "dataset",
            13,
            "Global",
            [],
            [
                HeldOut(
                    challengerReference)
            ]);

    private static LegendConnectTrainingDatasetExample HeldOut(
        string target) =>
        new(
            "held-out",
            "en:ht",
            "en",
            "ht",
            "I understand.",
            target,
            "FounderApproved",
            4,
            "held-source",
            "held-target");

    private static LegendModelEvaluationJudgement Perfect() =>
        new(
            true,
            1m,
            1m,
            1m,
            1m,
            1m,
            1m,
            1m,
            1m,
            false,
            false,
            false,
            [
                "faithful"
            ]);

    private sealed class FakeEvaluationBackend
        : ILegendConnectModelEvaluationBackend
    {
        public int GenerateCalls { get; private set; }

        public string ChallengerText { get; init; } =
            string.Empty;

        public LegendModelEvaluationJudgement Judgement { get; init; } =
            Perfect();

        public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
            string model,
            LegendConnectTrainingDatasetExample example,
            CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            return Task.FromResult(
                new LegendModelEvaluationGenerationResult(
                    true,
                    ChallengerText));
        }

        public Task<LegendModelEvaluationJudgement> JudgeAsync(
            LegendModelEvaluationJudgeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Judgement);
    }

    private sealed class FakeBaseline
        : ILegendConnectCurrentProductionBaseline
    {
        public string Text { get; init; } =
            string.Empty;

        public Task<LegendCurrentProductionEvaluationResult> TranslateAsync(
            LegendConnectTrainingDatasetExample example,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendCurrentProductionEvaluationResult(
                    true,
                    Text,
                    "CurrentProduction"));
    }
}

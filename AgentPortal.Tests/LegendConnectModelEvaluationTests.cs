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
    public void LockedEvaluator_RegistersGovernedResearchWithoutCreatingAnotherEvaluator()
    {
        Assert.True(
            LegendModelCapabilityEvaluationPolicies.TryResolve(
                LegendModelCapabilityKeys.GovernedResearch,
                out var policy));
        Assert.Equal(LegendModelCapabilityKeys.GovernedResearch, policy.CapabilityKey);
        Assert.False(policy.RequiresTranslationAccuracy);
        Assert.False(policy.RequiresMorphologyPreservation);
    }
    private const string DatasetSha =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

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
                    Judgement =
                        Perfect()
                },
                new FakeServingAuthority
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
                new FakeServingAuthority
                {
                    Text =
                        "Wrong meaning."
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
                DatasetSha,
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
                    Judgement =
                        Perfect()
                },
                new FakeServingAuthority
                {
                    Text =
                        leaked
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
    public async Task ExecutedRuntimeWorseThanGovernedReference_IsRejected()
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
                    Judgement =
                        judgement
                },
                new FakeServingAuthority
                {
                    Text =
                        "Almost correct."
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
    public async Task CapabilityWithoutRegisteredEvaluator_IsRejectedBeforeRuntimeExecution()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var backend = new FakeEvaluationBackend();
        var serving = new FakeServingAuthority { Text = "must-not-run" };
        var service = Service(db, backend, serving);
        var heldOut = HeldOut("Resolved governed state") with
        {
            CapabilityKey = "governed.unregistered",
            Instructions = "Apply only the governed transition.",
            OutputContract = "governed_state_only"
        };

        await service.EvaluateManifestAsync(
            run,
            new LegendConnectTrainingDatasetManifest(
                DatasetSha,
                13,
                "Global",
                [],
                [heldOut]));

        Assert.Equal("Rejected", run.EvaluationState);
        Assert.Equal("model_evaluation_capability_evaluator_unavailable", run.FailureCode);
        Assert.Equal(0, serving.EvaluationCalls);
    }

    [Fact]
    public async Task RegisteredGovernedSemanticCapability_UsesItsPolicyAndPassesWithoutTranslationMetrics()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();

        var semantic = HeldOut("Resolved governed state") with
        {
            CapabilityKey = LegendModelCapabilityKeys.SemanticTransition,
            Instructions = "Apply only the supplied governed semantic transition. Return the resolved state only.",
            OutputContract = "governed_state_only"
        };
        var service = Service(
            db,
            new FakeEvaluationBackend
            {
                Judgement = Perfect() with
                {
                    TranslationAccuracy = 0m,
                    MorphologyPreservation = 0m
                }
            },
            new FakeServingAuthority { Text = "Resolved governed state" });

        await service.EvaluateManifestAsync(
            run,
            new LegendConnectTrainingDatasetManifest(
                DatasetSha,
                13,
                "Global",
                [],
                [semantic]));

        Assert.Equal("Passed", run.EvaluationState);
        Assert.Equal(1m, run.HeldOutScore);
        Assert.Equal(1m, run.RegressionScore);
        Assert.Equal("NotEvaluated", run.PromotionState);
    }

    [Fact]
    public async Task LockedCase_JudgesExecutedRuntimeTextAgainstGovernedTargetAndRecordsProof()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var backend = new FakeEvaluationBackend();
        var service = Service(
            db,
            backend,
            new FakeServingAuthority
            {
                Text = "text-returned-by-serving-runtime"
            });

        await service.EvaluateManifestAsync(
            run,
            Manifest("stored-governed-target"));

        Assert.NotNull(backend.LastRequest);
        Assert.Equal(
            "text-returned-by-serving-runtime",
            backend.LastRequest!.ChallengerText);
        Assert.Equal(
            "stored-governed-target",
            backend.LastRequest.GovernedReferenceText);
        Assert.Equal("Passed", run.EvaluationState);
        var proof = db.Set<LegendConnectOperationalEvent>()
            .Where(item => item.Category == "ModelServingEvaluationProof")
            .OrderBy(item => item.Status)
            .ToArray();
        Assert.Equal(4, proof.Length);
        Assert.Contains(proof, item =>
            item.Summary!.Contains(
                "prompt_set=legend-held-out-v1",
                StringComparison.Ordinal) &&
            item.Summary.Contains(
                "response_authority=LegendConnectActiveModelInference",
                StringComparison.Ordinal));
        Assert.Contains(proof, item =>
            item.Summary!.Contains(
                "model_version=ft:test:challenger",
                StringComparison.Ordinal));
        Assert.Contains(proof, item =>
            item.Summary!.Contains(
                "proof_lineage=",
                StringComparison.Ordinal));
        Assert.Contains(proof, item =>
            item.Summary!.Contains(
                "cost_micro=1",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeReceiptForDifferentModel_CannotPassCandidate()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var backend = new FakeEvaluationBackend();
        var service = Service(
            db,
            backend,
            new FakeServingAuthority
            {
                Text = "Mwen konprann.",
                ModelVersionOverride =
                    "ft:test:different-model"
            });

        await service.EvaluateManifestAsync(
            run,
            Manifest("Mwen konprann."));

        Assert.Equal("Rejected", run.EvaluationState);
        Assert.Equal(
            "model_evaluation_runtime_model_mismatch",
            run.FailureCode);
        Assert.Null(backend.LastRequest);
        Assert.Equal("NotEvaluated", run.PromotionState);
    }

    [Fact]
    public async Task ServingProviderFailure_RecordsFailureAndNeverJudgesTarget()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var backend = new FakeEvaluationBackend();
        var service = Service(
            db,
            backend,
            new FakeServingAuthority
            {
                ErrorCode = "model_inference_provider_failed",
                Retryable = true
            });

        await service.EvaluateManifestAsync(
            run,
            Manifest("Mwen konprann."));

        Assert.Equal("PendingRetry", run.EvaluationState);
        Assert.Equal(
            "model_inference_provider_failed",
            run.FailureCode);
        Assert.Null(backend.LastRequest);
        Assert.Contains(
            db.Set<LegendConnectOperationalEvent>(),
            item =>
                item.Category == "ModelServingEvaluationProof" &&
                item.ErrorCode ==
                    "model_inference_provider_failed");
    }

    [Fact]
    public async Task SharedTrainingAndHeldOutLineage_IsRejectedBeforeRuntimeExecution()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var serving = new FakeServingAuthority
        {
            Text = "must-not-run"
        };
        var heldOut = HeldOut("Mwen konprann.") with
        {
            SplitGroupIdentity = "shared-governed-lineage"
        };
        var training = heldOut with
        {
            EvidenceIdentity = "training-case",
            SourceText = "Different surface.",
            TargetText = "Different target.",
            SourceTextHash = "different-source",
            TargetTextHash = "different-target"
        };

        await Service(
                db,
                new FakeEvaluationBackend(),
                serving)
            .EvaluateManifestAsync(
                run,
                new LegendConnectTrainingDatasetManifest(
                    DatasetSha,
                    13,
                    "Global",
                    [training],
                    [heldOut]));

        Assert.Equal("Rejected", run.EvaluationState);
        Assert.Equal(
            "model_evaluation_held_out_contaminated",
            run.FailureCode);
        Assert.Equal(0, serving.EvaluationCalls);
    }

    [Fact]
    public async Task IncompleteHeldOutCase_IsRejectedBeforeRuntimeExecution()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var serving = new FakeServingAuthority
        {
            Text = "must-not-run"
        };

        await Service(
                db,
                new FakeEvaluationBackend(),
                serving)
            .EvaluateManifestAsync(
                run,
                new LegendConnectTrainingDatasetManifest(
                    DatasetSha,
                    13,
                    "Global",
                    [],
                    [HeldOut(string.Empty)]));

        Assert.Equal("Rejected", run.EvaluationState);
        Assert.Equal(
            "model_evaluation_incomplete_case",
            run.FailureCode);
        Assert.Equal(0, serving.EvaluationCalls);
    }

    [Fact]
    public async Task LockedServingProof_IsReproducibleForExactCaseAndConfiguration()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        db.Add(run);
        await db.SaveChangesAsync();
        var transport = new FakeTransport(
            new LegendModelEvaluationGenerationResult(
                true,
                "Mwen konprann.",
                CostMicrounits: 7));
        var serving = new LegendConnectActiveModelInference(
            db,
            transport);
        var request = new LegendConnectLockedServingEvaluationRequest(
            run.Id,
            run.ChallengerModelVersion!,
            run.DatasetIdentity,
            run.DatasetEvaluatorVersion,
            "legend-held-out-v1",
            "0123456789abcdef0123456789abcdef01234567",
            LegendConnectServingEvaluationContracts.SuccessCriteria,
            HeldOut("Mwen konprann."));

        var first = await serving.EvaluateLockedCaseAsync(request);
        var second = await serving.EvaluateLockedCaseAsync(request);

        Assert.True(first.Succeeded, first.ErrorCode);
        Assert.True(second.Succeeded, second.ErrorCode);
        Assert.Equal(
            first.ConfigurationIdentity,
            second.ConfigurationIdentity);
        Assert.Equal(
            first.ProofLineageIdentity,
            second.ProofLineageIdentity);
        Assert.Equal(run.Id, first.ModelTrainingRunId);
        Assert.Equal(
            run.ChallengerModelVersion,
            first.ModelVersion);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task InactiveModelRun_CannotProduceLockedServingProof()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var run = Run();
        run.State = "Failed";
        db.Add(run);
        await db.SaveChangesAsync();
        var transport = new FakeTransport(
            new LegendModelEvaluationGenerationResult(
                true,
                "must-not-run",
                CostMicrounits: 1));
        var serving = new LegendConnectActiveModelInference(
            db,
            transport);

        var result = await serving.EvaluateLockedCaseAsync(
            new LegendConnectLockedServingEvaluationRequest(
                run.Id,
                run.ChallengerModelVersion!,
                run.DatasetIdentity,
                run.DatasetEvaluatorVersion,
                "legend-held-out-v1",
                "0123456789abcdef0123456789abcdef01234567",
                LegendConnectServingEvaluationContracts.SuccessCriteria,
                HeldOut("Mwen konprann.")));

        Assert.False(result.Succeeded);
        Assert.Equal(
            "model_evaluation_inactive_model",
            result.ErrorCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public void CapabilityPolicyRegistry_IsFailClosedAndSingleAuthority()
    {
        Assert.True(
            LegendModelCapabilityEvaluationPolicies.TryResolve(
                LegendModelCapabilityKeys.Translation,
                out var translation));
        Assert.True(translation.RequiresTranslationAccuracy);

        Assert.True(
            LegendModelCapabilityEvaluationPolicies.TryResolve(
                LegendModelCapabilityKeys.SemanticTransition,
                out var semantic));
        Assert.False(semantic.RequiresTranslationAccuracy);

        Assert.False(
            LegendModelCapabilityEvaluationPolicies.TryResolve(
                "governed.unregistered",
                out _));
    }

    [Fact]
    public void MultimodalEvidenceAdmission_RequiresGovernanceHashAndNoContradiction()
    {
        const string hash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var admitted = new LegendModelEvidencePart(
            "image",
            "https://evidence.example/founder-approved.png",
            "image/png",
            "founder-image-1",
            hash,
            "FounderApproved");
        var contradicted = admitted with
        {
            ContradictionState = "Contradictory"
        };
        var unverified = admitted with
        {
            ContentSha256 = "not-a-content-hash"
        };

        Assert.True(
            LegendModelEvidenceAdmission.IsAdmitted(admitted));
        Assert.False(
            LegendModelEvidenceAdmission.IsAdmitted(contradicted));
        Assert.False(
            LegendModelEvidenceAdmission.IsAdmitted(unverified));
    }

    private static LegendConnectModelEvaluationService Service(
        Infrastructure.Data.MasterAppDbContext db,
        ILegendConnectModelEvaluationBackend backend,
        ILegendConnectActiveModelInference serving)
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
                            "4",
                        ["LegendConnect:ModelEvaluation:PromptSetVersion"] =
                            "legend-held-out-v1",
                        ["LegendConnect:ModelEvaluation:CodeSha"] =
                            "0123456789abcdef0123456789abcdef01234567"
                    })
                .Build();

        return new(
            db,
            new LegendConnectTrainingDatasetCompiler(
                db),
            backend,
            serving,
            configuration);
    }

    private static LegendConnectModelTrainingRun Run() =>
        new()
        {
            Id = Guid.NewGuid(),
            RunKey = "phase8-run",
            ScopeKey = "Global",
            Generation = 1,
            DatasetIdentity = DatasetSha,
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
            DatasetSha,
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
        public LegendModelEvaluationJudgement Judgement { get; init; } =
            Perfect();

        public LegendModelEvaluationJudgeRequest? LastRequest { get; private set; }

        public Task<LegendModelEvaluationJudgement> JudgeAsync(
            LegendModelEvaluationJudgeRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(
                Judgement);
        }
    }

    private sealed class FakeServingAuthority
        : ILegendConnectActiveModelInference
    {
        public string Text { get; init; } =
            string.Empty;

        public string? ModelVersionOverride { get; init; }

        public string? ErrorCode { get; init; }

        public bool Retryable { get; init; }

        public int EvaluationCalls { get; private set; }

        public Task<LegendConnectLockedServingEvaluationResult>
            EvaluateLockedCaseAsync(
                LegendConnectLockedServingEvaluationRequest request,
                CancellationToken cancellationToken = default)
        {
            EvaluationCalls++;
            return Task.FromResult(
                new LegendConnectLockedServingEvaluationResult(
                    ErrorCode is null,
                    ErrorCode is null
                        ? Text
                        : null,
                    ModelVersionOverride ??
                        request.ExpectedModelVersion,
                    request.ModelTrainingRunId,
                    LegendConnectServingEvaluationContracts.RuntimeMode,
                    LegendConnectServingEvaluationContracts.ResponseAuthority,
                    request.PromptSetVersion,
                    request.CodeSha,
                    LegendConnectServingEvaluationContracts.InferenceSettings,
                    request.Example.EvidenceIdentity,
                    "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                    "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0",
                    request.SuccessCriteria,
                    10,
                    1,
                    ErrorCode,
                    Retryable));
        }

        public Task<LegendConnectActiveModelInferenceResult> TryTranslateAsync(
            string sourceLanguageCode,
            string targetLanguageCode,
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendConnectActiveModelInferenceResult(
                    false,
                    null,
                    null,
                    "not_used"));

        public Task<LegendConnectActiveModelInferenceResult>
            TryGenerateGovernedReasoningCandidateAsync(
                LegendConnectGovernedReasoningCandidateRequest request,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendConnectActiveModelInferenceResult(
                    false,
                    null,
                    null,
                    "not_used"));
    }

    private sealed class FakeTransport
        : ILegendConnectModelInferenceTransport
    {
        private readonly LegendModelEvaluationGenerationResult _result;

        public FakeTransport(
            LegendModelEvaluationGenerationResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
            string model,
            LegendModelTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                _result);
        }
    }
}

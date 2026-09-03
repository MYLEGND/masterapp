using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectNativeReasoningModelServingTests
{
    private const string RuntimeProof =
        "evaluated=1;reference=1.000000;blocking=0;protected=0;leakage=0;prompt_set=test-v1;code_sha=0123456789abcdef0123456789abcdef01234567;runtime_mode=LockedHeldOutEvaluation;response_authority=LegendConnectActiveModelInference;settings=responses-v1,store=false,max_output_tokens=1200;criteria=governed-reference-policy-v1,held_out>=0.950000,regression>=1.000000,protected>=0.980000,blocking=0,leakage=0,runtime_model=exact;proof_set=abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789;latency_us=1;cost_micro=1";

    private const string RequestText = "Give the governed model answer.";
    private const string SymbolicAnswer = "Founder governed model answer.";

    [Fact]
    public async Task NoQualifiedModel_RemainsDormantAndExplicitlyUnavailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var transport = FakeTransport.Success(SymbolicAnswer);
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);

        var result = await InferAsync(fixture.Operations);

        Assert.True(result.Supported, result.ReasonCode);
        Assert.Equal(SymbolicAnswer, result.Answer);
        Assert.Equal("CanonicalGovernedEndpoint", result.ArticulationMode);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Unavailable", assistance.State);
        Assert.Equal("active_reasoning_model_unavailable", assistance.ReasonCode);
        Assert.Null(assistance.ModelVersion);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task EvaluatedPromotedModel_ProvidesBoundedCandidateAfterSymbolicAuthorization()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Success(SymbolicAnswer);
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);

        var result = await InferAsync(fixture.Operations);

        Assert.True(result.Supported, result.ReasonCode);
        Assert.Equal(SymbolicAnswer, result.Answer);
        Assert.Equal("EvaluatedPromotedModelRealization", result.ArticulationMode);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(
            LegendModelCapabilityKeys.GovernedReasoning,
            transport.LastTask!.CapabilityKey);
        Assert.Contains("authorized_symbolic_answer", transport.LastTask.Input, StringComparison.Ordinal);
        Assert.Contains(SymbolicAnswer, transport.LastTask.Input, StringComparison.Ordinal);
        Assert.Equal("en", transport.LastTask.SourceLanguageCode);
        Assert.Equal("en", transport.LastTask.TargetLanguageCode);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Applied", assistance.State);
        Assert.Equal("ft:legend:reasoning-active", assistance.ModelVersion);
        Assert.NotNull(assistance.ModelTrainingRunId);
    }

    [Fact]
    public async Task RolledBackModel_IsNotServed()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "RolledBack");
        var transport = FakeTransport.Success(SymbolicAnswer);
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);

        var result = await InferAsync(fixture.Operations);

        Assert.True(result.Supported, result.ReasonCode);
        Assert.Equal(SymbolicAnswer, result.Answer);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Unavailable", assistance.State);
        Assert.Equal("active_reasoning_model_unavailable", assistance.ReasonCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task SymbolicContradiction_BlocksModelBeforeGeneration()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Success(SymbolicAnswer);
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);
        var transition = await db.LegendSemanticTransitionEvidence.SingleAsync();
        transition.ContributionState = "Contradictory";
        await db.SaveChangesAsync();

        var result = await InferAsync(fixture.Operations);

        Assert.False(result.Supported);
        Assert.Null(result.Answer);
        Assert.Equal(0, transport.CallCount);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Dormant", assistance.State);
        Assert.Equal("symbolic_authority_not_supported", assistance.ReasonCode);
    }

    [Fact]
    public async Task MalformedModelOutput_IsRejectedAndSymbolicAnswerSurvives()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Success(new string('x', 2001));
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);

        var result = await InferAsync(fixture.Operations);

        Assert.True(result.Supported, result.ReasonCode);
        Assert.Equal(SymbolicAnswer, result.Answer);
        Assert.Equal("CanonicalGovernedEndpoint", result.ArticulationMode);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Rejected", assistance.State);
        Assert.Equal("active_reasoning_model_malformed_output", assistance.ReasonCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task SemanticallyDifferentCandidate_CannotReplaceAuthorizedMeaning()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Success("The model selected an unrelated result.");
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);

        var result = await InferAsync(fixture.Operations);

        Assert.True(result.Supported, result.ReasonCode);
        Assert.Equal(SymbolicAnswer, result.Answer);
        Assert.Equal("CanonicalGovernedEndpoint", result.ArticulationMode);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Rejected", assistance.State);
        Assert.Equal(
            "active_reasoning_model_semantic_lineage_unproven",
            assistance.ReasonCode);
        Assert.Equal(
            LegendConnectNativeModelAssistanceContracts.CandidateAttemptProvenance,
            assistance.Provenance);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task ModelTimeout_PreservesAuthorizedSymbolicAnswer()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Failure("model_inference_timeout");
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);

        var result = await InferAsync(fixture.Operations);

        Assert.True(result.Supported, result.ReasonCode);
        Assert.Equal(SymbolicAnswer, result.Answer);
        Assert.Equal("CanonicalGovernedEndpoint", result.ArticulationMode);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Failed", assistance.State);
        Assert.Equal("model_inference_timeout", assistance.ReasonCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task AppliedCandidate_PreservesSymbolicEvidenceAndAddsSurfaceProvenance()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Success(SymbolicAnswer);
        var fixture = CreateFixture(db, transport);
        await SeedGovernedTransitionAsync(fixture.Curriculum);
        var symbolicOperations = CreateFixture(db, null).Operations;

        var symbolic = await InferAsync(symbolicOperations);
        var assisted = await InferAsync(fixture.Operations);

        Assert.Equal(symbolic.EvidenceCount, assisted.EvidenceCount);
        Assert.Equal(symbolic.EvidenceStandard, assisted.EvidenceStandard);
        Assert.Equal(symbolic.ReasonCode, assisted.ReasonCode);
        Assert.Equal(symbolic.RequiresEscalation, assisted.RequiresEscalation);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            assisted.ModelAssistance);
        Assert.Equal(
            LegendConnectNativeModelAssistanceContracts.Provenance,
            assistance.Provenance);
        Assert.Equal(
            LegendConnectNativeModelAssistanceContracts.GovernedReasoningCapability,
            assistance.CapabilityKey);
        Assert.Contains("symbolic evidence", assisted.AuthoritySummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("This surface has no governed meaning.")]
    [InlineData("What was Project Zephyr's exact conversion rate last Tuesday? Do not estimate.")]
    [InlineData("Explain a new operational scenario with owners, constraints, risks, and observable success signals.")]
    public async Task UnsupportedSymbolicRequest_NeverInvokesQualifiedModel(string request)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        await SeedPromotedReasoningModelAsync(db, "Promoted");
        var transport = FakeTransport.Success("invented answer");
        var fixture = CreateFixture(db, transport);

        var result = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            request,
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(result.Supported);
        Assert.Null(result.Answer);
        Assert.Equal(0, result.EvidenceCount);
        Assert.Equal(0, transport.CallCount);
        var assistance = Assert.IsType<LegendConnectNativeModelAssistanceSnapshot>(
            result.ModelAssistance);
        Assert.Equal("Dormant", assistance.State);
        Assert.Equal("symbolic_authority_not_supported", assistance.ReasonCode);
    }

    private static Task<LegendConnectNativeInferenceSnapshot> InferAsync(
        LegendConnectOperations operations) =>
        operations.TryInferConversationWithDiscourseAsync(
            RequestText,
            [],
            new LegendConnectDiscourseStateSnapshot([]));

    private static async Task SeedGovernedTransitionAsync(
        LegendConnectCurriculumService curriculum)
    {
        var submitted = await curriculum.SubmitFounderBatchAsync(
            new LegendConnectCurriculumBatchSubmission(
                "response.model-serving.reasoning",
                "Founder-governed symbolic authority before model realization",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        $"Founder model serving request: {RequestText}",
                        new Dictionary<string, string>
                        {
                            ["request_surface"] = RequestText,
                            ["conversation_function"] = "governed_model_request"
                        },
                        new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "function",
                                "conversation_function",
                                "governed_model_request",
                                RequestText.TrimEnd('.'))
                        ],
                        [])),
                    new LegendConnectCurriculumExampleSubmission(
                        SymbolicAnswer,
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = "governed_model_answer"
                        })
                ],
                [
                    new LegendConnectSemanticTransitionSubmission(
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] = "governed_model_request"
                            }),
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] = "governed_model_answer"
                            }))
                ],
                [
                    new LegendConnectSemanticSpanGroundingSubmission(
                        "conversation_function",
                        "request_surface")
                ]));

        Assert.True(submitted.Succeeded, submitted.Message);
    }

    private static async Task SeedPromotedReasoningModelAsync(
        MasterAppDbContext db,
        string promotionState)
    {
        var now = DateTime.UtcNow;
        db.Add(new LegendConnectModelTrainingRun
        {
            Id = Guid.NewGuid(),
            RunKey = $"native-reasoning-{promotionState.ToLowerInvariant()}",
            ScopeKey = $"capability:{LegendModelCapabilityKeys.GovernedReasoning}",
            Generation = 1,
            DatasetIdentity = "governed-reasoning-dataset",
            DatasetEvaluatorVersion = LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TrainingProvider = "OpenAI",
            BaseModel = "reasoning-base",
            ChallengerModelVersion = "ft:legend:reasoning-active",
            State = "TrainingCompleted",
            EvaluationState = "Passed",
            PromotionState = promotionState,
            TrainingExampleCount = 12,
            ValidationExampleCount = 4,
            HeldOutScore = 1m,
            RegressionScore = 1m,
            FailureDetail = RuntimeProof,
            CompletedUtc = now.AddMinutes(-1),
            PromotedUtc = now,
            UpdatedUtc = now
        });
        await db.SaveChangesAsync();
    }

    private static ServingFixture CreateFixture(
        MasterAppDbContext db,
        FakeTransport? transport)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        ILegendConnectActiveModelInference? inference = transport is null
            ? null
            : new LegendConnectActiveModelInference(db, transport);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            activeModelInference: inference);
        return new ServingFixture(curriculum, operations);
    }

    private sealed record ServingFixture(
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);

    private sealed class FakeTransport : ILegendConnectModelInferenceTransport
    {
        private readonly LegendModelEvaluationGenerationResult _result;

        private FakeTransport(LegendModelEvaluationGenerationResult result)
        {
            _result = result;
        }

        internal int CallCount { get; private set; }
        internal LegendModelTaskRequest? LastTask { get; private set; }

        internal static FakeTransport Success(string text) =>
            new(new LegendModelEvaluationGenerationResult(true, text));

        internal static FakeTransport Failure(string errorCode) =>
            new(new LegendModelEvaluationGenerationResult(false, null, errorCode));

        public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
            string model,
            LegendModelTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTask = task;
            return Task.FromResult(_result);
        }
    }
}

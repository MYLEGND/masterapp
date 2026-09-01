using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// Direct proof for Founder-authored non-literal semantic span grounding.
///
/// These tests deliberately exercise the canonical curriculum and source
/// understanding authorities. There is no greeting dictionary, phrase router,
/// mock semantic answer, or test-only production branch.
/// </summary>
public sealed class LegendConnectSemanticSpanGroundingTests
{
    [Fact]
    public async Task ProductionConversation_UsesSelectedGovernedEndpoint_WhenOriginalCompositionIsUnavailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RichResponsePlanFamily(family, "neutral", "acknowledgement"));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Hi there.");
        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.DoesNotContain(graph.Nodes, item => item.SemanticDimension == "register");

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Hi there.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(native.Answer));
        Assert.True(native.EvidenceCount >= 3);
        Assert.Equal("HigherStandard", native.EvidenceStandard);
        Assert.Equal("CanonicalGovernedEndpoint", native.ArticulationMode);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task GrowingCurriculum_UsesBroadGovernedEvidenceInsteadOfReportingKnownMeaningUnknown()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            RichResponsePlanFamily(21, "neutral", "broad_acknowledgement"));
        Assert.True(submitted.Succeeded, submitted.Message);

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Hi there.");
        Assert.True(graph.IsComposed, graph.ReasonCode);

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Hi there.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.Equal("Founder rich response evidence 21.", native.Answer);
        Assert.Equal("BroadGoverned", native.EvidenceStandard);
        Assert.Equal("CanonicalGovernedEndpoint", native.ArticulationMode);
        Assert.True(native.EvidenceCount >= 1);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task HigherStandardIntentEvidence_MustWinOverCompatibleBroadEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var broad = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            RichResponsePlanFamily(20, "neutral", "broad_dismissal"));
        Assert.True(broad.Succeeded, broad.Message);

        for (var family = 4; family <= 6; family++)
        {
            var higher = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RichResponsePlanFamily(family, "formal", "higher_standard_acknowledgement"));
            Assert.True(higher.Succeeded, higher.Message);
        }

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Hi there.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("HigherStandard", native.EvidenceStandard);
        Assert.Equal("CanonicalGovernedEndpoint", native.ArticulationMode);
        Assert.Contains("Founder rich response evidence", native.Answer!, StringComparison.Ordinal);
        Assert.DoesNotContain("20", native.Answer!, StringComparison.Ordinal);
        Assert.True(native.EvidenceCount >= 3);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task NativeConversation_BindsExplicitReadOnlyResultThroughProofCarryingReceipt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        const string requestText = "What is the current open issue count?";

        for (var support = 1; support <= 3; support++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ReadOnlyContentBindingFamily(support));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var pending = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            requestText,
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        var readRequest = Assert.IsType<LegendConnectReadOnlyContentBindingRequest>(
            pending.ReadOnlyContentRequest);
        Assert.False(pending.Supported);
        Assert.False(pending.RequiresEscalation);
        Assert.Equal("read_only_content_binding_required", pending.ReasonCode);
        Assert.Equal("legend_translation_quality", readRequest.ToolName);
        Assert.Equal("needsReviewCount", readRequest.ValuePath);

        var executedUtc = DateTime.UtcNow;
        var receipt = new LegendConnectReadOnlyContentBindingReceipt(
            readRequest.RequestIdentity,
            readRequest.TransitionSignature,
            readRequest.ResultSemanticFrameSignature,
            readRequest.ToolName,
            LegendLanguageIdentity.TextHash(readRequest.ArgumentsJson),
            readRequest.ValuePath,
            readRequest.SemanticVariable,
            readRequest.ResultDimension,
            "4458",
            LegendLanguageIdentity.TextHash("{\"needsReviewCount\":4458}"),
            executedUtc,
            executedUtc,
            LegendConnectReadOnlyContentBindingContracts.Provenance,
            IsReadOnly: true,
            ZeroWrite: true);

        var completed = await fixture.Operations.TryInferConversationWithReadOnlyContentAsync(
            requestText,
            [],
            new LegendConnectDiscourseStateSnapshot([]),
            receipt);

        Assert.True(completed.Supported, completed.ReasonCode);
        Assert.Equal("Open issues 4458.", completed.Answer);
        var provenance = Assert.Single(completed.ContentBindingProvenance!);
        Assert.Equal(LegendConnectReadOnlyContentBindingContracts.Provenance, provenance.Provenance);
        Assert.Equal(readRequest.RequestIdentity, provenance.RequestIdentity);
        Assert.True(provenance.IsReadOnly);
        Assert.True(provenance.ZeroWrite);
    }

    [Fact]
    public async Task NativeConversation_RejectsAStaleReadOnlyContentReceipt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        const string requestText = "What is the current open issue count?";

        for (var support = 1; support <= 3; support++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ReadOnlyContentBindingFamily(support));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var pending = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            requestText,
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        var readRequest = Assert.IsType<LegendConnectReadOnlyContentBindingRequest>(
            pending.ReadOnlyContentRequest);
        var staleUtc = DateTime.UtcNow.AddMinutes(-10);
        var staleReceipt = new LegendConnectReadOnlyContentBindingReceipt(
            readRequest.RequestIdentity,
            readRequest.TransitionSignature,
            readRequest.ResultSemanticFrameSignature,
            readRequest.ToolName,
            LegendLanguageIdentity.TextHash(readRequest.ArgumentsJson),
            readRequest.ValuePath,
            readRequest.SemanticVariable,
            readRequest.ResultDimension,
            "4458",
            "stale-output-hash",
            staleUtc,
            staleUtc,
            LegendConnectReadOnlyContentBindingContracts.Provenance,
            IsReadOnly: true,
            ZeroWrite: true);

        var completed = await fixture.Operations.TryInferConversationWithReadOnlyContentAsync(
            requestText,
            [],
            new LegendConnectDiscourseStateSnapshot([]),
            staleReceipt);

        Assert.False(completed.Supported);
        Assert.Equal("read_only_content_binding_stale", completed.ReasonCode);
        Assert.False(completed.RequiresEscalation);
        Assert.Null(completed.Answer);
    }

    [Fact]
    public async Task NativeRealization_EnforcesExactGovernedSentenceCount()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        const string request = "Explain the competing explanations in exactly three sentences.";
        const string response =
            "Both explanations remain possible after the shared observation. " +
            "Compare the different predictions under one controlled test. " +
            "Then collect independent evidence before selecting either explanation.";

        for (var support = 1; support <= 3; support++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                PresentationConstraintFamily(
                    "exact-three",
                    support,
                    request,
                    response,
                    "adult",
                    "general",
                    "neutral",
                    "concise",
                    3,
                    "sentence_sequence"));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            request,
            new LegendConnectDiscourseStateSnapshot([]));
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.True(planned.Supported, planned.ReasonCode);
        Assert.Equal(3, plan.PresentationConstraints?.SentenceCount);
        Assert.Equal("sentence_sequence", plan.PresentationConstraints?.Structure);

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            request,
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal(response, native.Answer);
        Assert.Equal(3, native.Answer!.Count(item => item == '.'));

        const string unmetRequest = "Apply an unproven three-sentence presentation.";
        for (var support = 1; support <= 3; support++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                PresentationConstraintFamily(
                    "exact-three-unmet",
                    support,
                    unmetRequest,
                    "Both explanations remain possible. More evidence is required.",
                    "adult",
                    "general",
                    "neutral",
                    "concise",
                    3,
                    "sentence_sequence"));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var unmet = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            unmetRequest,
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.False(unmet.Supported);
        Assert.Equal("result_presentation_constraints_unmet", unmet.ReasonCode);
    }

    [Fact]
    public async Task NativeRealization_SelectsGovernedAudienceAndExpertisePresentation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        (string Audience, string Expertise, string Request, string Response)[] cases =
        [
            (
                "child",
                "novice",
                "Explain this for a child.",
                "Both ideas fit the clue, so we need another test."),
            (
                "adult",
                "general",
                "Explain this for an adult.",
                "The observation fits both explanations. Gather evidence that separates them."),
            (
                "adult",
                "expert",
                "Explain this for an expert.",
                "The observation is non-dispositive because both hypotheses entail it. Use a discriminating test before attribution.")
        ];

        foreach (var item in cases)
        {
            var sentenceCount = item.Response.Count(character => character == '.');
            for (var support = 1; support <= 3; support++)
            {
                var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                    PresentationConstraintFamily(
                        $"{item.Audience}-{item.Expertise}",
                        support,
                        item.Request,
                        item.Response,
                        item.Audience,
                        item.Expertise,
                        "neutral",
                        "concise",
                        sentenceCount,
                        sentenceCount == 1 ? "single_sentence" : "sentence_sequence"));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        foreach (var item in cases)
        {
            var planned = await fixture.Operations.TryPlanConversationAsync(
                item.Request,
                new LegendConnectDiscourseStateSnapshot([]));
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.True(planned.Supported, planned.ReasonCode);
            Assert.Equal(item.Audience, plan.PresentationConstraints?.Audience);
            Assert.Equal(item.Expertise, plan.PresentationConstraints?.Expertise);

            var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
                item.Request,
                [],
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(native.Supported, native.ReasonCode);
            Assert.Equal(item.Response, native.Answer);
        }
    }

    [Fact]
    public async Task NativeRealization_EnforcesGovernedResponseLength()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        (string Length, string Request, string Response)[] cases =
        [
            (
                "concise",
                "Give a concise explanation.",
                "Both explanations remain possible, so gather discriminating evidence."),
            (
                "detailed",
                "Give a detailed explanation.",
                "The shared observation supports both explanations and does not justify selecting either one. Compare their different predictions with a controlled test, collect independent evidence, and stop only when the evidence uniquely supports one explanation.")
        ];

        foreach (var item in cases)
        {
            var sentenceCount = item.Response.Count(character => character == '.');
            for (var support = 1; support <= 3; support++)
            {
                var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                    PresentationConstraintFamily(
                        item.Length,
                        support,
                        item.Request,
                        item.Response,
                        "adult",
                        "general",
                        "neutral",
                        item.Length,
                        sentenceCount,
                        sentenceCount == 1 ? "single_sentence" : "sentence_sequence"));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        foreach (var item in cases)
        {
            var planned = await fixture.Operations.TryPlanConversationAsync(
                item.Request,
                new LegendConnectDiscourseStateSnapshot([]));
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.True(planned.Supported, planned.ReasonCode);
            Assert.Equal(item.Length, plan.PresentationConstraints?.Length);

            var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
                item.Request,
                [],
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(native.Supported, native.ReasonCode);
            Assert.Equal(item.Response, native.Answer);
            Assert.Equal(
                item.Length == "concise",
                item.Response.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 24);
        }
    }

    [Fact]
    public async Task NativeRealization_SelectsGovernedTone()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        (string Tone, string Request, string Response)[] cases =
        [
            (
                "empathetic",
                "Explain this empathetically.",
                "I know this uncertainty is frustrating. Both explanations remain possible, so more evidence is required."),
            (
                "neutral",
                "Explain this neutrally.",
                "Both explanations remain possible. More evidence is required.")
        ];

        foreach (var item in cases)
        {
            for (var support = 1; support <= 3; support++)
            {
                var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                    PresentationConstraintFamily(
                        item.Tone,
                        support,
                        item.Request,
                        item.Response,
                        "adult",
                        "general",
                        item.Tone,
                        "concise",
                        2,
                        "sentence_sequence"));
                Assert.True(submitted.Succeeded, submitted.Message);
            }
        }

        foreach (var item in cases)
        {
            var planned = await fixture.Operations.TryPlanConversationAsync(
                item.Request,
                new LegendConnectDiscourseStateSnapshot([]));
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.True(planned.Supported, planned.ReasonCode);
            Assert.Equal(item.Tone, plan.PresentationConstraints?.Tone);

            var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
                item.Request,
                [],
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(native.Supported, native.ReasonCode);
            Assert.Equal(item.Response, native.Answer);
        }
    }

    [Fact]
    public async Task ResponseMeaningPlan_RejectsConflictingPresentationConstraints()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        const string request = "Give a contradictory presentation instruction.";

        for (var support = 1; support <= 3; support++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                PresentationConstraintFamily(
                    "conflict",
                    support,
                    request,
                    "Both explanations remain possible. Compare their predictions. Gather independent evidence.",
                    "adult",
                    "general",
                    "neutral",
                    "concise",
                    3,
                    "single_sentence"));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            request,
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("conflicting_response_presentation_constraints", planned.ReasonCode);
        Assert.Null(planned.Plan);

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            request,
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.False(native.Supported);
        Assert.Equal("conflicting_response_presentation_constraints", native.ReasonCode);
    }

    [Fact]
    public async Task PresentationConstraints_PreserveEvidenceUncertaintyAndProvenance()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        const string empatheticRequest = "Preserve the evidence in an empathetic explanation.";
        const string neutralRequest = "Preserve the evidence in a neutral explanation.";

        for (var support = 1; support <= 3; support++)
        {
            var empathetic = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                PresentationConstraintFamily(
                    "evidence-empathetic",
                    support,
                    empatheticRequest,
                    "I understand the uncertainty is difficult. Both explanations remain possible, so discriminating evidence is still required.",
                    "adult",
                    "general",
                    "empathetic",
                    "concise",
                    2,
                    "sentence_sequence"));
            Assert.True(empathetic.Succeeded, empathetic.Message);
            var neutral = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                PresentationConstraintFamily(
                    "evidence-neutral",
                    support,
                    neutralRequest,
                    "Both explanations remain possible. Discriminating evidence is still required.",
                    "adult",
                    "general",
                    "neutral",
                    "concise",
                    2,
                    "sentence_sequence"));
            Assert.True(neutral.Succeeded, neutral.Message);
        }

        var empatheticPlanResult = await fixture.Operations.TryPlanConversationAsync(
            empatheticRequest,
            new LegendConnectDiscourseStateSnapshot([]));
        var neutralPlanResult = await fixture.Operations.TryPlanConversationAsync(
            neutralRequest,
            new LegendConnectDiscourseStateSnapshot([]));
        var empatheticPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
            empatheticPlanResult.Plan);
        var neutralPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
            neutralPlanResult.Plan);

        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
            empatheticPlan.ResultDimensions[LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            empatheticPlan.ResultDimensions[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue,
            empatheticPlan.ResultDimensions[LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension]);
        Assert.Equal(
            empatheticPlan.ResultDimensions
                .Where(item => !item.Key.StartsWith("response_", StringComparison.Ordinal))
                .OrderBy(item => item.Key),
            neutralPlan.ResultDimensions
                .Where(item => !item.Key.StartsWith("response_", StringComparison.Ordinal))
                .OrderBy(item => item.Key));
        Assert.Equal(empatheticPlan.IndependentEvidenceCount, neutralPlan.IndependentEvidenceCount);
        Assert.Equal(empatheticPlan.EvidenceStandard, neutralPlan.EvidenceStandard);

        var empatheticNative = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            empatheticRequest,
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        var neutralNative = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            neutralRequest,
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(empatheticNative.Supported, empatheticNative.ReasonCode);
        Assert.True(neutralNative.Supported, neutralNative.ReasonCode);
        Assert.Equal(empatheticNative.EvidenceCount, neutralNative.EvidenceCount);
        Assert.Equal(empatheticNative.EvidenceStandard, neutralNative.EvidenceStandard);
        Assert.Equal(empatheticNative.AuthoritySummary, neutralNative.AuthoritySummary);
        Assert.Contains("Both explanations remain possible", empatheticNative.Answer);
        Assert.Contains("Both explanations remain possible", neutralNative.Answer);
        Assert.Contains("evidence", empatheticNative.Answer!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence", neutralNative.Answer!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeRealization_ComposesOriginalSurface_AndNeverReturnsStoredResultSentence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                OriginalArticulationFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var storedResults = await db.LegendCurriculumExamples
            .Where(item => item.SupersededUtc == null &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == item.Id &&
                    variation.Dimension == "conversation_function" &&
                    variation.Value == "receipt_confirmation_response"))
            .Join(
                db.LegendLanguageTextUnits,
                example => example.TextUnitId,
                unit => unit.Id,
                (_, unit) => unit.Text)
            .ToArrayAsync();

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Please acknowledge the status update.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(native.Answer));
        Assert.DoesNotContain(
            storedResults,
            stored => string.Equals(
                LegendLanguageIdentity.NormalizeText(stored),
                LegendLanguageIdentity.NormalizeText(native.Answer!),
                StringComparison.Ordinal));
        Assert.EndsWith(".", native.Answer, StringComparison.Ordinal);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task NativeRealization_UsesValidLayoutsFromTheSelectedSameFamilyLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var support = 1; support <= 3; support++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationLineageFamily("selected", support));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Use selected realization.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("OriginalComposition", native.ArticulationMode);
        Assert.NotNull(native.Answer);
        Assert.DoesNotContain(
            new[] { "dispatch", "inventory", "scheduling" },
            word => native.Answer!.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NativeRealization_SharedResultFrameDoesNotAuthorizeDifferentFamilyLanguage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var support = 1; support <= 3; support++)
        {
            var selected = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationLineageFamily("selected", support));
            Assert.True(selected.Succeeded, selected.Message);
            var unrelated = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationLineageFamily("unrelated", support));
            Assert.True(unrelated.Succeeded, unrelated.Message);
        }

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Use selected realization.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("OriginalComposition", native.ArticulationMode);
        Assert.NotNull(native.Answer);
        Assert.DoesNotContain(
            new[] { "missed", "dispatch", "absent", "inventory", "broken", "scheduling" },
            word => native.Answer!.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NativeRealization_ExplicitGovernedTransferCarriesItsResultFamilyLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var support = 1; support <= 3; support++)
        {
            var source = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationTransferSourceFamily(support));
            Assert.True(source.Succeeded, source.Message);
            var result = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationTransferResultFamily(support));
            Assert.True(result.Succeeded, result.Message);
            await fixture.Curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"realization-transfer-source-{support}",
                    "transfer.realization.authorized",
                    $"realization-transfer-result-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            var unrelated = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationTransferUnrelatedFamily(support));
            Assert.True(unrelated.Succeeded, unrelated.Message);
        }

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Authorize governed transfer.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("HigherStandard", native.EvidenceStandard);
        Assert.Equal("OriginalComposition", native.ArticulationMode);
        Assert.NotNull(native.Answer);
        Assert.DoesNotContain(
            new[] { "missed", "dispatch", "absent", "inventory", "broken", "scheduling" },
            word => native.Answer!.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NativeRealization_EverySurfaceComponentComesFromTheCompleteSelectedLineage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var support = 1; support <= 3; support++)
        {
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationLineageFamily("selected", support))).Succeeded);
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RealizationLineageFamily("unrelated", support))).Succeeded);
        }

        var selectedResultFamilyIds = await (
            from transition in db.LegendSemanticTransitionEvidence
            join result in db.LegendCurriculumExamples
                on transition.ResultCurriculumExampleId equals result.Id
            where transition.SupersededUtc == null &&
                transition.SourceSemanticFrame.Contains("selected_realization_request")
            select result.CurriculumFamilyId
        ).Distinct().ToArrayAsync();
        Assert.Equal(3, selectedResultFamilyIds.Length);

        var lineageResultTexts = await (
            from example in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
            where selectedResultFamilyIds.Contains(example.CurriculumFamilyId) &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == example.Id &&
                    variation.Dimension == "conversation_function" &&
                    variation.Value == "shared_lineage_response")
            select unit.Text
        ).ToArrayAsync();
        var lineageWords = lineageResultTexts
            .SelectMany(item => item.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(item => item.Trim('.', ',', ';', ':', '!', '?'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Use selected realization.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.NotNull(native.Answer);
        var realizedWords = native.Answer!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim('.', ',', ';', ':', '!', '?'));
        Assert.All(realizedWords, word => Assert.Contains(word, lineageWords));
    }

    [Fact]
    public async Task NativeRealization_ComposesRepeatedSemanticSlots_FromIndependentFounderFamilies()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RepeatedSemanticSlotArticulationFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var storedResults = await db.LegendCurriculumExamples
            .Where(item => item.SupersededUtc == null &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == item.Id &&
                    variation.Dimension == "conversation_function" &&
                    variation.Value == "conversation_acknowledgement"))
            .Join(
                db.LegendLanguageTextUnits,
                example => example.TextUnitId,
                unit => unit.Id,
                (_, unit) => unit.Text)
            .ToArrayAsync();

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Good morning.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.True(native.EvidenceCount >= 3);
        Assert.False(string.IsNullOrWhiteSpace(native.Answer));
        Assert.DoesNotContain(
            storedResults,
            stored => string.Equals(
                LegendLanguageIdentity.NormalizeText(stored),
                LegendLanguageIdentity.NormalizeText(native.Answer!),
                StringComparison.Ordinal));
        Assert.EndsWith("?", native.Answer, StringComparison.Ordinal);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task WholeSpanFounderResponses_DecomposeObservedSurfaceStructure_WithoutStoredResponseRetrieval()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                WholeSpanArticulationFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var storedResults = await db.LegendCurriculumExamples
            .Where(item => item.SupersededUtc == null &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == item.Id &&
                    variation.Dimension == "conversation_function" &&
                    variation.Value == "conversation_acknowledgement"))
            .Join(db.LegendLanguageTextUnits, example => example.TextUnitId, unit => unit.Id, (_, unit) => unit.Text)
            .ToArrayAsync();

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Good morning.", [], new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.True(native.EvidenceCount >= 3);
        Assert.False(string.IsNullOrWhiteSpace(native.Answer));
        Assert.DoesNotContain(storedResults, stored => string.Equals(
            LegendLanguageIdentity.NormalizeText(stored),
            LegendLanguageIdentity.NormalizeText(native.Answer!),
            StringComparison.Ordinal));
        Assert.Contains("!", native.Answer!, StringComparison.Ordinal);
        Assert.EndsWith("?", native.Answer, StringComparison.Ordinal);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task WholeSpanPrimitive_RemainsAtomicWhenDenseCurriculumRecognizesContainedSubspans()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var response = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                WholeSpanArticulationFamily(family));
            Assert.True(response.Succeeded, response.Message);
            var subspan = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ContainedSubspanFamily(family));
            Assert.True(subspan.Succeeded, subspan.Message);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Good morning.");

        Assert.True(graph.IsComposed, graph.ReasonCode);
        var node = Assert.Single(graph.Nodes);
        Assert.Equal("conversation_function", node.SemanticDimension);
        Assert.Equal("conversation_opening", node.SemanticValue);
        Assert.Equal(0, node.StartTokenIndex);
        Assert.Equal(2, node.TokenLength);
        Assert.Empty(graph.Relations);

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Good morning.", [], new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(native.Supported, native.ReasonCode);
        Assert.Equal("semantic_transition_governed_composed", native.ReasonCode);
        Assert.True(native.EvidenceCount > 0);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task CoextensiveWholeSpanMeanings_StillRequireAGovernedRelation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var opening = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                WholeSpanArticulationFamily(family));
            Assert.True(opening.Succeeded, opening.Message);
            var competing = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                CompetingWholeSpanFamily(family));
            Assert.True(competing.Succeeded, competing.Message);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Good morning.");

        Assert.False(graph.IsComposed);
        Assert.Equal("meaning_graph_relation_unproven", graph.ReasonCode);
        Assert.Contains(graph.Nodes, item => item.SemanticDimension == "conversation_function");
        Assert.Contains(graph.Nodes, item => item.SemanticDimension == "social_act");
    }

    [Fact]
    public async Task ExactFounderSourceGraph_IsNotMergedWithConflictingReusableGraphsFromOtherUtterances()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var exact = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExactCoherentGraphFamily(family));
            Assert.True(exact.Succeeded, exact.Message);
            var conflicting = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ConflictingReusableGraphFamily(family));
            Assert.True(conflicting.Succeeded, conflicting.Message);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(
            "Compare the evidence.");

        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.Contains(graph.Nodes, item =>
            item.SemanticDimension == "function" && item.SemanticValue == "compare");
        Assert.Contains(graph.Nodes, item =>
            item.SemanticDimension == "subject" && item.SemanticValue == "evidence");
        Assert.DoesNotContain(graph.Nodes, item => item.SemanticValue == "reject");
        Assert.DoesNotContain(graph.Nodes, item => item.SemanticValue == "assumption");

        var plan = await fixture.Operations.TryPlanConversationAsync(
            "Compare the evidence.", new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(plan.Supported, plan.ReasonCode);
        Assert.Equal("response_meaning_plan_governed", plan.ReasonCode);
        var snapshot = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(plan.Plan);
        Assert.Equal("comparison_response", snapshot.ResultDimensions["conversation_function"]);
        Assert.Equal(3, snapshot.IndependentEvidenceCount);
    }

    [Fact]
    public async Task ExactCanonicalEndpoint_RemainsConsumableWhenAnotherConnectedGraphDominatesTheSameSurface()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var canonical = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExactAtomicEndpointFamily(family));
            Assert.True(canonical.Succeeded, canonical.Message);
            var dense = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExactDenseConnectedGraphFamily(family));
            Assert.True(dense.Succeeded, dense.Message);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync("Review the cost.");
        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.NotEmpty(graph.Relations);
        Assert.Contains(graph.Nodes, item =>
            item.SemanticDimension == "task" && item.SemanticValue == "cost_review");

        var plan = await fixture.Operations.TryPlanConversationAsync(
            "Review the cost.", new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(plan.Supported, plan.ReasonCode);
        var snapshot = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(plan.Plan);
        Assert.Equal("cost_review_response", snapshot.ResultDimensions["conversation_function"]);
        Assert.Equal("HigherStandard", snapshot.EvidenceStandard);

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Review the cost.", [], new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(native.Supported, native.ReasonCode);
        Assert.True(native.EvidenceCount >= 3);
        Assert.False(native.RequiresEscalation);
    }

    [Fact]
    public async Task ExactEndpoint_PrecedesConflictingBroadProjectedResultFrames()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var exact = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "exact",
                    family,
                    "Diagnose the handoff.",
                    "exact_endpoint",
                    "handoff_diagnostic_response"));
            Assert.True(exact.Succeeded, exact.Message);

            var projected = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "projected",
                    family,
                    $"Projected capacity evidence {family}.",
                    "capacity_projection",
                    "capacity_diagnostic_response"));
            Assert.True(projected.Succeeded, projected.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Diagnose the handoff.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal(
            "handoff_diagnostic_response",
            plan.ResultDimensions["conversation_function"]);
        Assert.Equal("HigherStandard", plan.EvidenceStandard);
        Assert.Equal(3, plan.IndependentEvidenceCount);
    }

    [Fact]
    public async Task ExactEndpoint_ContradictionStopsBeforeProjectedSupport()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var exact = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "contradicted-exact",
                    family,
                    "Diagnose the handoff.",
                    "exact_endpoint",
                    "exact_handoff_response"));
            Assert.True(exact.Succeeded, exact.Message);

            var projected = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "contradiction-projection",
                    family,
                    $"Projected support evidence {family}.",
                    "projected_support",
                    "projected_handoff_response"));
            Assert.True(projected.Succeeded, projected.Message);
        }

        var contradictedEvidence = await db.LegendSemanticTransitionEvidence
            .Where(item =>
                item.SupersededUtc == null &&
                item.ResultSemanticFrame.Contains("exact_handoff_response"))
            .OrderBy(item => item.Id)
            .FirstAsync();
        contradictedEvidence.ContributionState = "Contradictory";
        await db.SaveChangesAsync();

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Diagnose the handoff.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal(
            "exact_source_semantic_transition_contradicted",
            planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task ExactEndpoint_MultipleGovernedResultsRemainAmbiguous()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var first = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "multiple-first",
                    family,
                    "Diagnose the handoff.",
                    "first_exact_route",
                    "first_handoff_response"));
            Assert.True(first.Succeeded, first.Message);

            var second = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "multiple-second",
                    family,
                    "Diagnose the handoff.",
                    "second_exact_route",
                    "second_handoff_response"));
            Assert.True(second.Succeeded, second.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Diagnose the handoff.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal(
            "ambiguous_exact_source_semantic_transition",
            planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task ExactEndpoint_PrefersHigherStandardOverBroadGovernedEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var broad = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            SelectorOrderingFamily(
                "broad",
                1,
                "Diagnose the handoff.",
                "broad_route",
                "broad_handoff_response"));
        Assert.True(broad.Succeeded, broad.Message);

        for (var family = 1; family <= 3; family++)
        {
            var higher = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "higher",
                    family,
                    "Diagnose the handoff.",
                    "higher_route",
                    "higher_handoff_response"));
            Assert.True(higher.Succeeded, higher.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Diagnose the handoff.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal(
            "higher_handoff_response",
            plan.ResultDimensions["conversation_function"]);
        Assert.Equal("HigherStandard", plan.EvidenceStandard);
        Assert.Equal(3, plan.IndependentEvidenceCount);
    }

    [Fact]
    public async Task ExactEndpointOrdering_DoesNotSupportAnUnseenPrompt()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var exact = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                SelectorOrderingFamily(
                    "unseen-guard",
                    family,
                    "Diagnose the handoff.",
                    "exact_endpoint",
                    "handoff_diagnostic_response"));
            Assert.True(exact.Succeeded, exact.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Uncatalogued zephyr request.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("meaning_graph_component_unknown", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task GovernedControlledSurfaceVariations_ReusePrimitiveFamilyRelationAndTransitionIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                CompetingHypothesesTestDesignFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var governedFamilyIds = await db.LegendCurriculumFamilies
            .Where(item => item.FamilyKey.StartsWith("reasoning.competing-hypotheses.test-design."))
            .Select(item => item.Id)
            .ToArrayAsync();
        Assert.Equal(3, governedFamilyIds.Length);

        var nodeLineage = await db.LegendLanguageMeaningNodeEvidence
            .Where(item => governedFamilyIds.Contains(item.CurriculumFamilyId) &&
                item.SupersededUtc == null)
            .Select(item => new { item.SemanticSignature, item.CurriculumFamilyId })
            .Distinct()
            .ToArrayAsync();
        Assert.Equal(2, nodeLineage.Select(item => item.SemanticSignature).Distinct().Count());
        Assert.All(
            nodeLineage.GroupBy(item => item.SemanticSignature),
            group => Assert.Equal(3, group.Select(item => item.CurriculumFamilyId).Distinct().Count()));

        var relationLineage = await (
            from evidence in db.LegendLanguageMeaningRelationEvidence
            join relation in db.LegendLanguageMeaningRelations
                on evidence.MeaningRelationId equals relation.Id
            where governedFamilyIds.Contains(evidence.CurriculumFamilyId) &&
                evidence.SupersededUtc == null
            select new { relation.RelationSignature, evidence.CurriculumFamilyId }
        ).Distinct().ToArrayAsync();
        Assert.Single(relationLineage.Select(item => item.RelationSignature).Distinct());
        Assert.Equal(3, relationLineage.Select(item => item.CurriculumFamilyId).Distinct().Count());

        var transitionLineage = await (
            from evidence in db.LegendSemanticTransitionEvidence
            join source in db.LegendCurriculumExamples
                on evidence.SourceCurriculumExampleId equals source.Id
            where governedFamilyIds.Contains(source.CurriculumFamilyId) &&
                evidence.SupersededUtc == null
            select new { evidence.TransitionSignature, source.CurriculumFamilyId }
        ).Distinct().ToArrayAsync();
        Assert.Single(transitionLineage.Select(item => item.TransitionSignature).Distinct());
        Assert.Equal(3, transitionLineage.Select(item => item.CurriculumFamilyId).Distinct().Count());

        var canonical = await fixture.Operations.TryPlanConversationAsync(
            "Keep both hypotheses; design a test.",
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(canonical.Supported, canonical.ReasonCode);
        var canonicalPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(canonical.Plan);

        var heldOutParaphrases = new[]
        {
            "Keep both hypotheses; plan an experiment.",
            "Retain the competing explanations; devise a discriminating check.",
            "Preserve both theories; construct a separating trial.",
            "Maintain both candidate causes; create an evidence-producing test.",
            "Hold both possibilities; design a test."
        };
        foreach (var paraphrase in heldOutParaphrases)
        {
            var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(paraphrase);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.Equal(2, graph.Nodes.Count);
            Assert.Single(graph.Relations);
            Assert.Equal(
                new[] { "design_discriminating_test", "retain_competing_hypotheses" },
                graph.Nodes.Select(item => item.SemanticValue).OrderBy(item => item).ToArray());

            var planned = await fixture.Operations.TryPlanConversationAsync(
                paraphrase,
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.Equal(canonicalPlan.SourceMeaningGraphIdentity, plan.SourceMeaningGraphIdentity);
            Assert.Equal(canonicalPlan.TransitionSignature, plan.TransitionSignature);
            Assert.Equal(canonicalPlan.ResultSemanticFrameSignature, plan.ResultSemanticFrameSignature);
            Assert.Equal(canonicalPlan.PlanIdentity, plan.PlanIdentity);
            Assert.Equal("test_design_guidance", plan.ResultDimensions["conversation_function"]);
            Assert.Equal("HigherStandard", plan.EvidenceStandard);
        }
    }

    [Fact]
    public async Task ControlledSurfaceVariations_DoNotUseSubstringOrNearNeighborMatching()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        for (var family = 1; family <= 3; family++)
        {
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                CompetingHypothesesTestDesignFamily(family))).Succeeded);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(
            "Keep both hypotheseses; design a tester.");

        Assert.False(graph.IsComposed);
        Assert.Equal("meaning_graph_component_unknown", graph.ReasonCode);
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Relations);
    }

    [Fact]
    public async Task ControlledSurfaceVariations_PreserveContrastiveMeaningAndRelationBoundaries()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        for (var family = 1; family <= 3; family++)
        {
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                CompetingHypothesesTestDesignFamily(family))).Succeeded);
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                PrematureHypothesisSelectionFamily(family))).Succeeded);
        }

        var retained = await fixture.Operations.TryPlanConversationAsync(
            "Keep both hypotheses; plan an experiment.",
            new LegendConnectDiscourseStateSnapshot([]));
        var contrast = await fixture.Operations.TryPlanConversationAsync(
            "Choose one hypothesis before you design a test.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(retained.Supported, retained.ReasonCode);
        Assert.True(contrast.Supported, contrast.ReasonCode);
        var retainedPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(retained.Plan);
        var contrastPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(contrast.Plan);
        Assert.NotEqual(retainedPlan.SourceMeaningGraphIdentity, contrastPlan.SourceMeaningGraphIdentity);
        Assert.NotEqual(retainedPlan.TransitionSignature, contrastPlan.TransitionSignature);
        Assert.NotEqual(retainedPlan.ResultSemanticFrameSignature, contrastPlan.ResultSemanticFrameSignature);
        Assert.Equal("test_design_guidance", retainedPlan.ResultDimensions["conversation_function"]);
        Assert.Equal("premature_selection_warning", contrastPlan.ResultDimensions["conversation_function"]);
    }

    [Fact]
    public async Task ControlledSurfaceVariations_DoNotComposeAcrossUnrelatedFamilies()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        for (var family = 1; family <= 3; family++)
        {
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                CompetingHypothesesTestDesignFamily(family))).Succeeded);
        }
        Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            UnrelatedDispatchSurfaceFamily())).Succeeded);

        // Both spans resolve to mature primitives, and the primitive pair has
        // a mature global relation. The dispatch surface itself has no
        // Founder-governed relation in any family shared by both spans.
        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(
            "Keep both hypotheses; route a dispatch.");

        Assert.False(graph.IsComposed);
        Assert.Equal("meaning_graph_relation_unproven", graph.ReasonCode);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Empty(graph.Relations);

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Keep both hypotheses; route a dispatch.",
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.False(planned.Supported);
        Assert.Equal("meaning_graph_relation_unproven", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task BroadProjection_FailsClosedWhenNoExactEndpointCanResolveConflictingResults()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var neutral = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RichResponsePlanFamily(family, "neutral", "acknowledgement"));
            Assert.True(neutral.Succeeded, neutral.Message);

            var formal = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                RichResponsePlanFamily(family + 3, "formal", "formal_acknowledgement"));
            Assert.True(formal.Succeeded, formal.Message);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Hi there.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("ambiguous_semantic_transition_projection", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Theory]
    [InlineData("handoff", "failure", "dispatch")]
    [InlineData("handoff", "failure", "inventory")]
    [InlineData("handoff", "failure", "scheduling")]
    [InlineData("capacity", "shortage", "dispatch")]
    [InlineData("capacity", "shortage", "inventory")]
    [InlineData("capacity", "shortage", "scheduling")]
    public async Task BroadProjection_DoesNotCrossCanonicalSemanticFamiliesOnSharedDimensions(
        string subject,
        string descriptor,
        string unrelatedFamily)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var source = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            ProjectionIdentitySourceFamily(subject, descriptor));
        Assert.True(source.Succeeded, source.Message);
        for (var support = 1; support <= 3; support++)
        {
            var contaminant = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ProjectionCollisionFamily(subject, unrelatedFamily, support));
            Assert.True(contaminant.Succeeded, contaminant.Message);
        }

        var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(
            $"{subject} {descriptor}");
        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.Contains(graph.Nodes, item =>
            item.SemanticDimension == "diagnostic_subject" &&
            item.SemanticValue == subject);
        Assert.Contains(graph.Nodes, item =>
            item.SemanticDimension == "diagnostic_family" &&
            item.SemanticValue == $"{subject}_{descriptor}");

        var planned = await fixture.Operations.TryPlanConversationAsync(
            $"{subject} {descriptor}",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("semantic_transition_not_supported", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task BroadProjection_AllowsAValidSameFamilyTransition()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            SameFamilyProjectionFamily());
        Assert.True(submitted.Succeeded, submitted.Message);

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "handoff failure",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal("handoff_same_family_response", plan.ResultDimensions["conversation_function"]);
        Assert.Equal("BroadGoverned", plan.EvidenceStandard);
    }

    [Fact]
    public async Task BroadProjection_RejectsACrossFamilyTransitionWithoutAnExplicitGovernedTransfer()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
            SameFamilyProjectionFamily());
        Assert.True(submitted.Succeeded, submitted.Message);

        var unrelatedFamily = new LegendCurriculumFamily
        {
            FamilyKey = "projection.unrelated-result-family",
            SemanticCategory = "Unrelated result family",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
        db.LegendCurriculumFamilies.Add(unrelatedFamily);
        var resultExample = await (
            from example in db.LegendCurriculumExamples
            join variation in db.LegendCurriculumExampleVariations
                on example.Id equals variation.CurriculumExampleId
            where variation.Dimension == "conversation_function" &&
                variation.Value == "handoff_same_family_response"
            select example
        ).SingleAsync();
        resultExample.CurriculumFamilyId = unrelatedFamily.Id;
        await db.SaveChangesAsync();

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "handoff failure",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("semantic_transition_not_supported", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task BroadProjection_AllowsOnlyAProductionEligibleExplicitGovernedFamilyTransfer()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var support = 1; support <= 3; support++)
        {
            var source = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExplicitTransferSourceFamily(support));
            Assert.True(source.Succeeded, source.Message);
            var result = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExplicitTransferResultFamily(support));
            Assert.True(result.Succeeded, result.Message);
            await fixture.Curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"governed-transfer-source-{support}",
                    "transfer.governed.diagnostic",
                    $"governed-transfer-result-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "handoff",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal("governed_transfer_response", plan.ResultDimensions["decision_posture"]);
        Assert.Equal("HigherStandard", plan.EvidenceStandard);
        Assert.Equal(3, plan.IndependentEvidenceCount);
        var explicitTransfers = await db.LegendSemanticTransitionEvidence
            .Where(item => item.FounderSemanticExampleRelationEvidenceId != null)
            .ToArrayAsync();
        Assert.Equal(3, explicitTransfers.Length);
        Assert.All(explicitTransfers, item =>
            Assert.NotNull(item.FounderSemanticExampleRelationEvidenceId));
    }

    [Fact]
    public async Task BroadProjection_RejectsAnExplicitFamilyTransferBelowProductionEligibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var support = 1; support <= 2; support++)
        {
            var source = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExplicitTransferSourceFamily(support));
            Assert.True(source.Succeeded, source.Message);
            var result = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ExplicitTransferResultFamily(support));
            Assert.True(result.Succeeded, result.Message);
            await fixture.Curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"governed-transfer-source-{support}",
                    "transfer.governed.diagnostic",
                    $"governed-transfer-result-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "handoff",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("semantic_transition_not_supported", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task ResponseMeaningPlan_IsTextFreeAndUsesTheExistingGovernedTransitionAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        // Three independent Founder-controlled families mature the same
        // semantic transition. The test observes plan structure only; no
        // result text is supplied to or asserted from the planner.
        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ResponsePlanFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var surfaces = new[] { "Hi there.", "Good morning.", "Greetings friend." };
        var plans = new List<LegendConnectResponseMeaningPlanSnapshot>();
        foreach (var surface in surfaces)
        {
            var graph = await fixture.Operations.AnalyzeReusableMeaningGraphAsync(surface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            var planned = await fixture.Operations.TryPlanConversationAsync(
                surface,
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            plans.Add(Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan));
        }

        // Three surface-different inputs express the same taught meaning and
        // must converge on the same text-free semantic plan.
        Assert.Single(plans.Select(item => item.PlanIdentity).Distinct());
        var plan = plans[0];
        Assert.False(string.IsNullOrWhiteSpace(plan.PlanIdentity));
        Assert.False(string.IsNullOrWhiteSpace(plan.SourceMeaningGraphIdentity));
        Assert.False(string.IsNullOrWhiteSpace(plan.TransitionSignature));
        Assert.False(string.IsNullOrWhiteSpace(plan.ResultSemanticFrameSignature));
        Assert.Equal("acknowledgement", plan.ResultDimensions["conversation_function"]);
        Assert.Equal(3, plan.IndependentEvidenceCount);
        Assert.Empty(plan.ResolvedDiscourseBindings);

        // A Stage 4 plan carries no surface, canonical-result identity, or
        // realization template. Its JSON must be safe to retain for proof.
        var serialized = System.Text.Json.JsonSerializer.Serialize(plan);
        Assert.DoesNotContain("Acknowledged", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hi there", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("template", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResponseMeaningPlan_ChangesOnlyWhenTheGovernedComposedMeaningChanges()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ResponsePlanPolarityFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var positive = await fixture.Operations.TryPlanConversationAsync(
            "I agree.", new LegendConnectDiscourseStateSnapshot([]));
        var negative = await fixture.Operations.TryPlanConversationAsync(
            "I disagree.", new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(positive.Supported, positive.ReasonCode);
        Assert.True(negative.Supported, negative.ReasonCode);
        var positivePlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(positive.Plan);
        var negativePlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(negative.Plan);
        Assert.NotEqual(positivePlan.PlanIdentity, negativePlan.PlanIdentity);
        Assert.NotEqual(positivePlan.ResultSemanticFrameSignature, negativePlan.ResultSemanticFrameSignature);
        Assert.Equal("positive", positivePlan.ResultDimensions["polarity"]);
        Assert.Equal("negative", negativePlan.ResultDimensions["polarity"]);
    }

    [Fact]
    public async Task ResponseMeaningPlan_RetainsDecisionParityWithUnrelatedCorpusAndInactiveRelevantContradiction()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        for (var family = 1; family <= 3; family++)
        {
            var submitted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ResponsePlanFamily(family));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var before = await fixture.Operations.TryPlanConversationAsync(
            "Hi there.",
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(before.Supported, before.ReasonCode);
        var beforePlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(before.Plan);

        var relevant = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null)
            .OrderBy(item => item.Id)
            .FirstAsync();
        var relevantSource = await db.LegendCurriculumExamples
            .SingleAsync(item => item.Id == relevant.SourceCurriculumExampleId);
        var inactiveSourceUnit = TestTextUnit("Inactive relevant source evidence.");
        var inactiveResultUnit = TestTextUnit("Inactive relevant result evidence.");
        var inactiveSource = TestExample(relevantSource.CurriculumFamilyId, inactiveSourceUnit.Id);
        var inactiveResult = TestExample(relevantSource.CurriculumFamilyId, inactiveResultUnit.Id);
        var relevantContradiction = new LegendSemanticTransitionEvidence
        {
            Id = Guid.NewGuid(),
            TransitionSignature = relevant.TransitionSignature,
            SourceSemanticFrameSignature = relevant.SourceSemanticFrameSignature,
            ResultSemanticFrameSignature = relevant.ResultSemanticFrameSignature,
            SourceSemanticFrame = relevant.SourceSemanticFrame,
            ResultSemanticFrame = relevant.ResultSemanticFrame,
            SourceLanguageCode = "en",
            ResultLanguageCode = "en",
            SourceCurriculumExampleId = inactiveSource.Id,
            ResultCurriculumExampleId = inactiveResult.Id,
            IndependentSourceIdentity = "inactive-relevant-contradiction",
            ContributionState = "Contradictory",
            IsHumanVerifiedSupport = true,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            SupersededUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        db.AddRange(
            inactiveSourceUnit,
            inactiveResultUnit,
            inactiveSource,
            inactiveResult,
            relevantContradiction);

        for (var index = 0; index < 300; index++)
        {
            var unrelatedFamily = new LegendCurriculumFamily
            {
                Id = Guid.NewGuid(),
                FamilyKey = $"retrieval.parity.unrelated.{index:D3}",
                SemanticCategory = "unrelated",
                Provenance = LegendConnectKnowledgeProvenance.FounderApproved
            };
            var sourceUnit = TestTextUnit($"Unrelated source {index:D3}.");
            var resultUnit = TestTextUnit($"Unrelated result {index:D3}.");
            var source = TestExample(unrelatedFamily.Id, sourceUnit.Id);
            var result = TestExample(unrelatedFamily.Id, resultUnit.Id);
            var sourceFrame = System.Text.Json.JsonSerializer.Serialize(
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unrelated_dimension"] = $"source_{index:D3}"
                });
            var resultFrame = System.Text.Json.JsonSerializer.Serialize(
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unrelated_dimension"] = $"result_{index:D3}"
                });
            db.AddRange(
                unrelatedFamily,
                sourceUnit,
                resultUnit,
                source,
                result,
                new LegendSemanticTransitionEvidence
                {
                    Id = Guid.NewGuid(),
                    TransitionSignature = LegendLanguageIdentity.TextHash(
                        $"unrelated-transition-{index:D3}"),
                    SourceSemanticFrameSignature = LegendLanguageIdentity.TextHash(sourceFrame),
                    ResultSemanticFrameSignature = LegendLanguageIdentity.TextHash(resultFrame),
                    SourceSemanticFrame = sourceFrame,
                    ResultSemanticFrame = resultFrame,
                    SourceLanguageCode = "en",
                    ResultLanguageCode = "en",
                    SourceCurriculumExampleId = source.Id,
                    ResultCurriculumExampleId = result.Id,
                    IndependentSourceIdentity = $"unrelated-source-{index:D3}",
                    ContributionState = "Supported",
                    IsHumanVerifiedSupport = true,
                    Provenance = LegendConnectKnowledgeProvenance.FounderApproved
                });
        }
        await db.SaveChangesAsync();

        var after = await fixture.Operations.TryPlanConversationAsync(
            "Hi there.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(after.Supported, after.ReasonCode);
        var afterPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(after.Plan);
        Assert.Equal(before.ReasonCode, after.ReasonCode);
        Assert.Equal(beforePlan.PlanIdentity, afterPlan.PlanIdentity);
        Assert.Equal(beforePlan.ResultSemanticFrameSignature, afterPlan.ResultSemanticFrameSignature);
        Assert.Equal(
            beforePlan.ResultDimensions.OrderBy(item => item.Key, StringComparer.Ordinal),
            afterPlan.ResultDimensions.OrderBy(item => item.Key, StringComparer.Ordinal));

        relevantContradiction.SupersededUtc = null;
        await db.SaveChangesAsync();
        var contradicted = await fixture.Operations.TryPlanConversationAsync(
            "Hi there.",
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.False(contradicted.Supported);
        Assert.Equal("semantic_transition_contradicted", contradicted.ReasonCode);

        static LegendLanguageTextUnit TestTextUnit(string text) => new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            StoragePartition = "/en",
            NormalizedHash = LegendLanguageIdentity.TextHash(text),
            Text = text,
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved,
            IsTrainingEligible = true
        };

        static LegendCurriculumExample TestExample(Guid familyId, Guid unitId) => new()
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = familyId,
            TextUnitId = unitId,
            LanguageCode = "en",
            Provenance = LegendConnectKnowledgeProvenance.FounderApproved
        };
    }

    [Fact]
    public async Task ResponseMeaningPlan_FailsClosedForMissingOrContradictedTransitionEvidence()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        for (var family = 1; family <= 3; family++)
        {
            var unsupported = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ResponsePlanNoTransitionFamily(family));
            Assert.True(unsupported.Succeeded, unsupported.Message);
        }
        var noTransition = await fixture.Operations.TryPlanConversationAsync(
            "Observe carefully.", new LegendConnectDiscourseStateSnapshot([]));
        Assert.False(noTransition.Supported);
        Assert.Equal("semantic_transition_evidence_unknown", noTransition.ReasonCode);
        Assert.Null(noTransition.Plan);

        for (var family = 1; family <= 3; family++)
        {
            var supported = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(
                ResponsePlanFamily(family));
            Assert.True(supported.Succeeded, supported.Message);
        }
        var evidence = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null)
            .OrderBy(item => item.Id)
            .FirstAsync();
        // This models a retained governed contradiction. It does not create
        // a plan or response directly; the production eligibility gate must
        // remove the existing transition from planning.
        evidence.ContributionState = "Contradictory";
        await db.SaveChangesAsync();

        var contradicted = await fixture.Operations.TryPlanConversationAsync(
            "Hi there.", new LegendConnectDiscourseStateSnapshot([]));
        Assert.False(contradicted.Supported);
        Assert.Equal("semantic_transition_contradicted", contradicted.ReasonCode);
        Assert.Null(contradicted.Plan);
    }

    [Fact]
    public async Task ExplicitFounderGrounding_ProjectsNonLiteralGreetingMeaningOntoExactSurfaceSpan()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var batch = GreetingBatch(
            "grounding.greeting.explicit");

        var submitted =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(batch);

        Assert.True(
            submitted.Succeeded,
            submitted.Message);

        var hiExample = await (
            from example in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits
                on example.TextUnitId equals unit.Id
            where example.LanguageCode == "en" &&
                  example.DerivedFromCurriculumExampleId == null &&
                  example.SupersededUtc == null &&
                  unit.Text == "Hi there."
            select new
            {
                Example = example,
                Unit = unit
            }
        ).SingleAsync();

        var projected =
            await db.LegendLanguageCompositionalAnchors
                .Where(item =>
                    item.CurriculumExampleId ==
                        hiExample.Example.Id &&
                    item.Dimension ==
                        "conversation_function" &&
                    item.Value ==
                        "greeting" &&
                    item.SemanticSignature != null &&
                    item.LexemeId != null &&
                    item.ComponentStartTokenIndex != null &&
                    item.ComponentLength != null &&
                    item.ComponentLength > 0 &&
                    item.Provenance ==
                        "FounderApproved" &&
                    item.SupersededUtc == null)
                .ToListAsync();

        var anchor = Assert.Single(projected);

        Assert.Equal(0, anchor.ComponentStartTokenIndex);
        Assert.Equal(2, anchor.ComponentLength);

        var understood =
            await fixture.Curriculum
                .AnalyzeShadowSourceSemanticsAsync(
                    "en",
                    "Hi there.");

        Assert.Equal(
            LegendShadowSourceUnderstanding
                .SupportedForShadowEvaluation,
            understood.State);

        Assert.DoesNotContain(
            "source_semantic_component_unknown",
            understood.Reasons);

        Assert.Contains(
            understood.Components,
            item =>
                item.Dimension ==
                    "conversation_function" &&
                item.Value ==
                    "greeting" &&
                item.StartTokenIndex == 0 &&
                item.TokenLength == 2);
    }

    [Fact]
    public async Task ExplicitFounderGrounding_WorksForSecondIndependentSurfaceWithoutPhraseSpecificProductionLogic()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var submitted =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(
                    GreetingBatch(
                        "grounding.greeting.second-surface"));

        Assert.True(
            submitted.Succeeded,
            submitted.Message);

        var understood =
            await fixture.Curriculum
                .AnalyzeShadowSourceSemanticsAsync(
                    "en",
                    "Good morning.");

        Assert.Equal(
            LegendShadowSourceUnderstanding
                .SupportedForShadowEvaluation,
            understood.State);

        Assert.Contains(
            understood.Components,
            item =>
                item.Dimension ==
                    "conversation_function" &&
                item.Value ==
                    "greeting");

        Assert.DoesNotContain(
            "source_semantic_component_unknown",
            understood.Reasons);
    }

    [Fact]
    public async Task AbstractMeaningWithoutExplicitGrounding_DoesNotBecomeLexicalKnowledge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var batch =
            new LegendConnectCurriculumBatchSubmission(
                "grounding.greeting.no-explicit-grounding",
                "Fail-closed non-literal semantic evidence",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        "Salutations there.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] =
                                "Salutations there.",
                            ["conversation_function"] =
                                "greeting"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        "Salutations friend.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] =
                                "Salutations friend.",
                            ["conversation_function"] =
                                "greeting"
                        })
                ]);

        var submitted =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(batch);

        Assert.True(
            submitted.Succeeded,
            submitted.Message);

        var example = await (
            from curriculumExample
                in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits
                on curriculumExample.TextUnitId
                equals unit.Id
            where
                curriculumExample.LanguageCode == "en" &&
                curriculumExample
                    .DerivedFromCurriculumExampleId == null &&
                curriculumExample.SupersededUtc == null &&
                unit.Text == "Salutations there."
            select curriculumExample
        ).SingleAsync();

        var manufacturedSemanticSpan =
            await db.LegendLanguageCompositionalAnchors
                .AnyAsync(item =>
                    item.CurriculumExampleId == example.Id &&
                    item.Dimension ==
                        "conversation_function" &&
                    item.Value ==
                        "greeting" &&
                    item.LexemeId != null &&
                    item.ComponentStartTokenIndex != null &&
                    item.ComponentLength != null &&
                    item.ComponentLength > 0 &&
                    item.SupersededUtc == null);

        Assert.False(manufacturedSemanticSpan);

        var understood =
            await fixture.Curriculum
                .AnalyzeShadowSourceSemanticsAsync(
                    "en",
                    "Salutations there.");

        Assert.DoesNotContain(
            understood.Components,
            item =>
                item.Dimension ==
                    "conversation_function" &&
                item.Value ==
                    "greeting");
    }

    [Fact]
    public async Task AmbiguousControlledSurfaceSpan_IsRejectedBeforeMutation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var batch =
            new LegendConnectCurriculumBatchSubmission(
                "grounding.greeting.ambiguous",
                "Ambiguous span must fail closed",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        "Hello hello.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] = "hello",
                            ["conversation_function"] =
                                "greeting"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        "Welcome hello.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] = "hello",
                            ["conversation_function"] =
                                "greeting"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        "Greetings hello.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] = "hello",
                            ["conversation_function"] =
                                "greeting"
                        }),
                    Response(
                        "Hello acknowledged."),
                    Response(
                        "Welcome acknowledged."),
                    Response(
                        "Greetings acknowledged.")
                ],
                [
                    GreetingTransition()
                ],
                [
                    new LegendConnectSemanticSpanGroundingSubmission(
                        "conversation_function",
                        "surface_phrase")
                ]);

        var before =
            await db.LegendCurriculumFamilies.CountAsync();

        var result =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(batch);

        Assert.False(result.Succeeded);

        Assert.Equal(
            "semantic_span_grounding_not_explicit",
            result.ErrorCode);

        Assert.Equal(
            before,
            await db.LegendCurriculumFamilies.CountAsync());
    }

    [Fact]
    public async Task GroundingToResultOnlyDimension_IsRejected()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var batch =
            new LegendConnectCurriculumBatchSubmission(
                "grounding.result-only.rejected",
                "Result-only semantic dimensions cannot authorize source interpretation",
                [
                    SourceGreeting(
                        "Hello there."),
                    SourceGreeting(
                        "Hi again."),
                    SourceGreeting(
                        "Greetings friend."),
                    new LegendConnectCurriculumExampleSubmission(
                        "Hello acknowledged.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] =
                                "Hello acknowledged.",
                            ["conversation_function"] =
                                "acknowledgement",
                            ["response_role"] =
                                "response"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        "Hi acknowledged.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] =
                                "Hi acknowledged.",
                            ["conversation_function"] =
                                "acknowledgement",
                            ["response_role"] =
                                "response"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        "Greetings acknowledged.",
                        new Dictionary<string, string>
                        {
                            ["surface_phrase"] =
                                "Greetings acknowledged.",
                            ["conversation_function"] =
                                "acknowledgement",
                            ["response_role"] =
                                "response"
                        })
                ],
                [
                    new LegendConnectSemanticTransitionSubmission(
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] =
                                    "greeting"
                            }),
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] =
                                    "acknowledgement",
                                ["response_role"] =
                                    "response"
                            }))
                ],
                [
                    new LegendConnectSemanticSpanGroundingSubmission(
                        "response_role",
                        "surface_phrase")
                ]);

        var result =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(batch);

        Assert.False(result.Succeeded);

        Assert.Equal(
            "semantic_span_grounding_not_source",
            result.ErrorCode);
    }

    [Fact]
    public async Task GroundedProjection_IsIdempotentAndLanguageIsolated()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        var batch =
            GreetingBatch(
                "grounding.greeting.idempotent");

        var first =
            await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(batch);

        Assert.True(
            first.Succeeded,
            first.Message);

        var firstCount =
            await db.LegendLanguageCompositionalAnchors
                .CountAsync(item =>
                    item.Dimension ==
                        "conversation_function" &&
                    item.Value ==
                        "greeting" &&
                    item.LexemeId != null &&
                    item.ComponentStartTokenIndex != null &&
                    item.ComponentLength != null &&
                    item.ComponentLength > 0 &&
                    item.Provenance ==
                        "FounderApproved" &&
                    item.SupersededUtc == null);

        Assert.True(firstCount >= 3);

        // Exercise the existing canonical historical path again.
        // This must not manufacture duplicate semantic anchors.
        await fixture.Curriculum
            .ReevaluateHistoricalAlignmentsAsync(100);

        await fixture.Curriculum
            .ReevaluateHistoricalAlignmentsAsync(100);

        var afterReplayCount =
            await db.LegendLanguageCompositionalAnchors
                .CountAsync(item =>
                    item.Dimension ==
                        "conversation_function" &&
                    item.Value ==
                        "greeting" &&
                    item.LexemeId != null &&
                    item.ComponentStartTokenIndex != null &&
                    item.ComponentLength != null &&
                    item.ComponentLength > 0 &&
                    item.Provenance ==
                        "FounderApproved" &&
                    item.SupersededUtc == null);

        Assert.Equal(
            firstCount,
            afterReplayCount);

        var english =
            await fixture.Curriculum
                .AnalyzeShadowSourceSemanticsAsync(
                    "en",
                    "Hi there.");

        Assert.Equal(
            LegendShadowSourceUnderstanding
                .SupportedForShadowEvaluation,
            english.State);

        // Nothing in the English grounding may leak into another
        // language partition merely because its surface text matches.
        var otherLanguage =
            await fixture.Curriculum
                .AnalyzeShadowSourceSemanticsAsync(
                    "x-test",
                    "Hi there.");

        Assert.NotEqual(
            LegendShadowSourceUnderstanding
                .SupportedForShadowEvaluation,
            otherLanguage.State);
    }

    [Fact]
    public async Task HistoricalControlledSourceFrameProjection_IsPersistedAfterIndependentEligibility()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        // These deliberately model pre-@ground Founder curriculum. Each
        // source frame is explicit in the persisted controlled transition and
        // each source text has one exact full-text surface control, but no
        // lexical semantic projection exists at initial ingestion.
        for (var familyIndex = 1; familyIndex <= 3; familyIndex++)
        {
            var submission = new LegendConnectCurriculumBatchSubmission(
                $"historical.frame-projection.{familyIndex}",
                "Historical controlled source-frame proof",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        $"Historic opening {familyIndex}.",
                        new Dictionary<string, string>
                        {
                            ["discourse_role"] = "opening",
                            ["function"] = "greeting",
                            ["intent"] = "start_conversation",
                            ["register"] = "neutral"
                        }),
                    new LegendConnectCurriculumExampleSubmission(
                        $"Historic acknowledgement {familyIndex}.",
                        new Dictionary<string, string>
                        {
                            ["discourse_role"] = "response",
                            ["function"] = "acknowledgement",
                            ["intent"] = "acknowledge_and_continue",
                            ["register"] = "neutral"
                        })
                ],
                [
                    new LegendConnectSemanticTransitionSubmission(
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["discourse_role"] = "opening",
                                ["function"] = "greeting",
                                ["intent"] = "start_conversation",
                                ["register"] = "neutral"
                            }),
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["discourse_role"] = "response",
                                ["function"] = "acknowledgement",
                                ["intent"] = "acknowledge_and_continue",
                                ["register"] = "neutral"
                            }))
                ]);

            var accepted = await fixture.Curriculum
                .SubmitFounderEnglishBatchAsync(submission);
            Assert.True(accepted.Succeeded, accepted.Message);
        }

        // The submission that completes independent production eligibility
        // can immediately converge its own source endpoint through the
        // canonical source-evidence authority. Earlier families were processed
        // before that transition became eligible, so the bounded historical
        // SourceFamilies replay remains responsible for bringing those
        // already-governed endpoints forward. This preserves incremental
        // ingestion without introducing a broad synchronous backfill.
        var beforeReplay = await db.LegendLanguageCompositionalAnchors
            .CountAsync(item =>
                item.Dimension == "function" &&
                item.Value == "greeting" &&
                item.LexemeId != null &&
                item.ComponentStartTokenIndex != null &&
                item.ComponentLength != null &&
                item.ComponentLength > 0 &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.SupersededUtc == null);
        Assert.Equal(1, beforeReplay);

        var transitionSignatures = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null &&
                item.SourceLanguageCode == "en" &&
                item.ResultLanguageCode == "en" &&
                item.ContributionState == "Supported" &&
                item.IsHumanVerifiedSupport)
            .Select(item => item.TransitionSignature)
            .Distinct()
            .ToArrayAsync();
        Assert.Single(transitionSignatures);
        Assert.Contains(
            transitionSignatures[0],
            await fixture.Curriculum
                .GetProductionEligibleSemanticTransitionSignaturesAsync(
                    "en",
                    transitionSignatures));

        // The normal historical source-family evaluator persists the same
        // bounded, production-eligible source-frame projection. It must use
        // transition gates, not a phrase list or proximity heuristic.
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var projected = await db.LegendLanguageCompositionalAnchors
            .Where(item =>
                item.Dimension == "function" &&
                item.Value == "greeting" &&
                item.LexemeId != null &&
                item.ComponentStartTokenIndex != null &&
                item.ComponentLength != null &&
                item.ComponentLength > 0 &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.SupersededUtc == null)
            .ToListAsync();
        Assert.Equal(3, projected.Count);
        Assert.All(projected, item =>
        {
            Assert.Equal(0, item.ComponentStartTokenIndex);
            Assert.NotNull(item.SemanticSignature);
        });

        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        Assert.Equal(
            3,
            await db.LegendLanguageCompositionalAnchors.CountAsync(item =>
                item.Dimension == "function" &&
                item.Value == "greeting" &&
                item.LexemeId != null &&
                item.ComponentStartTokenIndex != null &&
                item.ComponentLength != null &&
                item.ComponentLength > 0 &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.SupersededUtc == null));
    }

    [Fact]
    public async Task HistoricalSourceFrame_IsNotProjectedFromOneFamilyAlone()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var sourceFrame = new Dictionary<string, string>
        {
            ["discourse_role"] = "opening",
            ["function"] = "greeting",
            ["intent"] = "start_conversation",
            ["register"] = "neutral"
        };
        var resultFrame = new Dictionary<string, string>
        {
            ["discourse_role"] = "response",
            ["function"] = "acknowledgement",
            ["intent"] = "acknowledge_and_continue",
            ["register"] = "neutral"
        };
        var batch = new LegendConnectCurriculumBatchSubmission(
            "historical.frame-projection.insufficient",
            "One-family historical frame must remain fail-closed",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Amber quartz alpha.", sourceFrame),
                new LegendConnectCurriculumExampleSubmission(
                    "Cobalt quartz beta.", sourceFrame),
                new LegendConnectCurriculumExampleSubmission(
                    "Violet cedar gamma.", resultFrame),
                new LegendConnectCurriculumExampleSubmission(
                    "Saffron cedar delta.", resultFrame)
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(sourceFrame),
                    new LegendConnectSemanticFrameSubmission(resultFrame))
            ]);
        var accepted = await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch);
        Assert.True(accepted.Succeeded, accepted.Message);

        var transitionSignature = await db.LegendSemanticTransitionEvidence
            .Select(item => item.TransitionSignature)
            .Distinct()
            .SingleAsync();
        Assert.DoesNotContain(
            transitionSignature,
            await fixture.Curriculum
                .GetProductionEligibleSemanticTransitionSignaturesAsync(
                    "en",
                    [transitionSignature]));

        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        Assert.Equal(
            0,
            await db.LegendLanguageCompositionalAnchors.CountAsync(item =>
                item.Dimension == "function" &&
                item.Value == "greeting" &&
                item.LexemeId != null &&
                item.ComponentStartTokenIndex != null &&
                item.ComponentLength != null &&
                item.ComponentLength > 0 &&
                item.SupersededUtc == null));
    }

    private static
        LegendConnectCurriculumBatchSubmission
        GreetingBatch(
            string familyKey) =>
        new(
            familyKey,
            "Founder-controlled conversational greeting transition",
            [
                SourceGreeting(
                    "Hi there."),
                SourceGreeting(
                    "Good morning."),
                SourceGreeting(
                    "Greetings friend."),

                Response(
                    "Hello acknowledged."),
                Response(
                    "Morning acknowledged."),
                Response(
                    "Greetings acknowledged.")
            ],
            [
                GreetingTransition()
            ],
            [
                new LegendConnectSemanticSpanGroundingSubmission(
                    "conversation_function",
                    "surface_phrase")
            ]);

    private static
        LegendConnectCurriculumExampleSubmission
        SourceGreeting(
            string text) =>
        new(
            text,
            new Dictionary<string, string>
            {
                ["surface_phrase"] = text,
                ["conversation_function"] =
                    "greeting"
            });

    private static
        LegendConnectCurriculumExampleSubmission
        Response(
            string text) =>
        new(
            text,
            new Dictionary<string, string>
            {
                ["surface_phrase"] = text,
                ["conversation_function"] =
                    "acknowledgement"
            });

    private static
        LegendConnectSemanticTransitionSubmission
        GreetingTransition() =>
        new(
            new LegendConnectSemanticFrameSubmission(
                new Dictionary<string, string>
                {
                    ["conversation_function"] =
                        "greeting"
                }),
            new LegendConnectSemanticFrameSubmission(
                new Dictionary<string, string>
                {
                    ["conversation_function"] =
                        "acknowledgement"
                }));

    private static LegendConnectCurriculumBatchSubmission ResponsePlanFamily(int family) =>
        new(
            $"response.plan.greeting.{family}",
            "Founder-controlled response meaning plan evidence",
            [
                PlanSource(family, "Hi there."),
                PlanSource(family, "Good morning."),
                PlanSource(family, "Greetings friend."),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder acknowledgement evidence {family}.",
                    new Dictionary<string, string>
                    {
                        ["surface_phrase"] = $"Acknowledged {family}.",
                        ["conversation_function"] = "acknowledgement",
                        ["discourse_role"] = "response"
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "greeting"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "acknowledgement"
                }))],
            family == 1
                ? [new LegendConnectSemanticSpanGroundingSubmission(
                    "conversation_function", "greeting_surface")]
                : []);

    private static LegendConnectCurriculumBatchSubmission OriginalArticulationFamily(int family)
    {
        var (sourceText, sourceFunctionSurface, sourceSubjectSurface, resultText,
            resultSubjectSurface, resultFunctionSurface, resultRegisterSurface,
            resultIntentSurface) = family switch
        {
            1 => (
                "Please confirm receipt of the status update.", "confirm receipt", "status update",
                "The update is clearly acknowledged.", "The update", "is", "clearly", "acknowledged"),
            2 => (
                "Kindly acknowledge the project report.", "acknowledge", "project report",
                "This report is plainly received.", "This report", "is", "plainly", "received"),
            _ => (
                "Let me know you received the rollout notice.", "received", "rollout notice",
                "The notice is expressly noted.", "The notice", "is", "expressly", "noted")
        };

        return new LegendConnectCurriculumBatchSubmission(
            $"response.original-articulation.{family}",
            "Founder-controlled original compositional realization evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    sourceText,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "receipt_confirmation_request",
                        ["subject"] = "status_message"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "function", "conversation_function", "receipt_confirmation_request", sourceFunctionSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "subject", "subject", "status_message", sourceSubjectSurface)
                    ],
                    [new LegendConnectMeaningRelationSubmission("function", "applies-to", "subject")])),
                new LegendConnectCurriculumExampleSubmission(
                    resultText,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "receipt_confirmation_response",
                        ["subject"] = "status_message",
                        ["register"] = "measured",
                        ["intent"] = "acknowledge_receipt"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "subject", "subject", "status_message", resultSubjectSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "function", "conversation_function", "receipt_confirmation_response", resultFunctionSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "register", "register", "measured", resultRegisterSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "intent", "intent", "acknowledge_receipt", resultIntentSurface)
                    ],
                    [
                        new LegendConnectMeaningRelationSubmission("function", "applies-to", "subject"),
                        new LegendConnectMeaningRelationSubmission("function", "qualified-by", "register"),
                        new LegendConnectMeaningRelationSubmission("function", "governs", "intent")
                    ]))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "receipt_confirmation_request",
                    ["subject"] = "status_message"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "receipt_confirmation_response",
                    ["subject"] = "status_message",
                    ["register"] = "measured",
                    ["intent"] = "acknowledge_receipt"
                }))]);
    }

    private static LegendConnectCurriculumBatchSubmission RealizationLineageFamily(
        string group,
        int support)
    {
        var selected = string.Equals(group, "selected", StringComparison.Ordinal);
        var (resultText, firstSurface, secondSurface) = (selected, support) switch
        {
            (true, 1) => ("Verified route.", "Verified", "route"),
            (true, 2) => ("Governed path.", "Governed", "path"),
            (true, _) => ("Confirmed course.", "Confirmed", "course"),
            (false, 1) => ("Foreign Missed dispatch evidence.", "Missed", "dispatch"),
            (false, 2) => ("Foreign Absent inventory evidence.", "Absent", "inventory"),
            _ => ("Foreign Broken scheduling evidence.", "Broken", "scheduling")
        };
        var requestValue = selected
            ? "selected_realization_request"
            : "unrelated_realization_request";
        var requestText = selected
            ? "Use selected realization."
            : "Use unrelated realization.";
        var firstDimension = selected ? "selected_lead" : "a_foreign_lead";
        var secondDimension = selected ? "selected_tail" : "b_foreign_tail";
        var firstValue = selected ? "approved_lead" : "foreign_lead";
        var secondValue = selected ? "approved_tail" : "foreign_tail";

        return new LegendConnectCurriculumBatchSubmission(
            $"realization.lineage.{group}.{support}",
            "Transition-scoped semantic realization lineage",
            [
                new LegendConnectCurriculumExampleSubmission(
                    requestText,
                    new Dictionary<string, string>
                    {
                        ["realization_request"] = requestValue
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "request", "realization_request", requestValue,
                            requestText.TrimEnd('.'))
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    resultText,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "shared_lineage_response",
                        [firstDimension] = firstValue,
                        [secondDimension] = secondValue
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "first", firstDimension, firstValue, firstSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "second", secondDimension, secondValue, secondSurface)
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "first", "followed-by", "second")]))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["realization_request"] = requestValue
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "shared_lineage_response"
                }))]);
    }

    private static LegendConnectCurriculumBatchSubmission RealizationTransferSourceFamily(int support) =>
        new(
            $"realization.transfer.source.{support}",
            "Source lineage for an explicit governed realization transfer",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Authorize governed transfer.",
                    new Dictionary<string, string>
                    {
                        ["transfer_request"] = "handoff"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "request", "transfer_request", "handoff",
                            "Authorize governed transfer")
                    ],
                    []),
                    $"realization-transfer-source-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Realization transfer source control {support}.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"realization-transfer-source-{support}"
                    })
            ]);

    private static LegendConnectCurriculumBatchSubmission RealizationTransferResultFamily(int support)
    {
        var (text, decisionSurface, registerSurface) = support switch
        {
            1 => ("Approved clearly.", "Approved", "clearly"),
            2 => ("Authorized plainly.", "Authorized", "plainly"),
            _ => ("Validated expressly.", "Validated", "expressly")
        };
        return new(
            $"realization.transfer.result.{support}",
            "Result lineage for an explicit governed realization transfer",
            [
                new LegendConnectCurriculumExampleSubmission(
                    text,
                    new Dictionary<string, string>
                    {
                        ["decision_posture"] = "governed_transfer_response",
                        ["register"] = "measured"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "decision", "decision_posture", "governed_transfer_response",
                            decisionSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "register", "register", "measured", registerSurface)
                    ],
                    []),
                    $"realization-transfer-result-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Realization transfer result control {support}.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"realization-transfer-result-{support}"
                    })
            ]);
    }

    private static LegendConnectCurriculumBatchSubmission RealizationTransferUnrelatedFamily(int support)
    {
        var (text, firstSurface, secondSurface) = support switch
        {
            1 => ("Foreign Missed dispatch evidence.", "Missed", "dispatch"),
            2 => ("Foreign Absent inventory evidence.", "Absent", "inventory"),
            _ => ("Foreign Broken scheduling evidence.", "Broken", "scheduling")
        };
        return new(
            $"realization.transfer.unrelated.{support}",
            "Unrelated same-frame transfer realization language",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Use unrelated transfer realization.",
                    new Dictionary<string, string>
                    {
                        ["transfer_request"] = "unrelated"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "request", "transfer_request", "unrelated",
                            "Use unrelated transfer realization")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    text,
                    new Dictionary<string, string>
                    {
                        ["decision_posture"] = "governed_transfer_response",
                        ["register"] = "measured",
                        ["a_foreign_lead"] = "foreign_lead",
                        ["b_foreign_tail"] = "foreign_tail"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "decision", "decision_posture", "governed_transfer_response",
                            firstSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "register", "register", "measured", secondSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "foreign-first", "a_foreign_lead", "foreign_lead", firstSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "foreign-second", "b_foreign_tail", "foreign_tail", secondSurface)
                    ],
                    []))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["transfer_request"] = "unrelated"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["decision_posture"] = "governed_transfer_response",
                    ["register"] = "measured"
                }))]);
    }

    private static LegendConnectCurriculumBatchSubmission
        WholeSpanArticulationFamily(int family)
    {
        var resultText = family switch
        {
            1 => "Hello! What can I help you with?",
            2 => "Welcome! How may I support you?",
            _ => "Greetings! What should we work on?"
        };

        return new LegendConnectCurriculumBatchSubmission(
            $"response.whole-span-articulation.{family}",
            "Founder-controlled whole-span response articulation evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder opening evidence {family}: Good morning.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "conversation_opening"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "opening", "conversation_function", "conversation_opening", "Good morning")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    resultText,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "conversation_acknowledgement"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "response", "conversation_function", "conversation_acknowledgement", resultText)
                    ],
                    []))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "conversation_opening"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "conversation_acknowledgement"
                }))]);
    }

    private static LegendConnectCurriculumBatchSubmission ContainedSubspanFamily(int family) =>
        new(
            $"grounding.contained-subspan.{family}",
            "Independent mature subspan evidence inside a governed whole-span primitive",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder time evidence {family}: morning.",
                    new Dictionary<string, string> { ["time_reference"] = "morning" },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "time", "time_reference", "morning", "morning")],
                        [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder time control {family}.",
                    new Dictionary<string, string> { ["control"] = $"time-{family}" })
            ]);

    private static LegendConnectCurriculumBatchSubmission CompetingWholeSpanFamily(int family) =>
        new(
            $"grounding.competing-whole-span.{family}",
            "Independent coextensive meaning that has no governed relation to the opening",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder social evidence {family}: Good morning.",
                    new Dictionary<string, string> { ["social_act"] = "wellbeing_inquiry" },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "social", "social_act", "wellbeing_inquiry", "Good morning")],
                        [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder social control {family}.",
                    new Dictionary<string, string> { ["control"] = $"social-{family}" })
            ]);

    private static LegendConnectCurriculumBatchSubmission ExactCoherentGraphFamily(int family) =>
        new(
            $"grounding.exact-coherent.{family}",
            "Exact Founder graph remains the source-grounding authority",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Compare the evidence.",
                    new Dictionary<string, string>
                    {
                        ["function"] = "compare",
                        ["subject"] = "evidence"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "function", "function", "compare", "Compare"),
                            new LegendConnectMeaningNodeSubmission(
                                "subject", "subject", "evidence", "evidence")
                        ],
                        [new LegendConnectMeaningRelationSubmission(
                            "function", "applies-to", "subject")])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Comparison response evidence {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "comparison_response"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "response", "conversation_function", "comparison_response",
                            $"Comparison response evidence {family}")],
                        []))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["function"] = "compare",
                    ["subject"] = "evidence"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "comparison_response"
                }))]);

    private static LegendConnectCurriculumBatchSubmission ConflictingReusableGraphFamily(int family) =>
        new(
            $"grounding.conflicting-reusable.{family}",
            "Reusable spans with different meaning remain scoped to their source graph",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder conflicting evidence {family}: Compare the evidence.",
                    new Dictionary<string, string>
                    {
                        ["function"] = "reject",
                        ["subject"] = "assumption"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "function", "function", "reject", "Compare"),
                            new LegendConnectMeaningNodeSubmission(
                                "subject", "subject", "assumption", "evidence")
                        ],
                        [new LegendConnectMeaningRelationSubmission(
                            "function", "applies-to", "subject")])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder conflicting control {family}.",
                    new Dictionary<string, string> { ["control"] = $"conflict-{family}" })
            ]);

    private static LegendConnectCurriculumBatchSubmission ExactAtomicEndpointFamily(int family) =>
        new(
            $"grounding.exact-atomic-endpoint.{family}",
            "Exact canonical source endpoint remains independently executable",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Review the cost.",
                    new Dictionary<string, string> { ["task"] = "cost_review" },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "task", "task", "cost_review", "Review the cost")],
                        [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder cost review response {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "cost_review_response"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "response", "conversation_function", "cost_review_response",
                            $"Founder cost review response {family}")],
                        []))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["task"] = "cost_review"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "cost_review_response"
                }))]);

    private static LegendConnectCurriculumBatchSubmission ExactDenseConnectedGraphFamily(int family) =>
        new(
            $"grounding.exact-dense-connected.{family}",
            "Independent connected observations coexist on the exact canonical surface",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Review the cost.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "evidence_question",
                        ["test"] = "probe"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "question", "conversation_function", "evidence_question", "Review"),
                            new LegendConnectMeaningNodeSubmission(
                                "probe", "test", "probe", "cost")
                        ],
                        [new LegendConnectMeaningRelationSubmission(
                            "question", "uses", "probe")])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder dense graph control {family}.",
                    new Dictionary<string, string> { ["control"] = $"dense-{family}" })
            ]);

    private static LegendConnectCurriculumBatchSubmission
        CompetingHypothesesTestDesignFamily(int family)
    {
        var controlledSurfaces = new[]
        {
            (Text: "Keep both hypotheses; design a test.",
                Posture: "Keep both hypotheses", Action: "design a test"),
            (Text: "Retain the competing explanations; plan an experiment.",
                Posture: "Retain the competing explanations", Action: "plan an experiment"),
            (Text: "Preserve both theories; devise a discriminating check.",
                Posture: "Preserve both theories", Action: "devise a discriminating check"),
            (Text: "Maintain both candidate causes; construct a separating trial.",
                Posture: "Maintain both candidate causes", Action: "construct a separating trial"),
            (Text: "Hold both possibilities; create an evidence-producing test.",
                Posture: "Hold both possibilities", Action: "create an evidence-producing test")
        };
        var examples = controlledSurfaces
            .Select(item => new LegendConnectCurriculumExampleSubmission(
                item.Text,
                new Dictionary<string, string>
                {
                    ["decision_posture"] = "retain_competing_hypotheses",
                    ["reasoning_action"] = "design_discriminating_test"
                },
                new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission(
                        "posture",
                        "decision_posture",
                        "retain_competing_hypotheses",
                        item.Posture),
                    new LegendConnectMeaningNodeSubmission(
                        "action",
                        "reasoning_action",
                        "design_discriminating_test",
                        item.Action)
                ],
                [new LegendConnectMeaningRelationSubmission(
                    "posture", "resolved-by", "action")])))
            .Append(new LegendConnectCurriculumExampleSubmission(
                $"Founder test-design guidance {family}.",
                new Dictionary<string, string>
                {
                    ["conversation_function"] = "test_design_guidance"
                }))
            .ToArray();

        return new LegendConnectCurriculumBatchSubmission(
            $"reasoning.competing-hypotheses.test-design.{family}",
            "Controlled surfaces for retaining competing causes and designing a discriminating test",
            examples,
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["decision_posture"] = "retain_competing_hypotheses",
                    ["reasoning_action"] = "design_discriminating_test"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "test_design_guidance"
                }))]);
    }

    private static LegendConnectCurriculumBatchSubmission
        PrematureHypothesisSelectionFamily(int family) =>
        new(
            $"reasoning.competing-hypotheses.contrast.{family}",
            "Contrastive evidence for selecting one cause before gathering discriminating evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Choose one hypothesis before you design a test.",
                    new Dictionary<string, string>
                    {
                        ["decision_posture"] = "select_single_hypothesis",
                        ["reasoning_action"] = "design_discriminating_test"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "posture",
                            "decision_posture",
                            "select_single_hypothesis",
                            "Choose one hypothesis"),
                        new LegendConnectMeaningNodeSubmission(
                            "action",
                            "reasoning_action",
                            "design_discriminating_test",
                            "design a test")
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "posture", "precedes", "action")])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder premature-selection warning {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "premature_selection_warning"
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["decision_posture"] = "select_single_hypothesis",
                    ["reasoning_action"] = "design_discriminating_test"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "premature_selection_warning"
                }))]);

    private static LegendConnectCurriculumBatchSubmission UnrelatedDispatchSurfaceFamily() =>
        new(
            "operations.dispatch.unrelated-controlled-surface",
            "Unrelated operational surface without a governed hypothesis relation",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Route a dispatch.",
                    new Dictionary<string, string>
                    {
                        ["reasoning_action"] = "design_discriminating_test"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "action",
                            "reasoning_action",
                            "design_discriminating_test",
                            "Route a dispatch")],
                        [])),
                new LegendConnectCurriculumExampleSubmission(
                    "Dispatch routing control evidence.",
                    new Dictionary<string, string>
                    {
                        ["control"] = "dispatch_routing"
                    })
            ]);

    private static LegendConnectCurriculumBatchSubmission ProjectionIdentitySourceFamily(
        string subject,
        string descriptor) =>
        new(
            $"projection.identity.{subject}.{descriptor}",
            "Canonical diagnostic family identity for projected selection",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Canonical {subject} {descriptor} source.",
                    new Dictionary<string, string>
                    {
                        ["diagnostic_subject"] = subject,
                        ["diagnostic_family"] = $"{subject}_{descriptor}"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "subject", "diagnostic_subject", subject, subject),
                        new LegendConnectMeaningNodeSubmission(
                            "family", "diagnostic_family", $"{subject}_{descriptor}", descriptor)
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "subject", "qualified-by", "family")])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Canonical {subject} {descriptor} control.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"{subject}-{descriptor}"
                    })
            ]);

    private static LegendConnectCurriculumBatchSubmission ProjectionCollisionFamily(
        string subject,
        string unrelatedFamily,
        int support) =>
        new(
            $"projection.collision.{subject}.{unrelatedFamily}.{support}",
            "Unrelated diagnostic family with a colliding controlled dimension",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed {unrelatedFamily} evidence {support}: relay.",
                    new Dictionary<string, string>
                    {
                        ["diagnostic_subject"] = subject,
                        ["diagnostic_route"] = unrelatedFamily
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "subject", "diagnostic_subject", subject, "relay")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed {unrelatedFamily} response evidence {support}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = $"{unrelatedFamily}_response"
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["diagnostic_subject"] = subject,
                    ["diagnostic_route"] = unrelatedFamily
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = $"{unrelatedFamily}_response"
                }))]);

    private static LegendConnectCurriculumBatchSubmission SameFamilyProjectionFamily() =>
        new(
            "projection.same-family.handoff",
            "Same-family projected diagnostic transition",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Canonical handoff failure source.",
                    new Dictionary<string, string>
                    {
                        ["diagnostic_subject"] = "handoff",
                        ["diagnostic_family"] = "handoff_failure",
                        ["diagnostic_route"] = "same_family"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "subject", "diagnostic_subject", "handoff", "handoff"),
                        new LegendConnectMeaningNodeSubmission(
                            "family", "diagnostic_family", "handoff_failure", "failure")
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "subject", "qualified-by", "family")])),
                new LegendConnectCurriculumExampleSubmission(
                    "Use the governed handoff response.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "handoff_same_family_response"
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["diagnostic_subject"] = "handoff",
                    ["diagnostic_family"] = "handoff_failure",
                    ["diagnostic_route"] = "same_family"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "handoff_same_family_response"
                }))]);

    private static LegendConnectCurriculumBatchSubmission ExplicitTransferSourceFamily(int support) =>
        new(
            $"projection.explicit-transfer.source.{support}",
            "Canonical source family for an explicit governed transfer",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed transfer evidence {support}: handoff scope.",
                    new Dictionary<string, string>
                    {
                        ["diagnostic_subject"] = "handoff",
                        ["transfer_scope"] = "bounded"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "subject", "diagnostic_subject", "handoff", "handoff"),
                        new LegendConnectMeaningNodeSubmission(
                            "scope", "transfer_scope", "bounded", "scope")
                    ],
                    []),
                    $"governed-transfer-source-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed transfer source control {support}.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"transfer-source-{support}"
                    })
            ]);

    private static LegendConnectCurriculumBatchSubmission ExplicitTransferResultFamily(int support) =>
        new(
            $"projection.explicit-transfer.result.{support}",
            "Canonical result family for an explicit governed transfer",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed transfer response {support}.",
                    new Dictionary<string, string>
                    {
                        ["decision_posture"] = "governed_transfer_response"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "response",
                            "decision_posture",
                            "governed_transfer_response",
                            $"Governed transfer response {support}")
                    ],
                    []),
                    $"governed-transfer-result-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed transfer result control {support}.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"transfer-result-{support}"
                    })
            ]);

    private static LegendConnectCurriculumBatchSubmission
        RepeatedSemanticSlotArticulationFamily(int family)
    {
        var (resultText, openingSurface, assistanceSurface) = family switch
        {
            1 => ("Hello! What can I help you with?", "Hello", "What can I help you with"),
            2 => ("Welcome! How may I support you?", "Welcome", "How may I support you"),
            _ => ("Greetings! What should we work on?", "Greetings", "What should we work on")
        };

        return new LegendConnectCurriculumBatchSubmission(
            $"response.repeated-semantic-slots.{family}",
            "Founder-controlled repeated semantic-slot realization evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder opening evidence {family}: Good morning.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "conversation_opening"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "opening", "conversation_function", "conversation_opening", "Good morning")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    resultText,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "conversation_acknowledgement"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "opening", "conversation_function", "conversation_acknowledgement", openingSurface),
                        new LegendConnectMeaningNodeSubmission(
                            "assistance", "conversation_function", "conversation_acknowledgement", assistanceSurface)
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "opening", "followed-by", "assistance")]))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "conversation_opening"
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "conversation_acknowledgement"
                }))]);
    }

    private static LegendConnectCurriculumBatchSubmission SelectorOrderingFamily(
        string group,
        int family,
        string sourceText,
        string route,
        string resultFunction) =>
        new(
            $"response.plan.selector-ordering.{group}.{family}",
            "Exact endpoint ordering over governed projection evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    sourceText,
                    new Dictionary<string, string>
                    {
                        ["task"] = "handoff_diagnosis",
                        ["route"] = route
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "task",
                            "task",
                            "handoff_diagnosis",
                            sourceText.TrimEnd('.'))
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder selector result {group} {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = resultFunction
                    })
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["task"] = "handoff_diagnosis",
                            ["route"] = route
                        }),
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = resultFunction
                        }))
            ]);

    private static LegendConnectCurriculumBatchSubmission ReadOnlyContentBindingFamily(
        int support)
    {
        var countSurface = support switch
        {
            1 => "two",
            2 => "four",
            _ => "six"
        };
        var countValue = (support * 2).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var resultVariations = new Dictionary<string, string>
        {
            ["response_kind"] = "current_issue_count",
            ["current_issue_count"] = countValue,
            ["content_binding_authority"] = "legend_founder_tool_authority",
            ["content_binding_access"] = "read_only",
            ["content_binding_tool"] = "legend_translation_quality",
            ["content_binding_arguments"] = "{}",
            ["content_binding_value_path"] = "needsReviewCount",
            ["content_binding_max_age_seconds"] = "60"
        };
        var resultFrame = new Dictionary<string, string>(resultVariations)
        {
            ["current_issue_count"] = "$IssueCount"
        };

        return new LegendConnectCurriculumBatchSubmission(
            $"response.read-only-content.{support}",
            "Founder-governed read-only operational content binding",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder current issue request {support}: What is the current open issue count?",
                    new Dictionary<string, string>
                    {
                        ["request_surface"] = "What is the current open issue count?",
                        ["conversation_function"] = "current_issue_count_request"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "function",
                            "conversation_function",
                            "current_issue_count_request",
                            "What is the current open issue count")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Open issues {countSurface}.",
                    resultVariations,
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "label",
                            "response_kind",
                            "current_issue_count",
                            "Open issues"),
                        new LegendConnectMeaningNodeSubmission(
                            "count",
                            "current_issue_count",
                            countValue,
                            countSurface)
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "label", "reports", "count")]))
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = "current_issue_count_request"
                        }),
                    new LegendConnectSemanticFrameSubmission(resultFrame))
            ],
            support == 1
                ? [new LegendConnectSemanticSpanGroundingSubmission(
                    "conversation_function", "request_surface")]
                : []);
    }

    private static LegendConnectCurriculumBatchSubmission PresentationConstraintFamily(
        string profile,
        int support,
        string requestText,
        string responseText,
        string? audience,
        string? expertise,
        string? tone,
        string? length,
        int? sentenceCount,
        string? structure)
    {
        var requestFunction = "presentation_request_" + profile.Replace('-', '_');
        var resultDimensions = new Dictionary<string, string>
        {
            ["conversation_function"] = "presentation_response",
            [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
            [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
        };
        AddOptionalResultDimension(resultDimensions, "response_audience", audience);
        AddOptionalResultDimension(resultDimensions, "response_expertise", expertise);
        AddOptionalResultDimension(resultDimensions, "response_tone", tone);
        AddOptionalResultDimension(resultDimensions, "response_length", length);
        AddOptionalResultDimension(
            resultDimensions,
            "response_sentence_count",
            sentenceCount?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddOptionalResultDimension(resultDimensions, "response_structure", structure);

        return new LegendConnectCurriculumBatchSubmission(
            $"response.presentation.{profile}.{support}",
            "Founder-governed response presentation constraints",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder presentation evidence {support}: {requestText}",
                    new Dictionary<string, string>
                    {
                        ["request_surface"] = requestText,
                        ["conversation_function"] = requestFunction
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "function",
                            "conversation_function",
                            requestFunction,
                            requestText.TrimEnd('.'))
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    responseText,
                    new Dictionary<string, string>(resultDimensions))
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = requestFunction
                        }),
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>(resultDimensions)))
            ],
            support == 1
                ? [new LegendConnectSemanticSpanGroundingSubmission(
                    "conversation_function", "request_surface")]
                : []);
    }

    private static void AddOptionalResultDimension(
        IDictionary<string, string> dimensions,
        string dimension,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            dimensions[dimension] = value;
    }

    private static LegendConnectCurriculumBatchSubmission RichResponsePlanFamily(
        int family,
        string register,
        string resultFunction) =>
        new(
            $"response.plan.rich.{register}.{family}",
            "Founder-controlled rich response frame evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder rich greeting evidence {family}: Hi there.",
                    new Dictionary<string, string>
                    {
                        ["greeting_surface"] = "Hi there.",
                        ["conversation_function"] = "greeting",
                        ["discourse_role"] = "opening",
                        ["register"] = register
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "function", "conversation_function", "greeting", "Hi there"),
                        new LegendConnectMeaningNodeSubmission(
                            "role", "discourse_role", "opening", "Hi there")
                    ],
                    [new LegendConnectMeaningRelationSubmission("function", "realized-as", "role")])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder rich response evidence {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = resultFunction
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = "greeting",
                    ["discourse_role"] = "opening",
                    ["register"] = register
                }),
                new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                {
                    ["conversation_function"] = resultFunction
                }))],
            family == 1 || family == 4
                ? [new LegendConnectSemanticSpanGroundingSubmission(
                    "conversation_function", "greeting_surface")]
                : []);

    private static LegendConnectCurriculumExampleSubmission PlanSource(int family, string surface) =>
        new(
            $"Founder greeting evidence {family}: {surface}",
            new Dictionary<string, string>
            {
                ["greeting_surface"] = surface,
                ["conversation_function"] = "greeting",
                ["discourse_role"] = "opening"
            },
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("function", "conversation_function", "greeting", surface.TrimEnd('.')),
                    new LegendConnectMeaningNodeSubmission("role", "discourse_role", "opening", surface.TrimEnd('.'))
                ],
                [new LegendConnectMeaningRelationSubmission("function", "realized-as", "role")]));

    private static LegendConnectCurriculumBatchSubmission ResponsePlanPolarityFamily(int family) =>
        new(
            $"response.plan.polarity.{family}",
            "Founder-controlled polarity response-plan evidence",
            [
                PlanPolaritySource(family, "I agree.", "positive"),
                PlanPolaritySource(family, "I disagree.", "negative"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Positive acknowledgement {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "acknowledgement",
                        ["polarity"] = "positive"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    $"Negative acknowledgement {family}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "clarification",
                        ["polarity"] = "negative"
                    })
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                    {
                        ["conversation_function"] = "statement",
                        ["polarity"] = "positive"
                    }),
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                    {
                        ["conversation_function"] = "acknowledgement",
                        ["polarity"] = "positive"
                    })),
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                    {
                        ["conversation_function"] = "statement",
                        ["polarity"] = "negative"
                    }),
                    new LegendConnectSemanticFrameSubmission(new Dictionary<string, string>
                    {
                        ["conversation_function"] = "clarification",
                        ["polarity"] = "negative"
                    }))
            ]);

    private static LegendConnectCurriculumExampleSubmission PlanPolaritySource(
        int family,
        string surface,
        string polarity) =>
        new(
            $"Founder polarity evidence {family}: {surface}",
            new Dictionary<string, string>
            {
                ["conversation_function"] = "statement",
                ["polarity"] = polarity
            },
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission("function", "conversation_function", "statement", surface.TrimEnd('.')),
                    new LegendConnectMeaningNodeSubmission("polarity", "polarity", polarity, surface.TrimEnd('.'))
                ],
                [new LegendConnectMeaningRelationSubmission("function", "qualified-by", "polarity")]));

    private static LegendConnectCurriculumBatchSubmission ResponsePlanNoTransitionFamily(int family) =>
        new(
            $"response.plan.unsupported.{family}",
            "Founder-controlled composed meaning without transition evidence",
            [NoTransitionSource(family, "primary"), NoTransitionSource(family, "secondary")]);

    private static LegendConnectCurriculumExampleSubmission NoTransitionSource(int family, string variant) =>
        new(
                $"Founder unsupported-plan evidence {family} {variant}: Observe carefully.",
                new Dictionary<string, string> { ["conversation_function"] = "observation" },
                new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission("function", "conversation_function", "observation", "Observe"),
                        new LegendConnectMeaningNodeSubmission("register", "register", "careful", "carefully")
                    ],
                    [new LegendConnectMeaningRelationSubmission("function", "qualified-by", "register")]));

    private static GroundingFixture
        CreateFixture(
            MasterAppDbContext db)
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["LegendConnect:CorpusAcquisition:Enabled"] =
                            "false",
                        ["LegendConnect:ContextualComposition:Mode"] =
                            "Shadow",
                        ["LegendConnect:LanguageRegistry:Baseline:0:Code"] =
                            "en",
                        ["LegendConnect:LanguageRegistry:Baseline:0:Name"] =
                            "English",
                        ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] =
                            "English",
                        ["LegendConnect:LanguageRegistry:Baseline:1:Code"] =
                            "x-test",
                        ["LegendConnect:LanguageRegistry:Baseline:1:Name"] =
                            "Synthetic registry language",
                        ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] =
                            "Synthetic registry language"
                    })
                .Build();

        var registry =
            new LegendLanguageRegistry(
                db,
                configuration);

        var intelligence =
            new LegendConnectTranslationIntelligence(
                db,
                configuration);

        var corpus =
            new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<
                    LegendConnectCorpusService>.Instance,
                intelligence: intelligence);

        var curriculum =
            new LegendConnectCurriculumService(
                db,
                registry,
                corpus);

        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum);

        return new GroundingFixture(
            curriculum,
            operations);
    }

    private sealed record GroundingFixture(
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);
}

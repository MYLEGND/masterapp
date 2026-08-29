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
    public async Task GovernedExecutableProjection_FailsClosedWhenOmittedMetadataCouldChangeTheResult()
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

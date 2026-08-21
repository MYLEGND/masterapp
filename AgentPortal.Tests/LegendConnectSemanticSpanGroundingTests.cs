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
    public async Task HistoricalControlledSourceFrame_IsAvailableAfterIndependentEligibilityAndThenPersistedByReplay()
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

        var beforeReplay = await db.LegendLanguageCompositionalAnchors
            .CountAsync(item =>
                item.Dimension == "function" &&
                item.Value == "greeting" &&
                item.LexemeId != null &&
                item.ComponentStartTokenIndex != null &&
                item.ComponentLength != null &&
                item.ComponentLength > 0 &&
                item.SupersededUtc == null);
        Assert.Equal(0, beforeReplay);

        var beforeInference = await fixture.Curriculum
            .TryInferSemanticTransitionAsync(
                "en",
                "Historic opening 1.",
                Array.Empty<LegendConnectConversationContextItem>());
        Assert.Equal(
            LegendSemanticTransitionInference.Supported,
            beforeInference.State);
        Assert.False(string.IsNullOrWhiteSpace(beforeInference.RealizedText));
        Assert.True(beforeInference.EvidenceCount > 0);

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
        // bounded, production-eligible source-frame projection used for the
        // read-only compatibility proof above. It must use transition gates,
        // not a phrase list or proximity heuristic.
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

        var afterInference = await fixture.Curriculum
            .TryInferSemanticTransitionAsync(
                "en",
                "Historic opening 1.",
                Array.Empty<LegendConnectConversationContextItem>());
        Assert.Equal(
            LegendSemanticTransitionInference.Supported,
            afterInference.State);
        Assert.False(string.IsNullOrWhiteSpace(afterInference.RealizedText));
        Assert.True(afterInference.EvidenceCount > 0);

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

        return new GroundingFixture(
            curriculum);
    }

    private sealed record GroundingFixture(
        LegendConnectCurriculumService Curriculum);
}

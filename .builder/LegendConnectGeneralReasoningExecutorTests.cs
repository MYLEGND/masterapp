using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectGeneralReasoningExecutorTests
{
    [Fact]
    public async Task MultiStepFounderReasoning_ReachesTerminalConclusion_AndArticulatesOriginalSurface()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SubmitStaticReasoningChainAsync(fixture.Curriculum, includeBranch: false, cycleOnly: false);

        var stored = await db.LegendCurriculumExamples
            .Where(example => example.SupersededUtc == null &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == example.Id &&
                    variation.Dimension == "reasoning_state" &&
                    variation.Value == "conclusion"))
            .Join(db.LegendLanguageTextUnits, example => example.TextUnitId, unit => unit.Id, (_, unit) => unit.Text)
            .ToArrayAsync();

        var native = await fixture.Operations.TryInferConversationWithDiscourseAsync(
            "Assess.", [], new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(native.Supported, native.ReasonCode);
        Assert.True(native.EvidenceCount >= 6);
        Assert.False(native.RequiresEscalation);
        Assert.False(string.IsNullOrWhiteSpace(native.Answer));
        Assert.DoesNotContain(stored, item => string.Equals(
            LegendLanguageIdentity.NormalizeText(item),
            LegendLanguageIdentity.NormalizeText(native.Answer!),
            StringComparison.Ordinal));
        Assert.Contains("!", native.Answer!, StringComparison.Ordinal);
        Assert.EndsWith("?", native.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiStepFounderReasoning_PropagatesVariablesAcrossGovernedFrames()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SubmitVariableReasoningChainAsync(fixture.Curriculum);

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Assess alpha.", new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal("conclusion", plan.ResultDimensions["reasoning_state"]);
        Assert.Equal("alpha", plan.ResultDimensions["subject"]);
        Assert.True(plan.IndependentEvidenceCount >= 6);
    }

    [Fact]
    public async Task MultiStepFounderReasoning_FailsClosedOnCompetingTerminalBranches()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SubmitStaticReasoningChainAsync(fixture.Curriculum, includeBranch: true, cycleOnly: false);

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Assess.", new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("ambiguous_semantic_reasoning_branch", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    [Fact]
    public async Task MultiStepFounderReasoning_FailsClosedOnCycle()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        await SubmitStaticReasoningChainAsync(fixture.Curriculum, includeBranch: false, cycleOnly: true);

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Assess.", new LegendConnectDiscourseStateSnapshot([]));

        Assert.False(planned.Supported);
        Assert.Equal("semantic_reasoning_cycle_detected", planned.ReasonCode);
        Assert.Null(planned.Plan);
    }

    private static async Task SubmitStaticReasoningChainAsync(
        LegendConnectCurriculumService curriculum,
        bool includeBranch,
        bool cycleOnly)
    {
        for (var family = 1; family <= 3; family++)
        {
            var sourceKey = $"reasoning-source-{family}";
            var middleKey = $"reasoning-middle-{family}";
            var conclusionKey = $"reasoning-conclusion-{family}";
            var alternateKey = $"reasoning-alternate-{family}";
            var examples = new List<LegendConnectCurriculumExampleSubmission>
            {
                AtomicExample($"Source evidence {family}: Assess.", "premise", "Assess", sourceKey),
                AtomicExample($"Intermediate evidence {family}: Continue.", "intermediate", "Continue", middleKey)
            };
            if (!cycleOnly)
            {
                var conclusion = family switch
                {
                    1 => "Therefore! The conclusion follows?",
                    2 => "Accordingly! The result follows?",
                    _ => "Consequently! The outcome follows?"
                };
                examples.Add(AtomicExample(conclusion, "conclusion", conclusion, conclusionKey));
                if (includeBranch)
                {
                    var alternate = family switch
                    {
                        1 => "Alternatively! Another conclusion follows?",
                        2 => "Otherwise! A different result follows?",
                        _ => "Instead! Another outcome follows?"
                    };
                    examples.Add(AtomicExample(alternate, "alternate_conclusion", alternate, alternateKey));
                }
            }

            var mode = cycleOnly ? "cycle" : includeBranch ? "branch" : "chain";
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(new(
                $"reasoning.general.static.{mode}.{family}",
                "Content-agnostic Founder reasoning graph evidence",
                examples));
            Assert.True(submitted.Succeeded, submitted.Message);

            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    sourceKey, middleKey, "reasoning.implication"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            if (cycleOnly)
            {
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                    new LegendConnectCrossExampleSemanticRelationshipSubmission(
                        middleKey, sourceKey, "reasoning.implication"),
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                continue;
            }

            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    middleKey, conclusionKey, "reasoning.implication"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            if (includeBranch)
            {
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                    new LegendConnectCrossExampleSemanticRelationshipSubmission(
                        middleKey, alternateKey, "reasoning.implication"),
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            }
        }
    }

    private static async Task SubmitVariableReasoningChainAsync(
        LegendConnectCurriculumService curriculum)
    {
        for (var family = 1; family <= 3; family++)
        {
            var sourceKey = $"reasoning-variable-source-{family}";
            var middleKey = $"reasoning-variable-middle-{family}";
            var conclusionKey = $"reasoning-variable-conclusion-{family}";
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(new(
                $"reasoning.general.variables.{family}",
                "Founder variable-propagation reasoning evidence",
                [
                    RelationalExample($"Variable source {family}: Assess alpha.", "premise", "Assess", sourceKey),
                    RelationalExample($"Variable middle {family}: Continue alpha.", "intermediate", "Continue", middleKey),
                    RelationalExample($"Variable result {family}: Conclude alpha.", "conclusion", "Conclude", conclusionKey)
                ]));
            Assert.True(submitted.Succeeded, submitted.Message);

            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    sourceKey, middleKey, "reasoning.variable.implication"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    middleKey, conclusionKey, "reasoning.variable.implication"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }
    }

    private static LegendConnectCurriculumExampleSubmission AtomicExample(
        string text,
        string state,
        string surface,
        string semanticKey) => new(
            text,
            new Dictionary<string, string> { ["reasoning_state"] = state },
            new LegendConnectMeaningGraphSubmission(
                [new LegendConnectMeaningNodeSubmission(
                    "state", "reasoning_state", state, surface)],
                []),
            semanticKey);

    private static LegendConnectCurriculumExampleSubmission RelationalExample(
        string text,
        string state,
        string stateSurface,
        string semanticKey) => new(
            text,
            new Dictionary<string, string>
            {
                ["reasoning_state"] = state,
                ["subject"] = "alpha"
            },
            new LegendConnectMeaningGraphSubmission(
                [
                    new LegendConnectMeaningNodeSubmission(
                        "state", "reasoning_state", state, stateSurface),
                    new LegendConnectMeaningNodeSubmission(
                        "subject", "subject", "alpha", "alpha")
                ],
                [new LegendConnectMeaningRelationSubmission(
                    "state", "applies-to", "subject")]),
            semanticKey);

    private static ReasoningFixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
            }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db, registry, corpus, configuration, curriculum: curriculum);
        return new ReasoningFixture(curriculum, operations);
    }

    private sealed record ReasoningFixture(
        LegendConnectCurriculumService Curriculum,
        LegendConnectOperations Operations);
}

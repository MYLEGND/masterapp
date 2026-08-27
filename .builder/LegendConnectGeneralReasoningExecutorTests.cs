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

        var conclusionExamples = await (
            from example in db.LegendCurriculumExamples
            join family in db.LegendCurriculumFamilies on example.CurriculumFamilyId equals family.Id
            join unit in db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
            where example.SupersededUtc == null &&
                db.LegendCurriculumExampleVariations.Any(variation =>
                    variation.CurriculumExampleId == example.Id &&
                    variation.Dimension == "reasoning_state" &&
                    variation.Value == "conclusion")
            select new { example.Id, example.CurriculumFamilyId, family.FamilyKey, unit.Text }
        ).ToArrayAsync();
        Assert.Equal(3, conclusionExamples.Length);
        Assert.Equal(3, conclusionExamples.Select(item => item.CurriculumFamilyId).Distinct().Count());

        var conclusionIds = conclusionExamples.Select(item => item.Id).ToArray();
        var anchors = await db.LegendLanguageCompositionalAnchors
            .Where(item => conclusionIds.Contains(item.CurriculumExampleId) &&
                item.SupersededUtc == null && item.LexemeId != null &&
                item.ComponentStartTokenIndex != null && item.ComponentLength != null &&
                item.ComponentLength > 0)
            .OrderBy(item => item.CurriculumExampleId)
            .ThenBy(item => item.ComponentStartTokenIndex)
            .Select(item => new
            {
                item.CurriculumExampleId,
                item.Dimension,
                item.Value,
                Start = item.ComponentStartTokenIndex!.Value,
                Length = item.ComponentLength!.Value
            })
            .ToArrayAsync();
        foreach (var example in conclusionExamples)
        {
            var semantic = anchors.Where(item => item.CurriculumExampleId == example.Id &&
                    item.Dimension is "subject" or "register" or "reasoning_state")
                .GroupBy(item => new { item.Dimension, item.Start, item.Length })
                .Select(group => group.First())
                .OrderBy(item => item.Start)
                .ToArray();
            Assert.Equal(3, semantic.Length);
            Assert.Equal(new[] { "subject", "register", "reasoning_state" },
                semantic.Select(item => item.Dimension).ToArray());
            Assert.False(semantic.Zip(semantic.Skip(1), (left, right) =>
                left.Start + left.Length > right.Start).Any(overlap => overlap));
        }

        var terminalTransitions = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null &&
                item.FounderSemanticExampleRelationEvidenceId != null &&
                conclusionIds.Contains(item.ResultCurriculumExampleId) &&
                item.ContributionState == "Supported" &&
                item.IsHumanVerifiedSupport)
            .ToArrayAsync();
        Assert.Equal(3, terminalTransitions
            .Select(item => item.IndependentSourceIdentity)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Single(terminalTransitions
            .Select(item => item.TransitionSignature)
            .Distinct(StringComparer.Ordinal));

        var planned = await fixture.Operations.TryPlanConversationAsync(
            "Assess.", new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(planned.Supported, planned.ReasonCode);
        var terminalPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal("conclusion", terminalPlan.ResultDimensions["reasoning_state"]);
        Assert.Equal("governed_case", terminalPlan.ResultDimensions["subject"]);
        Assert.Equal("measured", terminalPlan.ResultDimensions["register"]);

        var stored = conclusionExamples.Select(item => item.Text).ToArray();
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
        Assert.EndsWith(".", native.Answer, StringComparison.Ordinal);
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

    private static async Task SubmitStaticReasoningChainAsync(LegendConnectCurriculumService curriculum, bool includeBranch, bool cycleOnly)
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
                var (conclusion, subjectSurface, registerSurface) = family switch
                {
                    1 => ("The governed outcome clearly follows.", "The governed outcome", "clearly"),
                    2 => ("This supported result plainly follows.", "This supported result", "plainly"),
                    _ => ("The verified conclusion expressly follows.", "The verified conclusion", "expressly")
                };
                examples.Add(ConclusionExample(conclusion, subjectSurface, registerSurface, "conclusion", conclusionKey));
                if (includeBranch)
                {
                    var (alternate, alternateSubject, alternateRegister) = family switch
                    {
                        1 => ("The governed alternative clearly remains.", "The governed alternative", "clearly"),
                        2 => ("This supported alternative plainly remains.", "This supported alternative", "plainly"),
                        _ => ("The verified alternative expressly remains.", "The verified alternative", "expressly")
                    };
                    examples.Add(ConclusionExample(alternate, alternateSubject, alternateRegister,
                        "alternate_conclusion", alternateKey, "remains"));
                }
            }
            var mode = cycleOnly ? "cycle" : includeBranch ? "branch" : "chain";
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(new(
                $"reasoning.general.static.{mode}.{family}", "Content-agnostic Founder reasoning graph evidence", examples));
            Assert.True(submitted.Succeeded, submitted.Message);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(new(
                sourceKey, "reasoning.implication", middleKey), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            if (cycleOnly)
            {
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(new(
                    middleKey, "reasoning.implication", sourceKey), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                continue;
            }
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(new(
                middleKey, "reasoning.implication", conclusionKey), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            if (includeBranch)
                await curriculum.PersistFounderCrossExampleSemanticRelationAsync(new(
                    middleKey, "reasoning.alternative", alternateKey), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }
    }

    private static async Task SubmitVariableReasoningChainAsync(LegendConnectCurriculumService curriculum)
    {
        for (var family = 1; family <= 3; family++)
        {
            var sourceKey = $"reasoning-variable-source-{family}";
            var middleKey = $"reasoning-variable-middle-{family}";
            var conclusionKey = $"reasoning-variable-conclusion-{family}";
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(new(
                $"reasoning.general.variables.{family}", "Founder variable-propagation reasoning evidence",
                [
                    RelationalExample($"Variable source {family}: Assess alpha.", "premise", "Assess", sourceKey),
                    RelationalExample($"Variable middle {family}: Continue alpha.", "intermediate", "Continue", middleKey),
                    RelationalExample($"Variable result {family}: Conclude alpha.", "conclusion", "Conclude", conclusionKey)
                ]));
            Assert.True(submitted.Succeeded, submitted.Message);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(new(
                sourceKey, "reasoning.variable.implication", middleKey), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(new(
                middleKey, "reasoning.variable.implication", conclusionKey), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }
    }

    private static LegendConnectCurriculumExampleSubmission AtomicExample(string text, string state, string surface, string semanticKey) => new(
        text, new Dictionary<string, string> { ["reasoning_state"] = state },
        new LegendConnectMeaningGraphSubmission([new LegendConnectMeaningNodeSubmission("state", "reasoning_state", state, surface)], []), semanticKey);

    private static LegendConnectCurriculumExampleSubmission ConclusionExample(string text, string subjectSurface, string registerSurface,
        string state, string semanticKey, string stateSurface = "follows") => new(
        text,
        new Dictionary<string, string> { ["reasoning_state"] = state, ["subject"] = "governed_case", ["register"] = "measured" },
        new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("subject", "subject", "governed_case", subjectSurface),
                new LegendConnectMeaningNodeSubmission("register", "register", "measured", registerSurface),
                new LegendConnectMeaningNodeSubmission("state", "reasoning_state", state, stateSurface)
            ],
            [
                new LegendConnectMeaningRelationSubmission("state", "applies-to", "subject"),
                new LegendConnectMeaningRelationSubmission("state", "qualified-by", "register")
            ]), semanticKey);

    private static LegendConnectCurriculumExampleSubmission RelationalExample(string text, string state, string stateSurface, string semanticKey) => new(
        text,
        new Dictionary<string, string> { ["reasoning_state"] = state, ["subject"] = "alpha" },
        new LegendConnectMeaningGraphSubmission(
            [
                new LegendConnectMeaningNodeSubmission("state", "reasoning_state", state, stateSurface),
                new LegendConnectMeaningNodeSubmission("subject", "subject", "alpha", "alpha")
            ],
            [new LegendConnectMeaningRelationSubmission("state", "applies-to", "subject")]), semanticKey);

    private static ReasoningFixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);
        return new ReasoningFixture(curriculum, operations);
    }

    private sealed record ReasoningFixture(LegendConnectCurriculumService Curriculum, LegendConnectOperations Operations);
}

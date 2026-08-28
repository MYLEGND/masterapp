using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectGovernedReasoningExecutorTests
{
    [Fact]
    public void Executor_AlwaysKeepsHigherStandardProofWhenBroadRuleDerivesSameState()
    {
        var source = new Dictionary<string, string> { ["stage"] = "observed" };
        var result = new Dictionary<string, string> { ["stage"] = "resolved" };
        var broad = new LegendGovernedReasoningRule(
            "a-broad",
            "reasoning.forward.resolution",
            source,
            result,
            1,
            1);
        var higher = new LegendGovernedReasoningRule(
            "z-higher",
            "reasoning.forward.resolution",
            source,
            result,
            3,
            2);

        var execution = LegendConnectGovernedReasoningExecutor.Derive(source, [broad, higher]);

        var resolved = Assert.Single(execution.DerivedStates);
        Assert.Equal("z-higher", Assert.Single(resolved.TransitionPath));
        Assert.Equal(3, resolved.EvidenceCount);
        Assert.Equal(2, resolved.EvidenceStandard);
    }

    [Fact]
    public void Executor_ChainsVariablesAcrossMultipleGovernedRulesWithoutTopicLogic()
    {
        var rules = new[]
        {
            Rule("r1", "reasoning.forward.diagnosis",
                new Dictionary<string, string> { ["subject"] = "$s", ["stage"] = "observed" },
                new Dictionary<string, string> { ["subject"] = "$s", ["stage"] = "diagnosed" }),
            Rule("r2", "reasoning.forward.resolution",
                new Dictionary<string, string> { ["subject"] = "$s", ["stage"] = "diagnosed" },
                new Dictionary<string, string> { ["subject"] = "$s", ["stage"] = "resolved" })
        };

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["subject"] = "alpha", ["stage"] = "observed" },
            rules);

        Assert.False(execution.InitialContradiction);
        Assert.False(execution.BudgetExceeded);
        var resolved = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault("stage") == "resolved"));
        Assert.Equal("alpha", resolved.Values["subject"]);
        Assert.Equal(2, resolved.Depth);
        Assert.Equal(2, resolved.TransitionPath.Count);
        Assert.Equal(3, resolved.EvidenceCount);
    }

    [Fact]
    public void Executor_UsesBidirectionalRulesAndPrunesConstraintViolations()
    {
        var equivalence = Rule("equiv", "reasoning.bidirectional.naming",
            new Dictionary<string, string> { ["term"] = "canonical" },
            new Dictionary<string, string> { ["term"] = "alias" });
        var approve = Rule("approve", "reasoning.forward.approval",
            new Dictionary<string, string> { ["stage"] = "reviewed" },
            new Dictionary<string, string> { ["stage"] = "approved" });
        var constraint = Rule("constraint", "reasoning.constraint.blocked-approval",
            new Dictionary<string, string> { ["stage"] = "approved" },
            new Dictionary<string, string> { ["risk"] = "blocked" });

        var reverse = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["term"] = "alias" },
            [equivalence]);
        Assert.Contains(reverse.DerivedStates, item => item.Values.GetValueOrDefault("term") == "canonical");

        var constrained = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["stage"] = "reviewed", ["risk"] = "blocked" },
            [approve, constraint]);
        Assert.DoesNotContain(constrained.DerivedStates, item => item.Values.GetValueOrDefault("stage") == "approved");
    }

    [Fact]
    public async Task NativeSelector_ExecutesTwoReasoningHopsThenUsesTheSameOriginalArticulationAuthority()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum);

        for (var support = 1; support <= 3; support++)
        {
            Assert.True((await curriculum.SubmitFounderEnglishBatchAsync(
                StateFamily(support, "observed", "The problem is observed.", "observed"))).Succeeded);
            Assert.True((await curriculum.SubmitFounderEnglishBatchAsync(
                StateFamily(support, "diagnosed", "The problem is diagnosed.", "diagnosed"))).Succeeded);
            Assert.True((await curriculum.SubmitFounderEnglishBatchAsync(
                StateFamily(support, "resolved", "The problem is resolved.", "resolved"))).Succeeded);
        }

        for (var support = 1; support <= 3; support++)
        {
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"observed-{support}",
                    "reasoning.forward.problem-state",
                    $"diagnosed-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"diagnosed-{support}",
                    "reasoning.forward.problem-state",
                    $"resolved-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        var storedResponses = new[]
        {
            "Resolved! Proceed with the verified solution?",
            "Completed! Use the confirmed solution?",
            "Settled! Apply the validated solution?"
        };
        for (var support = 1; support <= 3; support++)
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(
                ResponseFamily(support, storedResponses[support - 1]));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var reasoningTransitionSignatures = await (
            from transition in db.LegendSemanticTransitionEvidence.AsNoTracking()
            join relation in db.LegendFounderSemanticExampleRelationEvidence.AsNoTracking()
                on transition.FounderSemanticExampleRelationEvidenceId equals (Guid?)relation.Id
            where transition.SupersededUtc == null &&
                relation.SupersededUtc == null &&
                relation.RelationshipSemanticIdentity.StartsWith("reasoning.")
            select transition.TransitionSignature
        ).Distinct().ToArrayAsync();
        Assert.Equal(2, reasoningTransitionSignatures.Length);
        var eligible = await curriculum.GetProductionEligibleSemanticTransitionSignaturesAsync(
            "en",
            reasoningTransitionSignatures);
        Assert.Equal(2, eligible.Count);

        var graph = await operations.AnalyzeReusableMeaningGraphAsync("The problem is observed.");
        Assert.True(graph.IsComposed, graph.ReasonCode);
        Assert.Contains(graph.Nodes, item =>
            item.SemanticDimension == "problem_state" && item.SemanticValue == "observed");

        var planResult = await operations.TryPlanConversationAsync(
            "The problem is observed.",
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(planResult.Supported, planResult.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planResult.Plan);
        Assert.NotNull(plan.ReasoningTransitionPath);
        Assert.Equal(2, plan.ReasoningTransitionPath!.Count);
        Assert.Equal(3, plan.ReasoningEvidenceCount);
        Assert.Equal(6, plan.IndependentEvidenceCount);
        Assert.Equal("solution_response", plan.ResultDimensions["conversation_function"]);
        Assert.Equal("complete", plan.ResultDimensions["resolution"]);

        var native = await operations.TryInferConversationWithDiscourseAsync(
            "The problem is observed.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(native.Supported, native.ReasonCode + "; " + native.AuthoritySummary);
        Assert.False(native.RequiresEscalation);
        Assert.NotNull(native.Answer);
        Assert.True(native.EvidenceCount >= 9);
        Assert.DoesNotContain(storedResponses, item => string.Equals(
            LegendLanguageIdentity.NormalizeText(item),
            LegendLanguageIdentity.NormalizeText(native.Answer!),
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeSelector_PreservesReasoningNamedRelationsThatAreNotExecutableOperators()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MasterAppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum);

        for (var support = 1; support <= 3; support++)
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(
                NonExecutableReasoningRelationFamily(support));
            Assert.True(submitted.Succeeded, submitted.Message);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"limited-{support}",
                    "reasoning.qualifies.evidence",
                    $"preserve-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        Assert.False(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            "reasoning.qualifies.evidence"));

        var planResult = await operations.TryPlanConversationAsync(
            "The evidence is limited.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planResult.Supported, planResult.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planResult.Plan);
        Assert.Equal("preserve_alternatives", plan.ResultDimensions["decision_posture"]);
        Assert.Null(plan.ReasoningTransitionPath);
        Assert.Equal(0, plan.ReasoningEvidenceCount);
        Assert.Equal(3, plan.IndependentEvidenceCount);
    }

    private static LegendGovernedReasoningRule Rule(
        string signature,
        string operation,
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> result) =>
        new(signature, operation, source, result, 3);

    private static LegendConnectCurriculumBatchSubmission StateFamily(
        int support,
        string state,
        string text,
        string surface) =>
        new(
            $"reasoning.state.{state}.{support}",
            "Governed state evidence for generic multi-step reasoning",
            [
                new LegendConnectCurriculumExampleSubmission(
                    text,
                    new Dictionary<string, string> { ["problem_state"] = state },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "state", "problem_state", state, surface)],
                        []),
                    $"{state}-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Control evidence {state} {support}.",
                    new Dictionary<string, string> { ["control"] = $"{state}-{support}" })
            ]);

    private static LegendConnectCurriculumBatchSubmission ResponseFamily(
        int support,
        string response) =>
        new(
            $"reasoning.response.{support}",
            "Generic conclusion-to-response articulation evidence",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Resolved source evidence {support}.",
                    new Dictionary<string, string> { ["problem_state"] = "resolved" },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "state", "problem_state", "resolved", "Resolved")],
                        [])),
                new LegendConnectCurriculumExampleSubmission(
                    response,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "solution_response",
                        ["resolution"] = "complete"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "response", "conversation_function", "solution_response", response)],
                        []))
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string> { ["problem_state"] = "resolved" }),
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "solution_response",
                        ["resolution"] = "complete"
                    }))]);

    private static LegendConnectCurriculumBatchSubmission NonExecutableReasoningRelationFamily(
        int support) =>
        new(
            $"reasoning.qualifier.{support}",
            "Governed evidence qualification and alternative-preservation relation",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "The evidence is limited.",
                    new Dictionary<string, string> { ["evidence_strength"] = "limited" },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "evidence", "evidence_strength", "limited", "limited")],
                        []),
                    $"limited-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    "Preserve the alternatives.",
                    new Dictionary<string, string> { ["decision_posture"] = "preserve_alternatives" },
                    new LegendConnectMeaningGraphSubmission(
                        [new LegendConnectMeaningNodeSubmission(
                            "posture", "decision_posture", "preserve_alternatives", "Preserve the alternatives")],
                        []),
                    $"preserve-{support}")
            ]);

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = string.Empty,
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
        }).Build();
}

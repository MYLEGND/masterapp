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
    private static readonly Guid DefaultSemanticFamilyId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Executor_AlwaysKeepsHigherStandardProofWhenBroadRuleDerivesSameState()
    {
        var source = new Dictionary<string, string> { ["stage"] = "observed" };
        var result = new Dictionary<string, string> { ["stage"] = "resolved" };
        var broad = Rule(
            "a-broad",
            "reasoning.forward.resolution",
            source,
            result,
            1,
            1);
        var higher = Rule(
            "z-higher",
            "reasoning.forward.resolution",
            source,
            result,
            3,
            2);

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            source,
            [broad, higher],
            [DefaultSemanticFamilyId]);

        var resolved = Assert.Single(execution.DerivedStates);
        Assert.Equal("z-higher", Assert.Single(resolved.TransitionPath));
        Assert.Equal(3, resolved.EvidenceCount);
        Assert.Equal(2, resolved.EvidenceStandard);
    }

    [Fact]
    public void Executor_ReplacesEarlierBroadDirectProofWithLaterHigherStandardChain()
    {
        var rules = new[]
        {
            Rule(
                "a-higher-first",
                "reasoning.forward.resolution",
                new Dictionary<string, string> { ["stage"] = "observed" },
                new Dictionary<string, string> { ["stage"] = "verified" },
                4,
                2),
            Rule(
                "b-higher-second",
                "reasoning.forward.resolution",
                new Dictionary<string, string> { ["stage"] = "verified" },
                new Dictionary<string, string> { ["stage"] = "resolved" },
                4,
                2),
            Rule(
                "z-broad-direct",
                "reasoning.forward.resolution",
                new Dictionary<string, string> { ["stage"] = "observed" },
                new Dictionary<string, string> { ["stage"] = "resolved" },
                1,
                1)
        };

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["stage"] = "observed" },
            rules,
            [DefaultSemanticFamilyId]);

        var resolved = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault("stage") == "resolved"));
        Assert.Equal(["a-higher-first", "b-higher-second"], resolved.TransitionPath);
        Assert.Equal(4, resolved.EvidenceCount);
        Assert.Equal(2, resolved.EvidenceStandard);
        Assert.Equal(2, resolved.Depth);
    }

    [Fact]
    public void Executor_ReplacesEarlierWeakProofWithStrongerEquivalentProofAtSameStandard()
    {
        var rules = new[]
        {
            Rule(
                "a-weak-direct",
                "reasoning.forward.resolution",
                new Dictionary<string, string> { ["stage"] = "observed" },
                new Dictionary<string, string> { ["stage"] = "resolved" },
                1,
                2),
            Rule(
                "z-strong-direct",
                "reasoning.forward.resolution",
                new Dictionary<string, string> { ["stage"] = "observed" },
                new Dictionary<string, string> { ["stage"] = "resolved" },
                5,
                2)
        };

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["stage"] = "observed" },
            rules,
            [DefaultSemanticFamilyId]);

        var resolved = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault("stage") == "resolved"));
        Assert.Equal(["z-strong-direct"], resolved.TransitionPath);
        Assert.Equal(5, resolved.EvidenceCount);
        Assert.Equal(2, resolved.EvidenceStandard);
        Assert.Equal(1, resolved.Depth);
    }

    [Fact]
    public void Executor_ConvergesDeterministicallyAcrossGovernedCycles()
    {
        var rules = new[]
        {
            Rule("forward", "reasoning.bidirectional.stage",
                new Dictionary<string, string> { ["stage"] = "observed" },
                new Dictionary<string, string> { ["stage"] = "verified" }),
            Rule("resolve", "reasoning.forward.stage",
                new Dictionary<string, string> { ["stage"] = "verified" },
                new Dictionary<string, string> { ["stage"] = "resolved" })
        };
        var initial = new Dictionary<string, string> { ["stage"] = "observed" };

        var first = LegendConnectGovernedReasoningExecutor.Derive(
            initial,
            rules,
            [DefaultSemanticFamilyId]);
        var second = LegendConnectGovernedReasoningExecutor.Derive(
            initial,
            rules.Reverse().ToArray(),
            [DefaultSemanticFamilyId]);

        Assert.False(first.BudgetExceeded);
        Assert.Equal(
            first.DerivedStates.Select(ProofIdentity),
            second.DerivedStates.Select(ProofIdentity));
        Assert.Equal(2, first.DerivedStates.Count);
        Assert.DoesNotContain(first.DerivedStates, item =>
            item.Values.GetValueOrDefault("stage") == "observed");
    }

    private static string ProofIdentity(LegendGovernedReasoningProof proof) =>
        string.Join("|", proof.Values.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}")) +
        $"::{string.Join(">", proof.TransitionPath)}::{proof.Depth}::{proof.EvidenceCount}::{proof.EvidenceStandard}" +
        $"::{string.Join(",", proof.SemanticFamilyIds.OrderBy(item => item))}" +
        $"::{string.Join(">", proof.EvidenceLineage.Select(item => item.TransitionSignature))}";

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
            rules,
            [DefaultSemanticFamilyId]);

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
            [equivalence],
            [DefaultSemanticFamilyId]);
        Assert.Contains(reverse.DerivedStates, item => item.Values.GetValueOrDefault("term") == "canonical");

        var constrained = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["stage"] = "reviewed", ["risk"] = "blocked" },
            [approve, constraint],
            [DefaultSemanticFamilyId]);
        Assert.DoesNotContain(constrained.DerivedStates, item => item.Values.GetValueOrDefault("stage") == "approved");
    }

    [Fact]
    public void Executor_DeductionAppliesGovernedUniversalRuleAndCarriesEvidenceProof()
    {
        var rule = Rule(
            "universal-bird-mortality",
            "reasoning.deduction.universal.mortality",
            new Dictionary<string, string>
            {
                ["subject"] = "$subject",
                ["kind"] = "bird"
            },
            new Dictionary<string, string>
            {
                ["subject"] = "$subject",
                ["mortality"] = "mortal"
            });

        Assert.True(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            rule.OperatorIdentity));
        Assert.False(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            "reasoning.deduction"));
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["subject"] = "robin",
                ["kind"] = "bird"
            },
            [rule],
            [DefaultSemanticFamilyId]);

        Assert.False(execution.InitialContradiction);
        Assert.False(execution.DerivedContradiction);
        Assert.False(execution.BudgetExceeded);
        var conclusion = Assert.Single(execution.DerivedStates);
        Assert.Equal("robin", conclusion.Values["subject"]);
        Assert.Equal("mortal", conclusion.Values["mortality"]);
        var proofStep = Assert.Single(conclusion.EvidenceLineage);
        Assert.Equal(rule.TransitionSignature, proofStep.TransitionSignature);
        Assert.Equal(rule.OperatorIdentity, proofStep.OperatorIdentity);
        Assert.Equal("robin", proofStep.Premises["subject"]);
        Assert.Equal("bird", proofStep.Premises["kind"]);
        Assert.Equal("robin", proofStep.Conclusions["subject"]);
        Assert.Equal("mortal", proofStep.Conclusions["mortality"]);
        Assert.Equal(rule.IndependentEvidenceIdentities, proofStep.IndependentEvidenceIdentities);
        Assert.Equal(
            [DefaultSemanticFamilyId],
            proofStep.SourceSemanticFamilyIds.OrderBy(item => item));
        Assert.Equal(
            [DefaultSemanticFamilyId],
            proofStep.ResultSemanticFamilyIds.OrderBy(item => item));
        Assert.False(proofStep.HasExplicitGovernedTransfer);
        Assert.False(proofStep.Reversed);
    }

    [Fact]
    public void Executor_DeductionRejectsInvalidConverse()
    {
        var rule = Rule(
            "universal-bird-mortality",
            "reasoning.deduction.universal.mortality",
            new Dictionary<string, string>
            {
                ["subject"] = "$subject",
                ["kind"] = "bird"
            },
            new Dictionary<string, string>
            {
                ["subject"] = "$subject",
                ["mortality"] = "mortal"
            });

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["subject"] = "robin",
                ["mortality"] = "mortal"
            },
            [rule],
            [DefaultSemanticFamilyId]);

        Assert.Empty(execution.DerivedStates);
        Assert.False(execution.InitialContradiction);
        Assert.False(execution.DerivedContradiction);
    }

    [Fact]
    public void Executor_ConditionalDeductionFailsClosedWhenOnePremiseIsMissing()
    {
        var rule = Rule(
            "conditional-alarm-action",
            "reasoning.deduction.conditional.safety-action",
            new Dictionary<string, string>
            {
                ["site"] = "$site",
                ["alarm"] = "active",
                ["power"] = "available"
            },
            new Dictionary<string, string>
            {
                ["site"] = "$site",
                ["action"] = "evacuate"
            });

        Assert.True(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            rule.OperatorIdentity));
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["site"] = "north",
                ["alarm"] = "active"
            },
            [rule],
            [DefaultSemanticFamilyId]);

        Assert.Empty(execution.DerivedStates);
        Assert.False(execution.DerivedContradiction);
    }

    [Fact]
    public void Executor_DeductionReportsContradictoryGovernedFactInsteadOfOverwritingIt()
    {
        var rule = Rule(
            "universal-bird-mortality",
            "reasoning.deduction.universal.mortality",
            new Dictionary<string, string>
            {
                ["subject"] = "$subject",
                ["kind"] = "bird"
            },
            new Dictionary<string, string>
            {
                ["subject"] = "$subject",
                ["mortality"] = "mortal"
            });

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["subject"] = "robin",
                ["kind"] = "bird",
                ["mortality"] = "not_mortal"
            },
            [rule],
            [DefaultSemanticFamilyId]);

        Assert.False(execution.InitialContradiction);
        Assert.True(execution.DerivedContradiction);
        Assert.False(execution.BudgetExceeded);
        Assert.Empty(execution.DerivedStates);
    }

    [Fact]
    public void Executor_DeductionDoesNotApplyRuleFromUnrelatedSemanticFamily()
    {
        var unrelatedFamily = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rule = Rule(
            "unrelated-universal-rule",
            "reasoning.deduction.universal.unrelated",
            new Dictionary<string, string> { ["kind"] = "bird" },
            new Dictionary<string, string> { ["mortality"] = "mortal" },
            sourceSemanticFamilyIds: new HashSet<Guid> { unrelatedFamily },
            resultSemanticFamilyIds: new HashSet<Guid> { unrelatedFamily });

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["kind"] = "bird" },
            [rule],
            [DefaultSemanticFamilyId]);

        Assert.Empty(execution.DerivedStates);
        Assert.False(execution.DerivedContradiction);

        var governedTransfer = Rule(
            "explicit-family-transfer",
            "reasoning.deduction.universal.explicit-transfer",
            new Dictionary<string, string> { ["kind"] = "bird" },
            new Dictionary<string, string> { ["mortality"] = "mortal" },
            sourceSemanticFamilyIds: new HashSet<Guid> { DefaultSemanticFamilyId },
            resultSemanticFamilyIds: new HashSet<Guid> { unrelatedFamily },
            hasExplicitGovernedTransfer: true);
        var transferred = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["kind"] = "bird" },
            [governedTransfer],
            [DefaultSemanticFamilyId]);
        var transferredProof = Assert.Single(transferred.DerivedStates);
        Assert.Equal("mortal", transferredProof.Values["mortality"]);
        Assert.Equal(
            [unrelatedFamily],
            transferredProof.SemanticFamilyIds.OrderBy(item => item));
        Assert.True(Assert.Single(transferredProof.EvidenceLineage)
            .HasExplicitGovernedTransfer);

        var secondSourceFamily = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var secondResultFamily = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var pairedTransfers = governedTransfer with
        {
            SourceSemanticFamilyIds = new HashSet<Guid>
            {
                DefaultSemanticFamilyId,
                secondSourceFamily
            },
            ResultSemanticFamilyIds = new HashSet<Guid>
            {
                unrelatedFamily,
                secondResultFamily
            },
            FamilyConnections =
            [
                new LegendGovernedReasoningFamilyConnection(
                    DefaultSemanticFamilyId,
                    unrelatedFamily,
                    true),
                new LegendGovernedReasoningFamilyConnection(
                    secondSourceFamily,
                    secondResultFamily,
                    true)
            ]
        };
        var paired = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["kind"] = "bird" },
            [pairedTransfers],
            [DefaultSemanticFamilyId]);
        Assert.Equal(
            [unrelatedFamily],
            Assert.Single(paired.DerivedStates).SemanticFamilyIds.OrderBy(item => item));
    }

    [Fact]
    public void Executor_DeductionStopsAtTheExistingMaximumProofDepth()
    {
        var rules = Enumerable.Range(0, LegendConnectGovernedReasoningExecutor.MaximumDepth + 2)
            .Select(index => Rule(
                $"deduction-step-{index:D2}",
                "reasoning.deduction.conditional.bounded-chain",
                new Dictionary<string, string> { [$"fact_{index}"] = "present" },
                new Dictionary<string, string> { [$"fact_{index + 1}"] = "present" }))
            .ToArray();

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["fact_0"] = "present" },
            rules,
            [DefaultSemanticFamilyId]);

        Assert.False(execution.BudgetExceeded);
        Assert.False(execution.DerivedContradiction);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.MaximumDepth,
            execution.DerivedStates.Max(item => item.Depth));
        Assert.Contains(execution.DerivedStates, item =>
            item.Values.GetValueOrDefault($"fact_{LegendConnectGovernedReasoningExecutor.MaximumDepth}") == "present");
        Assert.DoesNotContain(execution.DerivedStates, item =>
            item.Values.ContainsKey($"fact_{LegendConnectGovernedReasoningExecutor.MaximumDepth + 1}"));
    }

    [Fact]
    public async Task Executor_DeductionConsumesHeldOutSurfaceOnlyAfterGovernedMeaningComposition()
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
                DeductionSurfaceFamily(support));
            Assert.True(submitted.Succeeded, submitted.Message);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"deduction-premise-{support}",
                    "reasoning.deduction.universal.mortality",
                    $"deduction-conclusion-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        foreach (var heldOutSurface in new[] { "Robin bird.", "Bird Robin." })
        {
            Assert.False(await db.LegendLanguageTextUnits.AnyAsync(item =>
                item.Text == LegendLanguageIdentity.NormalizeText(heldOutSurface)));
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(heldOutSurface);
            Assert.True(graph.IsComposed, graph.ReasonCode);
            Assert.Contains(graph.Nodes, item =>
                item.SemanticDimension == "subject" && item.SemanticValue == "robin");
            Assert.Contains(graph.Nodes, item =>
                item.SemanticDimension == "kind" && item.SemanticValue == "bird");

            var planned = await operations.TryPlanConversationAsync(
                heldOutSurface,
                new LegendConnectDiscourseStateSnapshot([]));
            Assert.True(planned.Supported, planned.ReasonCode);
            var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
            Assert.Equal("deductive_answer", plan.ResultDimensions["conversation_function"]);
            Assert.NotNull(plan.ReasoningTransitionPath);
            Assert.Single(plan.ReasoningTransitionPath!);
            Assert.Equal(3, plan.ReasoningEvidenceCount);
        }
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
        IReadOnlyDictionary<string, string> result,
        int independentEvidenceCount = 3,
        int evidenceStandard = 2,
        IReadOnlySet<Guid>? sourceSemanticFamilyIds = null,
        IReadOnlySet<Guid>? resultSemanticFamilyIds = null,
        IReadOnlyList<string>? independentEvidenceIdentities = null,
        bool hasExplicitGovernedTransfer = false)
    {
        var sourceFamilies = sourceSemanticFamilyIds ??
            new HashSet<Guid> { DefaultSemanticFamilyId };
        var resultFamilies = resultSemanticFamilyIds ??
            new HashSet<Guid> { DefaultSemanticFamilyId };
        var familyConnections = hasExplicitGovernedTransfer
            ? sourceFamilies.SelectMany(sourceFamily => resultFamilies.Select(resultFamily =>
                new LegendGovernedReasoningFamilyConnection(
                    sourceFamily,
                    resultFamily,
                    true))).ToArray()
            : sourceFamilies.Intersect(resultFamilies)
                .Select(family => new LegendGovernedReasoningFamilyConnection(
                    family,
                    family,
                    false)).ToArray();
        return new(
            signature,
            operation,
            source,
            result,
            independentEvidenceCount,
            evidenceStandard,
            sourceFamilies,
            resultFamilies,
            independentEvidenceIdentities ?? Enumerable.Range(1, independentEvidenceCount)
                .Select(index => $"{signature}-evidence-{index}")
                .ToArray(),
            familyConnections);
    }

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

    private static LegendConnectCurriculumBatchSubmission DeductionSurfaceFamily(
        int support) =>
        new(
            $"reasoning.deduction.surface.{support}",
            "Governed semantic facts for held-out deductive composition",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Governed premise evidence {support}: Robin is a bird.",
                    new Dictionary<string, string>
                    {
                        ["subject"] = "robin",
                        ["kind"] = "bird"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "subject", "subject", "robin", "Robin"),
                            new LegendConnectMeaningNodeSubmission(
                                "kind", "kind", "bird", "bird")
                        ],
                        [new LegendConnectMeaningRelationSubmission(
                            "subject", "member-of", "kind")]),
                    $"deduction-premise-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Conclusion evidence {support}: Robin is mortal.",
                    new Dictionary<string, string>
                    {
                        ["subject"] = "robin",
                        ["mortality"] = "mortal"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "subject", "subject", "robin", "Robin"),
                            new LegendConnectMeaningNodeSubmission(
                                "mortality", "mortality", "mortal", "mortal")
                        ],
                        [new LegendConnectMeaningRelationSubmission(
                            "subject", "has-property", "mortality")]),
                    $"deduction-conclusion-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Deductive response evidence {support}.",
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "deductive_answer"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    $"Deduction surface control {support}.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"deduction-surface-{support}"
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string> { ["mortality"] = "mortal" }),
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "deductive_answer"
                    }))]);

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

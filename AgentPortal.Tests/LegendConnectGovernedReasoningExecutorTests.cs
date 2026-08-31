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
    public void Executor_ObservationalEquivalenceRetainsBothCausesAndRequestsDiscriminatingEvidence()
    {
        var rule = Rule(
            "shared-observation-equivalence",
            "reasoning.epistemic.observational-equivalence.competing-causes",
            new Dictionary<string, string>
            {
                ["observation"] = "$signal",
                ["first_cause_prediction"] = "$signal",
                ["second_cause_prediction"] = "$signal"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
            });

        Assert.True(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            rule.OperatorIdentity));
        Assert.False(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            "reasoning.epistemic"));
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["observation"] = "latency_spike",
                ["first_cause_prediction"] = "latency_spike",
                ["second_cause_prediction"] = "latency_spike"
            },
            [rule],
            [DefaultSemanticFamilyId]);

        Assert.False(execution.DerivedContradiction);
        Assert.Empty(execution.Conflicts);
        var assessment = Assert.Single(execution.DerivedStates);
        Assert.Equal(LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue, assessment.Values[
            LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            assessment.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue,
            assessment.Values[LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension]);
        Assert.DoesNotContain(assessment.Values, item =>
            item.Key.Contains("probability", StringComparison.OrdinalIgnoreCase));

        var illicitSelection = rule with
        {
            ResultFrame = new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] = "first_cause",
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
            }
        };
        var rejected = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["observation"] = "latency_spike",
                ["first_cause_prediction"] = "latency_spike",
                ["second_cause_prediction"] = "latency_spike"
            },
            [illicitSelection],
            [DefaultSemanticFamilyId]);
        Assert.Empty(rejected.DerivedStates);

        var broadEquivalence = rule with
        {
            IndependentEvidenceCount = 1,
            EvidenceStandard = 1,
            IndependentEvidenceIdentities = ["shared-observation-broad-evidence"]
        };
        var higherButNonDiscriminatingSelection = Rule(
            "higher-shared-observation-selection",
            "reasoning.deduction.conditional.shared-observation-selection",
            new Dictionary<string, string>
            {
                ["observation"] = "$signal",
                ["first_cause_prediction"] = "$signal",
                ["second_cause_prediction"] = "$signal"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] = "first_cause"
            },
            evidenceStandard: 2);
        var nonDispositive = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["observation"] = "latency_spike",
                ["first_cause_prediction"] = "latency_spike",
                ["second_cause_prediction"] = "latency_spike"
            },
            [broadEquivalence, higherButNonDiscriminatingSelection],
            [DefaultSemanticFamilyId]);

        var nonDispositiveConflict = Assert.Single(nonDispositive.Conflicts);
        Assert.Equal(
            LegendGovernedReasoningConflictResolution.UnresolvedWithoutDiscriminatingEvidence,
            nonDispositiveConflict.Resolution);
        Assert.Null(nonDispositiveConflict.Selected);
        Assert.True(nonDispositiveConflict.RequiresDiscriminatingEvidence);
        var nonDispositiveAssessment = Assert.Single(nonDispositive.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            nonDispositiveAssessment.Values[
                LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.NonDispositiveAuthorityValue,
            nonDispositiveAssessment.Values[
                LegendConnectGovernedReasoningExecutor.EvidenceAuthorityDimension]);
    }

    [Fact]
    public void Executor_InsufficientEvidenceDoesNotSelectAnUnsupportedCause()
    {
        var rule = Rule(
            "insufficient-cause-evidence",
            "reasoning.epistemic.insufficient-evidence.cause-selection",
            new Dictionary<string, string>
            {
                ["candidate_cause"] = "$cause",
                ["evidence_support"] = "insufficient"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.InsufficientEvidenceValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
            });

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["candidate_cause"] = "capacity_shortage",
                ["evidence_support"] = "insufficient"
            },
            [rule],
            [DefaultSemanticFamilyId]);

        var assessment = Assert.Single(execution.DerivedStates);
        Assert.Equal(LegendConnectGovernedReasoningExecutor.InsufficientEvidenceValue, assessment.Values[
            LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            assessment.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.NotEqual(
            assessment.Values["candidate_cause"],
            assessment.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
    }

    [Fact]
    public void Executor_EqualAuthorityConflictProducesOneUnresolvedContradictionState()
    {
        var first = Rule(
            "equal-cause-alpha",
            "reasoning.deduction.conditional.cause-alpha",
            new Dictionary<string, string> { ["observation"] = "shared_alert" },
            new Dictionary<string, string> { ["cause_selection"] = "cause_alpha" },
            evidenceStandard: 2);
        var second = Rule(
            "equal-cause-beta",
            "reasoning.deduction.conditional.cause-beta",
            new Dictionary<string, string> { ["observation"] = "shared_alert" },
            new Dictionary<string, string> { ["cause_selection"] = "cause_beta" },
            evidenceStandard: 2);

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["observation"] = "shared_alert" },
            [first, second],
            [DefaultSemanticFamilyId]);

        Assert.False(execution.DerivedContradiction);
        var conflict = Assert.Single(execution.Conflicts);
        Assert.Equal("cause_selection", conflict.SemanticDimension);
        Assert.Equal(
            LegendGovernedReasoningConflictResolution.UnresolvedEqualAuthority,
            conflict.Resolution);
        Assert.Null(conflict.Selected);
        Assert.True(conflict.RequiresDiscriminatingEvidence);
        Assert.Equal(2, conflict.First.EvidenceStandard);
        Assert.Equal(2, conflict.Second.EvidenceStandard);
        var unresolved = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            unresolved.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UnresolvedContradictionValue,
            unresolved.Values[LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.EqualAuthorityValue,
            unresolved.Values[LegendConnectGovernedReasoningExecutor.EvidenceAuthorityDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue,
            unresolved.Values[LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension]);
    }

    [Fact]
    public void Executor_HigherStandardConflictRejectsTheLowerAuthorityConclusion()
    {
        var lower = Rule(
            "broad-governed-finding",
            "reasoning.deduction.conditional.broad-finding",
            new Dictionary<string, string> { ["governed_record"] = "case_17" },
            new Dictionary<string, string> { ["finding"] = "finding_alpha" },
            independentEvidenceCount: 1,
            evidenceStandard: 1);
        var higher = Rule(
            "higher-governed-finding",
            "reasoning.deduction.conditional.higher-finding",
            new Dictionary<string, string> { ["governed_record"] = "case_17" },
            new Dictionary<string, string> { ["finding"] = "finding_beta" },
            independentEvidenceCount: 3,
            evidenceStandard: 2);

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string> { ["governed_record"] = "case_17" },
            [lower, higher],
            [DefaultSemanticFamilyId]);

        var conflict = Assert.Single(execution.Conflicts);
        Assert.Equal(
            LegendGovernedReasoningConflictResolution.ResolvedByHigherStandard,
            conflict.Resolution);
        Assert.False(conflict.RequiresDiscriminatingEvidence);
        Assert.NotNull(conflict.Selected);
        Assert.Equal("finding", conflict.SemanticDimension);
        Assert.Equal("higher-governed-finding", conflict.Selected!.TransitionSignature);
        Assert.Equal(2, conflict.Selected.EvidenceStandard);
        var conclusion = Assert.Single(execution.DerivedStates);
        Assert.Equal("finding_beta", conclusion.Values["finding"]);
        Assert.Equal(["higher-governed-finding"], conclusion.TransitionPath);
        Assert.DoesNotContain(execution.DerivedStates, item =>
            item.Values.GetValueOrDefault("finding") == "finding_alpha");
    }

    [Fact]
    public void Executor_CausalDiagnosticPreservesObservationalEquivalenceWhenPredictionsMatch()
    {
        var plan = CausalPlanRule("causal-equivalence-plan");
        var equivalence = Rule(
            "causal-observational-equivalence",
            "reasoning.epistemic.observational-equivalence.causal-diagnostic",
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.FirstPredictionDimension] = "$signal",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionDimension] = "$signal"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
            });

        Assert.False(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            "reasoning.epistemic.discriminating-evidence"));
        Assert.True(LegendConnectGovernedReasoningExecutor.IsExecutableOperatorIdentity(
            plan.OperatorIdentity));
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            CausalDiagnosticValues(
                "hypothesis_alpha",
                "hypothesis_beta",
                "shared_outcome",
                "shared_outcome",
                "bounded_probe"),
            [plan, equivalence],
            [DefaultSemanticFamilyId]);

        var assessment = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
            assessment.Values[LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            assessment.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.False(assessment.Values.ContainsKey(
            LegendConnectGovernedReasoningExecutor.DiagnosticPlanStatusDimension));
    }

    [Fact]
    public void Executor_CausalDiagnosticFailsClosedWhenOnePredictionIsMissing()
    {
        var values = CausalDiagnosticValues(
            "hypothesis_alpha",
            "hypothesis_beta",
            "outcome_alpha",
            "outcome_beta",
            "bounded_probe");
        values.Remove(LegendConnectGovernedReasoningExecutor.SecondPredictionDimension);
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            values,
            [CausalPlanRule("missing-prediction-plan")],
            [DefaultSemanticFamilyId]);

        Assert.Empty(execution.DerivedStates);
        Assert.Empty(execution.Conflicts);
        Assert.False(execution.DerivedContradiction);
    }

    [Fact]
    public void Executor_CausalDiagnosticRejectsPrematureCauseSelectionInThePlanContract()
    {
        var plan = CausalPlanRule("premature-selection-plan");
        var malformedResult = plan.ResultFrame.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        malformedResult[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] = "$first";
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            CausalDiagnosticValues(
                "hypothesis_alpha",
                "hypothesis_beta",
                "outcome_alpha",
                "outcome_beta",
                "bounded_probe"),
            [plan with { ResultFrame = malformedResult }],
            [DefaultSemanticFamilyId]);

        Assert.Empty(execution.DerivedStates);
        Assert.Empty(execution.Conflicts);
    }

    [Fact]
    public void Executor_CausalDiagnosticKeepsHypothesesWhenEvidenceContradictsBothPredictions()
    {
        var rules = new[]
        {
            CausalPlanRule("contradictory-plan"),
            CausalConclusionRule("contradictory-first", selectFirst: true),
            CausalConclusionRule("contradictory-second", selectFirst: false),
            CausalContradictoryEvidenceRule("contradictory-outcome")
        };
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            CausalDiagnosticValues(
                "hypothesis_alpha",
                "hypothesis_beta",
                "outcome_alpha",
                "outcome_beta",
                "bounded_probe",
                observedEvidence: "outcome_neither"),
            rules,
            [DefaultSemanticFamilyId]);

        Assert.False(execution.DerivedContradiction);
        Assert.Empty(execution.Conflicts);
        var contradiction = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.DiagnosticConclusionStatusDimension) ==
            LegendConnectGovernedReasoningExecutor.ContradictoryEvidenceValue));
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UnresolvedContradictionValue,
            contradiction.Values[LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            contradiction.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.AttributionWithheldValue,
            contradiction.Values[
                LegendConnectGovernedReasoningExecutor.PrematureAttributionStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.ReassessHypothesesValue,
            contradiction.Values[LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension]);
        Assert.Equal(["contradictory-plan", "contradictory-outcome"], contradiction.TransitionPath);
        Assert.Equal(2, contradiction.EvidenceLineage.Count);
        Assert.DoesNotContain(execution.DerivedStates, item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.CauseSelectionDimension) is
                "hypothesis_alpha" or "hypothesis_beta");
    }

    [Fact]
    public void Executor_CausalDiagnosticPreservesUncertaintyWhenTheSelectedProbeIsUnavailable()
    {
        var values = CausalDiagnosticValues(
            "hypothesis_alpha",
            "hypothesis_beta",
            "outcome_alpha",
            "outcome_beta",
            "bounded_probe");
        values[LegendConnectGovernedReasoningExecutor.DiagnosticResourceDimension] =
            "bounded_probe";
        values[LegendConnectGovernedReasoningExecutor.DiagnosticResourceStatusDimension] =
            LegendConnectGovernedReasoningExecutor.ResourceUnavailableValue;
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            values,
            [
                CausalPlanRule("resource-plan"),
                CausalResourceLimitedRule("resource-unavailable")
            ],
            [DefaultSemanticFamilyId]);

        var limited = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.DiagnosticConclusionStatusDimension) ==
            LegendConnectGovernedReasoningExecutor.ResourceLimitedValue));
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.CompetingHypothesesValue,
            limited.Values[LegendConnectGovernedReasoningExecutor.HypothesisStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            limited.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.AttributionWithheldValue,
            limited.Values[LegendConnectGovernedReasoningExecutor.PrematureAttributionStatusDimension]);
        Assert.Equal(["resource-plan", "resource-unavailable"], limited.TransitionPath);
    }

    [Fact]
    public void Executor_CausalDiagnosticCarriesCompleteProofAcrossExplicitGovernedTransfer()
    {
        var sourceFamily = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var targetFamily = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var plan = CausalPlanRule(
            "transfer-plan",
            sourceSemanticFamilyIds: new HashSet<Guid> { sourceFamily },
            resultSemanticFamilyIds: new HashSet<Guid> { targetFamily },
            hasExplicitGovernedTransfer: true);
        var first = CausalConclusionRule(
            "transfer-first-conclusion",
            selectFirst: true,
            semanticFamilyId: targetFamily);
        var second = CausalConclusionRule(
            "transfer-second-conclusion",
            selectFirst: false,
            semanticFamilyId: targetFamily);
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            CausalDiagnosticValues(
                "candidate_alpha",
                "candidate_beta",
                "signal_alpha",
                "signal_beta",
                "bounded_probe",
                observedEvidence: "signal_beta"),
            [plan, first, second],
            [sourceFamily]);

        var conclusion = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.CauseSelectionDimension) ==
            "candidate_beta"));
        Assert.Equal(["transfer-plan", "transfer-second-conclusion"], conclusion.TransitionPath);
        Assert.Equal([targetFamily], conclusion.SemanticFamilyIds.OrderBy(item => item));
        Assert.Equal(3, conclusion.EvidenceCount);
        Assert.Equal(2, conclusion.EvidenceStandard);
        Assert.Equal(2, conclusion.EvidenceLineage.Count);
        var planStep = conclusion.EvidenceLineage[0];
        Assert.Equal("reasoning.causal-diagnostic.plan", planStep.OperatorIdentity);
        Assert.Equal("candidate_alpha", planStep.Premises[
            LegendConnectGovernedReasoningExecutor.FirstHypothesisDimension]);
        Assert.Equal("signal_beta", planStep.Premises[
            LegendConnectGovernedReasoningExecutor.SecondPredictionDimension]);
        Assert.Equal("bounded_probe", planStep.Conclusions[
            LegendConnectGovernedReasoningExecutor.SelectedDiscriminatingEvidenceDimension]);
        Assert.True(planStep.HasExplicitGovernedTransfer);
        Assert.Equal(plan.IndependentEvidenceIdentities, planStep.IndependentEvidenceIdentities);
        var conclusionStep = conclusion.EvidenceLineage[1];
        Assert.Equal("reasoning.causal-diagnostic.conclude", conclusionStep.OperatorIdentity);
        Assert.Equal("signal_beta", conclusionStep.Premises[
            LegendConnectGovernedReasoningExecutor.ObservedEvidenceDimension]);
        Assert.Equal("candidate_beta", conclusionStep.Conclusions[
            LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.False(conclusionStep.HasExplicitGovernedTransfer);
    }

    [Fact]
    public void Executor_CausalDiagnosticRejectsAnUnrelatedSemanticFamily()
    {
        var unrelatedFamily = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            CausalDiagnosticValues(
                "candidate_alpha",
                "candidate_beta",
                "signal_alpha",
                "signal_beta",
                "bounded_probe"),
            [CausalPlanRule(
                "unrelated-causal-plan",
                sourceSemanticFamilyIds: new HashSet<Guid> { unrelatedFamily },
                resultSemanticFamilyIds: new HashSet<Guid> { unrelatedFamily })],
            [DefaultSemanticFamilyId]);

        Assert.Empty(execution.DerivedStates);
        Assert.Empty(execution.Conflicts);
    }

    [Theory]
    [InlineData("candidate_alpha", "candidate_beta", "signal_left", "signal_right", "signal_right", "candidate_beta")]
    [InlineData("explanation_one", "explanation_two", "outcome_one", "outcome_two", "outcome_one", "explanation_one")]
    public void Executor_CausalDiagnosticAppliesHeldOutSemanticRolesWithoutDomainTerms(
        string firstHypothesis,
        string secondHypothesis,
        string firstPrediction,
        string secondPrediction,
        string observedEvidence,
        string expectedSelection)
    {
        var rules = new[]
        {
            CausalPlanRule("held-out-causal-plan"),
            CausalConclusionRule("held-out-first-conclusion", selectFirst: true),
            CausalConclusionRule("held-out-second-conclusion", selectFirst: false)
        };
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            CausalDiagnosticValues(
                firstHypothesis,
                secondHypothesis,
                firstPrediction,
                secondPrediction,
                "bounded_probe",
                observedEvidence),
            rules,
            [DefaultSemanticFamilyId]);

        var plan = Assert.Single(execution.DerivedStates.Where(item =>
            item.Depth == 1 &&
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.DiagnosticPlanStatusDimension) ==
            LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceSelectedValue));
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.CompetingHypothesesValue,
            plan.Values[LegendConnectGovernedReasoningExecutor.HypothesisStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.DifferingPredictionsValue,
            plan.Values[LegendConnectGovernedReasoningExecutor.PredictionStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.AttributionWithheldValue,
            plan.Values[LegendConnectGovernedReasoningExecutor.PrematureAttributionStatusDimension]);
        Assert.Equal(
            "bounded_probe",
            plan.Values[
                LegendConnectGovernedReasoningExecutor.SelectedDiscriminatingEvidenceDimension]);
        Assert.False(plan.Values.ContainsKey(
            LegendConnectGovernedReasoningExecutor.CauseSelectionDimension));

        var resolution = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.CauseSelectionDimension) ==
            expectedSelection));
        Assert.Equal(2, resolution.Depth);
        Assert.Equal(2, resolution.TransitionPath.Count);
        Assert.Equal(2, resolution.EvidenceLineage.Count);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.ResolvedByDiscriminatingEvidenceValue,
            resolution.Values[
                LegendConnectGovernedReasoningExecutor.DiagnosticConclusionStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.AttributionSupportedValue,
            resolution.Values[LegendConnectGovernedReasoningExecutor.CausalAttributionStatusDimension]);
        Assert.DoesNotContain(resolution.Values, item =>
            item.Key.Contains("probability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Executor_ConstrainedPlanningBuildsOneEngineerThirtyMinutePlanWithoutAssumingCause()
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal: "restore_observable_service",
                candidateAction: "establish_baseline",
                durationMinutes: 5,
                timeLimitMinutes: 30,
                availableResources: 1,
                requiredResources: 1),
            [
                PlanningStepRule(
                    "thirty-minute-step-one",
                    resultingElapsedMinutes: 5,
                    nextAction: "compare_predictions",
                    nextOrder: 2,
                    nextDurationMinutes: 10),
                PlanningStepRule(
                    "thirty-minute-step-two",
                    resultingElapsedMinutes: 15,
                    nextAction: "run_discriminator",
                    nextOrder: 3,
                    nextDurationMinutes: 15),
                PlanningStepRule(
                    "thirty-minute-step-three",
                    resultingElapsedMinutes: 30)
            ],
            [DefaultSemanticFamilyId]);

        Assert.False(execution.DerivedContradiction);
        Assert.Empty(execution.Conflicts);
        var completed = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.PlanStatusDimension) ==
            LegendConnectGovernedReasoningExecutor.PlanCompletedValue));
        Assert.Equal("run_discriminator", completed.Values[
            LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension]);
        Assert.Equal("3", completed.Values[
            LegendConnectGovernedReasoningExecutor.CurrentActionOrderDimension]);
        Assert.Equal("30", completed.Values[
            LegendConnectGovernedReasoningExecutor.PlanElapsedMinutesDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            completed.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(
            ["thirty-minute-step-one", "thirty-minute-step-two", "thirty-minute-step-three"],
            completed.TransitionPath);
        Assert.Equal(3, completed.EvidenceLineage.Count);
        Assert.All(completed.EvidenceLineage, step => Assert.Equal(
            "reasoning.constrained-planning.step",
            step.OperatorIdentity));
        var firstStep = completed.EvidenceLineage[0];
        Assert.Equal("restore_observable_service", firstStep.Premises[
            LegendConnectGovernedReasoningExecutor.PlanGoalDimension]);
        Assert.Equal("5", firstStep.Premises[
            LegendConnectGovernedReasoningExecutor.ActionDurationMinutesDimension]);
        Assert.Equal("30", firstStep.Premises[
            LegendConnectGovernedReasoningExecutor.PlanTimeLimitMinutesDimension]);
        Assert.Equal("1", firstStep.Premises[
            LegendConnectGovernedReasoningExecutor.AvailableResourceUnitsDimension]);
        Assert.Equal("1", firstStep.Premises[
            LegendConnectGovernedReasoningExecutor.RequiredResourceUnitsDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.SafetySatisfiedValue,
            firstStep.Premises[
                LegendConnectGovernedReasoningExecutor.SafetyConstraintStatusDimension]);

        var overrun = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal: "restore_observable_service",
                candidateAction: "overrun_action",
                durationMinutes: 31,
                timeLimitMinutes: 30,
                availableResources: 1,
                requiredResources: 1),
            [
                PlanningStepRule("overrun-step-must-not-run", 31),
                PlanningBlockRule(
                    "time-limit-block",
                    LegendConnectGovernedReasoningExecutor.TimeLimitExceededValue)
            ],
            [DefaultSemanticFamilyId]);
        var timeBlocked = Assert.Single(overrun.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.TimeLimitExceededValue,
            timeBlocked.Values[LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension]);
    }

    [Fact]
    public void Executor_ConstrainedPlanningBlocksAnInsufficientResourcePlanBeforeActing()
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal: "produce_bounded_assessment",
                candidateAction: "collect_independent_evidence",
                durationMinutes: 10,
                timeLimitMinutes: 30,
                availableResources: 1,
                requiredResources: 2),
            [
                PlanningStepRule("resource-step-must-not-run", 10),
                PlanningBlockRule(
                    "resource-plan-blocked",
                    LegendConnectGovernedReasoningExecutor.InsufficientResourceValue)
            ],
            [DefaultSemanticFamilyId]);

        var blocked = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.PlanBlockedValue,
            blocked.Values[LegendConnectGovernedReasoningExecutor.PlanStatusDimension]);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.InsufficientResourceValue,
            blocked.Values[LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension]);
        Assert.Equal("plan_start", blocked.Values[
            LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension]);
        Assert.Equal(["resource-plan-blocked"], blocked.TransitionPath);
    }

    [Fact]
    public void Executor_ConstrainedPlanningBlocksAnUnsafeStepBeforeActing()
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal: "obtain_safe_evidence",
                candidateAction: "candidate_action",
                durationMinutes: 5,
                timeLimitMinutes: 30,
                availableResources: 1,
                requiredResources: 1,
                safetyStatus: LegendConnectGovernedReasoningExecutor.SafetyViolatedValue),
            [
                PlanningStepRule("unsafe-step-must-not-run", 5),
                PlanningBlockRule(
                    "unsafe-plan-blocked",
                    LegendConnectGovernedReasoningExecutor.UnsafeStepValue)
            ],
            [DefaultSemanticFamilyId]);

        var blocked = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UnsafeStepValue,
            blocked.Values[LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension]);
        Assert.DoesNotContain("unsafe-step-must-not-run", blocked.TransitionPath);
    }

    [Fact]
    public void Executor_ConstrainedPlanningEnforcesPrerequisiteOrderAcrossProofSteps()
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal: "complete_ordered_assessment",
                candidateAction: "first_action",
                durationMinutes: 4,
                timeLimitMinutes: 20,
                availableResources: 1,
                requiredResources: 1),
            [
                PlanningStepRule(
                    "ordered-first",
                    resultingElapsedMinutes: 4,
                    nextAction: "second_action",
                    nextOrder: 2,
                    nextDurationMinutes: 5),
                PlanningStepRule("ordered-second", resultingElapsedMinutes: 9)
            ],
            [DefaultSemanticFamilyId]);

        Assert.DoesNotContain(execution.DerivedStates, item =>
            item.Depth == 1 &&
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension) ==
            "second_action");
        var completed = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.PlanStatusDimension) ==
            LegendConnectGovernedReasoningExecutor.PlanCompletedValue));
        Assert.Equal(["ordered-first", "ordered-second"], completed.TransitionPath);
        Assert.Equal("first_action", completed.EvidenceLineage[1].Premises[
            LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension]);
        Assert.Equal("second_action", completed.EvidenceLineage[1].Conclusions[
            LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension]);

        var invalidValues = PlanningValues(
            goal: "complete_ordered_assessment",
            candidateAction: "second_action",
            durationMinutes: 5,
            timeLimitMinutes: 20,
            availableResources: 1,
            requiredResources: 1);
        invalidValues[LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension] =
            "missing_first_action";
        var invalid = LegendConnectGovernedReasoningExecutor.Derive(
            invalidValues,
            [
                PlanningStepRule("invalid-prerequisite-step", 5),
                PlanningBlockRule(
                    "invalid-prerequisite-block",
                    LegendConnectGovernedReasoningExecutor.PrerequisiteOrderViolationValue)
            ],
            [DefaultSemanticFamilyId]);
        var blocked = Assert.Single(invalid.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.PrerequisiteOrderViolationValue,
            blocked.Values[LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension]);
    }

    [Fact]
    public void Executor_ConstrainedPlanningStopsEarlyWhenGovernedStopEvidenceIsObserved()
    {
        var values = PlanningValues(
            goal: "bound_the_investigation",
            candidateAction: "next_action",
            durationMinutes: 10,
            timeLimitMinutes: 30,
            availableResources: 1,
            requiredResources: 1);
        values[LegendConnectGovernedReasoningExecutor.StopConditionDimension] = "goal_reached";
        values[LegendConnectGovernedReasoningExecutor.ObservedStopEvidenceDimension] = "goal_reached";
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            values,
            [
                PlanningStepRule("post-stop-step-must-not-run", 10),
                PlanningStopRule("governed-early-stop")
            ],
            [DefaultSemanticFamilyId]);

        var stopped = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.PlanStoppedValue,
            stopped.Values[LegendConnectGovernedReasoningExecutor.PlanStatusDimension]);
        Assert.Equal("goal_reached", stopped.Values[
            LegendConnectGovernedReasoningExecutor.PlanStopReasonDimension]);
        Assert.Equal("goal_reached", stopped.Values[
            LegendConnectGovernedReasoningExecutor.SelectedStopEvidenceDimension]);
        Assert.Equal(["governed-early-stop"], stopped.TransitionPath);
    }

    [Fact]
    public void Executor_ConstrainedPlanningFailsClosedOnContradictoryConstraints()
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal: "produce_consistent_plan",
                candidateAction: "candidate_action",
                durationMinutes: 5,
                timeLimitMinutes: 30,
                availableResources: 1,
                requiredResources: 1,
                safetyStatus: LegendConnectGovernedReasoningExecutor.ConstraintContradictionValue),
            [
                PlanningStepRule("contradicted-step-must-not-run", 5),
                PlanningBlockRule(
                    "contradictory-plan-blocked",
                    LegendConnectGovernedReasoningExecutor.ContradictoryConstraintsValue)
            ],
            [DefaultSemanticFamilyId]);

        var blocked = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.ContradictoryConstraintsValue,
            blocked.Values[LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension]);
        Assert.Equal(["contradictory-plan-blocked"], blocked.TransitionPath);
    }

    [Fact]
    public void Executor_ConstrainedPlanningSelectsAnEvidenceDependentBranchBeforeActing()
    {
        var values = PlanningValues(
            goal: "follow_governed_evidence",
            candidateAction: "await_branch_evidence",
            durationMinutes: 1,
            timeLimitMinutes: 20,
            availableResources: 1,
            requiredResources: 1);
        values[LegendConnectGovernedReasoningExecutor.RequiredBranchEvidenceDimension] =
            "branch_evidence_alpha";
        values[LegendConnectGovernedReasoningExecutor.ObservedBranchEvidenceDimension] =
            "branch_evidence_alpha";
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            values,
            [
                PlanningEvidenceBranchRule(
                    "evidence-selects-branch",
                    selectedAction: "evidence_conditioned_action",
                    selectedOrder: 1,
                    selectedDurationMinutes: 7),
                PlanningStepRule("execute-evidence-branch", resultingElapsedMinutes: 7)
            ],
            [DefaultSemanticFamilyId]);

        var completed = Assert.Single(execution.DerivedStates.Where(item =>
            item.Values.GetValueOrDefault(
                LegendConnectGovernedReasoningExecutor.PlanStatusDimension) ==
            LegendConnectGovernedReasoningExecutor.PlanCompletedValue));
        Assert.Equal(
            "evidence_conditioned_action",
            completed.Values[LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension]);
        Assert.Equal(
            ["evidence-selects-branch", "execute-evidence-branch"],
            completed.TransitionPath);
        Assert.Equal("branch_evidence_alpha", completed.EvidenceLineage[0].Conclusions[
            LegendConnectGovernedReasoningExecutor.SelectedBranchEvidenceDimension]);

        var unmatchedValues = new Dictionary<string, string>(values)
        {
            [LegendConnectGovernedReasoningExecutor.ObservedBranchEvidenceDimension] =
                "branch_evidence_beta"
        };
        var unmatched = LegendConnectGovernedReasoningExecutor.Derive(
            unmatchedValues,
            [PlanningEvidenceBranchRule(
                "unmatched-evidence-branch",
                selectedAction: "must_not_be_selected",
                selectedOrder: 1,
                selectedDurationMinutes: 7)],
            [DefaultSemanticFamilyId]);
        Assert.Empty(unmatched.DerivedStates);
    }

    [Fact]
    public void Executor_ConstrainedPlanningBlocksAnUnsupportedCausalAssumption()
    {
        var values = PlanningValues(
            goal: "test_before_attribution",
            candidateAction: "collect_discriminating_evidence",
            durationMinutes: 5,
            timeLimitMinutes: 30,
            availableResources: 1,
            requiredResources: 1);
        values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] = "suspected_cause";
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            values,
            [
                PlanningStepRule("unsupported-cause-step-must-not-run", 5),
                PlanningBlockRule(
                    "unsupported-cause-plan-blocked",
                    LegendConnectGovernedReasoningExecutor.UnprovenCausalAssumptionValue)
            ],
            [DefaultSemanticFamilyId]);

        var blocked = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UnprovenCausalAssumptionValue,
            blocked.Values[LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension]);
    }

    [Theory]
    [InlineData("preserve_specimen_integrity", "inspect_seal_state")]
    [InlineData("verify_archive_provenance", "compare_chain_records")]
    public void Executor_ConstrainedPlanningAppliesHeldOutDomainRolesWithoutTopicLogic(
        string goal,
        string action)
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            PlanningValues(
                goal,
                action,
                durationMinutes: 12,
                timeLimitMinutes: 30,
                availableResources: 1,
                requiredResources: 1),
            [PlanningStepRule("held-out-planning-step", 12)],
            [DefaultSemanticFamilyId]);

        var completed = Assert.Single(execution.DerivedStates);
        Assert.Equal(goal, completed.Values[
            LegendConnectGovernedReasoningExecutor.PlanGoalDimension]);
        Assert.Equal(action, completed.Values[
            LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension]);
        Assert.Equal("12", completed.Values[
            LegendConnectGovernedReasoningExecutor.PlanElapsedMinutesDimension]);
        Assert.Equal("reasoning.constrained-planning.step", completed.EvidenceLineage[0].OperatorIdentity);
    }

    [Theory]
    [InlineData("latency_spike", "handoff_delay", "capacity_shortage")]
    [InlineData("renewal_drop", "message_mismatch", "timing_mismatch")]
    public void Executor_ObservationalEquivalenceBindsHeldOutGovernedSemanticValues(
        string observation,
        string firstCause,
        string secondCause)
    {
        var rule = Rule(
            "held-out-observational-equivalence",
            "reasoning.epistemic.observational-equivalence.held-out",
            new Dictionary<string, string>
            {
                ["observation"] = "$observation",
                ["first_candidate_cause"] = "$first",
                ["second_candidate_cause"] = "$second",
                ["first_cause_prediction"] = "$observation",
                ["second_cause_prediction"] = "$observation"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
            });

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            new Dictionary<string, string>
            {
                ["observation"] = observation,
                ["first_candidate_cause"] = firstCause,
                ["second_candidate_cause"] = secondCause,
                ["first_cause_prediction"] = observation,
                ["second_cause_prediction"] = observation
            },
            [rule],
            [DefaultSemanticFamilyId]);

        var assessment = Assert.Single(execution.DerivedStates);
        Assert.Equal(
            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
            assessment.Values[LegendConnectGovernedReasoningExecutor.CauseSelectionDimension]);
        Assert.Equal(observation, assessment.Values["observation"]);
        Assert.Equal(firstCause, assessment.Values["first_candidate_cause"]);
        Assert.Equal(secondCause, assessment.Values["second_candidate_cause"]);
    }

    [Fact]
    public async Task NativeSelector_ArticulatesGovernedUncertaintyThroughTheExistingRealizer()
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

        var storedResponses = new[]
        {
            "Both causes fit the signal; retain both and request a discriminating check.",
            "Each cause predicts the signal; keep both and gather separating evidence.",
            "The observation cannot choose a cause; preserve both and design a discriminating test."
        };
        for (var support = 1; support <= 3; support++)
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(
                EpistemicSurfaceFamily(support, storedResponses[support - 1]));
            Assert.True(submitted.Succeeded, submitted.Message);
            await curriculum.PersistFounderCrossExampleSemanticRelationAsync(
                new LegendConnectCrossExampleSemanticRelationshipSubmission(
                    $"epistemic-observation-{support}",
                    "reasoning.epistemic.observational-equivalence.competing-causes",
                    $"epistemic-assessment-{support}"),
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        var planned = await operations.TryPlanConversationAsync(
            "Signal matches alpha and beta.",
            new LegendConnectDiscourseStateSnapshot([]));

        Assert.True(planned.Supported, planned.ReasonCode);
        var plan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(planned.Plan);
        Assert.Equal("epistemic_answer", plan.ResultDimensions["conversation_function"]);
        Assert.Equal("retain_competing_causes", plan.ResultDimensions["decision_posture"]);
        Assert.Equal(
            "request_discriminating_evidence",
            plan.ResultDimensions["reasoning_action"]);
        Assert.NotNull(plan.ReasoningTransitionPath);
        Assert.Single(plan.ReasoningTransitionPath!);

        var native = await operations.TryInferConversationWithDiscourseAsync(
            "Signal matches alpha and beta.",
            [],
            new LegendConnectDiscourseStateSnapshot([]));
        Assert.True(native.Supported, native.ReasonCode + "; " + native.AuthoritySummary);
        Assert.False(native.RequiresEscalation);
        Assert.NotNull(native.Answer);
        Assert.DoesNotContain(storedResponses, response => string.Equals(
            LegendLanguageIdentity.NormalizeText(response),
            LegendLanguageIdentity.NormalizeText(native.Answer!),
            StringComparison.Ordinal));
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

    private static Dictionary<string, string> PlanningValues(
        string goal,
        string candidateAction,
        int durationMinutes,
        int timeLimitMinutes,
        int availableResources,
        int requiredResources,
        string safetyStatus = LegendConnectGovernedReasoningExecutor.SafetySatisfiedValue)
    {
        return new Dictionary<string, string>
        {
            [LegendConnectGovernedReasoningExecutor.PlanGoalDimension] = goal,
            [LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension] = "plan_start",
            [LegendConnectGovernedReasoningExecutor.CandidatePlanActionDimension] = candidateAction,
            [LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension] = "plan_start",
            [LegendConnectGovernedReasoningExecutor.CurrentActionOrderDimension] = "0",
            [LegendConnectGovernedReasoningExecutor.CandidateActionOrderDimension] = "1",
            [LegendConnectGovernedReasoningExecutor.ActionDurationMinutesDimension] =
                durationMinutes.ToString(),
            [LegendConnectGovernedReasoningExecutor.PlanTimeLimitMinutesDimension] =
                timeLimitMinutes.ToString(),
            [LegendConnectGovernedReasoningExecutor.PlanElapsedMinutesDimension] = "0",
            [LegendConnectGovernedReasoningExecutor.AvailableResourceUnitsDimension] =
                availableResources.ToString(),
            [LegendConnectGovernedReasoningExecutor.RequiredResourceUnitsDimension] =
                requiredResources.ToString(),
            [LegendConnectGovernedReasoningExecutor.SafetyConstraintStatusDimension] = safetyStatus,
            [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] =
                LegendConnectGovernedReasoningExecutor.PlanReadyValue,
            [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                LegendConnectGovernedReasoningExecutor.UndeterminedValue
        };
    }

    private static LegendGovernedReasoningRule PlanningStepRule(
        string signature,
        int resultingElapsedMinutes,
        string? nextAction = null,
        int nextOrder = 0,
        int nextDurationMinutes = 0,
        int nextRequiredResources = 1,
        string nextSafetyStatus = LegendConnectGovernedReasoningExecutor.SafetySatisfiedValue)
    {
        var result = new Dictionary<string, string>
        {
            [LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension] = "$candidate",
            [LegendConnectGovernedReasoningExecutor.CurrentActionOrderDimension] = "$candidate_order",
            [LegendConnectGovernedReasoningExecutor.PlanElapsedMinutesDimension] =
                resultingElapsedMinutes.ToString(),
            [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] = nextAction is null
                ? LegendConnectGovernedReasoningExecutor.PlanCompletedValue
                : LegendConnectGovernedReasoningExecutor.PlanInProgressValue
        };
        if (nextAction is not null)
        {
            result[LegendConnectGovernedReasoningExecutor.CandidatePlanActionDimension] = nextAction;
            result[LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension] = "$candidate";
            result[LegendConnectGovernedReasoningExecutor.CandidateActionOrderDimension] =
                nextOrder.ToString();
            result[LegendConnectGovernedReasoningExecutor.ActionDurationMinutesDimension] =
                nextDurationMinutes.ToString();
            result[LegendConnectGovernedReasoningExecutor.RequiredResourceUnitsDimension] =
                nextRequiredResources.ToString();
            result[LegendConnectGovernedReasoningExecutor.SafetyConstraintStatusDimension] =
                nextSafetyStatus;
        }

        return Rule(
            signature,
            "reasoning.constrained-planning.step",
            PlanningActionSourceFrame(),
            result,
            independentEvidenceCount: 3,
            evidenceStandard: 2);
    }

    private static LegendGovernedReasoningRule PlanningBlockRule(
        string signature,
        string reason) =>
        Rule(
            signature,
            "reasoning.constrained-planning.block",
            PlanningActionSourceFrame(),
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.PlanBlockedValue,
                [LegendConnectGovernedReasoningExecutor.PlanBlockReasonDimension] = reason
            });

    private static LegendGovernedReasoningRule PlanningEvidenceBranchRule(
        string signature,
        string selectedAction,
        int selectedOrder,
        int selectedDurationMinutes) =>
        Rule(
            signature,
            "reasoning.constrained-planning.evidence-branch",
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.PlanGoalDimension] = "$goal",
                [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] = "$status",
                [LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension] = "$current",
                [LegendConnectGovernedReasoningExecutor.CandidatePlanActionDimension] = "$pending",
                [LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension] = "$current",
                [LegendConnectGovernedReasoningExecutor.CandidateActionOrderDimension] = "$pending_order",
                [LegendConnectGovernedReasoningExecutor.ActionDurationMinutesDimension] = "$pending_duration",
                [LegendConnectGovernedReasoningExecutor.RequiredResourceUnitsDimension] = "$pending_resource",
                [LegendConnectGovernedReasoningExecutor.SafetyConstraintStatusDimension] = "$pending_safety",
                [LegendConnectGovernedReasoningExecutor.RequiredBranchEvidenceDimension] =
                    "$branch_evidence",
                [LegendConnectGovernedReasoningExecutor.ObservedBranchEvidenceDimension] =
                    "$branch_evidence"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension] = "$current",
                [LegendConnectGovernedReasoningExecutor.CandidatePlanActionDimension] = selectedAction,
                [LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension] = "$current",
                [LegendConnectGovernedReasoningExecutor.CandidateActionOrderDimension] =
                    selectedOrder.ToString(),
                [LegendConnectGovernedReasoningExecutor.ActionDurationMinutesDimension] =
                    selectedDurationMinutes.ToString(),
                [LegendConnectGovernedReasoningExecutor.RequiredResourceUnitsDimension] = "1",
                [LegendConnectGovernedReasoningExecutor.SafetyConstraintStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.SafetySatisfiedValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceBranchStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.EvidenceBranchSelectedValue,
                [LegendConnectGovernedReasoningExecutor.SelectedBranchEvidenceDimension] =
                    "$branch_evidence"
            });

    private static LegendGovernedReasoningRule PlanningStopRule(string signature) =>
        Rule(
            signature,
            "reasoning.constrained-planning.stop",
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.PlanGoalDimension] = "$goal",
                [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] = "$status",
                [LegendConnectGovernedReasoningExecutor.CurrentPlanActionDimension] = "$current",
                [LegendConnectGovernedReasoningExecutor.StopConditionDimension] = "$stop",
                [LegendConnectGovernedReasoningExecutor.ObservedStopEvidenceDimension] = "$stop"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.PlanStoppedValue,
                [LegendConnectGovernedReasoningExecutor.PlanStopReasonDimension] = "$stop",
                [LegendConnectGovernedReasoningExecutor.SelectedStopEvidenceDimension] = "$stop"
            });

    private static IReadOnlyDictionary<string, string> PlanningActionSourceFrame() =>
        new Dictionary<string, string>
        {
            [LegendConnectGovernedReasoningExecutor.PlanGoalDimension] = "$goal",
            [LegendConnectGovernedReasoningExecutor.CandidatePlanActionDimension] = "$candidate",
            [LegendConnectGovernedReasoningExecutor.ActionPrerequisiteDimension] = "$prerequisite",
            [LegendConnectGovernedReasoningExecutor.CurrentActionOrderDimension] = "$current_order",
            [LegendConnectGovernedReasoningExecutor.CandidateActionOrderDimension] = "$candidate_order",
            [LegendConnectGovernedReasoningExecutor.ActionDurationMinutesDimension] = "$duration",
            [LegendConnectGovernedReasoningExecutor.PlanTimeLimitMinutesDimension] = "$time_limit",
            [LegendConnectGovernedReasoningExecutor.PlanElapsedMinutesDimension] = "$elapsed",
            [LegendConnectGovernedReasoningExecutor.AvailableResourceUnitsDimension] = "$available",
            [LegendConnectGovernedReasoningExecutor.RequiredResourceUnitsDimension] = "$required",
            [LegendConnectGovernedReasoningExecutor.SafetyConstraintStatusDimension] = "$safety",
            [LegendConnectGovernedReasoningExecutor.PlanStatusDimension] = "$status"
        };

    private static Dictionary<string, string> CausalDiagnosticValues(
        string firstHypothesis,
        string secondHypothesis,
        string firstPrediction,
        string secondPrediction,
        string discriminatingEvidence,
        string? observedEvidence = null)
    {
        var values = new Dictionary<string, string>
        {
            [LegendConnectGovernedReasoningExecutor.FirstHypothesisDimension] = firstHypothesis,
            [LegendConnectGovernedReasoningExecutor.SecondHypothesisDimension] = secondHypothesis,
            [LegendConnectGovernedReasoningExecutor.FirstPredictionDimension] = firstPrediction,
            [LegendConnectGovernedReasoningExecutor.SecondPredictionDimension] = secondPrediction,
            [LegendConnectGovernedReasoningExecutor.FirstPredictionHypothesisDimension] =
                firstHypothesis,
            [LegendConnectGovernedReasoningExecutor.SecondPredictionHypothesisDimension] =
                secondHypothesis,
            [LegendConnectGovernedReasoningExecutor.FirstPredictionEvidenceDimension] =
                discriminatingEvidence,
            [LegendConnectGovernedReasoningExecutor.SecondPredictionEvidenceDimension] =
                discriminatingEvidence,
            [LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceDimension] =
                discriminatingEvidence
        };
        if (observedEvidence is not null)
        {
            values[LegendConnectGovernedReasoningExecutor.ObservedEvidenceSourceDimension] =
                discriminatingEvidence;
            values[LegendConnectGovernedReasoningExecutor.ObservedEvidenceDimension] =
                observedEvidence;
        }
        return values;
    }

    private static LegendGovernedReasoningRule CausalPlanRule(
        string signature,
        IReadOnlySet<Guid>? sourceSemanticFamilyIds = null,
        IReadOnlySet<Guid>? resultSemanticFamilyIds = null,
        bool hasExplicitGovernedTransfer = false) =>
        Rule(
            signature,
            "reasoning.causal-diagnostic.plan",
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.FirstHypothesisDimension] = "$first",
                [LegendConnectGovernedReasoningExecutor.SecondHypothesisDimension] = "$second",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionDimension] = "$first_prediction",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionDimension] = "$second_prediction",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionHypothesisDimension] = "$first",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionHypothesisDimension] = "$second",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionEvidenceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionEvidenceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceDimension] = "$evidence"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.HypothesisStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.CompetingHypothesesValue,
                [LegendConnectGovernedReasoningExecutor.PredictionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.DifferingPredictionsValue,
                [LegendConnectGovernedReasoningExecutor.DiagnosticPlanStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceSelectedValue,
                [LegendConnectGovernedReasoningExecutor.PrematureAttributionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.AttributionWithheldValue,
                [LegendConnectGovernedReasoningExecutor.SelectedDiscriminatingEvidenceDimension] =
                    "$evidence"
            },
            independentEvidenceCount: 3,
            evidenceStandard: 2,
            sourceSemanticFamilyIds: sourceSemanticFamilyIds,
            resultSemanticFamilyIds: resultSemanticFamilyIds,
            hasExplicitGovernedTransfer: hasExplicitGovernedTransfer);

    private static LegendGovernedReasoningRule CausalConclusionRule(
        string signature,
        bool selectFirst,
        Guid? semanticFamilyId = null)
    {
        var families = semanticFamilyId.HasValue
            ? new HashSet<Guid> { semanticFamilyId.Value }
            : null;
        return Rule(
            signature,
            "reasoning.causal-diagnostic.conclude",
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.FirstHypothesisDimension] = "$first",
                [LegendConnectGovernedReasoningExecutor.SecondHypothesisDimension] = "$second",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionDimension] = "$first_prediction",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionDimension] = "$second_prediction",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionHypothesisDimension] = "$first",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionHypothesisDimension] = "$second",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionEvidenceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionEvidenceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.PredictionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.DifferingPredictionsValue,
                [LegendConnectGovernedReasoningExecutor.SelectedDiscriminatingEvidenceDimension] =
                    "$evidence",
                [LegendConnectGovernedReasoningExecutor.ObservedEvidenceSourceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.ObservedEvidenceDimension] = "$observed"
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.DiagnosticConclusionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ResolvedByDiscriminatingEvidenceValue,
                [LegendConnectGovernedReasoningExecutor.CausalAttributionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.AttributionSupportedValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    selectFirst ? "$first" : "$second"
            },
            independentEvidenceCount: 4,
            evidenceStandard: 2,
            sourceSemanticFamilyIds: families,
            resultSemanticFamilyIds: families);
    }

    private static LegendGovernedReasoningRule CausalContradictoryEvidenceRule(
        string signature) =>
        Rule(
            signature,
            "reasoning.causal-diagnostic.contradictory-evidence",
            CausalObservedSourceFrame(),
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.DiagnosticConclusionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ContradictoryEvidenceValue,
                [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.UnresolvedContradictionValue,
                [LegendConnectGovernedReasoningExecutor.HypothesisStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.CompetingHypothesesValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                [LegendConnectGovernedReasoningExecutor.PrematureAttributionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.AttributionWithheldValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.ReassessHypothesesValue
            });

    private static LegendGovernedReasoningRule CausalResourceLimitedRule(
        string signature) =>
        Rule(
            signature,
            "reasoning.causal-diagnostic.resource-limited",
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.FirstHypothesisDimension] = "$first",
                [LegendConnectGovernedReasoningExecutor.SecondHypothesisDimension] = "$second",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionDimension] = "$first_prediction",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionDimension] = "$second_prediction",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionHypothesisDimension] = "$first",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionHypothesisDimension] = "$second",
                [LegendConnectGovernedReasoningExecutor.FirstPredictionEvidenceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.SecondPredictionEvidenceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.PredictionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.DifferingPredictionsValue,
                [LegendConnectGovernedReasoningExecutor.SelectedDiscriminatingEvidenceDimension] =
                    "$evidence",
                [LegendConnectGovernedReasoningExecutor.DiagnosticResourceDimension] = "$evidence",
                [LegendConnectGovernedReasoningExecutor.DiagnosticResourceStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ResourceUnavailableValue
            },
            new Dictionary<string, string>
            {
                [LegendConnectGovernedReasoningExecutor.DiagnosticConclusionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.ResourceLimitedValue,
                [LegendConnectGovernedReasoningExecutor.HypothesisStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.CompetingHypothesesValue,
                [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                    LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                [LegendConnectGovernedReasoningExecutor.PrematureAttributionStatusDimension] =
                    LegendConnectGovernedReasoningExecutor.AttributionWithheldValue,
                [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                    LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
            });

    private static IReadOnlyDictionary<string, string> CausalObservedSourceFrame() =>
        new Dictionary<string, string>
        {
            [LegendConnectGovernedReasoningExecutor.FirstHypothesisDimension] = "$first",
            [LegendConnectGovernedReasoningExecutor.SecondHypothesisDimension] = "$second",
            [LegendConnectGovernedReasoningExecutor.FirstPredictionDimension] = "$first_prediction",
            [LegendConnectGovernedReasoningExecutor.SecondPredictionDimension] = "$second_prediction",
            [LegendConnectGovernedReasoningExecutor.FirstPredictionHypothesisDimension] = "$first",
            [LegendConnectGovernedReasoningExecutor.SecondPredictionHypothesisDimension] = "$second",
            [LegendConnectGovernedReasoningExecutor.FirstPredictionEvidenceDimension] = "$evidence",
            [LegendConnectGovernedReasoningExecutor.SecondPredictionEvidenceDimension] = "$evidence",
            [LegendConnectGovernedReasoningExecutor.PredictionStatusDimension] =
                LegendConnectGovernedReasoningExecutor.DifferingPredictionsValue,
            [LegendConnectGovernedReasoningExecutor.SelectedDiscriminatingEvidenceDimension] =
                "$evidence",
            [LegendConnectGovernedReasoningExecutor.ObservedEvidenceSourceDimension] = "$evidence",
            [LegendConnectGovernedReasoningExecutor.ObservedEvidenceDimension] = "$observed"
        };

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

    private static LegendConnectCurriculumBatchSubmission EpistemicSurfaceFamily(
        int support,
        string response) =>
        new(
            $"reasoning.epistemic.surface.{support}",
            "Governed observational equivalence and discriminating-evidence response",
            [
                new LegendConnectCurriculumExampleSubmission(
                    "Signal matches alpha and beta.",
                    new Dictionary<string, string>
                    {
                        ["observation"] = "shared_signal",
                        ["first_cause_prediction"] = "shared_signal",
                        ["second_cause_prediction"] = "shared_signal"
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "observation", "observation", "shared_signal", "Signal"),
                            new LegendConnectMeaningNodeSubmission(
                                "first", "first_cause_prediction", "shared_signal", "alpha"),
                            new LegendConnectMeaningNodeSubmission(
                                "second", "second_cause_prediction", "shared_signal", "beta")
                        ],
                        [
                            new LegendConnectMeaningRelationSubmission(
                                "first", "predicts", "observation"),
                            new LegendConnectMeaningRelationSubmission(
                                "second", "predicts", "observation")
                        ]),
                    $"epistemic-observation-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    $"Equivalence leaves selection undetermined and requires assessment {support}.",
                    new Dictionary<string, string>
                    {
                        [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                            LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                        [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                        [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                            LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
                    },
                    new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "status",
                                LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension,
                                LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                                "equivalence"),
                            new LegendConnectMeaningNodeSubmission(
                                "selection",
                                LegendConnectGovernedReasoningExecutor.CauseSelectionDimension,
                                LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                                "undetermined"),
                            new LegendConnectMeaningNodeSubmission(
                                "evidence",
                                LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension,
                                LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue,
                                "assessment")
                        ],
                        [
                            new LegendConnectMeaningRelationSubmission(
                                "status", "requires", "evidence"),
                            new LegendConnectMeaningRelationSubmission(
                                "status", "leaves", "selection")
                        ]),
                    SemanticExampleKey: $"epistemic-assessment-{support}"),
                new LegendConnectCurriculumExampleSubmission(
                    response,
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "epistemic_answer",
                        ["decision_posture"] = "retain_competing_causes",
                        ["reasoning_action"] = "request_discriminating_evidence"
                    }),
                new LegendConnectCurriculumExampleSubmission(
                    $"Epistemic surface control {support}.",
                    new Dictionary<string, string>
                    {
                        ["control"] = $"epistemic-surface-{support}"
                    })
            ],
            [new LegendConnectSemanticTransitionSubmission(
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string>
                    {
                        [LegendConnectGovernedReasoningExecutor.EpistemicStatusDimension] =
                            LegendConnectGovernedReasoningExecutor.ObservationalEquivalenceValue,
                        [LegendConnectGovernedReasoningExecutor.CauseSelectionDimension] =
                            LegendConnectGovernedReasoningExecutor.UndeterminedValue,
                        [LegendConnectGovernedReasoningExecutor.EvidenceRequirementDimension] =
                            LegendConnectGovernedReasoningExecutor.DiscriminatingEvidenceValue
                    }),
                new LegendConnectSemanticFrameSubmission(
                    new Dictionary<string, string>
                    {
                        ["conversation_function"] = "epistemic_answer",
                        ["decision_posture"] = "retain_competing_causes",
                        ["reasoning_action"] = "request_discriminating_evidence"
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

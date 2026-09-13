using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Independent contracts for the existing governed executor. These tests
/// deliberately supply already-qualified semantic frames and synthetic proof
/// identities. They establish numeric execution and proof conservation only;
/// they do not establish language understanding, curriculum admission, or a
/// native conversational answer.
/// </summary>
public sealed class LegendConnectGovernedArithmeticContractTests
{
    private static readonly Guid Family = Guid.Parse("ec482ddc-9fb8-43c4-aa31-588f4eabebbb");
    private static readonly Guid OtherFamily = Guid.Parse("f0b26561-41b4-4260-b5e4-079795290c8a");

    [Theory]
    [InlineData("add", "83", "-29", "54")]
    [InlineData("subtract", "83", "29", "54")]
    [InlineData("subtract", "29", "83", "-54")]
    [InlineData("multiply", "17", "-31", "-527")]
    [InlineData("divide", "18", "42", "3/7")]
    [InlineData("divide", "-18", "42", "-3/7")]
    [InlineData("divide", "18", "-42", "-3/7")]
    [InlineData("divide", "0", "43", "0")]
    [InlineData("add", "1/7", "2/7", "3/7")]
    [InlineData("multiply", "0.125", "24", "3")]
    [InlineData("add", "9223372036854775807/2", "9223372036854775807/2", "9223372036854775807")]
    public void Arithmetic_ComputesFreshValuesInsteadOfReadingAnAuthoredConclusion(
        string operation, string left, string right, string expected)
    {
        var rule = NumericRule("heldout-calculation", operation, "first_measure", "second_measure", "computed_measure");
        var input = Frame(("first_measure", left), ("second_measure", right));
        var before = input.ToArray();

        var execution = LegendConnectGovernedReasoningExecutor.Derive(input, [rule], [Family]);

        Assert.False(execution.InitialContradiction);
        Assert.False(execution.DerivedContradiction);
        Assert.False(execution.BudgetExceeded);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal(expected, proof.Values["computed_measure"]);
        Assert.Equal(before, input.ToArray());
        Assert.Equal([rule.TransitionSignature], proof.TransitionPath);
        var step = Assert.Single(proof.EvidenceLineage);
        Assert.Equal(rule.OperatorIdentity, step.OperatorIdentity);
        Assert.Equal(left, step.Premises["first_measure"]);
        Assert.Equal(right, step.Premises["second_measure"]);
        Assert.Equal(expected, step.Conclusions["computed_measure"]);
        Assert.Equal(rule.IndependentEvidenceIdentities, step.IndependentEvidenceIdentities);
        Assert.Equal(3, proof.EvidenceCount);
        Assert.Equal(2, proof.EvidenceStandard);
        Assert.False(step.HasExplicitGovernedTransfer);
        Assert.Equal(Family, Assert.Single(proof.SemanticFamilyIds));
        Assert.Equal("$numeric_result", rule.ResultFrame["computed_measure"]);
        Assert.All(rule.SourceFrame.Values, value => Assert.StartsWith("$numeric_", value));
    }

    [Theory]
    [InlineData("7/9", "5/6", "less")]
    [InlineData("19", "18.999", "greater")]
    [InlineData("0.5", "3/6", "equal")]
    [InlineData("-11", "-12", "greater")]
    public void Comparison_UsesNumericOrderAndExactEquality(string left, string right, string expected)
    {
        var rule = NumericRule("heldout-ordering", "compare", "observed", "reference", "ordering");
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Frame(("observed", left), ("reference", right)), [rule], [Family]);

        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal(expected, proof.Values["ordering"]);
        Assert.Equal("$numeric_comparison", rule.ResultFrame["ordering"]);
        Assert.Equal(expected, Assert.Single(proof.EvidenceLineage).Conclusions["ordering"]);
    }

    [Fact]
    public void ChainedArithmetic_PreservesEachComputedPremiseAndEveryRuleInTheProof()
    {
        var rules = new[]
        {
            NumericRule("remove-completed", "subtract", "initial_count", "completed_count", "after_completion"),
            NumericRule("include-arrivals", "add", "after_completion", "arriving_count", "remaining_count"),
            NumericRule("calculate-share", "divide", "flagged_count", "remaining_count", "flagged_share")
        };
        var input = Frame(("initial_count", "79"), ("completed_count", "23"),
            ("arriving_count", "11"), ("flagged_count", "17"));

        var execution = LegendConnectGovernedReasoningExecutor.Derive(input, rules, [Family]);

        var proof = Assert.Single(execution.DerivedStates.Where(state => state.Values.ContainsKey("flagged_share")));
        Assert.Equal("56", proof.Values["after_completion"]);
        Assert.Equal("67", proof.Values["remaining_count"]);
        Assert.Equal("17/67", proof.Values["flagged_share"]);
        Assert.Equal(["remove-completed", "include-arrivals", "calculate-share"], proof.TransitionPath);
        Assert.Equal(3, proof.Depth);
        Assert.Equal(3, proof.EvidenceLineage.Count);
        Assert.Equal("56", proof.EvidenceLineage[1].Premises["after_completion"]);
        Assert.Equal("67", proof.EvidenceLineage[2].Premises["remaining_count"]);
        Assert.Equal(3, proof.EvidenceCount);
        Assert.Equal(2, proof.EvidenceStandard);
        Assert.All(rules, rule => Assert.All(rule.ResultFrame.Values, value => Assert.Equal("$numeric_result", value)));
        Assert.DoesNotContain("remaining_count", input.Keys);
        Assert.DoesNotContain("flagged_share", input.Keys);
    }

    [Fact]
    public void OneAdmittedRule_ReusesUnseenIntegerOperandsWithoutAnAnswerTable()
    {
        var rule = NumericRule("reusable-difference", "subtract", "opening", "removed", "balance");
        // This deterministic stream is independent of both the operator's
        // implementation and its governing frame. No case is added to rules.
        for (var sample = 1; sample <= 19; sample++)
        {
            var left = sample * 127L - 509L;
            var right = sample * 29L + 37L;
            var execution = LegendConnectGovernedReasoningExecutor.Derive(
                Frame(("opening", Text(left)), ("removed", Text(right))), [rule], [Family]);
            var proof = Assert.Single(execution.DerivedStates);
            Assert.Equal(Text(left - right), proof.Values["balance"]);
            Assert.Equal(rule.TransitionSignature, Assert.Single(proof.TransitionPath));
        }
    }

    [Theory]
    [InlineData("divide", "13", "0")]
    [InlineData("add", "1/0", "4")]
    [InlineData("add", "NaN", "4")]
    [InlineData("multiply", "Infinity", "4")]
    [InlineData("subtract", "seven", "4")]
    [InlineData("add", "1,000", "4")]
    [InlineData("add", "1e2", "4")]
    [InlineData("add", " 17", "4")]
    [InlineData("add", "9223372036854775808", "1")]
    [InlineData("add", "9223372036854775807", "1")]
    [InlineData("multiply", "9223372036854775807", "2")]
    public void InvalidOrOutOfBoundNumbers_CannotProduceAComputedFact(string operation, string left, string right)
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Frame(("left", left), ("right", right)),
            [NumericRule("invalid-operands", operation, "left", "right", "answer")], [Family]);

        Assert.Empty(execution.DerivedStates);
        Assert.False(execution.BudgetExceeded);
    }

    [Fact]
    public void ArithmeticOperator_CannotLaunderAnAuthoredConstantAsAComputation()
    {
        var rule = NumericRule("authored-answer", "multiply", "left", "right", "answer") with
        {
            ResultFrame = Frame(("answer", "527"))
        };
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Frame(("left", "17"), ("right", "31")), [rule], [Family]);

        Assert.Empty(execution.DerivedStates);
    }

    [Fact]
    public void CancelledArithmetic_PropagatesCancellationBeforeProducingAnyProof()
    {
        var input = Frame(("left", "47"), ("right", "19"));
        var before = input.ToArray();

        Assert.Throws<OperationCanceledException>(() =>
            LegendConnectGovernedReasoningExecutor.Derive(input,
                [NumericRule("cancelled-calculation", "add", "left", "right", "answer")],
                [Family], new CancellationToken(canceled: true)));

        Assert.Equal(before, input.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericCorrectness_CannotReplaceGovernedFamilyAuthority(bool unauthorizedTransfer)
    {
        var rule = NumericRule("family-bound-calculation", "add", "left", "right", "answer");
        if (unauthorizedTransfer)
        {
            rule = rule with
            {
                ResultSemanticFamilyIds = new HashSet<Guid> { OtherFamily },
                FamilyConnections = [new(Family, OtherFamily, false)]
            };
        }
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Frame(("left", "47"), ("right", "19")), [rule],
            unauthorizedTransfer ? [Family] : [OtherFamily]);

        Assert.Empty(execution.DerivedStates);
    }

    private static LegendGovernedReasoningRule NumericRule(
        string signature, string operation, string leftDimension, string rightDimension, string resultDimension) =>
        new(signature, "reasoning.arithmetic." + operation + ".independent-contract",
            Frame((leftDimension, "$numeric_left"), (rightDimension, "$numeric_right")),
            Frame((resultDimension, operation == "compare" ? "$numeric_comparison" : "$numeric_result")),
            3, 2, new HashSet<Guid> { Family }, new HashSet<Guid> { Family },
            [signature + ":source-a", signature + ":source-b", signature + ":source-c"],
            [new(Family, Family, false)]);

    private static Dictionary<string, string> Frame(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(value => value.Dimension, value => value.Value, StringComparer.Ordinal);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}

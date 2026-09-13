using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectGovernedArithmeticExecutorTests
{
    private static readonly Guid Family = Guid.Parse("57be8c10-f253-43e7-9ae7-46dcbed0778f");

    [Fact]
    public void ConflictingComputedConclusionCannotOverwriteAnObservedFact()
    {
        var inputs = Values(("stock", "13"), ("shipment", "8"), ("total", "20"));
        var execution = LegendConnectGovernedReasoningExecutor.Derive(inputs,
            [ArithmeticRule("sum", "add", "stock", "shipment", "total")], [Family]);
        Assert.True(execution.DerivedContradiction);
        Assert.Empty(execution.DerivedStates);
        Assert.Equal("20", inputs["total"]);
    }

    [Fact]
    public void ArithmeticCannotInventAdditionalFactsOrBindTheComputedResultFromInput()
    {
        var rule = ArithmeticRule("sum", "add", "left", "right", "sum");
        var inputs = Values(("left", "11"), ("right", "7"), ("invented", "18"));
        var factualOverride = rule with
        {
            ResultFrame = Values(("sum", "$numeric_result"), ("permission", "approved"))
        };
        var preboundResult = rule with
        {
            SourceFrame = Values(("left", "$numeric_left"), ("right", "$numeric_right"),
                ("invented", "$numeric_result"))
        };
        Assert.Empty(LegendConnectGovernedReasoningExecutor.Derive(inputs, [factualOverride], [Family]).DerivedStates);
        Assert.Empty(LegendConnectGovernedReasoningExecutor.Derive(inputs, [preboundResult], [Family]).DerivedStates);
    }

    [Fact]
    public void MultipleOperationsRetainOperandsAndUseTheWeakestProofAuthority()
    {
        var rules = new[]
        {
            ArithmeticRule("subtotal", "multiply", "rate", "quantity", "subtotal"),
            ArithmeticRule("total", "add", "subtotal", "fee", "total") with
            {
                IndependentEvidenceCount = 1, EvidenceStandard = 1,
                IndependentEvidenceIdentities = ["second-step-only"]
            }
        };
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Values(("rate", "1.25"), ("quantity", "14"), ("fee", "2/3")), rules, [Family]);
        var completed = Assert.Single(execution.DerivedStates.Where(proof => proof.Values.ContainsKey("total")));
        Assert.Equal("109/6", completed.Values["total"]);
        Assert.Equal("1.25", completed.Values["rate"]);
        Assert.Equal(new[] { "subtotal", "total" }, completed.TransitionPath);
        Assert.Equal(1, completed.EvidenceStandard);
        Assert.Equal(1, completed.EvidenceCount);
        Assert.Equal("35/2", completed.EvidenceLineage[1].Premises["subtotal"]);
        Assert.Equal("109/6", completed.EvidenceLineage[1].Conclusions["total"]);
    }

    [Theory]
    [InlineData("-9223372036854775808", "1", "divide", "-9223372036854775808")]
    [InlineData("0.000000000000000001", "0.000000000000000001", "divide", "1")]
    [InlineData("9223372036854775807/2", "2/9223372036854775807", "multiply", "1")]
    public void ExactReductionDoesNotOverflowBoundedFinalResults(string left, string right, string operation, string expected)
    {
        var result = LegendConnectGovernedReasoningExecutor.Derive(
            Values(("a", left), ("b", right)), [ArithmeticRule("numeric", operation, "a", "b", "answer")], [Family]);
        Assert.Equal(expected, Assert.Single(result.DerivedStates).Values["answer"]);
    }

    [Fact]
    public void CancellationIsObservedBeforeComputation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LegendConnectGovernedReasoningExecutor.Derive(
            Values(("a", "1"), ("b", "2")), [ArithmeticRule("sum", "add", "a", "b", "sum")],
            [Family], cancelled.Token));
    }

    [Theory]
    [InlineData("a-undefined", "z-valid")]
    [InlineData("z-undefined", "a-valid")]
    public void UndefinedArithmeticBranchCannotEraseAnIndependentValidProof(string undefinedId, string validId)
    {
        var inputs = Values(("left", "5"), ("right", "0"), ("fee", "2"));
        var rules = new[]
        {
            ArithmeticRule(undefinedId, "divide", "left", "right", "ratio"),
            ArithmeticRule(validId, "add", "left", "right", "subtotal"),
            ArithmeticRule("follow-on", "add", "subtotal", "fee", "total")
        };
        var execution = LegendConnectGovernedReasoningExecutor.Derive(inputs, rules, [Family]);
        var completed = Assert.Single(execution.DerivedStates.Where(proof => proof.Values.ContainsKey("total")));
        Assert.Equal("7", completed.Values["total"]);
        Assert.Equal(new[] { validId, "follow-on" }, completed.TransitionPath);
        Assert.DoesNotContain(execution.DerivedStates, proof => proof.Values.ContainsKey("ratio"));
        Assert.Null(execution.FailureReasonCode);
        Assert.False(execution.DerivedContradiction);

        var onlyUndefined = LegendConnectGovernedReasoningExecutor.Derive(inputs, [rules[0]], [Family]);
        Assert.Empty(onlyUndefined.DerivedStates);
        Assert.Equal("governed_arithmetic_operation_undefined_or_out_of_bounds", onlyUndefined.FailureReasonCode);
    }

    [Fact]
    public void AdmissionSampleUsesTheSameRolesAndRequiresEveryDeclaredPremise()
    {
        var source = Values(("amount", "$numeric_left"), ("fee", "$numeric_right"), ("unit", "credits"));
        var result = Values(("total", "$numeric_result"));
        Assert.False(LegendConnectGovernedReasoningExecutor.TryEvaluateComputedOperatorSample(
            "reasoning.arithmetic.add", source, result, Values(("amount", "12"), ("fee", "13")),
            out var unavailable, out _, out var reason));
        Assert.Empty(unavailable);
        Assert.Equal("governed_computation_source_not_bound", reason);

        Assert.True(LegendConnectGovernedReasoningExecutor.TryEvaluateComputedOperatorSample(
            "reasoning.arithmetic.add", source, result,
            Values(("amount", "12"), ("fee", "13"), ("unit", "credits")),
            out var computed, out var schedule, out reason));
        Assert.Equal("25", computed["total"]);
        Assert.Empty(schedule);
        Assert.Null(reason);

        Assert.False(LegendConnectGovernedReasoningExecutor.TryEvaluateComputedOperatorSample(
            "reasoning.arithmetic.add", source, Values(("total", "99")),
            Values(("amount", "12"), ("fee", "13"), ("unit", "credits")), out _, out _, out reason));
        Assert.Equal("governed_computation_schema_invalid", reason);
    }

    [Theory]
    [InlineData("3/6", "1/2")]
    [InlineData("-0.125", "-1/8")]
    [InlineData("+00012", "12")]
    public void GroundingAndExecutionUseOneCanonicalNumericRepresentation(string surface, string expected)
    {
        Assert.True(LegendConnectGovernedReasoningExecutor.TryNormalizeArithmeticOperand(surface, out var actual));
        Assert.Equal(expected, actual);
    }

    private static LegendGovernedReasoningRule ArithmeticRule(string identity, string operation, string left, string right, string output) =>
        new(identity, "reasoning.arithmetic." + operation,
            Values((left, "$numeric_left"), (right, "$numeric_right")), Values((output, "$numeric_result")),
            3, 2, new HashSet<Guid> { Family }, new HashSet<Guid> { Family }, ["one", "two", "three"],
            [new LegendGovernedReasoningFamilyConnection(Family, Family, false)]);

    private static Dictionary<string, string> Values(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}

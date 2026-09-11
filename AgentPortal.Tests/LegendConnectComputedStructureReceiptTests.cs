using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectComputedStructureReceiptTests
{
    private static readonly Guid Family = Guid.Parse("5d7aa9b7-5bf2-43ab-8eed-b883c34550bb");
    private static readonly string Coordinate = "rel_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "meaning-graph-structure|v1|plus-fee|subtotal|fee|"))).ToLowerInvariant()[..32];

    [Fact]
    public void QualifiedResultStructure_IsPreservedInComputedProofAndSample()
    {
        var rule = Rule();
        rule = rule with { StructuralConclusions = Receipt(rule) };
        Assert.NotNull(rule.StructuralConclusions);
        var execution = LegendConnectGovernedReasoningExecutor.Derive(Input(), [rule], [Family]);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal("6", proof.Values["subtotal"]);
        Assert.Equal("1", proof.Values["fee"]);
        Assert.Equal("present", proof.Values[Coordinate]);
        Assert.Equal("present", Assert.Single(proof.EvidenceLineage).Conclusions[Coordinate]);
        Assert.Equal(new[] { "one", "two", "three" }, proof.EvidenceLineage[0].IndependentEvidenceIdentities);
        Assert.True(LegendConnectGovernedReasoningExecutor.TryEvaluateComputedOperatorSample(
            rule.OperatorIdentity, rule.SourceFrame, rule.ResultFrame, Input(), out var values, out _, out var reason,
            structuralConclusions: rule.StructuralConclusions), reason);
        Assert.Equal("present", values[Coordinate]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("operator")]
    [InlineData("source")]
    [InlineData("result")]
    [InlineData("extra_literal")]
    [InlineData("invented_coordinate")]
    public void UnqualifiedOrChangedScope_CannotAuthorizeStructuralOrOrdinaryLiterals(string change)
    {
        var rule = Rule();
        var receipt = Receipt(rule);
        switch (change)
        {
            case "missing": receipt = null; break;
            case "operator": rule = rule with { OperatorIdentity = "reasoning.arithmetic.add" }; break;
            case "source": rule = rule with { SourceFrame = Values(("rate", "$numeric_right"), ("quantity", "$numeric_left"), ("fee", "$fee")) }; break;
            case "result": rule = rule with { ResultFrame = Values(("subtotal", "$numeric_result"), ("fee", "$fee"), (Coordinate, "absent")) }; break;
            case "extra_literal":
                rule = rule with { ResultFrame = rule.ResultFrame.Concat(new[] { new KeyValuePair<string, string>("permission", "approved") }).ToDictionary(item => item.Key, item => item.Value) };
                receipt = Receipt(rule); // Even a correctly scoped graph receipt cannot grant an unrelated literal.
                break;
            case "invented_coordinate":
                rule = rule with { ResultFrame = rule.ResultFrame.Concat(new[] { new KeyValuePair<string, string>("rel_invented", "present") }).ToDictionary(item => item.Key, item => item.Value) };
                receipt = Receipt(rule);
                break;
        }
        rule = rule with { StructuralConclusions = receipt };
        Assert.Empty(LegendConnectGovernedReasoningExecutor.Derive(Input(), [rule], [Family]).DerivedStates);
        Assert.False(LegendConnectGovernedReasoningExecutor.TryEvaluateComputedOperatorSample(
            rule.OperatorIdentity, rule.SourceFrame, rule.ResultFrame, Input(), out _, out _, out _, structuralConclusions: receipt));
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("clause")]
    [InlineData("missing_evidence")]
    [InlineData("missing_endpoint")]
    [InlineData("missing_pair")]
    public void ReceiptFactory_RequiresExactGraphCoordinateAndEndpoints(string change)
    {
        var rule = Rule();
        var edge = Edge();
        if (change == "direction") edge = edge with { SourceDimension = "fee", TargetDimension = "subtotal" };
        if (change == "clause") edge = edge with { ClauseKey = "different-clause" };
        if (change == "missing_evidence") edge = edge with { RelationEvidenceId = Guid.Empty };
        if (change == "missing_endpoint") edge = edge with { TargetDimension = "absent_dimension" };
        Assert.Null(LegendGovernedComputedStructureReceipt.FromGraph(change == "missing_pair" ? Guid.Empty : Guid.NewGuid(), Guid.NewGuid(),
            rule.OperatorIdentity, rule.SourceFrame, rule.ResultFrame, [edge]));
    }

    [Fact]
    public void StructuralConclusion_CannotOverwriteContradictingObservation()
    {
        var rule = Rule();
        rule = rule with { StructuralConclusions = Receipt(rule) };
        var input = Input(); input[Coordinate] = "absent";
        var execution = LegendConnectGovernedReasoningExecutor.Derive(input, [rule], [Family]);
        Assert.True(execution.DerivedContradiction);
        Assert.Empty(execution.DerivedStates);
        Assert.Equal("absent", input[Coordinate]);
    }

    [Fact]
    public void OrdinaryArithmetic_StillExecutesWithoutStructuralReceipt()
    {
        var rule = Rule() with { ResultFrame = Values(("subtotal", "$numeric_result")) };
        Assert.Equal("6", Assert.Single(LegendConnectGovernedReasoningExecutor.Derive(Input(), [rule], [Family]).DerivedStates).Values["subtotal"]);
    }

    private static LegendGovernedComputedRelationEvidence Edge() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "plus-fee", "subtotal", "fee", null);
    private static LegendGovernedComputedStructureReceipt? Receipt(LegendGovernedReasoningRule rule) =>
        LegendGovernedComputedStructureReceipt.FromGraph(Guid.NewGuid(), Guid.NewGuid(), rule.OperatorIdentity,
            rule.SourceFrame, rule.ResultFrame, [Edge()]);
    private static LegendGovernedReasoningRule Rule() => new("multiply-with-fee", "reasoning.arithmetic.multiply",
        Values(("rate", "$numeric_left"), ("quantity", "$numeric_right"), ("fee", "$fee")),
        Values(("subtotal", "$numeric_result"), ("fee", "$fee"), (Coordinate, "present")),
        3, 2, new HashSet<Guid> { Family }, new HashSet<Guid> { Family }, ["one", "two", "three"],
        [new(Family, Family, false)]);
    private static Dictionary<string, string> Input() => Values(("rate", "2"), ("quantity", "3"), ("fee", "1"));
    private static Dictionary<string, string> Values(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}

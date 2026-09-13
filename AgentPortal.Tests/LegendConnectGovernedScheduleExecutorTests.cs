using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectGovernedScheduleExecutorTests
{
    private static readonly Guid Family = Guid.Parse("2d56e7a0-c076-4530-970e-23f941e67558");

    [Fact]
    public void BatchPlanComputesPartialBatchAndResourceLanesFromWorkload()
    {
        var inputs = Inputs(31, 7, 6, 4, 2, 24);
        var execution = LegendConnectGovernedReasoningExecutor.Derive(inputs, [Rule()], [Family]);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal("5", proof.Values["batch_count"]);
        Assert.Equal("18", proof.Values["elapsed_minutes"]);
        Assert.Equal("3", proof.Values["final_batch_size"]);
        Assert.Equal("feasible", proof.Values["schedule_status"]);
        var step = Assert.Single(proof.EvidenceLineage);
        Assert.NotNull(step.ScheduleSteps);
        Assert.Equal(31L, step.ScheduleSteps!.Sum(batch => batch.WorkUnits));
        Assert.Equal(new[] { 0, 0, 6, 6, 12 }, step.ScheduleSteps.Select(batch => batch.StartMinute));
        foreach (var first in step.ScheduleSteps)
        foreach (var second in step.ScheduleSteps.Where(batch => batch.BatchNumber > first.BatchNumber))
        {
            var overlappingTime = first.StartMinute < second.EndMinute && second.StartMinute < first.EndMinute;
            var overlappingResource = first.FirstResourceUnit < second.FirstResourceUnit + second.ResourceUnitCount &&
                second.FirstResourceUnit < first.FirstResourceUnit + first.ResourceUnitCount;
            Assert.False(overlappingTime && overlappingResource);
        }
        Assert.Equal("31", step.Premises["workload"]);
        Assert.Equal(proof.Values["schedule_signature"], step.Conclusions["schedule_signature"]);
        Assert.Equal(64, proof.Values["schedule_signature"].Length);
        Assert.DoesNotContain("batch_count", inputs.Keys);
    }

    [Theory]
    [InlineData("workload", "0", "governed_schedule_operand_invalid")]
    [InlineData("workload", "-1", "governed_schedule_operand_invalid")]
    [InlineData("capacity", "0", "governed_schedule_operand_invalid")]
    [InlineData("capacity", "-7", "governed_schedule_operand_invalid")]
    [InlineData("available", "0", "governed_schedule_operand_invalid")]
    [InlineData("available", "1", "governed_schedule_resources_insufficient")]
    [InlineData("required", "-1", "governed_schedule_operand_invalid")]
    [InlineData("duration", "1441", "governed_schedule_operand_invalid")]
    [InlineData("workload", "9223372036854775808", "governed_schedule_operand_invalid")]
    public void UndefinedOrUnavailablePlanCannotProduceASchedule(string field, string value, string reason)
    {
        var inputs = Inputs(31, 7, 6, 4, 2, 24);
        inputs[field] = value;
        var execution = LegendConnectGovernedReasoningExecutor.Derive(inputs, [Rule()], [Family]);
        Assert.Empty(execution.DerivedStates);
        Assert.Equal(reason, execution.FailureReasonCode);
    }

    [Fact]
    public void CountBoundIsExplicitAndCeilingCannotOverflow()
    {
        var bounded = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(513, 1, 1, 1, 1, 1440), [Rule()], [Family]);
        Assert.Empty(bounded.DerivedStates);
        Assert.Equal("governed_schedule_batch_bound_exceeded", bounded.FailureReasonCode);
        var large = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(long.MaxValue, long.MaxValue - 1, 1, 1, 1, 10), [Rule()], [Family]);
        var proof = Assert.Single(large.DerivedStates);
        Assert.Equal("2", proof.Values["batch_count"]);
        Assert.Equal("1", proof.Values["final_batch_size"]);
        Assert.Equal(long.MaxValue, Assert.Single(proof.EvidenceLineage).ScheduleSteps!.Sum(batch => batch.WorkUnits));
    }

    [Fact]
    public void TimeBudgetViolationIsAComputedFailureStatus()
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(31, 7, 6, 4, 2, 17), [Rule()], [Family]);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal("18", proof.Values["elapsed_minutes"]);
        Assert.Equal("time_limit_exceeded", proof.Values["schedule_status"]);
        Assert.DoesNotContain(execution.DerivedStates, state => state.Values["schedule_status"] == "feasible");
    }

    [Fact]
    public void ClaimedComputedTotalCannotOverrideArithmeticAndScheduleCertificateIsStable()
    {
        var inputs = Inputs(29, 6, 4, 3, 1, 24);
        var first = LegendConnectGovernedReasoningExecutor.Derive(inputs, [Rule()], [Family]);
        var second = LegendConnectGovernedReasoningExecutor.Derive(
            inputs.Reverse().ToDictionary(item => item.Key, item => item.Value), [Rule()], [Family]);
        Assert.Equal(Assert.Single(first.DerivedStates).Values["schedule_signature"],
            Assert.Single(second.DerivedStates).Values["schedule_signature"]);
        inputs["batch_count"] = "4";
        var contradicted = LegendConnectGovernedReasoningExecutor.Derive(inputs, [Rule()], [Family]);
        Assert.True(contradicted.DerivedContradiction);
        Assert.Empty(contradicted.DerivedStates);
    }

    [Fact]
    public void CancelledScheduleDoesNotReturnPartialCertificate()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Throws<OperationCanceledException>(() => LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(511, 1, 1, 1, 1, 1440), [Rule()], [Family], token.Token));
    }

    [Fact]
    public void ResourceFailureCannotDiscardAnIndependentArithmeticProof()
    {
        var sum = Rule() with
        {
            TransitionSignature = "independent-sum",
            OperatorIdentity = "reasoning.arithmetic.add",
            SourceFrame = new Dictionary<string, string>
            {
                ["workload"] = "$numeric_left", ["capacity"] = "$numeric_right"
            },
            ResultFrame = new Dictionary<string, string> { ["quantity_sum"] = "$numeric_result" }
        };
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(31, 7, 6, 1, 2, 24), [Rule(), sum], [Family]);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal("38", proof.Values["quantity_sum"]);
        Assert.Null(execution.FailureReasonCode);
        Assert.DoesNotContain("schedule_status", proof.Values.Keys);
        Assert.Empty(Assert.Single(proof.EvidenceLineage).ScheduleSteps ?? []);
    }

    [Fact]
    public void AdmissionSampleReturnsTheExecutableCertificateWithoutInventingEvidence()
    {
        var rule = Rule();
        var inputs = Inputs(31, 7, 6, 4, 2, 24);
        Assert.True(LegendConnectGovernedReasoningExecutor.TryEvaluateComputedOperatorSample(
            rule.OperatorIdentity, rule.SourceFrame, rule.ResultFrame, inputs,
            out var computed, out var schedule, out var reason));
        Assert.Null(reason);
        Assert.Equal(5, schedule.Count);
        Assert.Equal(31L, schedule.Sum(step => step.WorkUnits));
        var execution = LegendConnectGovernedReasoningExecutor.Derive(inputs, [rule], [Family]);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal(proof.Values["schedule_signature"], computed["schedule_signature"]);
        Assert.Equal(Assert.Single(proof.EvidenceLineage).ScheduleSteps, schedule);
    }

    private static Dictionary<string, string> Inputs(long workload, long capacity, int duration, int available, int required, int limit) =>
        new()
        {
            ["workload"] = workload.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["capacity"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["duration"] = duration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["available"] = available.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["required"] = required.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

    private static LegendGovernedReasoningRule Rule() => new("batch-proof", "reasoning.constrained-planning.batch",
        new Dictionary<string, string>
        {
            ["workload"] = "$schedule_workload", ["capacity"] = "$schedule_batch_capacity",
            ["duration"] = "$schedule_batch_duration", ["available"] = "$schedule_available_resources",
            ["required"] = "$schedule_required_resources", ["limit"] = "$schedule_time_limit"
        },
        new Dictionary<string, string>
        {
            ["batch_count"] = "$schedule_batch_count", ["elapsed_minutes"] = "$schedule_elapsed",
            ["final_batch_size"] = "$schedule_final_batch_size", ["schedule_status"] = "$schedule_status",
            ["schedule_signature"] = "$schedule_signature"
        },
        3, 2, new HashSet<Guid> { Family }, new HashSet<Guid> { Family }, ["one", "two", "three"],
        [new LegendGovernedReasoningFamilyConnection(Family, Family, false)]);
}

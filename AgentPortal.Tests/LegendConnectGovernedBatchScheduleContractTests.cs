using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Independent engine contracts for homogeneous, interchangeable work units.
/// Inputs are governed-frame test doubles, not natural-language evidence.
/// Aggregate batching does not prove named-site order, hazardous-site priority,
/// contact uniqueness, or assignment of concrete records.
/// </summary>
public sealed class LegendConnectGovernedBatchScheduleContractTests
{
    private static readonly Guid Family = Guid.Parse("2929c1a8-4dcf-4a16-bcbf-e83b17f92fbc");

    [Theory]
    [InlineData(53, 8, 17, 3, 1, 100, 7, 51, 5)]
    [InlineData(47, 9, 13, 5, 2, 100, 6, 39, 2)]
    [InlineData(72, 8, 11, 2, 1, 100, 9, 55, 8)]
    [InlineData(5, 11, 19, 1, 1, 100, 1, 19, 5)]
    public void BatchScheduler_GeneratesWorkConservingNonoverlappingCertificates(
        int workload, int capacity, int duration, int resources, int requiredResources,
        int deadline, int expectedBatches, int expectedElapsed, int expectedFinalSize)
    {
        var input = Inputs(workload, capacity, duration, resources, requiredResources, deadline);
        var before = input.ToArray();
        var rule = BatchRule();

        var execution = LegendConnectGovernedReasoningExecutor.Derive(input, [rule], [Family]);

        Assert.False(execution.BudgetExceeded);
        var proof = Assert.Single(execution.DerivedStates);
        Assert.Equal(Text(expectedBatches), proof.Values["batch_count"]);
        Assert.Equal(Text(expectedElapsed), proof.Values["elapsed"]);
        Assert.Equal(Text(expectedFinalSize), proof.Values["last_batch_size"]);
        Assert.Equal("feasible", proof.Values["status"]);
        Assert.Matches("^[0-9a-f]{64}$", proof.Values["certificate"]);
        var evidence = Assert.Single(proof.EvidenceLineage);
        Assert.NotNull(evidence.ScheduleSteps);
        var steps = evidence.ScheduleSteps!;
        Assert.Equal(expectedBatches, steps.Count);
        Assert.Equal((long)workload, steps.Sum(step => step.WorkUnits));
        Assert.Equal(Enumerable.Range(1, expectedBatches), steps.Select(step => step.BatchNumber));
        Assert.Equal(expectedFinalSize, steps[^1].WorkUnits);
        Assert.Equal(expectedElapsed, steps.Max(step => step.EndMinute));
        var lanes = resources / requiredResources;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            Assert.Equal(index / lanes * duration, step.StartMinute);
            Assert.Equal(step.StartMinute + duration, step.EndMinute);
            Assert.Equal(requiredResources, step.ResourceUnitCount);
            Assert.InRange(step.FirstResourceUnit, 1, resources);
            Assert.InRange(step.FirstResourceUnit + step.ResourceUnitCount - 1, 1, resources);
            Assert.InRange(step.WorkUnits, 1L, capacity);
            foreach (var other in steps.Take(index))
            {
                var overlapInTime = step.StartMinute < other.EndMinute && other.StartMinute < step.EndMinute;
                var overlapInResources = step.FirstResourceUnit < other.FirstResourceUnit + other.ResourceUnitCount &&
                    other.FirstResourceUnit < step.FirstResourceUnit + step.ResourceUnitCount;
                Assert.False(overlapInTime && overlapInResources, "Two batches may not reserve the same resource at the same time.");
            }
        }
        Assert.Equal(before, input.ToArray());
        Assert.Equal(rule.TransitionSignature, Assert.Single(proof.TransitionPath));
        Assert.Equal(rule.IndependentEvidenceIdentities, evidence.IndependentEvidenceIdentities);
        Assert.Equal(3, proof.EvidenceCount);
        Assert.Equal(2, proof.EvidenceStandard);
        Assert.All(rule.ResultFrame.Values, value => Assert.StartsWith("$schedule_", value));
    }

    [Fact]
    public void TimeExceededCertificate_CannotSatisfyAFeasibleScheduleTransition()
    {
        var rule = BatchRule();
        var acceptFeasible = Rule("accept-feasible", "reasoning.forward.accept-feasible",
            Frame(("status", "feasible")), Frame(("decision", "ready")));

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(53, 8, 17, 3, 1, 40), [rule, acceptFeasible], [Family]);

        var proposed = Assert.Single(execution.DerivedStates);
        Assert.Equal("time_limit_exceeded", proposed.Values["status"]);
        Assert.Equal("51", proposed.Values["elapsed"]);
        Assert.NotNull(Assert.Single(proposed.EvidenceLineage).ScheduleSteps);
        Assert.DoesNotContain(execution.DerivedStates, state => state.Values.ContainsKey("decision"));
    }

    [Fact]
    public void BatchCertificate_IsDeterministicAndBindsTheActualWorkload()
    {
        var rule = BatchRule();
        var firstInput = Inputs(53, 8, 17, 3, 1, 100);
        var first = Assert.Single(LegendConnectGovernedReasoningExecutor.Derive(firstInput, [rule], [Family]).DerivedStates);
        var reorderedInput = firstInput.Reverse().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var repeated = Assert.Single(LegendConnectGovernedReasoningExecutor.Derive(reorderedInput, [rule], [Family]).DerivedStates);
        var changed = Assert.Single(LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(54, 8, 17, 3, 1, 100), [rule], [Family]).DerivedStates);

        Assert.Equal(first.Values["certificate"], repeated.Values["certificate"]);
        Assert.Equal(first.EvidenceLineage[0].ScheduleSteps, repeated.EvidenceLineage[0].ScheduleSteps);
        Assert.NotEqual(first.Values["certificate"], changed.Values["certificate"]);
        Assert.Equal("5", first.Values["last_batch_size"]);
        Assert.Equal("6", changed.Values["last_batch_size"]);
    }

    [Theory]
    [InlineData(0, 8, 17, 3, 1, 100)]
    [InlineData(53, 0, 17, 3, 1, 100)]
    [InlineData(53, 8, 0, 3, 1, 100)]
    [InlineData(53, 8, 17, 3, 4, 100)]
    [InlineData(53, 8, 17, 0, 1, 100)]
    [InlineData(53, 8, 17, 3, 1, 0)]
    [InlineData(513, 1, 1, 1, 1, 1440)]
    public void InvalidInputsOrExcessiveWork_CannotProduceAFeasibleCertificate(
        int workload, int capacity, int duration, int resources, int requiredResources, int deadline)
    {
        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(workload, capacity, duration, resources, requiredResources, deadline), [BatchRule()], [Family]);

        Assert.Empty(execution.DerivedStates);
    }

    [Fact]
    public void MaximumBoundedBatchCount_StillHasACompleteCertificate()
    {
        var proof = Assert.Single(LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(512, 1, 1, 1, 1, 1440), [BatchRule()], [Family]).DerivedStates);

        Assert.Equal("512", proof.Values["batch_count"]);
        Assert.Equal("512", proof.Values["elapsed"]);
        Assert.Equal(512, Assert.Single(proof.EvidenceLineage).ScheduleSteps!.Count);
    }

    [Fact]
    public void HomogeneousBatches_DoNotDischargeUnprovenPriorityOrUniqueContactObligations()
    {
        var readyForNamedSites = Rule("named-site-readiness", "reasoning.deduction.conditional.named-site-readiness",
            Frame(("status", "feasible"), ("hazardous_sites_first", "proven"), ("unique_site_contacts", "proven")),
            Frame(("site_plan", "ready")));

        var execution = LegendConnectGovernedReasoningExecutor.Derive(
            Inputs(47, 9, 13, 5, 2, 100), [BatchRule(), readyForNamedSites], [Family]);

        var batch = Assert.Single(execution.DerivedStates);
        Assert.Equal("feasible", batch.Values["status"]);
        Assert.DoesNotContain("hazardous_sites_first", batch.Values.Keys);
        Assert.DoesNotContain("unique_site_contacts", batch.Values.Keys);
        Assert.DoesNotContain("site_plan", batch.Values.Keys);
        // Named-site readiness remains unsupported despite valid aggregate
        // capacity arithmetic; no list of task identities entered this proof.
        Assert.Equal("batch-schedule", Assert.Single(batch.TransitionPath));
    }

    private static Dictionary<string, string> Inputs(
        int workload, int capacity, int duration, int resources, int requiredResources, int deadline) =>
        Frame(("workload", Text(workload)), ("capacity", Text(capacity)), ("duration", Text(duration)),
            ("available_resources", Text(resources)), ("required_resources", Text(requiredResources)), ("deadline", Text(deadline)));

    private static LegendGovernedReasoningRule BatchRule() =>
        Rule("batch-schedule", "reasoning.constrained-planning.batch.independent-contract",
            Frame(("workload", "$schedule_workload"), ("capacity", "$schedule_batch_capacity"),
                ("duration", "$schedule_batch_duration"), ("available_resources", "$schedule_available_resources"),
                ("required_resources", "$schedule_required_resources"), ("deadline", "$schedule_time_limit")),
            Frame(("batch_count", "$schedule_batch_count"), ("elapsed", "$schedule_elapsed"),
                ("last_batch_size", "$schedule_final_batch_size"), ("status", "$schedule_status"),
                ("certificate", "$schedule_signature")));

    private static LegendGovernedReasoningRule Rule(
        string signature, string identity, IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> result) =>
        new(signature, identity, source, result, 3, 2,
            new HashSet<Guid> { Family }, new HashSet<Guid> { Family },
            [signature + ":first-source", signature + ":second-source", signature + ":third-source"],
            [new(Family, Family, false)]);

    private static Dictionary<string, string> Frame(params (string Dimension, string Value)[] values) =>
        values.ToDictionary(value => value.Dimension, value => value.Value, StringComparer.Ordinal);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

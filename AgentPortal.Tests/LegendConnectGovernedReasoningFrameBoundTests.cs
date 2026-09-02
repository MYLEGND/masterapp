using System;
using System.Collections.Generic;
using System.Linq;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectGovernedReasoningFrameBoundTests
{
    [Fact]
    public void Executor_AcceptsBoundedReasoningFramesWithStructuralGraphCoordinates()
    {
        var family = Guid.NewGuid();
        var causalSource = Dimensions(
            ("first_hypothesis", "sensor_fault"),
            ("second_hypothesis", "network_fault"),
            ("first_prediction", "local_alarm"),
            ("second_prediction", "remote_alarm"),
            ("first_prediction_hypothesis", "sensor_fault"),
            ("second_prediction_hypothesis", "network_fault"),
            ("first_prediction_evidence", "isolation_check"),
            ("second_prediction_evidence", "isolation_check"),
            ("discriminating_evidence", "isolation_check"),
            ("rel_causal_1", "present"),
            ("rel_causal_2", "present"),
            ("rel_causal_3", "present"),
            ("rel_causal_4", "present"),
            ("rel_causal_5", "present"));
        var causalResult = Dimensions(
            ("hypothesis_status", "competing"),
            ("prediction_status", "differing"),
            ("diagnostic_plan_status", "discriminating_evidence_selected"),
            ("premature_attribution_status", "withheld"),
            ("selected_discriminating_evidence", "isolation_check"));
        var causal = Rule(
            family,
            "reasoning.causal-diagnostic.plan.frame-bound",
            causalSource,
            causalResult);

        var causalExecution = LegendConnectGovernedReasoningExecutor.Derive(
            causalSource,
            [causal],
            [family]);

        Assert.Single(causalExecution.DerivedStates);

        var planningSource = Dimensions(
            ("plan_goal", "publish_report"),
            ("current_plan_action", "plan_start"),
            ("candidate_plan_action", "review_totals"),
            ("action_prerequisite", "plan_start"),
            ("current_action_order", "0"),
            ("candidate_action_order", "1"),
            ("action_duration_minutes", "10"),
            ("plan_time_limit_minutes", "30"),
            ("plan_elapsed_minutes", "0"),
            ("available_resource_units", "2"),
            ("required_resource_units", "1"),
            ("safety_constraint_status", "satisfied"),
            ("plan_status", "ready"),
            ("rel_planning_1", "present"),
            ("rel_planning_2", "present"),
            ("rel_planning_3", "present"),
            ("rel_planning_4", "present"),
            ("rel_planning_5", "present"),
            ("rel_planning_6", "present"));
        var planningResult = Dimensions(
            ("current_plan_action", "review_totals"),
            ("current_action_order", "1"),
            ("plan_elapsed_minutes", "10"),
            ("plan_status", "completed"));
        var planning = Rule(
            family,
            "reasoning.constrained-planning.step.frame-bound",
            planningSource,
            planningResult);

        var planningExecution = LegendConnectGovernedReasoningExecutor.Derive(
            planningSource,
            [planning],
            [family]);

        Assert.Single(planningExecution.DerivedStates);
    }

    private static LegendGovernedReasoningRule Rule(
        Guid family,
        string operation,
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> result) =>
        new(
            "frame-bound-transition",
            operation,
            source,
            result,
            3,
            2,
            new HashSet<Guid> { family },
            new HashSet<Guid> { family },
            ["evidence-one", "evidence-two", "evidence-three"],
            [new LegendGovernedReasoningFamilyConnection(family, family, false)]);

    private static Dictionary<string, string> Dimensions(
        params (string Key, string Value)[] values) =>
        values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}

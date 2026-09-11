using System.Collections.Generic;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderProgressEvidenceTests
{
    [Fact]
    public void FailedScope_ReplacesItsSuccessAndSurvivesIndependentCompletion()
    {
        var observations = new Dictionary<string, LegendFounderAiProgressEvent>();
        var scope = LegendFounderAiConversationService.ReadScopeIdentity("legend_operational_diagnostics", "{}");
        var equivalent = LegendFounderAiConversationService.ReadScopeIdentity(
            "legend_operational_diagnostics", "{\"section\":null,\"language\":null}");
        LegendFounderAiController.RecordWorkObservation(observations,
            new("tool_complete", "Read available", Tool: "legend_operational_diagnostics", ScopeIdentity: scope));
        LegendFounderAiController.RecordWorkObservation(observations,
            new("tool_unavailable", "Read unavailable", Tool: "legend_operational_diagnostics", ScopeIdentity: equivalent));
        LegendFounderAiController.RecordWorkObservation(observations,
            new("tool_complete", "Other scope available", Tool: "legend_operational_diagnostics", ScopeIdentity: "independent"));
        Assert.Equal(2, observations.Count);
        Assert.Equal("tool_unavailable", observations[$"legend_operational_diagnostics:{scope}"].Stage);
        Assert.Equal("tool_complete", observations["legend_operational_diagnostics:independent"].Stage);
    }

    [Fact]
    public void StartedOrAcceptedWork_IsNotAnObservedCompletion()
    {
        var observations = new Dictionary<string, LegendFounderAiProgressEvent>();
        LegendFounderAiController.RecordWorkObservation(observations, new("accepted", "Accepted"));
        LegendFounderAiController.RecordWorkObservation(observations,
            new("tool_start", "Starting", Tool: "legend_operational_diagnostics", ScopeIdentity: "scope"));
        Assert.Empty(observations);
    }
}

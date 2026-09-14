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
        LegendFounderAiConversationService.RecordWorkObservation(observations,
            new("tool_complete", "Read available", Tool: "legend_operational_diagnostics", ScopeIdentity: scope));
        LegendFounderAiConversationService.RecordWorkObservation(observations,
            new("tool_unavailable", "Read unavailable", Tool: "legend_operational_diagnostics", ScopeIdentity: equivalent));
        LegendFounderAiConversationService.RecordWorkObservation(observations,
            new("tool_complete", "Other scope available", Tool: "legend_operational_diagnostics", ScopeIdentity: "independent"));
        Assert.Equal(2, observations.Count);
        Assert.Equal("tool_unavailable", observations[scope].Stage);
        Assert.Equal("tool_complete", observations["independent"].Stage);
    }

    [Fact]
    public void StartedOrAcceptedWork_IsNotAnObservedCompletion()
    {
        var observations = new Dictionary<string, LegendFounderAiProgressEvent>();
        LegendFounderAiConversationService.RecordWorkObservation(observations, new("accepted", "Accepted"));
        LegendFounderAiConversationService.RecordWorkObservation(observations,
            new("tool_start", "Starting", Tool: "legend_operational_diagnostics", ScopeIdentity: "scope"));
        Assert.Empty(observations);
    }
}

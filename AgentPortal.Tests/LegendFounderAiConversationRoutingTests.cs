using System;
using System.Collections.Generic;
using System.Reflection;
using AgentPortal.Services;
using Domain.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiConversationRoutingTests
{
    [Theory]
    [InlineData("Hi")]
    [InlineData("Hello")]
    [InlineData("Hello legend")]
    [InlineData("Hello Legend® Ai")]
    [InlineData("Hi Legend")]
    [InlineData("Legend, hello")]
    [InlineData("How are you?")]
    [InlineData("Why are you being so slow?")]
    public void CasualConversation_DoesNotRequireGovernedInspection(string text)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", text)];
        Assert.False(Assert.IsType<bool>(method!.Invoke(null, new object[] { conversation, "legend" })));
    }

    [Theory]
    [InlineData("What does LEGEND currently know about Haitian Creole?")]
    [InlineData("Legend, what is the current system state?")]
    [InlineData("How many canonical entries do we have?")]
    [InlineData("Train LEGEND on this curriculum.")]
    [InlineData("What is our current model readiness?")]
    [InlineData("Search retained knowledge for discourse markers.")]
    public void SystemKnowledgeAndTrainingRequests_RequireGovernedInspection(string text)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", text)];
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, new object[] { conversation, "legend" })));
    }

    [Fact]
    public void TeacherMode_AlwaysKeepsGovernedInspectionAvailable()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "Hi")];
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, new object[] { conversation, "teacher" })));
    }

    [Fact]
    public void NativeFailureResponse_ExposesGovernedReasonAndProviderFailureDetail()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("NativeInferenceUnavailableResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var snapshot = new LegendConnectNativeInferenceSnapshot(
            false,
            0.42m,
            null,
            "semantic_transition_not_production_eligible",
            9,
            "The matching transition exists but has not crossed the production eligibility gate.",
            true);

        var response = Assert.IsType<LegendFounderAiChatResponse>(method!.Invoke(
            null,
            new object?[]
            {
                "legend",
                snapshot,
                null,
                "provider_http_429",
                "Provider reported no remaining credits."
            }));

        Assert.True(response.Succeeded);
        Assert.Contains("semantic_transition_not_production_eligible", response.Message);
        Assert.Contains("production eligibility gate", response.Message);
        Assert.Contains("EvidenceCount=9", response.Message);
        Assert.Contains("provider_http_429", response.Message);
        Assert.Contains("no remaining credits", response.Message);
        Assert.DoesNotContain("does not yet have enough governed evidence", response.Message);
    }

}

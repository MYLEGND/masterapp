using System;
using System.Collections.Generic;
using System.Reflection;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiConversationRoutingTests
{
    [Theory]
    [InlineData("Hi")]
    [InlineData("Hello")]
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
}

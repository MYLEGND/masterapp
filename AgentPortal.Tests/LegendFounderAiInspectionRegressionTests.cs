using Domain.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AgentPortal.Services;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendFounderAiInspectionRegressionTests
{
    /// <summary>
    /// The governed meaning-graph result for a request whose admitted relations
    /// established an owned-record state inspection. RequiresGovernedInspection
    /// now consumes this typed classification instead of matching request text,
    /// so a row that must require inspection has to supply the classification
    /// the production analysis would have produced for it.
    /// </summary>
    private static readonly LegendConnectOwnedRecordClassification
        EstablishedOwnedRecordIntent = new(
            LegendConnectOwnedRecordIntent.OwnedRecordStateInspection,
            true,
            null);

    [Theory]
    [InlineData("Hi")]
    [InlineData("Tell me a joke.")]
    [InlineData("Explain how transformers work.")]
    [InlineData("Explain in two sentences why independent evidence matters when resolving contradictory claims.")]
    [InlineData("Help me think through a product idea.")]
    public void CasualTeacherConversation_DoesNotForceLegendInspection(string text)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", text)];

        Assert.False(Assert.IsType<bool>(
            method!.Invoke(null, new object?[] { conversation, "teacher", null })));
    }

    [Theory]
    [InlineData("What is LEGEND's current system status?")]
    [InlineData("How many canonical entries does LEGEND have right now?")]
    [InlineData("What changed in production deployment?")]
    [InlineData("Inspect the current GitHub repository state.")]
    [InlineData("What is our current provider capacity?")]
    public void LiveLegendFacts_RequireGovernedInspectionInTeacherMode(string text)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", text)];

        Assert.True(Assert.IsType<bool>(method!.Invoke(
            null,
            new object?[] { conversation, "teacher", EstablishedOwnedRecordIntent })));
    }

    [Fact]
    public void CasualInstructions_PreserveResponderIdentity()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("BuildCasualInstructions", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var teacher = Assert.IsType<string>(method!.Invoke(null, new object[] { "teacher" }));
        var legend = Assert.IsType<string>(method.Invoke(null, new object[] { "legend" }));

        Assert.Contains("external OpenAI Teacher", teacher, StringComparison.Ordinal);
        Assert.Contains("Native LEGEND conversational inference is bypassed", teacher, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Legend® Ai speaking", teacher, StringComparison.Ordinal);

        Assert.Contains("You are Legend® Ai speaking", legend, StringComparison.Ordinal);
        Assert.DoesNotContain("external OpenAI Teacher", legend, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "auto")]
    [InlineData(true, true, "required")]
    public void ProviderToolChoice_RequiresAToolWhenGovernedInspectionIsOutstanding(
        bool allowTools,
        bool requireToolCall,
        string expected)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("ResolveToolChoice", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<string>(
            method!.Invoke(null, new object[] { allowTools, requireToolCall })));
    }

    [Fact]
    public void CasualProviderReasoning_UsesPortableLowEffort()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("ResolveReasoningEffortForRound", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("low", Assert.IsType<string>(
            method!.Invoke(null, new object[] { 0, false, "medium" })));
    }

    [Fact]
    public void MobileChat_DoesNotActivateChromeHidingReadingState()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "legend-founder-ai.js"));

        Assert.DoesNotContain("classList.toggle('is-reading'", script, StringComparison.Ordinal);
    }
}

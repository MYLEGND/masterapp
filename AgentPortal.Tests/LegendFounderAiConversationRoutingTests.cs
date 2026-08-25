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

    [Theory]
    [InlineData("legend", true)]
    [InlineData("teacher", false)]
    public void ConversationMode_ExplicitlyControlsNativeInference(string mode, bool expected)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("ShouldAttemptNativeInference", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, Assert.IsType<bool>(method!.Invoke(null, new object[] { mode })));
    }

    [Fact]
    public void OpenAiTeacherInstructions_DeclareDirectRoleAndNativeBypass()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("BuildInstructions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var instructions = Assert.IsType<string>(method!.Invoke(null, new object[] { "teacher" }));
        Assert.Contains("external OpenAI Teacher speaking directly with the Founder", instructions);
        Assert.Contains("Native LEGEND conversational inference is bypassed in this mode", instructions);
        Assert.Contains("existing governed tools", instructions);
        Assert.Contains("execute that tool rather than merely describing", instructions);
        Assert.Contains("explicit Founder instruction and request-level Founder confirmation", instructions);
        Assert.DoesNotContain("may autonomously retain", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CasualNativeEscalation_EntersGovernedDiagnosticTeacherPath()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresProviderGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "Hi")];
        var snapshot = new LegendConnectNativeInferenceSnapshot(
            false, 0m, null, "ambiguous_composed_meaning", 0,
            "No unique governed semantic transition could be selected.", true);
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, new object?[] { conversation, "legend", snapshot, null })));
    }

    [Fact]
    public void CasualNativeSuccess_DoesNotEnterProviderInspectionPath()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresProviderGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "How are you?")];
        var snapshot = new LegendConnectNativeInferenceSnapshot(
            true, 1m, "I'm doing great, thanks.", "supported", 4,
            "Governed native response selected.", false);
        Assert.False(Assert.IsType<bool>(method!.Invoke(null, new object?[] { conversation, "legend", snapshot, null })));
    }

    [Fact]
    public void NativeGapContext_RequiresEvidenceFirstRetentionWithoutSelfPromotion()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("BuildNativeDiagnosticTeachingContext", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var snapshot = new LegendConnectNativeInferenceSnapshot(
            false, 0m, null, "meaning_graph_component_unknown", 0,
            "A required meaning component was unknown.", true);
        var context = Assert.IsType<string>(method!.Invoke(null, new object?[] { snapshot, null }));
        Assert.Contains("LEGEND_NATIVE_GAP_CONTEXT", context);
        Assert.Contains("meaning_graph_component_unknown", context);
        Assert.Contains("legend_search_retained_knowledge", context);
        Assert.Contains("legend_submit_machine_learning_candidate", context);
        Assert.Contains("MachineProposed", context);
        Assert.Contains("independent critic", context);
        Assert.Contains("request explicit Founder confirmation", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("instead of inventing", context, StringComparison.OrdinalIgnoreCase);
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

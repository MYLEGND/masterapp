using System;
using System.Collections.Generic;
using System.Reflection;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
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
    public void TeacherMode_DoesNotForceGovernedInspectionForCasualConversation()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequiresGovernedInspection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", "Hi")];
        Assert.False(Assert.IsType<bool>(method!.Invoke(null, new object[] { conversation, "teacher" })));
    }

    [Theory]
    [InlineData("Train LEGEND on this exact reusable distinction.", true)]
    [InlineData("Teach Legend using the governed curriculum.", true)]
    [InlineData("Inspect LEGEND training status without changing anything.", false)]
    [InlineData("Hello Legend.", false)]
    public void FounderLearningMutationIntent_IsExplicitAndNatural(
        string text,
        bool expected)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("RequestsFounderLearningMutation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation = [new("user", text)];
        Assert.Equal(
            expected,
            Assert.IsType<bool>(method!.Invoke(null, new object[] { conversation })));
    }

    [Theory]
    [InlineData("meaning_graph_component_unknown", true)]
    [InlineData("meaning_graph_relation_unproven", true)]
    [InlineData("semantic_transition_not_supported", true)]
    [InlineData("ambiguous_composed_meaning", false)]
    [InlineData("contradicted_semantic_transition", false)]
    public void NativeEscalation_AllowsMissingKnowledgeButNotAmbiguityOrContradiction(
        string reason,
        bool expected)
    {
        var method = typeof(LegendConnectOperations)
            .GetMethod("CanEscalateFromUnavailableComposedSource", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var inference = new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.InsufficientEvidence,
            null,
            0,
            [reason]);

        Assert.Equal(
            expected,
            Assert.IsType<bool>(method!.Invoke(null, new object[] { inference })));
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
        Assert.Contains("must execute the matching existing governed training tool", instructions);
        Assert.Contains("legend_submit_founder_curriculum", instructions);
        Assert.Contains("legend_submit_machine_learning_candidate", instructions);
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
        Assert.Contains("explicit instruction and request-level confirmation", context);
        Assert.DoesNotContain("automatically", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("instead of inventing", context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeGapHasNoAutomaticLearningAuthorizationPath()
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod("CanAutomaticallyRetainNativeGapProposal", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(method);
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

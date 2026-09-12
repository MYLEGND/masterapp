using System;
using System.Linq;
using AgentPortal.Services;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using Domain.Messaging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    [Fact]
    public async Task SupportedApprovedKnowledge_ReachesLocalFoundationAsEvidenceWithoutReplacingItsReply()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        const string approvedEvidence = "The approved Fjord pilot review interval is twenty-three days.";
        operations.Setup(item => item.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(),
                "en", It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(true, 1m, approvedEvidence,
                "semantic_transition_governed_composed", 2, "Approved curriculum evidence.", false,
                "HigherStandard", "OriginalComposition"));
        var handler = new FounderAiScenarioHandler(ProviderText("The existing foundation executor produced this articulation."));
        var response = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("legend", "Explain the approved Fjord pilot review interval."));
        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("LocalFoundation", response.ResponseAuthority);
        Assert.Equal("The existing foundation executor produced this articulation.", response.Message);
        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.DoesNotContain(approvedEvidence, payload.RootElement.GetProperty("instructions").GetString());
        var available = payload.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("legend_submit_founder_curriculum", available);
        Assert.DoesNotContain("legend_remember_conversation_facts", available);
        Assert.Contains("legend_search_retained_knowledge", available);

        var evidence = payload.RootElement.GetProperty("input").EnumerateArray().First();
        Assert.Equal("user", evidence.GetProperty("role").GetString());
        Assert.Contains(approvedEvidence, evidence.GetProperty("content").GetString());
        Assert.Contains("ApprovedLegendKnowledge", evidence.GetProperty("content").GetString());
    }

    [Fact]
    public async Task LocalFoundation_ExhaustedContinuationReturnsExplicitPartialAnswer()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(Enumerable.Range(0, 16)
            .Select(_ => ProviderIncompleteText("A partial explanation with an unfinished argument.")).ToArray());
        var reply = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("legend", "Explain the limitations of drawing conclusions from a small sample."));
        Assert.True(reply.Succeeded, Describe(reply));
        Assert.Equal("response_partial", reply.Stage);
        Assert.Equal("provider_output_incomplete", reply.Reason);
        Assert.Equal("LocalFoundation", reply.ResponseAuthority);
        Assert.False(reply.ExternalAnsweringUsed);
        Assert.False(reply.EscalationUsed);
    }

    [Fact]
    public async Task NativeOnly_LocalModelCannotAuthorizeItsOwnExternalEscalation()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_request_teacher_escalation", "{}"),
            ProviderText("External answering is blocked, so I cannot verify that fact here."));
        var reply = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            new LegendFounderAiChatRequest
            {
                Mode = "legend", NativeOnly = true, SourceLanguageCode = "en",
                Messages = [new LegendFounderAiChatMessage("user", "What is the latest verified count?")]
            });
        Assert.True(reply.Succeeded, Describe(reply));
        Assert.Equal("LocalFoundation", reply.ResponseAuthority);
        Assert.False(reply.ExternalAnsweringUsed);
        Assert.False(reply.EscalationUsed);
        Assert.DoesNotContain(operations.Invocations, call =>
            call.Method.Name is nameof(ILegendConnectOperations.ExecuteResearchAsync) or
                nameof(ILegendConnectOperations.RecordExternalEscalationDispositionAsync));
    }

    [Fact]
    public async Task SuccessfulMutation_InvalidatesPriorReadsWhileKeepingConsequentialDuplicateReceipt()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        operations.SetupSequence(operation => operation.SearchRetainedKnowledgeAsync(
                "authority", null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("before", 0, []))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("after", 1, []));
        operations.Setup(operation => operation.SubmitMachineTeachingProposalAsync(
                It.IsAny<LegendConnectMachineTeachingSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectMachineTeachingSubmissionResult(
                true, false, "AwaitingCritic", null, "Submitted for independent review.",
                Guid.NewGuid(), Guid.NewGuid()));

        var mutation = SameLanguageMachineProposalArguments();
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderTool("legend_submit_machine_learning_candidate", mutation),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderTool("legend_submit_machine_learning_candidate", mutation),
            ProviderText("The submitted candidate is awaiting independent review."));
        var reply = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("teacher", "Train LEGEND on this exact reusable distinction, then inspect the resulting state.",
                founderCommandConfirmed: true));

        Assert.True(reply.Succeeded, Describe(reply));
        Assert.Equal("AwaitingCritic", reply.LearningState);
        Assert.Contains("NonCanonical", reply.Message);
        Assert.Equal(5, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "authority", null, null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        operations.Verify(operation => operation.SubmitMachineTeachingProposalAsync(
            It.IsAny<LegendConnectMachineTeachingSubmission>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

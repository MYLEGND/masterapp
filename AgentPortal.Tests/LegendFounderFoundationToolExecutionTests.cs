using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
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

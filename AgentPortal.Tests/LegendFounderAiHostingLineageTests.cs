using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    [Theory]
    [InlineData("LegendControlled", "LocalFoundation", false)]
    [InlineData("ExternalHosted", "HostedFoundation", true)]
    [InlineData(null, null, false)]
    [InlineData("UnverifiedProxy", null, false)]
    public async Task AppliedGovernedModel_UsesVerifiedHostingReceiptWithoutAnotherInference(
        string? hosting, string? expectedAuthority, bool expectedExternal)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        const string prompt = "Report the current open issue count from its approved record.";
        var readRequest = ReadOnlyContentRequest();
        operations.Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                prompt, It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(), "en",
                It.Is<LegendConnectExternalProviderPolicy?>(policy => policy == LegendConnectExternalProviderPolicy.NativeOnly)))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(false, 0m, null,
                "read_only_content_binding_required", 3, "An authorized read is required.", false,
                ReadOnlyContentRequest: readRequest));
        operations.Setup(operation => operation.GetTranslationQualityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectTranslationQualitySnapshot(4458, 2999, 300, 12, 8796, []));
        var runId = Guid.NewGuid();
        operations.Setup(operation => operation.TryInferConversationWithReadOnlyContentAsync(
                prompt, It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<LegendConnectReadOnlyContentBindingReceipt>(),
                It.IsAny<CancellationToken>(), "en",
                It.Is<LegendConnectExternalProviderPolicy?>(policy => policy == LegendConnectExternalProviderPolicy.NativeOnly)))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(true, 1m, "Open issues 4,458.",
                "semantic_transition_governed_composed", 4, "Authorized evidence with a verified model receipt.",
                false, "HigherStandard", "EvaluatedPromotedModelRealization",
                ReadOnlyContentRequest: readRequest,
                ModelAssistance: new LegendConnectNativeModelAssistanceSnapshot("Applied",
                    "active_reasoning_model_candidate_governed", "governed.reasoning", "opaque-model-identity",
                    runId, LegendConnectNativeModelAssistanceContracts.Provenance, 0, Hosting: hosting)));
        var handler = new FounderAiScenarioHandler();
        var response = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("legend", prompt, nativeOnly: !expectedExternal));

        Assert.Equal(0, handler.RequestCount);
        if (expectedAuthority is null)
        {
            Assert.False(response.Succeeded);
            Assert.Equal("model_assistance_hosting_unverified", response.Reason);
            return;
        }
        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(expectedAuthority, response.ResponseAuthority);
        Assert.Equal(hosting, response.FoundationHosting);
        Assert.Equal(expectedExternal, response.ExternalAnsweringUsed);
        Assert.Equal(false, response.EscalationUsed);
        Assert.Equal("opaque-model-identity", response.FoundationModel);
        Assert.Equal(runId, response.ModelTrainingRunId);
        Assert.Equal("Open issues 4,458.", response.Message);
    }
}

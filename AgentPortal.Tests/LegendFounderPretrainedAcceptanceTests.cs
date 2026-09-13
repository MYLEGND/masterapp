using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    // Held-out orchestration acceptance: the external boundary is scripted,
    // so these cases prove policy/context routing, never model answer quality.
    [Theory]
    [InlineData("meaning_graph_component_unknown", "Help me turn a vague weekend idea into three practical next steps.")]
    [InlineData("semantic_transition_unavailable", "Explain why an analogy can clarify a difficult idea, then give its limitations.")]
    [InlineData("curriculum_processing", "Rewrite this sentence more clearly: progress depends on the order of the work.")]
    public async Task HeldOutFoundation_CurriculumInsufficiencyDoesNotBlockConversation(
        string curriculumReason,
        string prompt)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        operations.Setup(item => item.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(),
                "en", It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, curriculumReason, 0, "No approved example supports this request.", false));
        var handler = new FounderAiScenarioHandler(ProviderText("Scripted foundation boundary completion."));

        var result = await CreateService(db, operations.Object, handler).ReplyAsync(
            founder, Request("legend", prompt));

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal("LocalFoundation", result.ResponseAuthority);
        Assert.Equal("Scripted foundation boundary completion.", result.Message);
        Assert.Equal(1, handler.RequestCount);
        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(1024, payload.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("fixture-local-model", payload.RootElement.GetProperty("model").GetString());
        Assert.DoesNotContain(operations.Invocations, invocation =>
            invocation.Method.Name is nameof(ILegendConnectOperations.ExecuteResearchAsync) ||
            invocation.Method.Name.StartsWith("Submit", StringComparison.Ordinal) ||
            invocation.Method.Name.Contains("Promot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HeldOutFoundation_MultiTurnReferencePreservesTranscriptWithoutCurriculumMatch()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        operations.Setup(item => item.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(),
                "en", It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, "meaning_graph_component_unknown", 0, "No matching approved semantic evidence.", false));
        var handler = new FounderAiScenarioHandler(ProviderText("The earlier option has the smaller scope."));
        var conversationId = Guid.NewGuid();
        var cursor = await ControllerTestHelpers.SeedFounderHistoryAsync(
            ControllerTestHelpers.BuildFounderHistoryScopes(db), FounderEnvironmentScope.FounderId, conversationId,
            [new("user", "Option cedar takes one afternoon; option quartz takes three weekends."),
             new("assistant", "I can help compare their scope.")]);
        Assert.NotNull(cursor);
        var request = new LegendFounderAiChatRequest
        {
            Mode = "legend",
            SourceLanguageCode = "en",
            ConversationId = conversationId.ToString("D"),
            ExpectedLastMessageId = cursor,
            // Prior content comes from the canonical store, never client echo.
            Messages = [new LegendFounderAiChatMessage("user", "Compare the earlier option with the later one.")]
        };

        var result = await CreateService(db, operations.Object, handler).ReplyAsync(founder, request);

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal("LocalFoundation", result.ResponseAuthority);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("Option cedar takes one afternoon", Assert.Single(handler.RequestBodies));
        Assert.Contains("option quartz takes three weekends", Assert.Single(handler.RequestBodies));
        Assert.Contains("I can help compare their scope.", Assert.Single(handler.RequestBodies));
        Assert.Equal(conversationId, result.ConversationId);
        Assert.NotNull(result.MessageId);
        Assert.NotEqual(cursor, result.MessageId);
        Assert.DoesNotContain(operations.Invocations, invocation =>
            invocation.Method.Name == nameof(ILegendConnectOperations.ExecuteResearchAsync));
    }

    [Fact]
    public async Task HeldOutFoundation_FailedOwnedRecordReadCannotBecomeAnUngroundedAnswer()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(item => item.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(),
                "en", It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false, 0m, null, "read_only_content_binding_required", 1,
                "This response requires an authenticated operational record.", false,
                ReadOnlyContentRequest: ReadOnlyContentRequest()));
        operations.Setup(item => item.GetTranslationQualityAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The authorized record store is unavailable."));
        var handler = new FounderAiScenarioHandler(ProviderText("An invented operational count is 900."));

        var result = await CreateService(db, operations.Object, handler).ReplyAsync(
            founder, Request("legend", "How many translations currently await our review?"));

        Assert.False(result.Succeeded);
        // The foundation may plan recovery, but its unsupported completion
        // cannot satisfy the authenticated receipt requirement.
        Assert.InRange(handler.RequestCount, 0, 1);
        Assert.DoesNotContain("900", result.Message ?? string.Empty);
        operations.Verify(item => item.GetTranslationQualityAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HeldOutFoundation_ResearchFailureSurvivesSubsequentProviderFailure()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var configuration = new ConfigurationBuilder().Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var researchAuthority = new LegendConnectOperations(db, registry,
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance), configuration);
        var decision = new LegendConnectResearchNeededDecision(true,
            LegendConnectResearchNeed.ExplicitVerificationRequest, "explicit_verification_requires_research",
            LegendConnectResearchAccessClass.PublicReadOnly, "en", false, false, false, null, DateTime.UtcNow);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        operations.Setup(item => item.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(), It.IsAny<CancellationToken>(),
                "en", It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(false, 0m, null,
                "external_evidence_needed", 0, "The claim requires external verification.", true,
                ResearchDecision: decision));
        operations.Setup(item => item.ExecuteResearchAsync(It.IsAny<LegendConnectResearchRequest>(),
                It.IsAny<CancellationToken>(), It.IsAny<LegendConnectExternalProviderPolicy?>()))
            .Returns((LegendConnectResearchRequest request, CancellationToken token, LegendConnectExternalProviderPolicy? policy) =>
                researchAuthority.ExecuteResearchAsync(request, token, policy));
        operations.Setup(item => item.RecordResearchObservabilityAsync(It.IsAny<LegendConnectResearchOutcome>(),
                It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var handler = new FounderAiScenarioHandler(Enumerable.Range(0, 4).Select(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"message\":\"Temporarily unavailable\"}}")
            }).ToArray());

        var result = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            Request("legend", "Verify the published sampling interval for this field study."));

        Assert.False(result.Succeeded);
        Assert.Equal("Failure", result.ResearchState);
        Assert.NotNull(result.ResearchOutcome);
        Assert.Equal("internet_research_transport_unavailable", result.ResearchOutcome!.Failure?.ReasonCode);
        Assert.InRange(handler.RequestCount, 1, 3);
        operations.Verify(item => item.ExecuteResearchAsync(It.IsAny<LegendConnectResearchRequest>(),
            It.IsAny<CancellationToken>(), It.IsAny<LegendConnectExternalProviderPolicy?>()), Times.Once);
    }
}

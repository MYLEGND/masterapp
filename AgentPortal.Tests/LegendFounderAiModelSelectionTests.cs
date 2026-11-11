using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Messaging;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    [Fact]
    public async Task ModelSelection_AutoAndManualUseOneCanonicalConversation()
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(ProviderText("First answer."), ProviderText("Second answer."));
        var service = CreateService(db, operations.Object, handler);
        var id = Guid.NewGuid().ToString("D");
        var first = await service.ReplyAsync(founder, new LegendFounderAiChatRequest {
            Mode = "legend", ModelSelection = "auto", ConversationId = id, SourceLanguageCode = "en",
            PagePath = "/WebsiteAnalytics", Messages = [new("user", "Explain this supplied scenario.")] });
        Assert.True(first.Succeeded, Describe(first));
        Assert.Equal("legend", first.Mode);
        Assert.Contains("/WebsiteAnalytics", handler.RequestBodies.First());
        var next = await service.ReplyAsync(founder, new LegendFounderAiChatRequest {
            Mode = "legend", ModelSelection = "openai:gpt-5", ConversationId = id, SourceLanguageCode = "en",
            ExpectedLastMessageId = first.MessageId, Messages = [new("user", "Continue this scenario.")] });
        Assert.True(next.Succeeded, Describe(next));
        Assert.Equal(first.ConversationId, next.ConversationId);
        Assert.Equal(0, NativeInferenceCalls(operations));
        Assert.Equal(2, handler.RequestCount);
        var history = await service.GetConversationPageAsync(founder, first.ConversationId!.Value, new(), CancellationToken.None);
        Assert.Equal(4, history.Conversation!.Messages.Count);
        Assert.Equal("openai:gpt-5", history.Conversation.Messages.Last().ResponseProvenance!.ModelSelection);
    }

    [Fact]
    public async Task ModelSelection_CompletedTurnReplaysAfterProviderRevocation()
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(ProviderText("Saved answer."));
        var service = CreateService(db, operations.Object, handler);
        var configuration = (Microsoft.Extensions.Configuration.IConfiguration)typeof(LegendFounderAiConversationService)
            .GetField("_configuration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
        var operation = Guid.NewGuid();
        var request = new LegendFounderAiChatRequest { Mode = "legend", ModelSelection = "auto",
            Messages = [new("user", "Explain this supplied scenario.")], SourceLanguageCode = "en" };
        var first = await service.ReplyAsync(founder, request, operationId: operation);
        Assert.True(first.Succeeded, Describe(first));
        configuration["OpenAI:ApiKey"] = "";
        configuration["OpenAI:LegendFounderAiModel"] = "unavailable-new-default";
        configuration["LegendConnect:Foundation:Enabled"] = "false";
        var replay = await service.ReplyAsync(founder, request, operationId: operation);
        Assert.True(replay.Succeeded, Describe(replay));
        Assert.Equal(first.MessageId, replay.MessageId);
        Assert.Equal(first.Message, replay.Message);
        Assert.Equal(1, handler.RequestCount);
        var changed = await service.ReplyAsync(founder, new LegendFounderAiChatRequest {
            Mode = "legend", ModelSelection = "openai:unavailable-new-default",
            Messages = request.Messages, SourceLanguageCode = "en"
        }, operationId: operation);
        Assert.False(changed.Succeeded);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DirectProvider_ReadFailureReturnsMissingEvidenceToSameProviderWithoutNativeGate()
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(x => x.SearchRetainedKnowledgeAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic read outage"));
        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\",\"source_language\":null,\"target_language\":null}"),
            ProviderText("The requested records are unavailable. I cannot verify their current state."));
        var response = await CreateService(db, operations.Object, handler).ReplyAsync(founder,
            new LegendFounderAiChatRequest { Mode = "legend", ModelSelection = "auto",
                Messages = [new("user", "Read the current records.")] });
        Assert.True(response.Succeeded, Describe(response));
        Assert.Contains("unavailable", response.Message);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("tool_read_failed", handler.RequestBodies.Last());
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Theory]
    [InlineData("openai:unapproved-model", false, null, "selected_model_unavailable")]
    [InlineData("openai:gpt-5", true, null, "selected_model_unavailable")]
    [InlineData("auto", false, "https://untrusted.invalid", "invalid_page_path")]
    [InlineData("auto", false, "/page?secret=example", "invalid_page_path")]
    public async Task ModelSelection_RejectsTamperingBeforeProviderDispatch(string selected, bool nativeOnly, string? page, string reason)
    {
        using var environment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);
        var response = await service.ReplyAsync(founder, new LegendFounderAiChatRequest {
            Mode = "legend", ModelSelection = selected, NativeOnly = nativeOnly, PagePath = page,
            Messages = [new("user", "Hello")] });
        Assert.False(response.Succeeded);
        Assert.Equal(reason, response.Reason);
        Assert.Equal(0, handler.RequestCount);
    }
}

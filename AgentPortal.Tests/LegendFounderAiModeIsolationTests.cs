using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Contract tests for the single Founder chat endpoint's responder boundary.
/// They deliberately use the real orchestration service with a recorded
/// ILegendConnectOperations adapter, rather than a second chat endpoint.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderAiModeIsolationTests
{
    [Fact]
    public async Task TeacherMode_CallsOpenAiDirectlyAndNeverCallsNativeLegendInference()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderText("The OpenAI Teacher is responding directly."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Explain the current repair boundary."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("teacher", response.Mode);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider_response", response.Stage);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_ProgressResultStreamRemainsActiveBeyondFormerGatewayBoundary()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "bounded diagnostic",
                0,
                []));
        var handler = new FounderAiScenarioHandler(
            TimeSpan.FromSeconds(16),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"bounded diagnostic\"}"),
            ProviderText("The OpenAI Teacher completed the bounded request after sustained progress."));
        var service = CreateService(db, operations.Object, handler);
        var body = new MemoryStream();
        var context = ControllerContextFor(founder);
        context.HttpContext.Request.Headers.Accept = "application/x-ndjson";
        context.HttpContext.Response.Body = body;
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = context
        };

        var result = await controller.Chat(
            Request("teacher", "Complete this bounded diagnostic with the required governed inspection."),
            CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var transcript = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"type\":\"accepted\"", transcript, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"heartbeat\"", transcript, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"result\"", transcript, StringComparison.Ordinal);
        Assert.Contains("OpenAITeacher", transcript, StringComparison.Ordinal);
        Assert.Contains("provider_response", transcript, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "bounded diagnostic",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task LegendMode_ProgressResultStreamKeepsGovernedNativeFirstRequestActiveBeyondFormerGatewayBoundary()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationAsync(
                "Explain the governed gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "insufficient_evidence",
                0,
                "External escalation is permitted after governed native inference.",
                true));
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "governed gap",
                0,
                []));

        var handler = new FounderAiScenarioHandler(
            TimeSpan.FromSeconds(31),
            ProviderText("The OpenAI Teacher completed the permitted escalation after LEGEND’s governed first refusal."));
        var service = CreateService(db, operations.Object, handler);
        var body = new MemoryStream();
        var context = ControllerContextFor(founder);
        context.HttpContext.Request.Headers.Accept = "application/x-ndjson";
        context.HttpContext.Response.Body = body;
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = context
        };

        var result = await controller.Chat(
            Request("legend", "Explain the governed gap."),
            CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        var transcript = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("\"type\":\"heartbeat\"", transcript, StringComparison.Ordinal);
        Assert.Contains("OpenAITeacher", transcript, StringComparison.Ordinal);
        Assert.Contains("provider_response", transcript, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_GovernedReadToolMayExecuteWithoutChangingResponderIdentity()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "authority",
                0,
                []));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderText("The governed inspection completed; I remain the OpenAI Teacher responder."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("teacher", "Inspect the current authority."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal(2, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "authority",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_GovernedToolReadFailureIsStructuredAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("read transport failed"));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderText("This answer is unsupported because its only governed read failed."));
        var service = CreateService(db, operations.Object, handler);
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = ControllerContextFor(founder)
        };

        var result = await controller.Chat(
            Request("teacher", "Inspect the current authority."),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var response = Assert.IsType<LegendFounderAiChatResponse>(objectResult.Value);
        Assert.Equal(502, objectResult.StatusCode);
        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal("required_governed_inspection_missing", response.Reason);
        Assert.Equal("governed_inspection", response.FailureKind);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_FailedGovernedReadContinuesToAWorkingGovernedRead()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .SetupSequence(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first read transport failed"))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "authority",
                1,
                []));

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"),
            ProviderText("The second governed read succeeded and supports this assessment."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("teacher", "Inspect the current authority."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(
            "The second governed read succeeded and supports this assessment.",
            response.Message);
        Assert.Equal(3, handler.RequestCount);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "authority",
            null,
            null,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_IncompleteProviderAnswersAreReturnedAsOneCompleteResponse()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderIncompleteText("Steps 1-4\nStep 5"),
            ProviderText("Step 5\nSteps 6-12"));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Give one complete clean answer."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal(
            "Steps 1-4\nStep 5\nSteps 6-12",
            response.Message);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_ProviderRejectionPreservesSafeProviderStatusAndReference()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var rejected = ProviderResponse(new
        {
            error = new { message = "safe test rejection" }
        }, HttpStatusCode.BadRequest);
        rejected.Headers.TryAddWithoutValidation("x-request-id", "provider-test-reference");
        var handler = new FounderAiScenarioHandler(rejected);
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Return a controlled provider failure."));

        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider", response.Stage);
        Assert.Equal("provider_http_400", response.Reason);
        Assert.Equal("provider_http", response.FailureKind);
        Assert.Equal(400, response.ProviderStatusCode);
        Assert.Equal("provider-test-reference", response.Reference);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task TeacherMode_GovernedToolCancellationIsStructuredTimeoutAndNeverBecomesHttp500()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var handler = new FounderAiScenarioHandler(
            ProviderTool("legend_search_retained_knowledge", "{\"query\":\"authority\"}"));
        var service = CreateService(db, operations.Object, handler);
        var controller = new LegendFounderAiController(
            service,
            new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance)
        {
            ControllerContext = ControllerContextFor(founder)
        };

        var result = await controller.Chat(
            Request("teacher", "Inspect the current authority."),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        var response = Assert.IsType<LegendFounderAiChatResponse>(objectResult.Value);
        Assert.Equal(504, objectResult.StatusCode);
        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal("tool_timeout", response.Reason);
        Assert.Equal("timeout", response.FailureKind);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("native")]
    public async Task MissingOrInvalidMode_FailsClosedWithoutLegendOrProvider(string? mode)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request(mode, "Hello."));

        Assert.False(response.Succeeded);
        Assert.Equal("invalid", response.Mode);
        Assert.Equal("NoResponder", response.ResponseAuthority);
        Assert.Equal("mode_validation", response.Stage);
        Assert.Equal("invalid_mode", response.Reason);
        Assert.Equal("validation", response.FailureKind);
        Assert.Equal(400, StatusFor(response));
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    [Fact]
    public async Task LegendMode_StillAttemptsNativeLegendFirstAndLabelsNativeResponseCorrectly()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationAsync(
                "Hello.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "Governed native reply.",
                "supported",
                1,
                "FounderApproved evidence",
                false));

        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Hello."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("legend", response.Mode);
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Equal("native_response", response.Stage);
        Assert.Equal("Governed native reply.", response.Message);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LegendMode_NativeOnlyReturnsNativeAnswerWithoutCallingOpenAi()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationAsync(
                "Answer directly.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                true,
                1m,
                "Native-only answer.",
                "supported",
                3,
                "FounderApproved evidence",
                false));

        var handler = new FounderAiScenarioHandler(
            ProviderText("This provider response must never be requested."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Answer directly.", nativeOnly: true));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Equal("native_response", response.Stage);
        Assert.Equal("Native-only answer.", response.Message);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LegendMode_NativeOnlyBlocksPermittedEscalationBeforeOpenAiIsCalled()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationAsync(
                "Explain the unsupported gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "insufficient_evidence",
                0,
                "External escalation would normally be permitted.",
                true));

        var handler = new FounderAiScenarioHandler(
            ProviderText("This provider response must never be requested."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Explain the unsupported gap.", nativeOnly: true));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("SystemDiagnostic", response.ResponseAuthority);
        Assert.Equal("native_only_blocked", response.Stage);
        Assert.Equal("insufficient_evidence", response.Reason);
        Assert.Contains("OpenAIEscalation=blocked", response.Message, StringComparison.Ordinal);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TeacherMode_NativeOnlyIsRejectedWithoutCallingNativeOrOpenAi()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler(
            ProviderText("This provider response must never be requested."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            ControllerTestHelpers.BuildUser(),
            Request("teacher", "Do not contact a provider.", nativeOnly: true));

        Assert.False(response.Succeeded);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("native_only_validation", response.Stage);
        Assert.Equal("native_only_requires_legend_mode", response.Reason);
        Assert.Equal(0, NativeInferenceCalls(operations));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ProviderResponseAfterLegendEscalation_IsLabeledOpenAiTeacherRatherThanLegendAi()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationAsync(
                "Explain the gap.",
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "insufficient_evidence",
                0,
                "Escalation permitted.",
                true));
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "Explain the gap.",
                0,
                []));

        var handler = new FounderAiScenarioHandler(
            ProviderText("The OpenAI Teacher is handling this governed escalation."));
        var service = CreateService(db, operations.Object, handler);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "Explain the gap."));

        Assert.True(response.Succeeded, Describe(response));
        Assert.Equal("legend", response.Mode);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal("provider_response", response.Stage);
        Assert.Equal(1, NativeInferenceCalls(operations));
        Assert.Equal(1, handler.RequestCount);
    }

    private static LegendFounderAiConversationService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        ILegendConnectOperations operations,
        FounderAiScenarioHandler handler) =>
        new(
            new FounderAiHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "test-only-key",
                    ["OpenAI:LegendFounderAiTimeoutSeconds"] = "45"
                })
                .Build(),
            new FounderLegendConnectService(
                operations,
                new AgentProfileAccessResolver(db)),
            NullLogger<LegendFounderAiConversationService>.Instance);

    private static LegendFounderAiChatRequest Request(
        string? mode,
        string prompt,
        bool nativeOnly = false) =>
        new()
        {
            Mode = mode,
            NativeOnly = nativeOnly,
            Messages = [new LegendFounderAiChatMessage("user", prompt)]
        };

    private static async Task<System.Security.Claims.ClaimsPrincipal> AddFounderProfileAsync(
        Infrastructure.Data.MasterAppDbContext db)
    {
        const string founderId = FounderEnvironmentScope.FounderId;
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = founderId,
            AgentUpn = "mode-isolation-founder@legend.test",
            NormalizedEmail = "mode-isolation-founder@legend.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        return ControllerTestHelpers.BuildUser(founderId);
    }

    private static ControllerContext ControllerContextFor(
        System.Security.Claims.ClaimsPrincipal user) =>
        new()
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = user
            }
        };

    private static int NativeInferenceCalls(
        Mock<ILegendConnectOperations> operations) =>
        operations.Invocations.Count(invocation =>
            invocation.Method.Name is
                nameof(ILegendConnectOperations.TryInferConversationAsync) or
                nameof(ILegendConnectOperations.TryInferConversationWithDiscourseAsync));

    private static int StatusFor(LegendFounderAiChatResponse response)
    {
        var mapper = typeof(LegendFounderAiController).GetMethod(
            "MapStatus",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mapper);
        return Assert.IsType<int>(mapper!.Invoke(null, [response]));
    }

    private static string Describe(LegendFounderAiChatResponse response) =>
        $"mode={response.Mode}; authority={response.ResponseAuthority}; stage={response.Stage}; reason={response.Reason}; error={response.Error}";

    private static HttpResponseMessage ProviderText(string text) =>
        ProviderResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new { type = "output_text", text }
                    }
                }
            }
        });

    private static HttpResponseMessage ProviderIncompleteText(string text) =>
        ProviderResponse(new
        {
            status = "incomplete",
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[]
                    {
                        new { type = "output_text", text }
                    }
                }
            }
        });

    private static HttpResponseMessage ProviderTool(
        string name,
        string arguments) =>
        ProviderResponse(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "function_call",
                    call_id = "tool-call-1",
                    name,
                    arguments
                }
            }
        });

    private static HttpResponseMessage ProviderResponse(
        object payload,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class FounderAiHttpClientFactory(
        FounderAiScenarioHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("OpenAI", name);
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://openai.test/")
            };
        }
    }

    private sealed class FounderAiScenarioHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        private readonly TimeSpan _responseDelay;

        public FounderAiScenarioHandler(params HttpResponseMessage[] responses)
            : this(TimeSpan.Zero, responses)
        {
        }

        public FounderAiScenarioHandler(
            TimeSpan responseDelay,
            params HttpResponseMessage[] responses)
        {
            _responseDelay = responseDelay;
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            if (_responses.Count == 0)
                throw new InvalidOperationException("No OpenAI response was queued for this test.");

            if (_responseDelay > TimeSpan.Zero)
                await Task.Delay(_responseDelay, cancellationToken);

            return _responses.Dequeue();
        }
    }

    private sealed class FounderEnvironmentScope : IDisposable
    {
        public const string FounderId = "11f6f9d9-0fe2-44c3-8cac-7d88d3fc3ac6";

        private readonly string? _previousFounderOid =
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        public FounderEnvironmentScope() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);

        public void Dispose() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", _previousFounderOid);
    }
}

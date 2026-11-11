using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Mobile;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Mobile;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

// Reuse the established real orchestration fixture and its recorded provider.
public sealed partial class LegendFounderAiModeIsolationTests
{
    [Fact]
    public async Task BusinessModelInspectionRejectsMalformedInputBeforeProviderAccess()
    {
        var controller = new LegendFounderAiController(null!, new LegendFounderAiProgressBroker(),
            NullLogger<LegendFounderAiController>.Instance);
        Assert.IsType<BadRequestResult>(await controller.BusinessModelAccess(null!, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.BusinessModelAccess(new(" "), CancellationToken.None));
        controller.ModelState.AddModelError("Workspace", "Invalid input");
        Assert.IsType<BadRequestResult>(await controller.BusinessModelAccess(new("agent:" + Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task MobileRetailRoleCannotUseAssistantEvenWithFounderIdentity()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        var context = ControllerContextFor(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("oid", FounderEnvironmentScope.FounderId) }, "synthetic")));
        var actor = new MobileResolvedActor(new MessagingActor(FounderEnvironmentScope.FounderId, MessagingParticipantTypes.Client), Guid.NewGuid(), "Synthetic retail role");
        var resolver = new Mock<IMobileActorResolver>(MockBehavior.Strict);
        resolver.Setup(x => x.ResolveAsync(context.HttpContext.User, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));
        var controller = new MobileFounderAiController(resolver.Object, null!, new LegendFounderAiProgressBroker(),
            NullLogger<MobileFounderAiController>.Instance) { ControllerContext = context };
        Assert.IsType<ForbidResult>(await controller.Access(CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.BusinessWorkspaces(CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.BusinessConfiguration("agent:" + Guid.NewGuid().ToString("N"), "openai", CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.Conversations(CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.Chat(new() { Mode = "business" }, CancellationToken.None));
    }

    private static Task<IActionResult> ChatThroughControllerAsync(
        bool mobile, LegendFounderAiConversationService service, ControllerContext context,
        LegendFounderAiChatRequest request, CancellationToken token)
    {
        if (!mobile)
            return new LegendFounderAiController(service, new LegendFounderAiProgressBroker(),
                NullLogger<LegendFounderAiController>.Instance) { ControllerContext = context }.Chat(request, token);
        var actor = new MobileResolvedActor(new MessagingActor(FounderEnvironmentScope.FounderId, "agent"),
            Guid.NewGuid(), "Founder");
        var resolver = new Mock<IMobileActorResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.ResolveAsync(context.HttpContext.User, It.IsAny<string?>(), token))
            .ReturnsAsync(new MobileActorResolution(true, null, null, [actor], actor, false));
        return new MobileFounderAiController(resolver.Object, service, new LegendFounderAiProgressBroker(),
            NullLogger<MobileFounderAiController>.Instance) { ControllerContext = context }.Chat(request, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedChatStream_PreservesStructuredFailureWithoutProviderWork(bool mobile)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);
        var context = ControllerContextFor(founder);
        using var body = new MemoryStream();
        context.HttpContext.Response.Body = body;
        context.HttpContext.Request.Headers.Accept = "application/x-ndjson";

        Assert.IsType<EmptyResult>(await ChatThroughControllerAsync(mobile, service, context,
            Request("invalid", "No responder should run."), CancellationToken.None));
        var lines = Encoding.UTF8.GetString(body.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var accepted = JsonDocument.Parse(lines[0]);
        using var terminal = JsonDocument.Parse(lines[^1]);
        Assert.Equal("accepted", accepted.RootElement.GetProperty("type").GetString());
        Assert.Equal("result", terminal.RootElement.GetProperty("type").GetString());
        Assert.Equal(400, terminal.RootElement.GetProperty("status").GetInt32());
        Assert.False(terminal.RootElement.GetProperty("result").GetProperty("succeeded").GetBoolean());
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(operations.Invocations);
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 200)]
    public async Task SharedChatTransport_PreservesLegacyJsonStatusContract(bool mobile, int status)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var service = CreateService(db, new Mock<ILegendConnectOperations>(MockBehavior.Strict).Object,
            new FounderAiScenarioHandler());
        var result = Assert.IsAssignableFrom<ObjectResult>(await ChatThroughControllerAsync(mobile, service,
            ControllerContextFor(founder), Request("invalid", "No responder should run."), CancellationToken.None));
        Assert.Equal(status, result.StatusCode);
        Assert.False(Assert.IsType<LegendFounderAiChatResponse>(result.Value).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedChatStream_StopCancelsAndJoinsTheOriginalProviderOperation(bool mobile)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var handler = new FounderAiScenarioHandler(TimeSpan.FromMinutes(1), ProviderText("Must not finish."));
        var service = CreateService(db, operations.Object, handler);
        var context = ControllerContextFor(founder);
        using var body = new MemoryStream();
        context.HttpContext.Response.Body = body;
        context.HttpContext.Request.Headers.Accept = "application/x-ndjson";
        using var cancellation = new CancellationTokenSource();
        var request = ChatThroughControllerAsync(mobile, service, context,
            Request("teacher", "Explain the existing diagnostic."), cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handler.CancellationObserved);
        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain("\"type\":\"result\"", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }
}

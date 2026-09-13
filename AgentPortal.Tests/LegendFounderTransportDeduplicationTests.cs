using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed partial class LegendFounderAiModeIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOperationIsRejectedBeforeInferenceForBothResponseFormats(bool stream)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        SetupUnclassifiedContentPlan(operations);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FounderAiScenarioHandler(ProviderText("Completed once.")) { ResponseRelease = release.Task };
        var service = CreateService(db, operations.Object, handler);
        var request = Request("teacher", "Help me organize a short explanation.");
        var context = ControllerContextFor(founder).HttpContext;
        context.Response.Body = new MemoryStream();
        var operation = Guid.NewGuid();
        context.Request.Headers["X-Legend-Ai-Operation-Id"] = operation.ToString();
        if (stream) context.Request.Headers.Accept = "application/x-ndjson";
        var broker = new LegendFounderAiProgressBroker();
        Assert.True(broker.TryBeginExecution(founder, operation));
        var running = service.ReplyAsync(founder, request, operationId: operation);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var callsBeforeRetry = operations.Invocations.Count;
            var transport = new LegendFounderAiHttpTransport(context, service, broker, NullLogger.Instance);
            var result = await transport.ChatAsync(request, CancellationToken.None);
            LegendFounderAiChatResponse receipt;
            if (stream)
            {
                Assert.IsType<EmptyResult>(result);
                context.Response.Body.Position = 0;
                using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
                var frames = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                using var terminal = JsonDocument.Parse(frames.Last());
                Assert.Equal("result", terminal.RootElement.GetProperty("type").GetString());
                Assert.Equal(409, terminal.RootElement.GetProperty("status").GetInt32());
                receipt = JsonSerializer.Deserialize<LegendFounderAiChatResponse>(
                    terminal.RootElement.GetProperty("result").GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            }
            else
            {
                var json = Assert.IsType<ObjectResult>(result);
                Assert.Equal(409, json.StatusCode);
                receipt = Assert.IsType<LegendFounderAiChatResponse>(json.Value);
            }
            Assert.Equal("operation_pending", receipt.Reason);
            Assert.Equal("history_pending", receipt.FailureKind);
            Assert.NotNull(receipt.UserMessageId);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(callsBeforeRetry, operations.Invocations.Count);
            Assert.False(broker.TryBeginExecution(founder, operation));
        }
        finally
        {
            release.TrySetResult();
            broker.Complete(founder, operation);
        }
        Assert.True((await running).Succeeded);
    }
}

public sealed class LegendFounderProgressIdentityTests
{
    [Fact]
    public void HostedFoundationCompletionIsRetainedInSharedTransportWorkSummary()
    {
        var observations = new Dictionary<string, LegendFounderAiProgressEvent>();
        var response = new LegendFounderAiProgressEvent("foundation_response", "A response was produced.");
        LegendFounderAiConversationService.RecordWorkObservation(observations, response);
        Assert.Same(response, Assert.Single(observations).Value);
    }

    private static ClaimsPrincipal Actor(string oid, string tenant) => new(new ClaimsIdentity(
        [new Claim("oid", oid), new Claim("tid", tenant)], "test"));

    [Fact]
    public void CanonicalOidSupportsProgressWithoutLegacyNameIdentifierAndIsolatesTenants()
    {
        var broker = new LegendFounderAiProgressBroker();
        var operation = Guid.NewGuid();
        var first = Actor("canonical-founder", "tenant-one");
        Assert.True(broker.TryBeginExecution(first, operation));
        Assert.False(broker.TryBeginExecution(first, operation));
        var mapped = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "canonical-founder"),
             new Claim("http://schemas.microsoft.com/identity/claims/tenantid", "tenant-one")], "test"));
        Assert.False(broker.TryBeginExecution(mapped, operation));
        Assert.True(broker.TryBeginExecution(Actor("canonical-founder", "tenant-two"), operation));
        Assert.True(broker.TryBeginExecution(Actor("another-user", "tenant-one"), operation));
        broker.Complete(first, operation);
        Assert.True(broker.TryBeginExecution(first, operation));
    }
}

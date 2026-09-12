using System;
using System.Collections.Generic;
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
        var handler = new FounderAiScenarioHandler();
        var service = CreateService(db, operations.Object, handler);
        var context = ControllerContextFor(founder).HttpContext;
        var operation = Guid.NewGuid();
        context.Request.Headers["X-Legend-Ai-Operation-Id"] = operation.ToString();
        if (stream) context.Request.Headers.Accept = "application/x-ndjson";
        var broker = new LegendFounderAiProgressBroker();
        Assert.True(broker.TryBeginExecution(founder, operation));
        var transport = new LegendFounderAiHttpTransport(context, service, broker, NullLogger.Instance);
        var result = Assert.IsType<ObjectResult>(await transport.ChatAsync(
            Request("legend", "Help me organize a short explanation."), CancellationToken.None));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("operation_already_running", Assert.IsType<LegendFounderAiChatResponse>(result.Value).Reason);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(operations.Invocations);
        Assert.False(broker.TryBeginExecution(founder, operation));
        broker.Complete(founder, operation);
    }
}

public sealed class LegendFounderProgressIdentityTests
{
    [Fact]
    public void HostedFoundationCompletionIsRetainedInSharedTransportWorkSummary()
    {
        var observations = new Dictionary<string, LegendFounderAiProgressEvent>();
        var response = new LegendFounderAiProgressEvent("foundation_response", "A response was produced.");
        LegendFounderAiHttpTransport.RecordWorkObservation(observations, response);
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

[Collection("Founder operational diagnostic section")]
public sealed class LegendFounderDiagnosticExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSameScopeFailure_InvalidatesPriorSuccessAndSurvivesUnrelatedRead(bool includeUnrelated)
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedAsync(db);
        var operations = Operations();
        operations.SetupSequence(operation => operation.SearchRetainedKnowledgeAsync(
                "requested", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("requested", 1, []))
            .ThrowsAsync(new InvalidOperationException("private failing read"));
        operations.Setup(operation => operation.SearchRetainedKnowledgeAsync(
                "unrelated", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("unrelated", 1, []));
        var arguments = new List<string> { "{\"query\":\"requested\"}", "{ \"query\" : \"requested\" }" };
        if (includeUnrelated)
            arguments.Add("{\"query\":\"unrelated\"}");
        using var handler = new Responses(Tools(arguments), Answer());
        var progress = new List<LegendFounderAiProgressEvent>();
        var response = await Service(db, operations.Object, handler).ReplyAsync(founder, Request(),
            progress: (item, _) => { progress.Add(item); return ValueTask.CompletedTask; });
        Assert.Equal(includeUnrelated, response.Succeeded);
        Assert.Contains(progress, item => item.Message.StartsWith("Unavailable:", StringComparison.Ordinal));
        if (includeUnrelated)
        {
            Assert.Equal("partial_governed_inspection", response.Reason);
            Assert.Contains("LEGEND_GOVERNED_READ_DIAGNOSTICS", response.Message);
            Assert.Contains(LegendFounderAiConversationService.ReadScopeIdentity(
                "legend_search_retained_knowledge", arguments[0]), response.Message);
        }
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "requested", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TwoToolsInOneRound_WindowClosesAfterFirst_SecondAuthorityNeverRuns()
    {
        using var environment = new FounderEnvironment();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await SeedAsync(db);
        var operations = Operations();
        operations.Setup(operation => operation.SearchRetainedKnowledgeAsync(
                "first", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot("first", 1, []));
        using var handler = new Responses(Tools(["{\"query\":\"first\"}", "{\"query\":\"second\"}"]));
        var delayed = false;
        var response = await Service(db, operations.Object, handler).ReplyAsync(founder, Request(),
            progress: async (item, token) =>
            {
                if (!delayed && item.Stage == "tool_complete")
                {
                    delayed = true;
                    // Exercise the actual monotonic request clock and production
                    // 120-second minimum; no injected time or budget override.
                    await Task.Delay(TimeSpan.FromSeconds(61), token);
                }
            });
        Assert.True(delayed);
        Assert.False(response.Succeeded);
        Assert.Equal("provider_tool_execution_not_allowed", response.Reason);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "first", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        operations.Verify(operation => operation.SearchRetainedKnowledgeAsync(
            "second", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<ILegendConnectOperations> Operations()
    {
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations.Setup(operation => operation.TryBindConversationContentAsync(
                It.IsAny<string>(), It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(new LegendConnectContentBoundResponseMeaningPlanResult(
                false, "meaning_graph_component_unknown", null,
                OwnedRecordIntent: new LegendConnectOwnedRecordClassification(
                    LegendConnectOwnedRecordIntent.Unknown, false, LegendConnectOwnedRecordRequest.RequiredRelationKind)));
        return operations;
    }

    private static LegendFounderAiConversationService Service(MasterAppDbContext db,
        ILegendConnectOperations operations, Responses handler) => new(
        new ClientFactory(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OpenAI:ApiKey"] = "test-only-key", ["OpenAI:LegendFounderAiTimeoutSeconds"] = "120" }).Build(),
        new FounderLegendConnectService(operations, new AgentProfileAccessResolver(db)),
        NullLogger<LegendFounderAiConversationService>.Instance,
        new LegendFounderAiDiscourseStateService(db, new AgentProfileAccessResolver(db), operations),
        new LegendLanguageRegistry(db, new ConfigurationBuilder().Build()), ControllerTestHelpers.BuildTranslationService());

    private static LegendFounderAiChatRequest Request() => new()
    {
        Mode = "teacher", SourceLanguageCode = "en",
        Messages = [new LegendFounderAiChatMessage("user", "Inspect the requested evidence.")]
    };

    private static async Task<System.Security.Claims.ClaimsPrincipal> SeedAsync(MasterAppDbContext db)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(), AgentUserId = FounderEnvironment.Oid,
            AgentUpn = "followup-founder@legend.test", NormalizedEmail = "followup-founder@legend.test", IsActive = true
        });
        await db.SaveChangesAsync();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en");
        return ControllerTestHelpers.BuildUser(FounderEnvironment.Oid);
    }

    private static string Tools(IEnumerable<string> arguments) => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = arguments.Select((args, index) => new
        { type = "function_call", call_id = "call-" + index, name = "legend_search_retained_knowledge", arguments = args })
    });
    private static string Answer() => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "Only the available observations are established." } } } }
    });
    private sealed class Responses(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") });
    }
    private sealed class ClientFactory(Responses handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://openai.test/") };
    }
    private sealed class FounderEnvironment : IDisposable
    {
        public const string Oid = "11f6f9d9-0fe2-44c3-8cac-7d88d3fc3ac6";
        private readonly string? _previous = Environment.GetEnvironmentVariable("FOUNDER_OID");
        public FounderEnvironment() => Environment.SetEnvironmentVariable("FOUNDER_OID", Oid);
        public void Dispose() => Environment.SetEnvironmentVariable("FOUNDER_OID", _previous);
    }
}

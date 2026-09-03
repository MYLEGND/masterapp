using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Regressions for the Founder-reported loss of independent Legend® Ai
/// operation. Each test names the exact authority defect it reproduces and
/// uses generalized wording, never a fixture phrase that could be answered by
/// a prompt-specific branch.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderAiIndependentOperationRegressionTests
{
    // Root cause A: a failed governed source-language identification returned
    // before any response authority existed, stranding provider-enabled
    // conversations even though the single permitted escalation path was
    // available.
    [Theory]
    [InlineData("translation_provider_failed")]
    [InlineData("translation_language_ambiguous")]
    public async Task SourceLanguageFailure_ProviderEnabledLegendModeUsesTheExistingEscalationPath(
        string detectorError)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new RecordingProviderHandler(
            ProviderText("Escalated response after unavailable language identification."));
        var service = CreateService(
            db,
            operations.Object,
            handler,
            new FixedLanguageDetector(
                new TranslationDetectionResult(false, null, detectorError)));

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Summarize the trade-offs between two delivery sequences and justify the ordering.",
                nativeOnly: false,
                sourceLanguageCode: null));

        Assert.True(response.Succeeded, response.Error);
        Assert.Equal("OpenAITeacher", response.ResponseAuthority);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, NativeInferenceCalls(operations));
    }

    // The same failure in native-only testing remains an absolute zero-OpenAI
    // boundary with its exact governed reason.
    [Fact]
    public async Task SourceLanguageFailure_NativeOnlyStillFailsClosedWithZeroProviderCalls()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        var handler = new RecordingProviderHandler();
        var service = CreateService(
            db,
            operations.Object,
            handler,
            new FixedLanguageDetector(
                new TranslationDetectionResult(false, null, "translation_provider_failed")));

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Summarize the trade-offs between two delivery sequences and justify the ordering.",
                nativeOnly: true,
                sourceLanguageCode: null));

        Assert.False(response.Succeeded);
        Assert.Equal("source_language_identification", response.Stage);
        Assert.Equal(
            "source_language_identification_unavailable",
            response.Reason);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(operations.Invocations);
    }

    // Root cause B: explicit governed read requests and Founder operational
    // record questions were not recognized as governed inspection.
    [Theory]
    [InlineData("Inspect the Founder-visible client and lead counts read-only.")]
    [InlineData("How many leads are in the pipeline currently?")]
    [InlineData("Use a tool to report the current subscription count.")]
    [InlineData("What is the current renewal status across our policies?")]
    public void GovernedReadRequests_RequireGovernedInspection(string text)
    {
        Assert.True(RequiresGovernedInspection(text, "legend"));
        Assert.True(RequiresGovernedInspection(text, "teacher"));
    }

    [Theory]
    [InlineData("Rewrite this rough note as a concise professional update.")]
    [InlineData("Explain why two independent observations outrank one repeated claim.")]
    [InlineData("Help me structure an argument about delivery sequencing.")]
    public void OrdinarySubjectMatter_DoesNotRequireGovernedInspection(string text)
    {
        Assert.False(RequiresGovernedInspection(text, "teacher"));
    }

    // Root cause C: the retained-knowledge preload was treated as completion
    // of governed inspection, so the governed tool catalog was never offered
    // and the provider correctly reported that no tools were exposed.
    [Fact]
    public async Task GovernedOperationalRequest_ExposesTheRegisteredToolCatalogAndConsumesItsReceipt()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-regression-1",
            AgentUserId = FounderEnvironmentScope.FounderId,
            FirstName = "Read",
            LastName = "Only",
            Email = "read.only@legend.test",
            Phone = "0000000000",
            CrmStatus = "Lead"
        });
        await db.SaveChangesAsync();

        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string>()))
            .ReturnsAsync(new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "meaning_graph_relation_unproven",
                0,
                "Governed relation evidence was unavailable.",
                true));
        operations
            .Setup(operation => operation.SearchRetainedKnowledgeAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectRetainedKnowledgeSearchSnapshot(
                "portfolio",
                0,
                []));

        var handler = new RecordingProviderHandler(
            ProviderTool("legend_client_lead_portfolio", "{}"),
            ProviderText("Reported the governed counts returned by the read-only tool receipt."));
        var service = CreateService(
            db,
            operations.Object,
            handler,
            operationalPortfolio: new FounderOperationalPortfolioService(db));

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Inspect the Founder-visible client and lead counts read-only."));

        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(
            "legend_client_lead_portfolio",
            handler.RequestBodies[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "\"tool_choice\":\"required\"",
            handler.RequestBodies[0],
            StringComparison.Ordinal);
        Assert.Contains(
            @"workstationLeadCount\u0022:1",
            handler.RequestBodies[1],
            StringComparison.Ordinal);
        Assert.Equal(1, await db.WorkstationLeadProfiles.CountAsync());
    }

    // The smallest Founder-authorized read-only adapter stays inside the
    // canonical Founder authorization boundary and writes nothing.
    [Fact]
    public async Task ClientLeadPortfolio_RequiresFounderAuthorizationAndWritesNothing()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "lead-regression-2",
            AgentUserId = FounderEnvironmentScope.FounderId,
            FirstName = "Read",
            LastName = "Only",
            Email = "read.only.2@legend.test",
            Phone = "0000000000",
            CrmStatus = "Sold"
        });
        await db.SaveChangesAsync();
        var service = new FounderOperationalPortfolioService(db);

        await Assert.ThrowsAsync<ForbidResultException>(() =>
            service.GetPortfolioAsync(
                ControllerTestHelpers.BuildUser("not-the-founder")));

        var snapshot = await service.GetPortfolioAsync(founder);

        Assert.Equal(1, snapshot.WorkstationLeadCount);
        Assert.Equal("read_only_zero_write", snapshot.AccessClass);
        Assert.Equal(
            "Sold",
            Assert.Single(snapshot.WorkstationLeadsByCrmStatus).CrmStatus);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // Root cause D: tenant-owned operational history was classified as an
    // internet-research gap.
    [Theory]
    [InlineData("What was our client renewal percentage for the previous quarter?")]
    [InlineData("How many leads did we convert last month?")]
    [InlineData("What is our current subscription revenue?")]
    public void InternalOperationalHistory_IsNotInternetResearch(string question)
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            question,
            "en",
            UnsupportedEscalatable(),
            new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            "internal_operational_data_requires_governed_tools",
            decision.ReasonCode);
    }

    [Theory]
    [InlineData("What is the current published inflation rate?")]
    [InlineData("Which public standard defines this exchange format?")]
    public void ExternalFactualQuestions_RemainResearchable(string question)
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            question,
            "en",
            UnsupportedEscalatable(),
            new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(decision.ResearchRequired);
    }

    // Root cause E: a source-meaning ambiguity that selected zero governed
    // evidence was treated as a governed boundary and blocked the single
    // permitted escalation path; an unavailable discourse state did the same.
    [Theory]
    [InlineData("ambiguous_composed_meaning", true)]
    [InlineData("ambiguous_source_semantic_dimension", true)]
    public void ZeroEvidenceSourceAmbiguity_RemainsEscalatable(
        string reason,
        bool expected)
    {
        var inference = new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.Ambiguous,
            null,
            0,
            [reason]);

        Assert.Equal(expected, AmbiguityIsEscalatable(inference));
    }

    [Fact]
    public void GovernedEvidenceAmbiguity_RemainsFailClosed()
    {
        var inference = new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.Ambiguous,
            null,
            3,
            ["ambiguous_composed_meaning"]);

        Assert.False(AmbiguityIsEscalatable(inference));
    }

    [Fact]
    public void ContradictedMeaning_IsNeverEscalatable()
    {
        var inference = new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.Contradicted,
            null,
            0,
            ["semantic_transition_contradicted"]);

        Assert.False(AmbiguityIsEscalatable(inference));
    }

    [Theory]
    [InlineData("discourse_reference_state_unavailable", true)]
    [InlineData("discourse_reference_unresolved", false)]
    [InlineData("discourse_reference_binding_invalid", false)]
    [InlineData("discourse_reference_current_turn_mismatch", false)]
    public void DiscourseStateAvailability_SeparatesUnavailableInputFromGovernedRefusal(
        string reason,
        bool expected)
    {
        var method = typeof(LegendConnectOperations).GetMethod(
            "CanEscalateFromUnavailableComposedSource",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var inference = new LegendSemanticTransitionInference(
            LegendSemanticTransitionInference.InsufficientEvidence,
            null,
            0,
            [reason]);

        Assert.Equal(
            expected,
            Assert.IsType<bool>(method!.Invoke(null, [inference])));
    }

    private static bool AmbiguityIsEscalatable(
        LegendSemanticTransitionInference inference)
    {
        var method = typeof(LegendConnectOperations).GetMethod(
            "CanEscalateFromUnprovenSourceAmbiguity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [inference]));
    }

    private static bool RequiresGovernedInspection(string text, string mode)
    {
        var method = typeof(LegendFounderAiConversationService)
            .GetMethod(
                "RequiresGovernedInspection",
                BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        IReadOnlyList<LegendFounderAiChatMessage> conversation =
            [new("user", text)];
        return Assert.IsType<bool>(
            method!.Invoke(null, [conversation, mode]));
    }

    private static LegendConnectNativeInferenceSnapshot UnsupportedEscalatable() =>
        new(
            false,
            0m,
            null,
            "meaning_graph_relation_unproven",
            0,
            "Governed relation evidence was unavailable.",
            true);

    private static int NativeInferenceCalls(
        Mock<ILegendConnectOperations> operations) =>
        operations.Invocations.Count(invocation =>
            invocation.Method.Name is
                nameof(ILegendConnectOperations.TryInferConversationWithDiscourseAsync));

    private static LegendFounderAiChatRequest Request(
        string? mode,
        string prompt,
        bool nativeOnly = false,
        string? sourceLanguageCode = "en") =>
        new()
        {
            Mode = mode,
            NativeOnly = nativeOnly,
            SourceLanguageCode = sourceLanguageCode,
            Messages = [new LegendFounderAiChatMessage("user", prompt)]
        };

    private static LegendFounderAiConversationService CreateService(
        Infrastructure.Data.MasterAppDbContext db,
        ILegendConnectOperations operations,
        RecordingProviderHandler handler,
        ITranslationService? translation = null,
        FounderOperationalPortfolioService? operationalPortfolio = null) =>
        new(
            new RecordingHttpClientFactory(handler),
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
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(
                db,
                new AgentProfileAccessResolver(db),
                operations),
            new LegendLanguageRegistry(
                db,
                new ConfigurationBuilder().Build()),
            translation ?? ControllerTestHelpers.BuildTranslationService(),
            softwareRemediation: null,
            operationalPortfolio: operationalPortfolio);

    private static async Task<ClaimsPrincipal> AddFounderProfileAsync(
        Infrastructure.Data.MasterAppDbContext db)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = FounderEnvironmentScope.FounderId,
            AgentUpn = "independent-operation-founder@legend.test",
            NormalizedEmail = "independent-operation-founder@legend.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        return ControllerTestHelpers.BuildUser(
            FounderEnvironmentScope.FounderId);
    }

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

    private static HttpResponseMessage ProviderResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class RecordingProviderHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public RecordingProviderHandler(params HttpResponseMessage[] responses) =>
            _responses = new Queue<HttpResponseMessage>(responses);

        public int RequestCount { get; private set; }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestBodies.Add(
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "No provider response was queued for this test.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed class RecordingHttpClientFactory(
        RecordingProviderHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.openai.com/")
            };
    }

    private sealed class FixedLanguageDetector(
        TranslationDetectionResult result) : ITranslationService
    {
        public int DetectionCount { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            DetectionCount++;
            return Task.FromResult(result);
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Founder AI language identification must not translate text.");
    }

    private sealed class FounderEnvironmentScope : IDisposable
    {
        public const string FounderId = "11f6f9d9-0fe2-44c3-8cac-7d88d3fc3ac6";

        private readonly string? _previousFounderOid =
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        public FounderEnvironmentScope() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", FounderId);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(
                "FOUNDER_OID",
                _previousFounderOid);
    }
}

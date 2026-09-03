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
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    [InlineData("translation_service_timeout")]
    public async Task TransientLanguageIdentificationOutage_ProviderEnabledLegendModeUsesTheExistingEscalationPath(
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

    // A governed determination that the source language is ambiguous or
    // unsupported is not an outage. Without a proven language identity the
    // input semantics cannot be established, so it fails closed in every mode
    // and never reaches the provider.
    [Theory]
    [InlineData("translation_language_ambiguous", "source_language_ambiguous")]
    [InlineData("translation_language_unsupported", "source_language_unsupported")]
    public async Task SemanticLanguageAmbiguity_FailsClosedEvenWhenEscalationIsAllowed(
        string detectorError,
        string expectedReason)
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
                new TranslationDetectionResult(false, null, detectorError)));

        var response = await service.ReplyAsync(
            founder,
            Request(
                "legend",
                "Summarize the trade-offs between two delivery sequences and justify the ordering.",
                nativeOnly: false,
                sourceLanguageCode: null));

        Assert.False(response.Succeeded);
        Assert.Equal("source_language_identification", response.Stage);
        Assert.Equal(expectedReason, response.Reason);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(
            LegendFounderAiConversationService
                .IsTransientLanguageIdentificationOutage(response.Reason));
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
    // Paraphrases of one request, plus unrelated domains, to show the rule is
    // ownership deixis and not a subject-matter phrase list.
    [InlineData("How many clients and leads do we have right now?")]
    [InlineData("What is the current count of our open households?")]
    [InlineData("Show me the status of our renewals today.")]
    [InlineData("How many machines are currently in our maintenance queue?")]
    [InlineData("What is the total in my portfolio at the moment?")]
    public void OwnedRecordStateRequests_RequireGovernedInspection(string text)
    {
        Assert.True(RequiresGovernedInspection(text, "legend"));
        Assert.True(RequiresGovernedInspection(text, "teacher"));
        Assert.True(
            LegendConnectOwnedRecordRequest.RequestsOwnedRecordState(text));
    }

    [Theory]
    [InlineData("Rewrite this rough note as a concise professional update.")]
    [InlineData("Explain why two independent observations outrank one repeated claim.")]
    [InlineData("Help me structure an argument about delivery sequencing.")]
    // Third-party record state is not owned record state.
    [InlineData("How many public filings did that company publish last year?")]
    public void OrdinarySubjectMatter_DoesNotRequireGovernedInspection(string text)
    {
        Assert.False(RequiresGovernedInspection(text, "teacher"));
        Assert.False(
            LegendConnectOwnedRecordRequest.RequestsOwnedRecordState(text));
    }

    // Root cause C: the retained-knowledge preload was treated as completion
    // of governed inspection, so the governed tool catalog was never offered
    // and the provider correctly reported that no tools were exposed.
    //
    // Scope of this proof: it shows that the registered governed read tool is
    // offered with a required tool call, that its receipt is executed against
    // the authenticated database, and that the exact canonical counts reach the
    // model before it answers. The provider fixture stands in for the real
    // selection policy, so this is not evidence that OpenAI autonomously
    // chooses this tool in production.
    [Theory]
    [InlineData("How many clients and leads do we have right now?")]
    [InlineData("What is the current count of our leads and clients?")]
    [InlineData("Report the status of our client and lead records today.")]
    public async Task OwnedRecordRequest_OffersTheRegisteredToolAndBindsItsExactReceipt(
        string prompt)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        using var writeSentinel = new WriteAttemptSentinel();
        await using var db = BuildSentinelDb(writeSentinel);
        var founder = await AddFounderProfileAsync(db);
        db.WorkstationLeadProfiles.Add(NewLead("lead-regression-1", "Lead"));
        db.WorkstationLeadProfiles.Add(
            NewLead("lead-regression-converted", "Converted"));
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            FirstName = "Website",
            Email = "website.regression@legend.test"
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            FirstName = "Website",
            Email = "website.deleted@legend.test",
            IsDeleted = true
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
            .ReturnsAsync(UnsupportedEscalatable());
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

        writeSentinel.Arm();
        var response = await service.ReplyAsync(founder, Request("legend", prompt));

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

        // The second request must carry the exact canonical receipt values:
        // one active lead (the converted lead is excluded) and one external
        // website lead (the deleted one is excluded).
        var receiptRound = Unescape(handler.RequestBodies[1]);
        Assert.Contains(
            "\"activeLeadCount\":1",
            receiptRound,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"websiteLeadCount\":1",
            receiptRound,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"accessClass\":\"read_only_zero_write\"",
            receiptRound,
            StringComparison.Ordinal);
        Assert.Equal(0, writeSentinel.OperationalWriteAttempts);
    }

    // A required governed read that cannot execute must fail closed. The
    // provider is never allowed to answer an owned-record question from
    // recollection.
    [Fact]
    public async Task OwnedRecordRequest_FailsClosedWhenTheGovernedReadIsUnavailable()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);

        var operations = new Mock<ILegendConnectOperations>(MockBehavior.Strict);
        operations
            .Setup(operation => operation.TryInferConversationWithDiscourseAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<LegendConnectConversationContextItem>>(),
                It.IsAny<LegendConnectDiscourseStateSnapshot?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string>()))
            .ReturnsAsync(UnsupportedEscalatable());
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
            ProviderText("Roughly a few hundred clients and leads."));
        var service = CreateService(
            db,
            operations.Object,
            handler,
            operationalPortfolio: null);

        var response = await service.ReplyAsync(
            founder,
            Request("legend", "How many clients and leads do we have right now?"));

        Assert.False(response.Succeeded);
        Assert.Equal("governed_tool", response.Stage);
        Assert.Equal(
            "required_governed_inspection_missing",
            response.Reason);
    }

    // The smallest Founder-authorized read-only adapter stays inside the
    // canonical Founder authorization boundary, reuses the canonical
    // active/ownership/deleted definitions, and attempts no write.
    [Fact]
    public async Task ClientLeadPortfolio_ReusesCanonicalVisibilityAndAttemptsNoWrite()
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        using var writeSentinel = new WriteAttemptSentinel();
        await using var db = BuildSentinelDb(writeSentinel);
        var founder = await AddFounderProfileAsync(db);
        db.WorkstationLeadProfiles.Add(NewLead("lead-regression-2", "Sold"));
        db.WorkstationLeadProfiles.Add(NewLead("lead-regression-3", "Converted"));
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            FirstName = "Website",
            Email = "website.two@legend.test"
        });
        db.WebsiteLeads.Add(new WebsiteLead
        {
            LeadId = Guid.NewGuid(),
            FirstName = "Website",
            Email = "website.internal@legend.test",
            IsInternal = true
        });
        await db.SaveChangesAsync();
        var service = new FounderOperationalPortfolioService(db);

        writeSentinel.Arm();

        await Assert.ThrowsAsync<ForbidResultException>(() =>
            service.GetPortfolioAsync(
                ControllerTestHelpers.BuildUser("not-the-founder")));

        var snapshot = await service.GetPortfolioAsync(founder);

        // ActiveLeadQueue excludes the converted lead.
        Assert.Equal(1, snapshot.ActiveLeadCount);
        Assert.Equal(
            "Sold",
            Assert.Single(snapshot.ActiveLeadsByCrmStatus).CrmStatus);
        // Internal website leads are excluded by the canonical rule.
        Assert.Equal(1, snapshot.WebsiteLeadCount);
        // No client has a current entitlement, so no client is active.
        Assert.Equal(0, snapshot.ActiveClientCount);
        Assert.Equal(0, snapshot.AgentLinkedClientCount);
        Assert.Equal("read_only_zero_write", snapshot.AccessClass);
        Assert.Equal(0, writeSentinel.OperationalWriteAttempts);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // Root cause D: tenant-owned operational history was classified as an
    // internet-research gap.
    [Theory]
    [InlineData("What was our client renewal percentage for the previous quarter?")]
    [InlineData("What is the current total of our converted leads?")]
    [InlineData("What is our current subscription revenue?")]
    public void OwnedRecordHistory_IsNotInternetResearch(string question)
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

    // Availability of some governed evidence is not proof that it answers the
    // requested record state, so it can never substitute for the read receipt.
    [Fact]
    public void OwnedRecordRequest_IsNotSatisfiedByUnrelatedGovernedEvidence()
    {
        var decision = LegendConnectOperations.DecideResearchNeeded(
            "What is our current client renewal percentage?",
            "en",
            new LegendConnectNativeInferenceSnapshot(
                false,
                0m,
                null,
                "meaning_graph_relation_unproven",
                7,
                "Unrelated governed evidence was available.",
                true),
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

    private static string Unescape(string body) =>
        body.Replace("\\u0022", "\"", StringComparison.Ordinal);

    private static WorkstationLeadProfile NewLead(
        string leadId,
        string crmStatus) =>
        new()
        {
            LeadId = leadId,
            AgentUserId = FounderEnvironmentScope.FounderId,
            FirstName = "Read",
            LastName = "Only",
            Email = $"{leadId}@legend.test",
            Phone = "0000000000",
            CrmStatus = crmStatus
        };

    private static Infrastructure.Data.MasterAppDbContext BuildSentinelDb(
        WriteAttemptSentinel sentinel) =>
        new(new DbContextOptionsBuilder<Infrastructure.Data.MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings =>
                warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId
                        .TransactionIgnoredWarning))
            .AddInterceptors(sentinel)
            .Options);

    /// <summary>
    /// Observes every persistence attempt on the read path. Any attempt that
    /// touches an operational record set is counted and rejected, so zero
    /// operational writes is proven at the persistence boundary instead of
    /// inferred from unchanged row counts. Other persistence attempts (the
    /// language registry provisioning its governed baseline) are recorded by
    /// entity name rather than hidden.
    /// </summary>
    private sealed class WriteAttemptSentinel : SaveChangesInterceptor, IDisposable
    {
        private static readonly string[] OperationalEntities =
        [
            nameof(ClientProfile),
            nameof(AgentClient),
            nameof(WorkstationLeadProfile),
            nameof(WebsiteLead)
        ];

        private bool _armed;

        public int OperationalWriteAttempts { get; private set; }

        public SortedSet<string> ObservedWriteEntities { get; } =
            new(StringComparer.Ordinal);

        public void Arm() => _armed = true;

        public void Dispose() => _armed = false;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            Observe(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Observe(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void Observe(DbContextEventData eventData)
        {
            if (!_armed || eventData.Context is null)
                return;

            var entities = eventData.Context.ChangeTracker
                .Entries()
                .Where(entry => entry.State != EntityState.Unchanged &&
                                entry.State != EntityState.Detached)
                .Select(entry => entry.Entity.GetType().Name)
                .ToList();

            foreach (var entity in entities)
                ObservedWriteEntities.Add(entity);

            var operational = entities
                .Where(entity => OperationalEntities.Contains(entity))
                .ToList();

            if (operational.Count == 0)
                return;

            OperationalWriteAttempts += operational.Count;
            throw new InvalidOperationException(
                "A governed read path attempted to persist operational records: " +
                string.Join(", ", operational));
        }
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

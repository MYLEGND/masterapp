using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Held-out operation matrix. Every prompt uses wording that appears nowhere in
/// the governed corpus, the production source, or any other test, so a passing
/// row cannot come from a memorized phrase. The matrix exercises the real
/// native inference authority (no mocked <see cref="ILegendConnectOperations"/>)
/// and records the authority, stage, reason, evidence count, model provenance,
/// provider HTTP calls, tool calls and database writes for each exchange.
/// The recorded matrix is written to LEGEND_HELDOUT_MATRIX_PATH when that
/// variable is set, so the exact prompts and actual responses can be preserved.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderAiHeldOutOperationMatrixTests
{
    private const string ArithmeticPrompt =
        "A dispatch board opens with 41 open tickets. Twelve are closed, five new tickets arrive, and four of the remaining tickets are flagged for review. How many tickets are still open, and what share of them are flagged? Show the calculation.";

    private const string RewritingPrompt =
        "Rewrite this rough note as a concise professional update for a regional supervisor without changing any facts: we checked nine renewals, two lack proof of payment, Devon will call those households before Monday, nothing has been reinstated yet.";

    private const string DeductionPrompt =
        "Every audited file carries a signed attestation. File Kestrel was audited and carries no signed attestation. What follows logically, and which premise must be false if both records describe the same file?";

    private const string CausalPrompt =
        "Intake forms began arriving with blank effective dates the same week two intake clerks were reassigned and a form template was republished. Which explanation best accounts for the pattern, and what observation would rule it out?";

    private const string PlanningPrompt =
        "Thirty-one overdue inspections must be cleared at seven per day, hazardous sites first, and no site may be contacted twice. Give the daily sequence and the completion day.";

    private const string UncertaintyPrompt =
        "What was our exact client renewal percentage for the third quarter of last year in the portal? Do not estimate.";

    private const string MemoryFirstPrompt =
        "Record two facts for this conversation: Project Marlin closes on the ninth of November, and Corine owns the vendor review.";

    private const string MemoryFollowUpPrompt =
        "Using only what I told you in this conversation, who owns the vendor review and what is the closing date?";

    private const string CreolePrompt =
        "Yon dosye gen ven-senk fòm. Rapò a di gen ven-sèt fòm ki resevwa. Ki kontradiksyon ki genyen, e ki chif ki dwe verifye anvan nou kontinye?";

    private const string ToolPrompt =
        "Inspect, read-only, how many client records and how many workstation leads are currently visible to me.";

    /// <summary>
    /// Isolation evidence only. It proves the native-only boundary makes no
    /// provider call and attempts no write, and that every refusal carries an
    /// explicit governed reason. It deliberately does NOT assert that the
    /// request was answered: native general reasoning is not implemented, and
    /// the capability gate below is the only place capability is judged.
    /// </summary>
    [Fact]
    public async Task NativeOnlyRequests_MakeNoProviderCallAndAttemptNoWrite()
    {
        var rows = new List<MatrixRow>();

        foreach (var (category, prompt) in CapabilityMatrix)
        {
            rows.Add(await RunAsync(
                $"native_only:{category}",
                prompt,
                nativeOnly: true));
        }

        Record(rows);

        Assert.All(rows, row => Assert.Equal(0, row.ProviderCalls));
        Assert.All(rows, row => Assert.Equal(0, row.OperationalWriteAttempts));
        Assert.All(rows, row => Assert.NotEqual("OpenAITeacher", row.ResponseAuthority));
        Assert.All(rows, row => Assert.False(
            string.IsNullOrWhiteSpace(row.Reason),
            "A native-only refusal must carry an explicit governed reason."));
    }

    /// <summary>
    /// The native capability gate. Each row demands a correct answer produced
    /// under LEGEND authority with zero provider calls. It is skipped, never
    /// passed, because no native general-reasoning model exists in this
    /// architecture; an explicit refusal must never be recorded as a pass.
    /// Remove the skip only when a genuine native model can satisfy it.
    /// </summary>
    [Theory(Skip = "Native general reasoning is not implemented: the promoted reasoning transport is provider-backed and only re-words symbolically supported answers. This gate must fail until a genuine native model exists; an explicit governed refusal is not a pass.")]
    [InlineData("arithmetic", "29")]
    [InlineData("deduction", "Kestrel")]
    [InlineData("constrained_planning", "five")]
    public async Task NativeOnlyRequests_ProduceCorrectAnswersUnderLegendAuthority(
        string category,
        string requiredAnswerFragment)
    {
        var prompt = CapabilityMatrix.Single(row => row.Category == category).Prompt;

        var row = await RunAsync(
            $"native_capability:{category}",
            prompt,
            nativeOnly: true);

        Record([row]);

        Assert.True(row.Succeeded, row.Error);
        Assert.Equal(0, row.ProviderCalls);
        Assert.Equal("LegendAi", row.ResponseAuthority);
        Assert.NotNull(row.Message);
        Assert.Contains(
            requiredAnswerFragment,
            row.Message!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same-conversation memory gate. It demands the exact owner and date
    /// stated earlier in the same conversation. Skipped for the same structural
    /// reason; a different failure can never satisfy it.
    /// </summary>
    [Fact(Skip = "Same-conversation recall through native governed discourse is not implemented; see the native general-reasoning structural blocker.")]
    public async Task SameConversationMemory_ReturnsTheExactValuesStatedEarlier()
    {
        var row = await RunAsync(
            "native_capability:same_conversation_memory",
            MemoryFollowUpPrompt,
            nativeOnly: true,
            priorTurns:
            [
                new LegendFounderAiChatMessage("user", MemoryFirstPrompt),
                new LegendFounderAiChatMessage(
                    "assistant",
                    "Recorded: Project Marlin closes on the ninth of November and Corine owns the vendor review.")
            ]);

        Record([row]);

        Assert.True(row.Succeeded, row.Error);
        Assert.Equal(0, row.ProviderCalls);
        Assert.Equal("LegendAi", row.ResponseAuthority);
        Assert.NotNull(row.Message);
        Assert.Contains("Corine", row.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("November", row.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PermittedEscalationAndDirectProviderModeAreAttributedToTheProvider()
    {
        var escalated = await RunAsync(
            "escalation_allowed:deduction",
            DeductionPrompt,
            nativeOnly: false,
            providerText: "Kestrel contradicts the audited-file rule, so one of the two records must be false.");

        var direct = await RunAsync(
            "direct_provider:deduction",
            DeductionPrompt,
            nativeOnly: false,
            mode: "teacher",
            providerText: "The premises cannot both hold for one file.");

        Record([escalated, direct]);

        Assert.True(escalated.Succeeded, escalated.Error);
        Assert.Equal("OpenAITeacher", escalated.ResponseAuthority);
        Assert.Equal(1, escalated.ProviderCalls);
        Assert.Equal(0, escalated.OperationalWriteAttempts);
        Assert.True(direct.Succeeded, direct.Error);
        Assert.Equal("OpenAITeacher", direct.ResponseAuthority);
        Assert.Equal(1, direct.ProviderCalls);
    }

    /// <summary>
    /// Held-out paraphrases of an owned-record request. The routing decision,
    /// the offered catalog, the executed receipt and the exact returned counts
    /// are asserted. The provider fixture stands in for the real selection
    /// policy, so this is not evidence of autonomous tool selection by OpenAI.
    /// </summary>
    [Theory]
    [InlineData("How many client records and how many leads do we have right now?")]
    [InlineData("What is the current count of our leads?")]
    [InlineData("Show me the status of our lead records today.")]
    public async Task OwnedRecordRequests_ExecuteTheRegisteredReadToolAndCarryItsExactCounts(
        string prompt)
    {
        var row = await RunAsync(
            "escalation_allowed:governed_tool_read",
            prompt,
            nativeOnly: false,
            providerResponses:
            [
                ProviderTool("legend_client_lead_portfolio", "{}"),
                ProviderText("Read-only inspection returned the governed counts.")
            ],
            seedOperationalRecords: true);

        Record([row]);

        Assert.True(row.Succeeded, row.Error);
        Assert.Equal(2, row.ProviderCalls);
        Assert.Contains("legend_client_lead_portfolio", row.ToolCalls);
        Assert.Contains(
            "\"activeLeadCount\":1",
            row.ReceiptRoundRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"websiteLeadCount\":0",
            row.ReceiptRoundRequest,
            StringComparison.Ordinal);
        Assert.Equal(0, row.OperationalWriteAttempts);
    }

    private static readonly (string Category, string Prompt)[] CapabilityMatrix =
    [
        ("arithmetic", ArithmeticPrompt),
        ("rewriting", RewritingPrompt),
        ("deduction", DeductionPrompt),
        ("causal_diagnosis", CausalPrompt),
        ("constrained_planning", PlanningPrompt),
        ("internal_data_uncertainty", UncertaintyPrompt),
        ("same_conversation_memory", MemoryFirstPrompt),
        ("haitian_creole_conflict", CreolePrompt)
    ];

    private static async Task<MatrixRow> RunAsync(
        string label,
        string prompt,
        bool nativeOnly,
        string mode = "legend",
        string? providerText = null,
        HttpResponseMessage[]? providerResponses = null,
        IReadOnlyList<LegendFounderAiChatMessage>? priorTurns = null,
        bool seedOperationalRecords = false)
    {
        using var founderEnvironment = new FounderEnvironmentScope();
        using var writeSentinel = new WriteAttemptSentinel();
        await using var db = BuildSentinelDb(writeSentinel);
        var founder = await AddFounderProfileAsync(db);
        if (seedOperationalRecords)
        {
            await SeedOperationalRecordsAsync(db);
        }

        // Every persistence attempt from this point on is counted and rejected,
        // so zero writes is proven at the command boundary instead of inferred
        // from unchanged row counts.
        writeSentinel.Arm();
        var responses = providerResponses
            ?? (providerText is null ? [] : new[] { ProviderText(providerText) });
        var handler = new RecordingProviderHandler(responses);
        var service = CreateService(db, handler);

        var messages = new List<LegendFounderAiChatMessage>(
            priorTurns ?? []) { new("user", prompt) };

        var response = await service.ReplyAsync(
            founder,
            new LegendFounderAiChatRequest
            {
                Mode = mode,
                NativeOnly = nativeOnly,
                SourceLanguageCode = null,
                Messages = messages
            });

        return new MatrixRow(
            label,
            prompt,
            response.Succeeded,
            response.Message,
            response.Error,
            response.ResponseAuthority,
            response.Stage,
            response.Reason,
            response.ResearchOutcome?.Session.ClaimEvidence.Count ?? 0,
            response.ModelProvenance,
            handler.RequestCount,
            handler.ToolCalls,
            handler.RequestBodies.Count > 1
                ? handler.RequestBodies[^1].Replace(
                    "\\u0022",
                    "\"",
                    StringComparison.Ordinal)
                : string.Empty,
            writeSentinel.OperationalWriteAttempts,
            writeSentinel.ObservedWriteEntities.ToList(),
            db.ChangeTracker.Entries().Count(entry =>
                entry.State != EntityState.Unchanged));
    }

    private static void Record(IReadOnlyList<MatrixRow> rows)
    {
        var path = Environment.GetEnvironmentVariable(
            "LEGEND_HELDOUT_MATRIX_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            rows.Select(row => JsonSerializer.Serialize(row)));
        lock (RecordLock)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }
    }

    private static readonly object RecordLock = new();

    private sealed record MatrixRow(
        string Label,
        string Prompt,
        bool Succeeded,
        string? Message,
        string? Error,
        string ResponseAuthority,
        string? Stage,
        string? Reason,
        int EvidenceCount,
        string? ModelProvenance,
        int ProviderCalls,
        IReadOnlyList<string> ToolCalls,
        string ReceiptRoundRequest,
        int OperationalWriteAttempts,
        IReadOnlyList<string> ObservedWriteEntities,
        int PendingTrackedChanges);

    private static async Task SeedOperationalRecordsAsync(MasterAppDbContext db)
    {
        db.WorkstationLeadProfiles.Add(new WorkstationLeadProfile
        {
            LeadId = "held-out-lead-1",
            AgentUserId = FounderEnvironmentScope.FounderId,
            FirstName = "Held",
            LastName = "Out",
            Email = "held.out@legend.test",
            Phone = "0000000000",
            CrmStatus = "Lead"
        });
        await db.SaveChangesAsync();
    }

    private static LegendFounderAiConversationService CreateService(
        MasterAppDbContext db,
        RecordingProviderHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "ht",
                ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Haitian Creole",
                ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Kreyòl ayisyen"
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            intelligence: intelligence);
        var accessResolver = new AgentProfileAccessResolver(db);

        return new LegendFounderAiConversationService(
            new RecordingHttpClientFactory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "test-only-key",
                    ["OpenAI:LegendFounderAiTimeoutSeconds"] = "45"
                })
                .Build(),
            new FounderLegendConnectService(operations, accessResolver),
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(db, accessResolver, operations),
            registry,
            ControllerTestHelpers.BuildTranslationService(),
            softwareRemediation: null,
            operationalPortfolio: new FounderOperationalPortfolioService(db));
    }

    private static async Task<ClaimsPrincipal> AddFounderProfileAsync(
        MasterAppDbContext db)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = FounderEnvironmentScope.FounderId,
            AgentUpn = "held-out-founder@legend.test",
            NormalizedEmail = "held-out-founder@legend.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        return ControllerTestHelpers.BuildUser(FounderEnvironmentScope.FounderId);
    }

    private static MasterAppDbContext BuildSentinelDb(
        WriteAttemptSentinel sentinel) =>
        new(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings =>
                warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
                    content = new[] { new { type = "output_text", text } }
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
                    call_id = "held-out-tool-call",
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

    private sealed class RecordingProviderHandler(
        params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }

        public List<string> ToolCalls { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            if (body.Contains("function_call_output", StringComparison.Ordinal))
            {
                ToolCalls.Add("legend_client_lead_portfolio");
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    "A provider call was made without a queued response.");
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

    private sealed class FounderEnvironmentScope : IDisposable
    {
        public const string FounderId = "3f0d6de5-9d3b-4d3a-8f6f-2f8f0d6cbf41";

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

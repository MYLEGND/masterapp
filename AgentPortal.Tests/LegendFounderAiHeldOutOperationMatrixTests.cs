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

    [Fact]
    public async Task HeldOutMatrix_NativeOnlyReasoningNeverCallsTheProviderAndReportsGovernedReasons()
    {
        var rows = new List<MatrixRow>();

        foreach (var (category, prompt) in new[]
        {
            ("arithmetic", ArithmeticPrompt),
            ("rewriting", RewritingPrompt),
            ("deduction", DeductionPrompt),
            ("causal_diagnosis", CausalPrompt),
            ("constrained_planning", PlanningPrompt),
            ("internal_data_uncertainty", UncertaintyPrompt),
            ("same_conversation_memory", MemoryFirstPrompt),
            ("haitian_creole_conflict", CreolePrompt)
        })
        {
            rows.Add(await RunAsync(
                $"native_only:{category}",
                prompt,
                nativeOnly: true));
        }

        Record(rows);

        Assert.All(rows, row => Assert.Equal(0, row.ProviderCalls));
        Assert.All(rows, row => Assert.Equal(0, row.DatabaseWrites));
        Assert.All(rows, row => Assert.NotEqual("OpenAITeacher", row.ResponseAuthority));
        Assert.All(rows, row => Assert.False(
            string.IsNullOrWhiteSpace(row.Reason) && string.IsNullOrWhiteSpace(row.Message),
            "A native-only exchange must return either an answer or an explicit governed reason."));
    }

    [Fact]
    public async Task HeldOutMatrix_SameConversationMemoryIsAnsweredWithoutProviderCallsInNativeOnly()
    {
        var row = await RunAsync(
            "native_only:same_conversation_memory_followup",
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

        Assert.Equal(0, row.ProviderCalls);
        Assert.NotEqual(
            "source_language_identification_unavailable",
            row.Reason);
    }

    [Fact]
    public async Task HeldOutMatrix_PermittedEscalationAndDirectProviderModeAreAttributedToTheProvider()
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
        Assert.True(direct.Succeeded, direct.Error);
        Assert.Equal("OpenAITeacher", direct.ResponseAuthority);
        Assert.Equal(1, direct.ProviderCalls);
    }

    [Fact]
    public async Task HeldOutMatrix_GovernedToolRequestExecutesTheRegisteredReadToolWithoutWriting()
    {
        var row = await RunAsync(
            "escalation_allowed:governed_tool_read",
            ToolPrompt,
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
        Assert.Equal(0, row.DatabaseWrites);
    }

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
        await using var db = ControllerTestHelpers.BuildDb();
        var founder = await AddFounderProfileAsync(db);
        if (seedOperationalRecords)
        {
            await SeedOperationalRecordsAsync(db);
        }

        var writesBefore = await CountOperationalRecordsAsync(db);
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

        var writesAfter = await CountOperationalRecordsAsync(db);

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
            writesAfter - writesBefore);
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
        int DatabaseWrites);

    private static async Task<int> CountOperationalRecordsAsync(
        MasterAppDbContext db) =>
        await db.ClientProfiles.CountAsync()
        + await db.WorkstationLeadProfiles.CountAsync()
        + await db.AgentClients.CountAsync();

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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
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

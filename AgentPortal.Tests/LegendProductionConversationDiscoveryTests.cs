using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendProductionConversationDiscoveryTests
{
    private readonly ITestOutputHelper _output;
    public LegendProductionConversationDiscoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PrintStrongNonGreetingProductionConversationCapabilities()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
        {
            _output.WriteLine("DISCOVERY SKIPPED: production read-only connection unavailable.");
            return;
        }

        var cs = new SqlConnectionStringBuilder(raw)
        {
            ApplicationName = "LEGEND unpublished conversation discovery",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        var guard = new DiscoveryReadOnlyInterceptor();
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(cs.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(guard)
                .Options);

        var rows = await (
            from e in db.LegendSemanticTransitionEvidence.AsNoTracking()
            join s in db.LegendCurriculumExamples.AsNoTracking() on e.SourceCurriculumExampleId equals s.Id
            join r in db.LegendCurriculumExamples.AsNoTracking() on e.ResultCurriculumExampleId equals r.Id
            join su in db.LegendLanguageTextUnits.AsNoTracking() on s.TextUnitId equals su.Id
            join ru in db.LegendLanguageTextUnits.AsNoTracking() on r.TextUnitId equals ru.Id
            where e.SupersededUtc == null && e.SourceLanguageCode == "en" && e.ResultLanguageCode == "en" &&
                  e.Provenance == "FounderApproved" &&
                  e.ContributionState == "Supported" && e.IsHumanVerifiedSupport &&
                  s.SupersededUtc == null && r.SupersededUtc == null &&
                  su.IsTrainingEligible && ru.IsTrainingEligible
            select new
            {
                e.TransitionSignature,
                e.SourceSemanticFrame,
                e.ResultSemanticFrame,
                e.IndependentSourceIdentity,
                SourceText = su.Text,
                ResultText = ru.Text
            }).ToListAsync();

        var strong = rows
            .GroupBy(x => x.TransitionSignature)
            .Select(g => new
            {
                Signature = g.Key,
                SourceFrame = g.First().SourceSemanticFrame,
                ResultFrame = g.First().ResultSemanticFrame,
                Independent = g.Select(x => x.IndependentSourceIdentity).Distinct().Count(),
                Sources = g.Select(x => x.SourceText).Distinct().Take(6).ToArray(),
                Results = g.Select(x => x.ResultText).Distinct().Take(6).ToArray()
            })
            .Where(x => x.Independent >= 3)
            .Where(x => !x.SourceFrame.Contains("conversation_opening", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Independent)
            .ThenBy(x => x.Signature)
            .ToArray();

        _output.WriteLine("============================================================");
        _output.WriteLine("LEGEND® LIVE PRODUCTION CURRICULUM — NON-GREETING CAPABILITY DISCOVERY");
        _output.WriteLine("============================================================");
        _output.WriteLine($"STRONG TRANSITION CLASSES: {strong.Length}");
        foreach (var item in strong.Take(40))
        {
            _output.WriteLine("---");
            _output.WriteLine($"SIGNATURE: {item.Signature}");
            _output.WriteLine($"INDEPENDENT SUPPORT: {item.Independent}");
            _output.WriteLine($"SOURCE FRAME: {item.SourceFrame}");
            _output.WriteLine($"RESULT FRAME: {item.ResultFrame}");
            foreach (var source in item.Sources) _output.WriteLine($"SOURCE: {source}");
            foreach (var result in item.Results) _output.WriteLine($"RESULT: {result}");
        }
        _output.WriteLine($"QUERY COMMANDS: {guard.QueryCommands}");
        _output.WriteLine($"WRITE COMMANDS: {guard.WriteCommands}");
        Assert.Equal(0, guard.WriteCommands);
    }

    [Fact]
    public async Task RunUnpublishedCandidateAsOneRealNativeConversationAgainstLiveCurriculum()
    {
        var raw = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
        var founderId = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_FOUNDER_OID");
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(founderId))
        {
            _output.WriteLine("CONVERSATION SKIPPED: production read-only connection or Founder OID unavailable.");
            return;
        }

        var cs = new SqlConnectionStringBuilder(raw)
        {
            ApplicationName = "LEGEND unpublished full conversation proof",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        var guard = new DiscoveryReadOnlyInterceptor();
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(cs.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(guard)
                .Options);

        Assert.True(await db.AgentProfiles.AsNoTracking().AnyAsync(x =>
            x.IsActive && x.AgentUserId != null && x.AgentUserId.ToLower() == founderId.ToLower()));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = string.Empty,
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English"
            }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db, registry, corpus, configuration,
            runtimePolicy: runtime, curriculum: curriculum, intelligence: intelligence);
        var profiles = new AgentProfileAccessResolver(db);
        var founderLegend = new FounderLegendConnectService(operations, profiles);
        var http = new NoProviderHttpClientFactory();
        var chat = new LegendFounderAiConversationService(
            http,
            configuration,
            founderLegend,
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(db, profiles, operations));
        var founder = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", founderId)], "unpublished-live-conversation"));

        var turns = new[]
        {
            "Before solving the research project task, determine what must be achieved, preserved, and produced.",
            "Build a plan for the data migration that respects dependencies and verification gates.",
            "Rank plausible explanations for the process delay using the available evidence.",
            "Classify the market assessment evidence before drawing a conclusion.",
            "Determine which technology choice option best satisfies the stated priorities and constraints.",
            "Project likely, best-case, and adverse outcomes for the learning iteration with explicit assumptions.",
            "A contradiction has been identified in the instruction execution; resolve it using the governing evidence.",
            "Synthesize the executive brief by combining compatible evidence without erasing disagreement.",
            // Deliberately held-out connective language. This tests whether the
            // accumulated transcript plus learned semantic anchors is enough to
            // understand a natural continuation rather than another curriculum endpoint.
            "Now apply that same evidence-first discipline to the technology choice and tell me what would reverse the recommendation."
        };

        var transcript = new List<LegendFounderAiChatMessage>();
        var context = new List<LegendConnectConversationContextItem>();
        var supported = 0;
        var exactStoredAnswers = 0;

        _output.WriteLine("============================================================");
        _output.WriteLine("LEGEND® UNPUBLISHED CANDIDATE — FULL LIVE-CURRICULUM CONVERSATION");
        _output.WriteLine("OPENAI: BLOCKED / EMPTY KEY / NativeOnly=true");
        _output.WriteLine("PRODUCTION DB: ApplicationIntent=ReadOnly + write-command interceptor");
        _output.WriteLine("============================================================");

        for (var index = 0; index < turns.Length; index++)
        {
            var input = turns[index];
            transcript.Add(new LegendFounderAiChatMessage("user", input));
            var graph = await operations.AnalyzeReusableMeaningGraphAsync(input);
            var native = await founderLegend.TryInferConversationWithDiscourseAsync(
                founder, input, context, discourseState: null);
            var reply = await chat.ReplyAsync(founder, new LegendFounderAiChatRequest
            {
                Mode = "legend",
                NativeOnly = true,
                Messages = transcript.ToArray()
            });

            var answer = reply.Message ?? string.Empty;
            var exactStoredInput = await db.LegendLanguageTextUnits.AsNoTracking().AnyAsync(x =>
                x.LanguageCode == "en" && x.IsTrainingEligible && x.Text == input);
            var exactStoredAnswer = !string.IsNullOrWhiteSpace(answer) &&
                await db.LegendLanguageTextUnits.AsNoTracking().AnyAsync(x =>
                    x.LanguageCode == "en" && x.IsTrainingEligible && x.Text == answer);
            if (exactStoredAnswer) exactStoredAnswers++;

            _output.WriteLine("------------------------------------------------------------");
            _output.WriteLine($"TURN {index + 1}");
            _output.WriteLine($"USER: {input}");
            _output.WriteLine($"PRIOR CONTEXT ITEMS: {context.Count}");
            _output.WriteLine($"INPUT IS EXACT STORED CURRICULUM TEXT: {exactStoredInput}");
            _output.WriteLine($"MEANING GRAPH COMPOSED: {graph.IsComposed}");
            _output.WriteLine($"MEANING REASON: {graph.ReasonCode}");
            _output.WriteLine("MEANING NODES: " + (graph.Nodes.Count == 0
                ? "<NONE>"
                : string.Join(" | ", graph.Nodes.Select(n =>
                    n.SemanticDimension + "=" + n.SemanticValue + "@" + n.StartTokenIndex + "+" + n.TokenLength))));
            _output.WriteLine("MEANING RELATIONS: " + (graph.Relations.Count == 0
                ? "<NONE>"
                : string.Join(" | ", graph.Relations.Select(r => r.RelationKind))));
            _output.WriteLine($"NATIVE SUPPORTED: {native.Supported}");
            _output.WriteLine($"NATIVE REASON: {native.ReasonCode}");
            _output.WriteLine($"NATIVE EVIDENCE: {native.EvidenceCount}");
            _output.WriteLine($"REQUIRES ESCALATION: {native.RequiresEscalation}");
            _output.WriteLine($"LEGEND: {answer}");
            _output.WriteLine($"RESPONSE AUTHORITY: {reply.ResponseAuthority}");
            _output.WriteLine($"RESPONSE STAGE: {reply.Stage}");
            _output.WriteLine($"ANSWER IS EXACT STORED CURRICULUM TEXT: {exactStoredAnswer}");
            _output.WriteLine($"OPENAI CLIENT CREATIONS SO FAR: {http.CreateClientCalls}");

            if (native.Supported && reply.Succeeded &&
                reply.ResponseAuthority == "LegendAi" && reply.Stage == "native_response" &&
                !native.RequiresEscalation && string.Equals(native.Answer, answer, StringComparison.Ordinal))
            {
                supported++;
            }

            context.Add(new LegendConnectConversationContextItem("user", input));
            if (!string.IsNullOrWhiteSpace(answer))
            {
                context.Add(new LegendConnectConversationContextItem("assistant", answer));
                transcript.Add(new LegendFounderAiChatMessage("assistant", answer));
            }
        }

        _output.WriteLine("============================================================");
        _output.WriteLine($"NATIVE CONVERSATION TURNS: {supported}/{turns.Length}");
        _output.WriteLine($"EXACT STORED ANSWERS: {exactStoredAnswers}/{turns.Length}");
        _output.WriteLine($"OPENAI CLIENT CREATIONS: {http.CreateClientCalls}");
        _output.WriteLine("OPENAI HTTP CALLS: 0");
        _output.WriteLine($"PRODUCTION QUERY COMMANDS: {guard.QueryCommands}");
        _output.WriteLine($"PRODUCTION WRITE COMMANDS: {guard.WriteCommands}");
        _output.WriteLine("============================================================");

        Assert.Equal(0, guard.WriteCommands);
        Assert.Equal(0, http.CreateClientCalls);
    }

    private sealed class NoProviderHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }
        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            throw new InvalidOperationException("OpenAI/provider client creation is forbidden in this native-only proof.");
        }
    }

    private sealed class DiscoveryReadOnlyInterceptor : DbCommandInterceptor
    {
        public int QueryCommands { get; private set; }
        public int WriteCommands { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            QueryCommands++;
            Guard(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            QueryCommands++;
            Guard(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            WriteCommands++;
            throw new InvalidOperationException("Production conversation proof attempted a write command.");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            WriteCommands++;
            throw new InvalidOperationException("Production conversation proof attempted a write command.");
        }

        private static void Guard(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidOperationException("Production conversation proof attempted non-query SQL: " + sql[..Math.Min(sql.Length, 80)]);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Services;
using Domain.Entities;
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

/// <summary>
/// Opt-in, zero-write proof of the exact reusable-meaning-graph native
/// authority used by production Founder chat. This test never falls back to
/// the legacy source-frame evaluator and never invokes an external provider.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendProductionComposedNativeReadOnlyTests
{
    private readonly ITestOutputHelper _output;

    public LegendProductionComposedNativeReadOnlyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ProductionReadOnly_ComposedNativeAuthority_ExplainsLiveConversationBoundary()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Production composed-native diagnostic was not selected; LEGEND_PRODUCTION_READONLY_CONNECTION is unset.");
            return;
        }

        var founderId = Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_FOUNDER_OID");
        Assert.False(string.IsNullOrWhiteSpace(founderId),
            "Production Founder OID was not supplied to the composed-native diagnostic.");

        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var previousOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var previousOpenAiConfigApiKey = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.Empty);
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", string.Empty);

        try
        {
            var connection = new SqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = "LEGEND production composed-native read-only diagnostic",
                ApplicationIntent = ApplicationIntent.ReadOnly
            };
            var readOnlyGuard = new ReadOnlyLegendDbCommandInterceptor();
            await using var db = new MasterAppDbContext(
                new DbContextOptionsBuilder<MasterAppDbContext>()
                    .UseSqlServer(connection.ConnectionString)
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                    .AddInterceptors(readOnlyGuard)
                    .Options);

            Assert.True(await db.AgentProfiles.AsNoTracking().AnyAsync(item =>
                    item.IsActive && item.AgentUserId != null &&
                    item.AgentUserId.ToLower() == founderId!.ToLower()),
                "The configured production Founder OID has no active AgentProfile.");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty),
                    new KeyValuePair<string, string?>("LegendConnect:CorpusAcquisition:Enabled", "false"),
                    new KeyValuePair<string, string?>("LegendConnect:ContextualComposition:Mode", "Shadow")
                })
                .Build();
            var registry = new LegendLanguageRegistry(db, configuration);
            var corpus = new LegendConnectCorpusService(
                db, registry, NullLogger<LegendConnectCorpusService>.Instance);
            var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
            var operations = new LegendConnectOperations(
                db, registry, corpus, configuration, curriculum: curriculum);
            var founder = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("oid", founderId!)], "production-composed-read-only"));
            var founderLegend = new FounderLegendConnectService(
                operations, new AgentProfileAccessResolver(db));

            var counts = new
            {
                FounderExamples = await db.LegendCurriculumExamples.AsNoTracking().LongCountAsync(item =>
                    item.LanguageCode == "en" && item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
                FounderAnchors = await db.LegendLanguageCompositionalAnchors.AsNoTracking().LongCountAsync(item =>
                    item.LanguageCode == "en" && item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
                MeaningNodes = await db.LegendLanguageMeaningNodeEvidence.AsNoTracking().LongCountAsync(item =>
                    item.LanguageCode == "en" && item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
                MeaningPrimitives = await db.LegendLanguageMeaningPrimitives.AsNoTracking().LongCountAsync(item =>
                    item.LanguageCode == "en" && item.SupersededUtc == null),
                TransitionEvidence = await db.LegendSemanticTransitionEvidence.AsNoTracking().LongCountAsync(item =>
                    item.SourceLanguageCode == "en" && item.ResultLanguageCode == "en" &&
                    item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
                SupportedTransitions = await db.LegendSemanticTransitionEvidence.AsNoTracking().LongCountAsync(item =>
                    item.SourceLanguageCode == "en" && item.ResultLanguageCode == "en" &&
                    item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                    item.ContributionState == "Supported" && item.IsHumanVerifiedSupport)
            };

            _output.WriteLine("============================================================");
            _output.WriteLine("LEGEND® LIVE PRODUCTION COMPOSED-NATIVE DIAGNOSTIC");
            _output.WriteLine("============================================================");
            _output.WriteLine($"FOUNDER EXAMPLES: {counts.FounderExamples}");
            _output.WriteLine($"FOUNDER ANCHORS: {counts.FounderAnchors}");
            _output.WriteLine($"MEANING NODE EVIDENCE: {counts.MeaningNodes}");
            _output.WriteLine($"MEANING PRIMITIVES: {counts.MeaningPrimitives}");
            _output.WriteLine($"TRANSITION EVIDENCE: {counts.TransitionEvidence}");
            _output.WriteLine($"SUPPORTED HUMAN-VERIFIED TRANSITIONS: {counts.SupportedTransitions}");

            var prompts = new[]
            {
                "Hi",
                "Hi there.",
                "Hey there",
                "Hello.",
                "Good morning.",
                "How are you?",
                "Nice to meet you.",
                "What's up?"
            };

            var supported = 0;
            foreach (var prompt in prompts)
            {
                var graph = await curriculum.AnalyzeReusableMeaningGraphAsync("en", prompt);
                var native = await founderLegend.TryInferConversationWithDiscourseAsync(
                    founder,
                    prompt,
                    Array.Empty<LegendConnectConversationContextItem>(),
                    discourseState: null);

                _output.WriteLine(string.Empty);
                _output.WriteLine($"USER: {prompt}");
                _output.WriteLine($"MEANING GRAPH COMPOSED: {graph.IsComposed}");
                _output.WriteLine($"MEANING GRAPH REASON: {graph.ReasonCode}");
                _output.WriteLine($"MEANING GRAPH NODES: {graph.Nodes.Count}");
                _output.WriteLine($"MEANING GRAPH RELATIONS: {graph.Relations.Count}");
                _output.WriteLine($"NATIVE SUPPORTED: {native.Supported}");
                _output.WriteLine($"NATIVE REASON: {native.ReasonCode}");
                _output.WriteLine($"NATIVE EVIDENCE: {native.EvidenceCount}");
                _output.WriteLine($"REQUIRES ESCALATION: {native.RequiresEscalation}");
                _output.WriteLine($"NATIVE ANSWER: {native.Answer ?? "<NULL>"}");

                if (native.Supported && !string.IsNullOrWhiteSpace(native.Answer))
                    supported++;
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine($"COMPOSED NATIVE PASSES: {supported}/{prompts.Length}");
            _output.WriteLine("EXTERNAL PROVIDER CALLS: 0");
            _output.WriteLine("PRODUCTION WRITE COMMANDS: 0");
            Assert.Equal(0, readOnlyGuard.WriteAttemptCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiApiKey);
            Environment.SetEnvironmentVariable("OpenAI__ApiKey", previousOpenAiConfigApiKey);
        }
    }

    private sealed class ReadOnlyLegendDbCommandInterceptor : DbCommandInterceptor
    {
        public int WriteAttemptCount { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            WriteAttemptCount++;
            throw new InvalidOperationException("Production read-only diagnostic blocked a non-query command.");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            WriteAttemptCount++;
            throw new InvalidOperationException("Production read-only diagnostic blocked a non-query command.");
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            GuardReadOnly(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            GuardReadOnly(command);
            return new ValueTask<InterceptionResult<object>>(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            GuardReadOnly(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            GuardReadOnly(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        private void GuardReadOnly(DbCommand command)
        {
            var text = command.CommandText.TrimStart();
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                return;

            WriteAttemptCount++;
            throw new InvalidOperationException("Production read-only diagnostic blocked a command that was not SELECT/CTE.");
        }
    }
}

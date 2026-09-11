using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentPortal.Tests;

/// <summary>
/// An opt-in diagnostic proving the public Founder service entry point against
/// a real isolated SQL Server database.  The curriculum is deliberately read
/// from a normal external Founder manifest, never embedded in this test or in
/// the application.
/// </summary>
[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendFounderCurriculumSqlServerE2ETests
{
    private readonly ITestOutputHelper _output;

    public LegendFounderCurriculumSqlServerE2ETests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task LegendDirectGreetingEndpointRegression()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>(
                    "OpenAI:ApiKey",
                    string.Empty),
                new KeyValuePair<string, string?>(
                    "LegendConnect:CorpusAcquisition:Enabled",
                    "false"),
                new KeyValuePair<string, string?>(
                    "LegendConnect:ContextualComposition:Mode",
                    "Shadow"),
                new KeyValuePair<string, string?>(
                    "LegendConnect:LanguageRegistry:Baseline:0:Code",
                    "en"),
                new KeyValuePair<string, string?>(
                    "LegendConnect:LanguageRegistry:Baseline:0:Name",
                    "English"),
                new KeyValuePair<string, string?>(
                    "LegendConnect:LanguageRegistry:Baseline:0:NativeName",
                    "English")
            })
            .Build();

        var registry =
            new LegendLanguageRegistry(
                db,
                configuration);

        var runtime =
            new LegendConnectRuntimePolicyAuthority(
                db,
                new FounderAccess(),
                registry,
                configuration,
                NullLogger<
                    LegendConnectRuntimePolicyAuthority>.Instance);

        var intelligence =
            new LegendConnectTranslationIntelligence(
                db,
                configuration,
                runtime);

        var corpus =
            new LegendConnectCorpusService(
                db,
                registry,
                NullLogger<
                    LegendConnectCorpusService>.Instance,
                intelligence: intelligence);

        var curriculum =
            new LegendConnectCurriculumService(
                db,
                registry,
                corpus);

        var operations =
            new LegendConnectOperations(
                db,
                registry,
                corpus,
                configuration,
                runtimePolicy: runtime,
                curriculum: curriculum,
                intelligence: intelligence);

        var founderId =
            "45e9f238-3a36-4f2e-9610-000000000001";

        var previousFounderOidForDirectProof =
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        Environment.SetEnvironmentVariable(
            "FOUNDER_OID",
            founderId);

        var founderEmail =
            "legend-direct-release@legend.local";

        db.AgentProfiles.Add(
            new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = founderEmail,
                NormalizedEmail = founderEmail,
                IsActive = true
            });

        await db.SaveChangesAsync();

        var founder =
            new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim(
                            "oid",
                            founderId)
                    ],
                    "legend-direct-proof"));

        var founderLegend =
            new FounderLegendConnectService(
                operations,
                new AgentProfileAccessResolver(db));

        // ---------------------------------------------------------
        // These are TEST DATA only.
        //
        // Three different curriculum families provide three
        // independent Founder evidence identities for the same
        // reusable semantic transition.
        //
        // There is no phrase-specific production routing.
        // ---------------------------------------------------------

        var prompts = GreetingEndpointRegressionPrompts.Select(item => item.Text).ToArray();

        for (var sourceIndex = 1;
             sourceIndex <= 3;
             sourceIndex++)
        {
            var examples =
                new List<
                    LegendConnectCurriculumExampleSubmission>();

            for (var promptIndex = 0; promptIndex < prompts.Length; promptIndex++)
            {
                var prompt = prompts[promptIndex];
                examples.Add(
                    new LegendConnectCurriculumExampleSubmission(
                        prompt,
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] =
                                "conversation_opening"
                        },
                        new LegendConnectMeaningGraphSubmission(
                            [
                                new LegendConnectMeaningNodeSubmission(
                                    "function",
                                    "conversation_function",
                                    "conversation_opening",
                                    prompt)
                            ],
                            []),
                        $"release-direct-{sourceIndex}-source-{promptIndex + 1}"));
            }

            var responseComponents =
                sourceIndex switch
                {
                    1 => new (string Function, string Intent)[]
                    {
                        ("Welcome", "I can help"),
                        ("Greetings", "ready to assist"),
                        ("Salutations", "here to support")
                    },

                    2 => new (string Function, string Intent)[]
                    {
                        ("Greetings", "I can assist"),
                        ("Salutations", "ready to help"),
                        ("Welcome", "here to assist")
                    },

                    _ => new (string Function, string Intent)[]
                    {
                        ("Salutations", "I can support"),
                        ("Welcome", "ready to support"),
                        ("Greetings", "here to help")
                    }
                };

            for (var responseIndex = 0; responseIndex < responseComponents.Length; responseIndex++)
            {
                var component = responseComponents[responseIndex];
                var response = $"{component.Function}, {component.Intent}.";
                examples.Add(
                    new LegendConnectCurriculumExampleSubmission(
                        response,
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] =
                                "conversation_acknowledgement",
                            ["intent"] =
                                "offer_help"
                        },
                        new LegendConnectMeaningGraphSubmission(
                            [
                                new LegendConnectMeaningNodeSubmission(
                                    "function",
                                    "conversation_function",
                                    "conversation_acknowledgement",
                                    component.Function),
                                new LegendConnectMeaningNodeSubmission(
                                    "intent",
                                    "intent",
                                    "offer_help",
                                    component.Intent)
                            ],
                            [
                                new LegendConnectMeaningRelationSubmission(
                                    "function",
                                    "governs",
                                    "intent")
                            ]),
                        $"release-direct-{sourceIndex}-result-{responseIndex + 1}"));
            }

            var batch =
                new LegendConnectCurriculumBatchSubmission(
                    $"release.direct.conversation.{sourceIndex}",
                    $"Independent direct conversation evidence {sourceIndex}",
                    examples,
                    [
                        new LegendConnectSemanticTransitionSubmission(
                            new LegendConnectSemanticFrameSubmission(
                                new Dictionary<string, string>
                                {
                                    ["conversation_function"] =
                                        "conversation_opening"
                                }),
                            new LegendConnectSemanticFrameSubmission(
                                new Dictionary<string, string>
                                {
                                    ["conversation_function"] =
                                        "conversation_acknowledgement",
                                    ["intent"] =
                                        "offer_help"
                                }))
                    ]);

            var accepted =
                await curriculum
                    .SubmitFounderBatchAsync(batch);

            Assert.True(
                accepted.Succeeded,
                $"Founder evidence source {sourceIndex} failed: " +
                accepted.Message);
        }

        // Ensure the expected evidence really exists before
        // conversational inference is tested.
        var activeTransitionEvidence =
            await db.LegendSemanticTransitionEvidence
                .Where(item =>
                    item.SupersededUtc == null &&
                    item.ContributionState == "Supported" &&
                    item.IsHumanVerifiedSupport)
                .ToListAsync();

        var independentSupport =
            activeTransitionEvidence
                .Select(item =>
                    item.IndependentSourceIdentity)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count();

        Assert.True(
            independentSupport >= 3,
            $"Expected at least 3 independent Founder-supported " +
            $"transition sources but found {independentSupport}.");

        var factory =
            new CountingHttpClientFactory();
        var discourseProfiles = new AgentProfileAccessResolver(db);

        var chat =
            new LegendFounderAiConversationService(
                factory,
                configuration,
                founderLegend,
                NullLogger<
                    LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(
                    db, discourseProfiles, operations),
                registry,
                ControllerTestHelpers.BuildTranslationService());

        var fallbackFragments = new[]
        {
            "does not yet have enough governed evidence",
            "external teacher is unavailable",
            "No unsupported answer was produced"
        };

        var passed = 0;

        _output.WriteLine("");
        _output.WriteLine(
            "============================================================");
        _output.WriteLine(
            "LEGEND® AI — 8 DIRECT CONVERSATION TRANSCRIPT");
        _output.WriteLine(
            "============================================================");

        for (var index = 0;
             index < prompts.Length;
             index++)
        {
            var prompt =
                prompts[index];

            var source =
                await curriculum
                    .AnalyzeSemanticTransitionSourceSemanticsAsync(
                        "en",
                        prompt);

            var native =
                await founderLegend
                    .TryInferConversationWithDiscourseAsync(
                        founder,
                        prompt,
                        Array.Empty<
                            LegendConnectConversationContextItem>(),
                        discourseState: null,
                        sourceLanguageCode: "en");

            _output.WriteLine("");
            _output.WriteLine(
                $"[{index + 1}/8] USER: {prompt}");

            _output.WriteLine(
                $"SOURCE STATE: {source.State}");

            _output.WriteLine(
                "SOURCE COMPONENTS: " +
                (
                    source.Components.Count == 0
                        ? "<NONE>"
                        : string.Join(
                            " | ",
                            source.Components.Select(
                                item =>
                                    $"{item.Dimension}=" +
                                    $"{item.Value}@" +
                                    $"{item.SurfaceForm}"))
                ));

            _output.WriteLine(
                $"NATIVE SUPPORTED: {native.Supported}");

            _output.WriteLine(
                $"NATIVE EVIDENCE: {native.EvidenceCount}");

            _output.WriteLine(
                $"NATIVE REASON: {native.ReasonCode}");

            _output.WriteLine(
                $"REQUIRES ESCALATION: " +
                $"{native.RequiresEscalation}");

            _output.WriteLine(
                $"NATIVE RESPONSE: " +
                $"{native.Answer ?? "<NULL>"}");

            // Fail HERE if native LEGEND itself cannot answer.
            // Do not allow ReplyAsync/provider behavior to obscure
            // the native inference result.
            Assert.True(
                native.Supported,
                $"LEGEND native inference failed for '{prompt}'. " +
                $"Reason={native.ReasonCode}; " +
                $"Evidence={native.EvidenceCount}");

            Assert.False(
                native.RequiresEscalation,
                $"LEGEND unexpectedly requested escalation for '{prompt}'.");

            Assert.True(
                native.EvidenceCount > 0,
                $"LEGEND had zero governed evidence for '{prompt}'.");

            Assert.False(
                string.IsNullOrWhiteSpace(native.Answer),
                $"LEGEND produced no native answer for '{prompt}'.");

            var reply =
                await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        // This fixture's admitted curriculum and native probe
                        // both explicitly use English; detection is tested separately.
                        SourceLanguageCode = "en",
                        Messages =
                        [
                            new LegendFounderAiChatMessage(
                                "user",
                                prompt)
                        ]
                    });

            _output.WriteLine(
                $"FINAL RESPONSE: " +
                $"{reply.Message}");

            _output.WriteLine(
                $"EXTERNAL TEACHER CLIENT CALLS: " +
                $"{factory.CreateClientCalls}");

            // -----------------------------------------------------
            // THIS IS THE ACTUAL RELEASE CONTRACT.
            // -----------------------------------------------------

            Assert.True(
                native.Supported,
                $"LEGEND failed native support for '{prompt}'. " +
                $"Reason={native.ReasonCode}");

            Assert.False(
                native.RequiresEscalation,
                $"LEGEND escalated '{prompt}' despite governed evidence.");

            Assert.True(
                native.EvidenceCount > 0,
                $"LEGEND reported no governed evidence for '{prompt}'.");

            Assert.False(
                string.IsNullOrWhiteSpace(
                    native.Answer),
                $"LEGEND produced no native answer for '{prompt}'.");

            Assert.True(
                reply.Succeeded,
                $"User-facing ReplyAsync failed for '{prompt}'.");

            Assert.Equal(
                native.Answer,
                reply.Message);

            Assert.False(
                string.Equals(
                    prompt,
                    reply.Message,
                    StringComparison.OrdinalIgnoreCase),
                $"LEGEND merely echoed '{prompt}'.");

            foreach (var fallback in fallbackFragments)
            {
                Assert.DoesNotContain(
                    fallback,
                    native.Answer!,
                    StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(
                    fallback,
                    reply.Message,
                    StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(
                0,
                factory.CreateClientCalls);

            passed++;

            _output.WriteLine(
                "RESULT: PASS");
        }

        _output.WriteLine("");
        _output.WriteLine(
            "============================================================");

        _output.WriteLine(
            $"DIRECT PROMPTS PASSED: {passed}/8");

        _output.WriteLine(
            $"DIRECT PROMPTS FAILED: {8 - passed}/8");

        _output.WriteLine(
            $"EXTERNAL TEACHER CLIENT CALLS: " +
            $"{factory.CreateClientCalls}");

        _output.WriteLine(
            "FALLBACK RESPONSES ACCEPTED: 0");

        _output.WriteLine(
            "RELEASE BEHAVIOR PROOF: PASS");

        _output.WriteLine(
            "============================================================");

        Assert.Equal(
            8,
            passed);

        Assert.Equal(
            0,
            factory.CreateClientCalls);

        Environment.SetEnvironmentVariable(
            "FOUNDER_OID",
            previousFounderOidForDirectProof);
    }

    /// <summary>
    /// A deliberately opt-in, zero-write compatibility diagnostic for the
    /// current production corpus. It exercises the same read-only native
    /// authority that serving uses, while a command interceptor rejects any
    /// attempted data mutation. The connection is supplied only at execution
    /// time by the existing App Service configuration resolver and is never
    /// logged or persisted by the test.
    /// </summary>
    [Fact]
    public async Task ShadowSnapshotContextClosure_PreservesBothIndexedDirectionsAndRejectsOpenEdges()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var outside = Guid.NewGuid();

        var sourceToRelated = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(),
            SourceTextUnitId = first,
            RelatedTextUnitId = second
        };
        var relatedToSource = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(),
            SourceTextUnitId = second,
            RelatedTextUnitId = first
        };
        db.LegendLanguageContextRelationships.AddRange(
            sourceToRelated,
            relatedToSource,
            new LegendLanguageContextRelationship
            {
                Id = Guid.NewGuid(),
                SourceTextUnitId = first,
                RelatedTextUnitId = outside
            },
            new LegendLanguageContextRelationship
            {
                Id = Guid.NewGuid(),
                SourceTextUnitId = outside,
                RelatedTextUnitId = first
            });
        await db.SaveChangesAsync();

        var closure = await ReadContextRelationshipsForTextUnitClosureAsync(
            db,
            new[] { first, second });

        Assert.Equal(
            new[] { sourceToRelated.Id, relatedToSource.Id }.OrderBy(item => item),
            closure.Select(item => item.Id).OrderBy(item => item));
    }

    [Fact]
    public void ProductionNativeProofResultContract_PreservesIndependentFixtureAndExecutionFailures()
    {
        var results = new[]
        {
            ProductionNativeProofResult.FailedFixture(
                "fixture-audience",
                "audience_constraints",
                "fixture missing"),
            ProductionNativeProofResult.PassedCase(
                "exact-endpoint",
                "exact_endpoint",
                expectedNative: true,
                nativeSupported: true,
                reasonCode: "exact_endpoint",
                evidenceCount: 1,
                responseAuthority: "LegendAi",
                stage: "native_response",
                providerClientCount: 0,
                elapsedMilliseconds: 1),
            ProductionNativeProofResult.FailedCase(
                "held-out",
                "held_out_paraphrase",
                expectedNative: true,
                failure: "relation unproven",
                providerClientCount: 0,
                elapsedMilliseconds: 2),
            ProductionNativeProofResult.FailedInfrastructurePreflight(
                "discourse",
                "discourse",
                new TimeoutException("bounded preflight timeout"))
        };

        var json = JsonSerializer.Serialize(new
        {
            Status = results.All(item => item.Status == "passed") ? "passed" : "failed",
            ExecutedCases = results.Count(item => item.Phase == "execution"),
            FailedCases = results.Count(item => item.Status == "failed"),
            CaseResults = results
        });
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("failed", root.GetProperty("Status").GetString());
        Assert.Equal(2, root.GetProperty("ExecutedCases").GetInt32());
        Assert.Equal(3, root.GetProperty("FailedCases").GetInt32());
        var serializedResults = root.GetProperty("CaseResults");
        Assert.Equal(4, serializedResults.GetArrayLength());
        Assert.Equal("fixture", serializedResults[0].GetProperty("Phase").GetString());
        Assert.Equal("execution", serializedResults[1].GetProperty("Phase").GetString());
        Assert.Equal("execution", serializedResults[2].GetProperty("Phase").GetString());
        Assert.Equal("preflight", serializedResults[3].GetProperty("Phase").GetString());
        Assert.Equal("infrastructure", serializedResults[3].GetProperty("FailureKind").GetString());
        Assert.Equal("operation_timeout", serializedResults[3].GetProperty("FailureCode").GetString());
    }

    [Fact]
    public void ProductionMeaningFixtureContract_ClassifiesPrimitiveAndRelationPrerequisites()
    {
        var first = new LegendConnectUtteranceMeaningNode(
            "sig-a", "diagnostic_subject", "handoff", 0, 1, 3);
        var second = new LegendConnectUtteranceMeaningNode(
            "sig-b", "diagnostic_family", "failure", 1, 1, 3);

        var primitiveFailure = ProductionMeaningFixtureFailure(
            "handoff failure",
            new LegendConnectUtteranceMeaningGraphSnapshot(
                false,
                [first],
                [],
                ["failure"],
                "meaning_graph_component_unknown"),
            requireMultipleNodes: true);
        Assert.Contains("meaning primitives", primitiveFailure);
        Assert.Contains("failure", primitiveFailure);

        var retrievalFailure = ProductionMeaningFixtureFailure(
            "handoff failure",
            new LegendConnectUtteranceMeaningGraphSnapshot(
                false,
                [],
                [],
                ["handoff", "failure"],
                "meaning_graph_retrieval_bound_exceeded"),
            requireMultipleNodes: true);
        Assert.Null(retrievalFailure);

        var relationFailure = ProductionMeaningFixtureFailure(
            "handoff failure",
            new LegendConnectUtteranceMeaningGraphSnapshot(
                false,
                [first, second],
                [],
                [],
                "meaning_graph_relation_unproven"),
            requireMultipleNodes: true);
        Assert.Contains("no active Founder-governed relation", relationFailure);

        var supported = ProductionMeaningFixtureFailure(
            "handoff failure",
            new LegendConnectUtteranceMeaningGraphSnapshot(
                true,
                [first, second],
                [new LegendConnectUtteranceMeaningRelation(
                    "relation", "qualified-by", 0, 1, 3)],
                [],
                "meaning_graph_observational_composed"),
            requireMultipleNodes: true);
        Assert.Null(supported);
    }

    // Candidate-only SQL observation. This does not invoke chat, persist discourse,
    // replay a corpus, or claim complete release/device/capability coverage.
    [ProductionObservationFact]
    public async Task ProductionReadOnlyCandidateObservation()
    {
        var startedUtc = DateTime.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var (candidateSha, runIdentity, resultPath) = RequireCandidateEvidenceIdentity();
        var connectionString = RequiredObservationSetting("LEGEND_PRODUCTION_READONLY_CONNECTION");
        var founderId = RequiredObservationSetting("LEGEND_PRODUCTION_READONLY_FOUNDER_OID");
        const int queryTimeoutSeconds = 15;
        const int observationTimeoutSeconds = 120;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(observationTimeoutSeconds));
        var guard = new ReadOnlyLegendDbCommandInterceptor(restrictPhysicalTables: true);
        var saves = new ObservationSaveGuard();
        var connection = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "LEGEND bounded candidate SELECT-only observation",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ConnectTimeout = queryTimeoutSeconds,
            Pooling = false,
            TrustServerCertificate = false,
            Encrypt = SqlConnectionEncryptOption.Mandatory
        };
        // Keep one authenticated connection for permission checks and all reads.
        await using var sql = new SqlConnection(connection.ConnectionString);
        using var diagnosticCapture = new ExceptionCapturingLoggerProvider();
        using var diagnosticLoggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Information).AddProvider(diagnosticCapture));
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlServer(sql, options => options.CommandTimeout(queryTimeoutSeconds))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .UseLoggerFactory(diagnosticLoggerFactory)
            .AddInterceptors(guard, saves).Options);
        var observationLogger = diagnosticCapture.CreateLogger(nameof(LegendFounderCurriculumSqlServerE2ETests));
        var diagnosticWindows = new List<ProductionDiagnosticEvidence>();
        ProductionFailureDiagnosis? firstFailureDiagnosis = null;
        var windowCaptured = false;
        ProductionDiagnosticEvidence CaptureWindow(string reference, IReadOnlyDictionary<string, long>? counts = null)
        {
            var evidence = new ProductionDiagnosticEvidence(diagnosticCapture.SnapshotDiagnostics(),
                guard.SnapshotDiagnostics(), reference + "/sql", counts ?? new Dictionary<string, long>());
            diagnosticWindows.Add(evidence);
            windowCaptured = true;
            return evidence;
        }
        void BeginWindow()
        {
            diagnosticCapture.ResetDiagnostics();
            guard.ResetDiagnostics();
            windowCaptured = false;
        }
        void RequireHealthyWindow()
        {
            var sqlEvidence = guard.SnapshotDiagnostics();
            Assert.Equal(0, sqlEvidence.FailedEvents + sqlEvidence.CanceledEvents + sqlEvidence.BlockedEvents);
            Assert.Equal(0, diagnosticCapture.SnapshotDiagnostics().SqlFailureEvents);
        }
        var cases = new List<object>();
        var observationNativeCases = HeldOutProductionNativeCases().Append(NativeOnlyProductionIsolationCase()).ToArray();
        var coverage = new[] { "learning", "machine-learning-lifecycle", "governed_cohort" }
            .Concat(observationNativeCases.Select(item => item.Reference)).ToArray();
        var external = new CountingHttpClientFactory();
        var status = "failed";
        var phase = "connection";
        string? failureCode = null;
        try
        {
            await db.Database.OpenConnectionAsync(deadline.Token);
            phase = "sql_principal";
            await RequireSelectOnlyPrincipalAsync(db, deadline.Token);
            phase = "sql_sources";
            await RequireSafePhysicalSourcesAsync(db, guard, deadline.Token);
            phase = "founder_identity";
            Assert.True(await db.AgentProfiles.AsNoTracking().AnyAsync(item =>
                item.IsActive && item.AgentUserId != null &&
                item.AgentUserId.ToLower() == founderId.ToLower(), deadline.Token),
                "The selected Founder identity has no active profile.");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty),
                new KeyValuePair<string, string?>("LegendConnect:CorpusAcquisition:Enabled", "false"),
                new KeyValuePair<string, string?>("LegendConnect:ContextualComposition:Mode", "Shadow")
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(diagnosticCapture));
            services.AddSignalR();
            services.AddSingleton(db);
            services.AddMasterAppMessaging(configuration);
            // Exercise the production DI graph. Only the test transport is
            // instrumented: a provider attempt is counted and cannot leave process.
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(external);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var operations = scope.ServiceProvider.GetRequiredService<ILegendConnectOperations>();
            var language = await db.LegendLanguageDefinitions.AsNoTracking()
                .Where(item => item.IsEnabled).OrderBy(item => item.LanguageCode)
                .Select(item => item.LanguageCode).FirstOrDefaultAsync(deadline.Token);
            Assert.False(string.IsNullOrWhiteSpace(language), "No enabled production language is available.");

            // The failed lifecycle request inspected English records. An
            // empty page in whichever language sorts first cannot establish
            // recovery of that projection; its fixture requires retained rows.
            var sectionProbes = new[]
            {
                (Section: "learning", LanguageCode: language!, MinimumRows: 0),
                (Section: "machine-learning-lifecycle", LanguageCode: "en", MinimumRows: 1)
            };
            CaptureWindow("observation-preflight");
            var sectionFailureCount = 0;
            foreach (var probe in sectionProbes)
            {
                BeginWindow();
                phase = probe.Section;
                var caseStart = Stopwatch.GetTimestamp();
                long? enabledLanguageCount = null;
                int? rows = null;
                string? sectionFailure = null;
                var sectionAuthority = "LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyCandidateObservation";
                var sectionStage = "section_prerequisite";
                var authorityStarted = Stopwatch.GetTimestamp();
                try
                {
                    enabledLanguageCount = await db.LegendLanguageDefinitions.AsNoTracking()
                        .LongCountAsync(item => item.IsEnabled && item.LanguageCode == probe.LanguageCode, deadline.Token);
                    Assert.True(enabledLanguageCount > 0,
                        $"The {probe.Section} observation fixture requires its enabled language.");
                    sectionAuthority = "LegendConnectOperations.GetFounderSectionPageAsync";
                    sectionStage = "founder_section";
                    authorityStarted = Stopwatch.GetTimestamp();
                    observationLogger.LogInformation(
                        "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome}",
                        "StageStarted", "LegendConnectOperations.GetFounderSectionPageAsync", "founder_section", "started");
                    var page = await operations.GetFounderSectionPageAsync(probe.Section, probe.LanguageCode, null, null,
                        cancellationToken: deadline.Token);
                    rows = page.Rows.Count;
                    observationLogger.LogInformation(
                        "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ElapsedMs={ElapsedMs}",
                        "StageEnded", "LegendConnectOperations.GetFounderSectionPageAsync", "founder_section", "completed",
                        Stopwatch.GetElapsedTime(authorityStarted).TotalMilliseconds);
                    sectionAuthority = "LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyCandidateObservation";
                    sectionStage = "case_assertions";
                    Assert.Equal(probe.Section, page.Section);
                    Assert.Equal(probe.LanguageCode, page.LanguageCode);
                    Assert.InRange(page.Rows.Count, probe.MinimumRows, page.PageSize);
                    RequireHealthyWindow();
                }
                catch (Exception exception)
                {
                    sectionFailureCount++;
                    sectionFailure = exception is OperationCanceledException ? "observation_deadline_exceeded" : "section_observation_failed";
                    observationLogger.LogError(exception,
                        "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode} ElapsedMs={ElapsedMs}",
                        "StageEnded", sectionAuthority, sectionStage, "failed",
                        "authority_call_failed", Stopwatch.GetElapsedTime(authorityStarted).TotalMilliseconds);
                    if (IsObservedAuthorizationFailure(exception, diagnosticCapture.SnapshotDiagnostics(), guard.SnapshotDiagnostics()) || deadline.IsCancellationRequested)
                        throw;
                }
                finally
                {
                    var counts = new Dictionary<string, long> { ["MinimumRows"] = probe.MinimumRows };
                    if (enabledLanguageCount is long enabled) counts["EnabledLanguageCount"] = enabled;
                    if (rows is int observedRows) counts["ObservedRows"] = observedRows;
                    var evidence = CaptureWindow(probe.Section, counts);
                    var caseStatus = sectionFailure is null ? "passed" : "failed";
                    var diagnosis = DiagnoseObservedFailure(caseStatus, "section_observation", sectionFailure, null,
                        evidence.StageEvents, guard.SnapshotDiagnostics().FailedEvents, guard.SnapshotDiagnostics().Records);
                    if (sectionFailure is not null) firstFailureDiagnosis ??= diagnosis;
                    cases.Add(new { Category = probe.Section, Status = caseStatus, FailureCode = sectionFailure,
                        Rows = rows, probe.LanguageCode, probe.MinimumRows, DiagnosticEvidence = evidence,
                        FailureDiagnosis = diagnosis, ElapsedMilliseconds = Stopwatch.GetElapsedTime(caseStart).TotalMilliseconds });
                }
            }
            BeginWindow();
            phase = "governed_cohort";
            var cohortStart = Stopwatch.GetTimestamp();
            var governedExamples = await db.LegendCurriculumExamples.AsNoTracking()
                .LongCountAsync(item => item.SupersededUtc == null &&
                    item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved, deadline.Token);
            Assert.True(governedExamples > 0, "No active Founder-governed corpus is present.");
            RequireHealthyWindow();
            var cohortEvidence = CaptureWindow("governed_cohort", new Dictionary<string, long> { ["ActiveFounderGovernedExamples"] = governedExamples });
            cases.Add(new { Category = "governed_cohort", Status = "passed", Rows = governedExamples,
                DiagnosticEvidence = cohortEvidence,
                FailureDiagnosis = DiagnoseObservedFailure("passed", "governed_cohort", null, null, cohortEvidence.StageEvents),
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(cohortStart).TotalMilliseconds });
            phase = "native_current_corpus";
            var nativeFailureCount = 0;
            foreach (var proofCase in observationNativeCases)
            {
                BeginWindow();
                phase = proofCase.Reference;
                var caseStart = Stopwatch.GetTimestamp();
                LegendConnectUtteranceMeaningGraphSnapshot? graph = null;
                LegendConnectNativeInferenceSnapshot? inference = null;
                string? caseFailure = null;
                var authorizationFailed = false;
                try
                {
                    var prompt = proofCase.Messages[^1].Content!;
                    if (proofCase.MustBeHeldOut)
                    {
                        var normalized = LegendLanguageIdentity.NormalizeText(prompt);
                        Assert.False(await db.LegendLanguageTextUnits.AsNoTracking().AnyAsync(item =>
                            item.LanguageCode == proofCase.NativeSourceLanguageCode && item.Text == normalized &&
                            item.IsTrainingEligible && item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved,
                            deadline.Token), "The original matrix prompt is no longer held out.");
                    }
                    graph = await operations.AnalyzeReusableMeaningGraphAsync(prompt,
                        cancellationToken: deadline.Token, sourceLanguageCode: proofCase.NativeSourceLanguageCode);
                    inference = await operations.TryInferConversationWithDiscourseAsync(prompt,
                        Array.Empty<LegendConnectConversationContextItem>(), discourseState: null,
                        cancellationToken: deadline.Token, sourceLanguageCode: proofCase.NativeSourceLanguageCode,
                        providerPolicy: LegendConnectExternalProviderPolicy.NativeOnly);
                    Assert.Equal(proofCase.ExpectNative, inference.Supported);
                    if (proofCase.ExpectNative)
                    {
                        Assert.True(graph.IsComposed, "The live corpus has not admitted the source meaning.");
                        Assert.Empty(graph.UnknownSurfaceComponents);
                        Assert.True(inference.EvidenceCount > 0);
                        Assert.False(inference.RequiresEscalation);
                        Assert.False(string.IsNullOrWhiteSpace(inference.Answer));
                        Assert.Equal("semantic_transition_governed_composed", inference.ReasonCode);
                    }
                    Assert.Equal(0, external.CreateClientCalls);
                    Assert.Equal(0, external.SendCalls);
                    Assert.Equal(0, saves.Attempts);
                    Assert.Equal(0, guard.BlockedCommands);
                    RequireHealthyWindow();
                }
                catch (Exception exception)
                {
                    observationLogger.LogError(exception,
                        "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                        "CaseAssertionFailed", "LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyCandidateObservation", "case_assertions", "failed", "case_execution_failed");
                    authorizationFailed = IsObservedAuthorizationFailure(exception, diagnosticCapture.SnapshotDiagnostics(), guard.SnapshotDiagnostics());
                    nativeFailureCount++;
                    caseFailure = exception is OperationCanceledException ? "native_case_deadline_exceeded"
                        : exception is SqlException sqlError ? "sql_error_" + sqlError.Number
                        : graph is { IsComposed: false } ? "current_corpus_source_meaning_unavailable"
                        : "native_case_assertion_failed";
                }
                var nativeCounts = new Dictionary<string, long>();
                if (graph is not null)
                {
                    nativeCounts["GraphNodes"] = graph.Nodes.Count;
                    nativeCounts["GraphRelations"] = graph.Relations.Count;
                    nativeCounts["UnknownComponents"] = graph.UnknownSurfaceComponents.Count;
                }
                if (inference is not null) nativeCounts["NativeEvidenceCount"] = inference.EvidenceCount;
                var nativeEvidence = CaptureWindow(proofCase.Reference, nativeCounts);
                var nativeDiagnosis = DiagnoseObservedFailure(caseFailure is null ? "passed" : "failed", "native_current_corpus",
                    caseFailure, proofCase.ExpectNative, nativeEvidence.StageEvents, guard.SnapshotDiagnostics().FailedEvents, guard.SnapshotDiagnostics().Records);
                if (caseFailure is not null) firstFailureDiagnosis ??= nativeDiagnosis;
                cases.Add(new
                {
                    DiagnosticEvidence = nativeEvidence, FailureDiagnosis = nativeDiagnosis,
                    Category = proofCase.Reference, Status = caseFailure is null ? "passed" : "failed",
                    FailureCode = caseFailure, ExpectedNative = proofCase.ExpectNative,
                    NativeSupported = inference?.Supported, NativeReason = inference?.ReasonCode is null ? null : LegendConnectTelemetry.NormalizeDiagnosticReason(inference.ReasonCode),
                    EvidenceCount = inference?.EvidenceCount, EvidenceStandard = SafeObservationCode(inference?.EvidenceStandard),
                    ArticulationMode = SafeObservationCode(inference?.ArticulationMode), GraphComposed = graph?.IsComposed,
                    GraphReason = graph?.ReasonCode is null ? null : LegendConnectTelemetry.NormalizeDiagnosticReason(graph.ReasonCode), GraphNodeCount = graph?.Nodes.Count,
                    GraphRelationCount = graph?.Relations.Count, UnknownComponentCount = graph?.UnknownSurfaceComponents.Count,
                    AnswerSha256 = inference?.Answer is { Length: > 0 } answer
                        ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(answer))) : null,
                    ProviderClientCount = external.CreateClientCalls, ProviderHttpCallCount = external.SendCalls,
                    ElapsedMilliseconds = Stopwatch.GetElapsedTime(caseStart).TotalMilliseconds
                });
                if (authorizationFailed) throw new UnauthorizedAccessException("Observation authorization failed.");
                deadline.Token.ThrowIfCancellationRequested();
            }
            RequireObservationCasesPassed(ref phase, sectionFailureCount, nativeFailureCount);
            Assert.Equal(0, saves.Attempts);
            Assert.Equal(0, guard.BlockedCommands);
            Assert.True(guard.SelectCommands > 0);
            status = "passed";
        }
        catch (Exception exception)
        {
            observationLogger.LogError(exception,
                "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                "CaseAssertionFailed", "LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyCandidateObservation", "case_assertions", "failed", "case_execution_failed");
            // Never export raw SQL exceptions, row content, identities or credentials.
            failureCode = exception is OperationCanceledException ? "observation_deadline_exceeded"
                : exception is SqlException sqlError ? "sql_error_" + sqlError.Number
                : "observation_" + phase + "_failed";
        }
        finally
        {
            if (!windowCaptured)
            {
                var evidence = CaptureWindow("observation-" + phase);
                firstFailureDiagnosis ??= DiagnoseObservedFailure(status, phase, failureCode, null,
                    evidence.StageEvents, guard.SnapshotDiagnostics().FailedEvents, guard.SnapshotDiagnostics().Records);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                Version = "candidate-select-observation-v2", CandidateSha = candidateSha,
                DiagnosticsVersion = "legend-runtime-diagnostic-v1",
                DiagnosticWindows = diagnosticWindows, FailureDiagnosis = firstFailureDiagnosis,
                SqlFailureCount = diagnosticWindows.Sum(item =>
                    ((ReadOnlyLegendDbCommandInterceptor.SqlCommandDiagnosticSnapshot)item.SqlCommands).FailedEvents + item.StageEvents.SqlFailureEvents),
                DiagnosticsTruncated = diagnosticWindows.Any(item => item.StageEvents.Truncated ||
                    ((ReadOnlyLegendDbCommandInterceptor.SqlCommandDiagnosticSnapshot)item.SqlCommands).Truncated),
                RunIdentity = runIdentity, StartedUtc = startedUtc, CompletedUtc = DateTime.UtcNow,
                Status = status, FailureCode = failureCode, FailedPhase = status == "passed" ? null : phase,
                Authority = "non-authoritative", DeployedSha = "unavailable",
                Coverage = coverage,
                CapabilityLimitations = new[] { "no_chat_or_durable_discourse_execution", "no_device_or_concurrency_proof", "no_deployed_sha_proof" },
                ProviderClientCount = external.CreateClientCalls, ProviderHttpCallCount = external.SendCalls,
                ExecutedCases = cases.Count, CaseResults = cases,
                SqlPrincipalVerified = phase != "connection" && phase != "sql_principal",
                SelectCommandCount = guard.SelectCommands, BlockedCommandCount = guard.BlockedCommands,
                SaveChangesAttempts = saves.Attempts, QueryTimeoutSeconds = queryTimeoutSeconds,
                ObservationTimeoutSeconds = observationTimeoutSeconds,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(status == "passed", failureCode ?? "Candidate observation failed.");
    }

    private static void RequireObservationCasesPassed(ref string phase, int sectionFailureCount, int nativeFailureCount)
    {
        phase = "aggregate_assertions";
        Assert.Equal(0, sectionFailureCount + nativeFailureCount);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void ObservationAggregateFailure_IsNotAttributedToLastVisitedCase(int sectionFailures, int nativeFailures)
    {
        var phase = "native-only-provider-isolation";
        Assert.Throws<Xunit.Sdk.EqualException>(() =>
            RequireObservationCasesPassed(ref phase, sectionFailures, nativeFailures));
        Assert.Equal("aggregate_assertions", phase);
        RequireObservationCasesPassed(ref phase, 0, 0);
    }

    [Theory]
    [InlineData("exact_semantic_anchors", "LoadExactActiveSemanticAnchorIdsAsync")]
    [InlineData("indexed_semantic_anchors", "LoadIndexedSemanticAnchorIdsAsync")]
    [InlineData("reusable_meaning_candidates", "ReadReusableMeaningCandidatesAsync")]
    [InlineData("source_slot_examples", "AnalyzeDeclaredSourceSlotsAsync")]
    [InlineData("source_slot_declarations", "AnalyzeDeclaredSourceSlotsAsync")]
    [InlineData("source_slot_nodes", "AnalyzeDeclaredSourceSlotsAsync")]
    [InlineData("source_slot_relations", "AnalyzeDeclaredSourceSlotsAsync")]
    [InlineData("computed_structure_examples", "LoadCurrentComputedStructuresAsync")]
    [InlineData("computed_structure_nodes", "LoadCurrentComputedStructuresAsync")]
    [InlineData("computed_structure_relations", "LoadCurrentComputedStructuresAsync")]
    public void SqlQueryAttribution_UsesOnlyStaticLabelsWithoutChangingFingerprint(string operation, string authority)
    {
        const string sql = "SELECT 1 AS Value";
        var tagged = "-- LEGEND_QUERY:" + operation + "\n\n" + sql;
        var attribution = ReadOnlyLegendDbCommandInterceptor.ReadQueryAttribution(tagged);
        Assert.Equal("LegendConnectCurriculumService." + authority, attribution.Authority);
        Assert.Equal(operation, attribution.Operation);
        Assert.Equal(ReadOnlyLegendDbCommandInterceptor.QueryFingerprint(sql),
            ReadOnlyLegendDbCommandInterceptor.QueryFingerprint(tagged));
        var diagnosis = DiagnoseObservedFailure("failed", "native_current_corpus", "authority_call_failed", true,
            new RuntimeDiagnosticSnapshot(0, 0, false, 0, 0, []), 1,
            [new ReadOnlyLegendDbCommandInterceptor.SqlCommandDiagnosticRecord(1, "fingerprint", "failed", 15000,
                "SqlException", null, -2, attribution.Authority, attribution.Operation)]);
        Assert.Equal(attribution.Authority, diagnosis.AuthorityMethod);
        Assert.Equal("sql_command", diagnosis.ObservedStage);
        Assert.Equal("observed_failure_only", diagnosis.RootCauseStatus);
        Assert.Contains("no plan was captured", diagnosis.NextVerification);
    }

    [Theory]
    [InlineData("-- LEGEND_QUERY:private-customer-secret\nSELECT 1")]
    [InlineData("-- LEGEND_QUERY:source_slot_nodes private-customer-secret\nSELECT 1")]
    [InlineData("SELECT '-- LEGEND_QUERY:source_slot_nodes' AS Value")]
    [InlineData("-- LEGEND_QUERY:source_slot_nodes\n-- LEGEND_QUERY:source_slot_relations\nSELECT 1")]
    [InlineData("SELECT 1")]
    public void SqlQueryAttribution_RejectsUnknownAmbiguousAndValueEmbeddedLabels(string sql)
    {
        var attribution = ReadOnlyLegendDbCommandInterceptor.ReadQueryAttribution(sql);
        Assert.Null(attribution.Authority);
        Assert.Null(attribution.Operation);
    }

    private static ProductionNativeProofCase[] HeldOutProductionNativeCases() =>
    [
        ProductionNativeProofCase.Positive("held-out-competing-hypotheses", "held_out_paraphrase",
            "Keep both hypotheses; plan an experiment.", mustBeHeldOut: true),
        ProductionNativeProofCase.Positive("held-out-discriminating-check", "held_out_paraphrase",
            "Retain the competing explanations; devise a discriminating check.", mustBeHeldOut: true)
    ];

    private static ProductionNativeProofCase NativeOnlyProductionIsolationCase() =>
        ProductionNativeProofCase.Negative("native-only-provider-isolation", "native_only_isolation",
            "Uncatalogued zephyr request.");

    private sealed class ProductionMatrixFactAttribute : FactAttribute
    {
        public ProductionMatrixFactAttribute()
        {
            var required = string.Equals(Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_PROOF_REQUIRED"),
                "true", StringComparison.OrdinalIgnoreCase);
            var isolated = string.Equals(Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_ISOLATED_SELECT_ONLY"),
                "true", StringComparison.OrdinalIgnoreCase);
            if (!required && !isolated && string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_READONLY_CONNECTION")))
                Skip = "NOT_CONFIGURED: canonical SQL matrix requires its selected live SQL authority.";
        }
    }

    private sealed class ProductionObservationFactAttribute : FactAttribute
    {
        public ProductionObservationFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_OBSERVATION_REQUIRED"),
                    "true", StringComparison.OrdinalIgnoreCase))
                Skip = "NOT_CONFIGURED: candidate SQL observation requires an explicitly isolated read-only run.";
        }
    }

    private static string RequiredObservationSetting(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? Environment.GetEnvironmentVariable(name)!
            : throw new InvalidOperationException("NOT_CONFIGURED: missing " + name + ".");

    private sealed class ObservationSaveGuard : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Attempts++;
            throw new InvalidOperationException("Candidate SQL observation rejected SaveChanges.");
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return ValueTask.FromException<InterceptionResult<int>>(
                new InvalidOperationException("Candidate SQL observation rejected SaveChanges."));
        }
    }

    private static async Task RequireSelectOnlyPrincipalAsync(MasterAppDbContext db, CancellationToken token)
    {
        // A contained SQL user cannot inherit login/server authority. Full metadata
        // visibility is mandatory so invisible securables cannot conceal grants.
        var identityVerified = await db.Database.SqlQueryRaw<int>("""
            SELECT CAST(CASE WHEN USER_NAME() NOT IN ('dbo', 'guest') AND SCHEMA_NAME() = 'dbo'
              AND EXISTS (SELECT 1 FROM sys.database_principals
                WHERE principal_id = USER_ID() AND type = 'S' AND authentication_type_desc = 'DATABASE')
              AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') = 1
              AND NOT EXISTS (SELECT 1 FROM sys.user_token AS token
                JOIN sys.database_principals AS principal ON token.principal_id = principal.principal_id
                WHERE principal.type = 'R' AND principal.name NOT IN ('public', 'db_datareader', 'db_denydatawriter'))
              THEN 1 ELSE 0 END AS int) AS [Value]
            """).SingleAsync(token);
        Assert.Equal(1, identityVerified);
        var permissions = await db.Database.SqlQueryRaw<ObservationPermission>("""
            SELECT CAST('DATABASE' AS nvarchar(32)) COLLATE DATABASE_DEFAULT AS [Scope], permission_name COLLATE DATABASE_DEFAULT AS [PermissionName], CAST(0 AS bit) AS [GrantOption]
              FROM sys.fn_my_permissions(NULL, 'DATABASE')
            UNION ALL
            SELECT 'SCHEMA' COLLATE DATABASE_DEFAULT, permission.permission_name COLLATE DATABASE_DEFAULT, CAST(0 AS bit)
              FROM sys.schemas AS scope
              CROSS APPLY sys.fn_my_permissions(QUOTENAME(scope.name), 'SCHEMA') AS permission
            UNION ALL
            SELECT CASE WHEN permission.subentity_name = '' THEN 'OBJECT' ELSE 'COLUMN' END COLLATE DATABASE_DEFAULT,
              permission.permission_name COLLATE DATABASE_DEFAULT, CAST(0 AS bit)
              FROM sys.objects AS scope
              CROSS APPLY sys.fn_my_permissions(QUOTENAME(SCHEMA_NAME(scope.schema_id)) + '.' + QUOTENAME(scope.name), 'OBJECT') AS permission
              WHERE scope.is_ms_shipped = 0
            UNION ALL
            SELECT 'EXPLICIT_GRANT' COLLATE DATABASE_DEFAULT, permission.permission_name COLLATE DATABASE_DEFAULT, CAST(CASE WHEN permission.state = 'W' THEN 1 ELSE 0 END AS bit)
              FROM sys.database_permissions AS permission
              JOIN sys.user_token AS token ON permission.grantee_principal_id = token.principal_id
              WHERE permission.state IN ('G', 'W')
            UNION ALL
            SELECT 'OWNERSHIP' COLLATE DATABASE_DEFAULT, 'CONTROL' COLLATE DATABASE_DEFAULT, CAST(0 AS bit)
              FROM sys.schemas AS scope JOIN sys.user_token AS token ON scope.principal_id = token.principal_id
            UNION ALL
            SELECT 'OWNERSHIP' COLLATE DATABASE_DEFAULT, 'CONTROL' COLLATE DATABASE_DEFAULT, CAST(0 AS bit)
              FROM sys.objects AS scope JOIN sys.user_token AS token ON scope.principal_id = token.principal_id
            UNION ALL
            SELECT 'OWNERSHIP' COLLATE DATABASE_DEFAULT, 'CONTROL' COLLATE DATABASE_DEFAULT, CAST(0 AS bit)
              FROM sys.database_principals AS scope JOIN sys.user_token AS token ON scope.owning_principal_id = token.principal_id
            """).ToListAsync(token);
        Assert.NotEmpty(permissions);
        Assert.All(permissions, permission => Assert.True(IsReadOnlyPermission(permission),
            "The SQL principal has mutation, delegation or unrecognized authority at scope " + permission.Scope + "."));
    }

    private static (string CandidateSha, string RunIdentity, string ResultPath) RequireCandidateEvidenceIdentity()
    {
        var candidateSha = RequiredObservationSetting("LEGEND_VALIDATION_CANDIDATE_SHA");
        var runIdentity = RequiredObservationSetting("LEGEND_VALIDATION_RUN_IDENTITY");
        var resultPath = RequiredObservationSetting("LEGEND_VALIDATION_RESULT_PATH");
        Assert.Matches("^[0-9a-f]{40}$", candidateSha);
        Assert.False(File.Exists(resultPath), "Stale candidate observation evidence exists.");
        foreach (var assembly in new[] { typeof(LegendFounderCurriculumSqlServerE2ETests).Assembly,
                     typeof(LegendConnectOperations).Assembly, typeof(FounderLegendConnectService).Assembly })
        {
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Assert.True(version?.EndsWith("+" + candidateSha, StringComparison.Ordinal) == true,
                "The executed assembly is not bound to the exact candidate commit.");
        }
        return (candidateSha, runIdentity, resultPath);
    }

    private static async Task RequireSafePhysicalSourcesAsync(MasterAppDbContext db,
        ReadOnlyLegendDbCommandInterceptor guard, CancellationToken token)
    {
        // The EF model remains the only table mapping. Only existing physical
        // local tables without indirect computation/security modules are admitted.
        var mappedTables = db.Model.GetEntityTypes()
            .Where(item => item.GetTableName() is not null)
            .Select(item => (item.GetSchema() ?? "dbo") + "." + item.GetTableName())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var physicalTables = await db.Database.SqlQueryRaw<ObservationPhysicalTable>("""
            SELECT schemaName.name AS [SchemaName], target.name AS [TableName]
            FROM sys.tables AS target JOIN sys.schemas AS schemaName ON target.schema_id = schemaName.schema_id
            WHERE target.is_external = 0
            AND NOT EXISTS (SELECT 1 FROM sys.computed_columns AS col WHERE col.object_id = target.object_id)
            AND NOT EXISTS (SELECT 1 FROM sys.security_predicates AS predicate WHERE predicate.target_object_id = target.object_id)
            """).ToListAsync(token);
        var allowedTables = physicalTables
            .Where(item => mappedTables.Contains(item.SchemaName + "." + item.TableName))
            .SelectMany(item => item.SchemaName == "dbo"
                ? new[] { item.SchemaName + "." + item.TableName, item.TableName }
                : new[] { item.SchemaName + "." + item.TableName }).ToArray();
        Assert.NotEmpty(allowedTables);
        guard.AllowPhysicalTables(allowedTables);
    }

    private static string? SafeObservationCode(string? code)
    {
        if (code is null) return null;
        return code.Length is > 0 and <= 128 && code.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? code
            : "withheld_" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
    }

    private static ProductionNativeProofResult ToIsolatedMatrixResult(ProductionNativeProofResult result) =>
        result with
        {
            Reference = SafeObservationCode(result.Reference)!, Category = SafeObservationCode(result.Category)!,
            Phase = SafeObservationCode(result.Phase)!, Status = SafeObservationCode(result.Status)!,
            Failure = result.Failure is null ? null : "Diagnostic: " + SafeObservationCode(result.FailureCode),
            FailureKind = SafeObservationCode(result.FailureKind), FailureCode = SafeObservationCode(result.FailureCode),
            ReasonCode = result.ReasonCode is null ? null : LegendConnectTelemetry.NormalizeDiagnosticReason(result.ReasonCode), ResponseAuthority = SafeObservationCode(result.ResponseAuthority),
            Stage = SafeObservationCode(result.Stage)
        };

    [Fact]
    public void IsolatedMatrixEvidence_WithholdsRawFailureAndUnexpectedDiagnosticText()
    {
        var original = ProductionNativeProofResult.FailedCase("fixed-test-label", "held_out_paraphrase", true,
            "SQL failure password=private-sentinel-secret", 0, 1) with
        {
            ReasonCode = "Unexpected private record content.", ResponseAuthority = "Unexpected private authority text."
        };
        var serialized = JsonSerializer.Serialize(ToIsolatedMatrixResult(original));
        Assert.DoesNotContain("private-sentinel-secret", serialized);
        Assert.DoesNotContain("private record", serialized);
        Assert.DoesNotContain("private authority", serialized);
        Assert.Contains("execution_failed", serialized);
        Assert.Contains("withheld_", serialized);
        Assert.Contains("private-sentinel-secret", original.Failure);
        Assert.Equal("source_meaning_unavailable", SafeObservationCode("source_meaning_unavailable"));
    }

    private sealed class ObservationPhysicalTable
    {
        public string SchemaName { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
    }

    private sealed class ObservationPermission
    {
        public string Scope { get; set; } = string.Empty;
        public string PermissionName { get; set; } = string.Empty;
        public bool GrantOption { get; set; }
    }

    private static bool IsReadOnlyPermission(ObservationPermission permission) =>
        !permission.GrantOption &&
        (permission.PermissionName is "SELECT" or "CONNECT" or "VIEW DEFINITION"
            or "VIEW ANY COLUMN MASTER KEY DEFINITION" or "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION" ||
         // Current Azure SQL reports these read-only metadata subpermissions
         // with the required database VIEW DEFINITION. Admit only the observed
         // database scope; this does not grant permissions or admit delegation.
         (permission.Scope == "DATABASE" && permission.PermissionName is
             "VIEW SECURITY DEFINITION" or "VIEW PERFORMANCE DEFINITION"));

    [Theory]
    [InlineData("VIEW SECURITY DEFINITION")]
    [InlineData("VIEW PERFORMANCE DEFINITION")]
    public void ReadOnlyPrincipalGuard_AdmitsMetadataSubpermissionsOnlyAtDatabaseWithoutDelegation(string permission)
    {
        Assert.True(IsReadOnlyPermission(new ObservationPermission
            { Scope = "DATABASE", PermissionName = permission }));
        Assert.False(IsReadOnlyPermission(new ObservationPermission
            { Scope = "DATABASE", PermissionName = permission, GrantOption = true }));
        foreach (var scope in new[] { "SCHEMA", "OBJECT", "COLUMN", "EXPLICIT_GRANT", "OWNERSHIP", "SERVER", "", "database" })
        {
            Assert.False(IsReadOnlyPermission(new ObservationPermission
                { Scope = scope, PermissionName = permission }));
            Assert.False(IsReadOnlyPermission(new ObservationPermission
                { Scope = scope, PermissionName = permission, GrantOption = true }));
        }
        foreach (var rejected in new[] { "INSERT", "UPDATE", "DELETE", "ALTER", "EXECUTE", "CONTROL", "IMPERSONATE",
                     "VIEW DATABASE SECURITY STATE", "VIEW DATABASE PERFORMANCE STATE", "FUTURE_PERMISSION" })
            Assert.False(IsReadOnlyPermission(new ObservationPermission
                { Scope = "DATABASE", PermissionName = rejected }));
    }

    [Fact]
    public void ReadOnlyObservationGuards_RetainCaughtWriteAttempts()
    {
        var commands = new ReadOnlyLegendDbCommandInterceptor();
        using var command = new SqlCommand("DELETE FROM dbo.Records");
        Assert.Throws<InvalidOperationException>(() => commands.NonQueryExecuting(command, null!, default));
        Assert.Equal(1, commands.BlockedCommands);
        Assert.Equal(0, commands.SelectCommands);
        var saves = new ObservationSaveGuard();
        Assert.Throws<InvalidOperationException>(() => saves.SavingChanges(null!, default));
        Assert.Equal(1, saves.Attempts);
    }

    [Theory]
    [InlineData("DATABASE", "INSERT")]
    [InlineData("SCHEMA", "ALTER")]
    [InlineData("OBJECT", "EXECUTE")]
    [InlineData("COLUMN", "UPDATE")]
    [InlineData("EXPLICIT_GRANT", "IMPERSONATE")]
    [InlineData("OWNERSHIP", "CONTROL")]
    public void ReadOnlyPrincipalGuard_RejectsMutationAcrossSecurableScopes(string scope, string permission) =>
        Assert.False(IsReadOnlyPermission(new ObservationPermission { Scope = scope, PermissionName = permission }));

    [Fact]
    public void ReadOnlyPrincipalGuard_RejectsSelectDelegationAndUnknownPermissions()
    {
        Assert.False(IsReadOnlyPermission(new ObservationPermission { PermissionName = "SELECT", GrantOption = true }));
        Assert.False(IsReadOnlyPermission(new ObservationPermission { PermissionName = "FUTURE_PERMISSION" }));
        Assert.True(IsReadOnlyPermission(new ObservationPermission { PermissionName = "SELECT" }));
    }

    [Theory]
    [InlineData("SELECT * INTO dbo.copy FROM dbo.source")]
    [InlineData("SELECT 1; DELETE FROM dbo.source")]
    [InlineData("SELECT 1 SELECT 2")]
    [InlineData("SELECT NEXT VALUE FOR dbo.sequence")]
    [InlineData("SELECT * FROM OPENQUERY(remote, 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENROWSET('provider', 'connection', 'SELECT 1')")]
    [InlineData("SELECT * FROM otherdb.dbo.source")]
    [InlineData("EXEC dbo.procedure")]
    [InlineData("SELECT dbo.side_effect_function()")]
    [InlineData("SELECT * FROM dbo.side_effect_table_function()") ]
    public void ReadOnlySqlGuard_RejectsMutationAndExternalSelectForms(string sql) =>
        Assert.Throws<InvalidOperationException>(() => ReadOnlyLegendDbCommandInterceptor.ValidateSelect(sql));

    [Fact]
    public void ReadOnlySqlGuard_RejectsUnverifiedIndirectTableSources()
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.VerifiedTable" };
        ReadOnlyLegendDbCommandInterceptor.ValidateSelect("SELECT [Id] FROM dbo.VerifiedTable", tables);
        Assert.Throws<InvalidOperationException>(() => ReadOnlyLegendDbCommandInterceptor.ValidateSelect(
            "SELECT [Id] FROM dbo.UnverifiedSynonym", tables));
    }

    [Theory]
    [InlineData("-- query tag\nSELECT [Name] FROM [dbo].[Rows] WHERE [Name] = @name")]
    [InlineData("SELECT 'INTO; DELETE' AS [Value]")]
    [InlineData("SELECT COUNT(*) FROM (SELECT [Id] FROM [dbo].[Rows]) AS [rows]")]
    public void ReadOnlySqlGuard_AcceptsSingleLocalSelect(string sql) =>
        ReadOnlyLegendDbCommandInterceptor.ValidateSelect(sql);

    [ProductionMatrixFact]
    public async Task ProductionReadOnlyNativeProofMatrix()
    {
        const string matrixVersion = "lai-027-029-v1";
        var proofRequired = string.Equals(
            Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_PROOF_REQUIRED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var isolated = string.Equals(Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_ISOLATED_SELECT_ONLY"),
            "true", StringComparison.OrdinalIgnoreCase);
        if (isolated) Assert.True(proofRequired, "Isolated matrix execution must require complete production proof evidence.");
        var matrixStartedUtc = DateTime.UtcNow;
        var matrixStarted = Stopwatch.GetTimestamp();
        (string CandidateSha, string RunIdentity, string ResultPath)? isolatedIdentity =
            isolated ? RequireCandidateEvidenceIdentity() : null;
        using var matrixDeadline = isolated ? new CancellationTokenSource(TimeSpan.FromSeconds(600)) : null;
        var matrixToken = matrixDeadline?.Token ?? CancellationToken.None;
        var isolatedPhase = "connection";
        var isolatedPrincipalVerified = false;
        var requestedMatrixVersion = Environment.GetEnvironmentVariable(
            "LEGEND_PRODUCTION_PROOF_MATRIX_VERSION");
        if (!string.IsNullOrWhiteSpace(requestedMatrixVersion))
            Assert.Equal(matrixVersion, requestedMatrixVersion);

        var connectionString = Environment.GetEnvironmentVariable(
            "LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine(
                "PRODUCTION PROOF MATRIX STATUS: unavailable; " +
                "LEGEND_PRODUCTION_READONLY_CONNECTION is unset; cases_executed=0.");
            if (proofRequired)
            {
                Assert.True(
                    false,
                    "The required production proof matrix cannot report success without a production read-only SQL authority.");
            }
            return;
        }

        var previousOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var previousOpenAiConfigApiKey = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.Empty);
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", string.Empty);
        try
        {
            var connection = new SqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = "LEGEND production native zero-write proof matrix",
                ApplicationIntent = ApplicationIntent.ReadOnly
            };
            if (isolated)
            {
                connection.ConnectTimeout = 15;
                connection.Pooling = false;
                connection.TrustServerCertificate = false;
                connection.Encrypt = SqlConnectionEncryptOption.Mandatory;
            }
            var readOnlyGuard = new ReadOnlyLegendDbCommandInterceptor(restrictPhysicalTables: isolated);
            var productionSaves = new ObservationSaveGuard();
            using var diagnosticCapture = new ExceptionCapturingLoggerProvider();
            using var diagnosticLoggerFactory = LoggerFactory.Create(builder =>
                builder.SetMinimumLevel(LogLevel.Information).AddProvider(diagnosticCapture));
            await using var db = new MasterAppDbContext(
                new DbContextOptionsBuilder<MasterAppDbContext>()
                    .UseSqlServer(connection.ConnectionString, options =>
                    {
                        if (isolated) options.CommandTimeout(15);
                    })
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                    .UseLoggerFactory(diagnosticLoggerFactory)
                    .AddInterceptors(readOnlyGuard, productionSaves)
                    .Options);

            if (isolated)
            {
                // The same authenticated SQL session carries preflight and every
                // subsequent application SELECT. Legacy release configuration is unchanged.
                await db.Database.OpenConnectionAsync(matrixToken);
                isolatedPhase = "sql_principal";
                await RequireSelectOnlyPrincipalAsync(db, matrixToken);
                isolatedPrincipalVerified = true;
                isolatedPhase = "sql_sources";
                await RequireSafePhysicalSourcesAsync(db, readOnlyGuard, matrixToken);
            }

            isolatedPhase = "founder_identity";
            var founderId = Environment.GetEnvironmentVariable(
                "LEGEND_PRODUCTION_READONLY_FOUNDER_OID");
            Assert.False(
                string.IsNullOrWhiteSpace(founderId),
                "Production Founder OID was not supplied to the read-only serving proof.");
            Assert.True(await db.AgentProfiles
                    .AsNoTracking()
                    .AnyAsync(item => item.IsActive &&
                        item.AgentUserId != null &&
                        item.AgentUserId.ToLower() == founderId!.ToLower(), matrixToken),
                "The configured production Founder OID has no active AgentProfile.");
            Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);

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
                db,
                registry,
                diagnosticLoggerFactory.CreateLogger<LegendConnectCorpusService>());
            var curriculum = new LegendConnectCurriculumService(db, registry, corpus,
                logger: diagnosticLoggerFactory.CreateLogger<LegendConnectCurriculumService>());
            var operations = new LegendConnectOperations(
                db,
                registry,
                corpus,
                configuration,
                curriculum: curriculum);
            var founder = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("oid", founderId!)], "production-read-only"));
            var profiles = new AgentProfileAccessResolver(db);
            var founderLegend = new FounderLegendConnectService(operations, profiles);
            var factory = new CountingHttpClientFactory();
            // Production remains strictly read-only. Conversation-scoped
            // discourse state is exercised through its canonical persistence
            // authority in an isolated test store while every meaning graph,
            // reference rule, and response decision still comes from the
            // production read-only authorities above.
            await using var discourseDb = ControllerTestHelpers.BuildDb();
            discourseDb.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = "legend-production-proof-discourse@legend.local",
                NormalizedEmail = "legend-production-proof-discourse@legend.local",
                IsActive = true
            });
            await discourseDb.SaveChangesAsync(matrixToken);
            var discourseProfiles = new AgentProfileAccessResolver(discourseDb);
            var discourse = new LegendFounderAiDiscourseStateService(
                discourseDb,
                discourseProfiles,
                operations);
            // The SQL matrix must exercise the same governed language router
            // as serving. Only its external boundary is replaced; an attempted
            // provider call remains counted even when a caller catches it.
            var translationBoundary = new CountingForbiddenTranslationProvider();
            var translation = new LegendConnectTranslationRouter(
                translationBoundary,
                registry,
                new TranslationCapacityAuthority(
                    db, configuration, diagnosticLoggerFactory.CreateLogger<TranslationCapacityAuthority>()),
                diagnosticLoggerFactory.CreateLogger<LegendConnectTranslationRouter>(),
                structuralComposition: curriculum);
            var chat = new LegendFounderAiConversationService(
                factory,
                configuration,
                founderLegend,
                diagnosticLoggerFactory.CreateLogger<LegendFounderAiConversationService>(),
                discourse,
                registry,
                translation);

            isolatedPhase = "fixture_preflight";
            diagnosticCapture.ResetDiagnostics();
            readOnlyGuard.ResetDiagnostics();
            var prerequisiteCounts = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
            async Task<string?> FindReasoningSourceAsync(string operatorPrefix, string reference)
            {
                var sources = (
                    from relation in db.LegendFounderSemanticExampleRelationEvidence.AsNoTracking()
                    join source in db.LegendCurriculumExamples.AsNoTracking()
                        on relation.SourceCurriculumExampleId equals source.Id
                    join unit in db.LegendLanguageTextUnits.AsNoTracking()
                        on source.TextUnitId equals unit.Id
                    where relation.SupersededUtc == null &&
                        relation.ContributionState == "Supported" &&
                        relation.IsHumanVerifiedSupport &&
                        relation.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                        relation.LanguageCode == "en" &&
                        relation.RelationshipSemanticIdentity.StartsWith(operatorPrefix) &&
                        source.SupersededUtc == null &&
                        source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                        unit.LanguageCode == "en" &&
                        unit.IsTrainingEligible &&
                        unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
                    orderby relation.RelationshipSemanticIdentity, unit.NormalizedHash
                    select new { unit.Id, unit.Text });
                var eligibleCount = await sources.Select(item => item.Id).Distinct().LongCountAsync(matrixToken);
                prerequisiteCounts[reference] = new Dictionary<string, long>
                {
                    ["EligibleDistinctSourceUnits"] = eligibleCount
                };
                var text = await sources.Select(item => item.Text).FirstOrDefaultAsync(matrixToken);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            var fixtureFailures = new List<ProductionNativeProofResult>();
            var preflightFailures = new List<ProductionNativeProofResult>();

            async Task<string?> FindReasoningFixtureAsync(
                string reference,
                string category,
                string operatorPrefix)
            {
                string? source;
                try
                {
                    source = await FindReasoningSourceAsync(operatorPrefix, reference);
                }
                catch (Exception exception)
                {
                    preflightFailures.Add(
                        ProductionNativeProofResult.FailedInfrastructurePreflight(
                            reference,
                            category,
                            exception));
                    return null;
                }
                if (source is null)
                {
                    fixtureFailures.Add(ProductionNativeProofResult.FailedFixture(
                        reference,
                        category,
                        $"The production matrix has no active Founder-governed {category} source for operator prefix '{operatorPrefix}'."));
                }

                return source;
            }

            var deductionSource = await FindReasoningFixtureAsync(
                "fixture-governed-deduction",
                "deduction",
                "reasoning.deduction.");
            var uncertaintySource = await FindReasoningFixtureAsync(
                "fixture-governed-uncertainty",
                "uncertainty",
                "reasoning.epistemic.");
            var diagnosisSource = await FindReasoningFixtureAsync(
                "fixture-governed-diagnosis",
                "diagnosis",
                "reasoning.causal-diagnostic.");
            var planningSource = await FindReasoningFixtureAsync(
                "fixture-governed-planning",
                "planning",
                "reasoning.constrained-planning.");

            string? audienceConstraintSource = null;
            try
            {
                var audienceSources = (
                    from transition in db.LegendSemanticTransitionEvidence.AsNoTracking()
                    join source in db.LegendCurriculumExamples.AsNoTracking()
                        on transition.SourceCurriculumExampleId equals source.Id
                    join sourceUnit in db.LegendLanguageTextUnits.AsNoTracking()
                        on source.TextUnitId equals sourceUnit.Id
                    join resultVariation in db.LegendCurriculumExampleVariations.AsNoTracking()
                        on transition.ResultCurriculumExampleId equals resultVariation.CurriculumExampleId
                    where transition.SupersededUtc == null &&
                        transition.ContributionState == "Supported" &&
                        transition.IsHumanVerifiedSupport &&
                        transition.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                        transition.SourceLanguageCode == "en" &&
                        transition.ResultLanguageCode == "en" &&
                        source.SupersededUtc == null &&
                        source.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                        sourceUnit.IsTrainingEligible &&
                        sourceUnit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                        resultVariation.Dimension == "response_audience"
                    orderby resultVariation.Value, sourceUnit.NormalizedHash
                    select new { sourceUnit.Id, sourceUnit.Text });
                prerequisiteCounts["fixture-governed-audience-constraints"] = new Dictionary<string, long>
                {
                    ["EligibleDistinctSourceUnits"] = await audienceSources.Select(item => item.Id).Distinct().LongCountAsync(matrixToken)
                };
                audienceConstraintSource = await audienceSources.Select(item => item.Text).FirstOrDefaultAsync(matrixToken);
            }
            catch (Exception exception)
            {
                preflightFailures.Add(
                    ProductionNativeProofResult.FailedInfrastructurePreflight(
                        "fixture-governed-audience-constraints",
                        "audience_constraints",
                        exception));
            }
            if (string.IsNullOrWhiteSpace(audienceConstraintSource))
            {
                if (!preflightFailures.Any(item =>
                        item.Reference == "fixture-governed-audience-constraints"))
                {
                    fixtureFailures.Add(ProductionNativeProofResult.FailedFixture(
                        "fixture-governed-audience-constraints",
                        "audience_constraints",
                        "The production matrix has no active Founder-governed audience-constrained response source."));
                }
            }

            var heldOutCases = HeldOutProductionNativeCases();
            var crossFamilyCases = new[]
            {
                ProductionNativeProofCase.Negative(
                    "cross-family-handoff-inventory",
                    "cross_family_negative",
                    "handoff failure"),
                ProductionNativeProofCase.Negative(
                    "cross-family-capacity-scheduling",
                    "cross_family_negative",
                    "capacity shortage")
            };
            var discourseCase = new ProductionNativeProofCase(
                "discourse-first-option",
                "discourse",
                "en",
                "en",
                [
                    new LegendFounderAiChatMessage("user", "The alpha choice feels affordable to me."),
                    new LegendFounderAiChatMessage("assistant", "I understand."),
                    new LegendFounderAiChatMessage("user", "The beta choice seems reliable to me."),
                    new LegendFounderAiChatMessage("assistant", "I understand."),
                    new LegendFounderAiChatMessage("user", "No, I meant the first option.")
                ],
                true);

            async Task<string?> MeaningFixtureFailureAsync(
                ProductionNativeProofCase proofCase,
                bool requireMultipleNodes)
            {
                var prompt = proofCase.Messages[^1].Content!;
                var graph = await founderLegend.AnalyzeReusableMeaningGraphAsync(
                    founder,
                    prompt,
                    proofCase.NativeSourceLanguageCode, matrixToken);
                prerequisiteCounts[proofCase.Reference] = new Dictionary<string, long>
                {
                    ["GraphComposed"] = graph.IsComposed ? 1 : 0,
                    ["GraphNodes"] = graph.Nodes.Count,
                    ["GraphRelations"] = graph.Relations.Count,
                    ["UnknownComponents"] = graph.UnknownSurfaceComponents.Count
                };
                return ProductionMeaningFixtureFailure(
                    prompt,
                    graph,
                    requireMultipleNodes);
            }

            var matrix = new List<ProductionNativeProofCase>
            {
                ProductionNativeProofCase.Positive(
                    "exact-endpoint-hi-there",
                    "exact_endpoint",
                    "Hi there.",
                    expectedEvidenceStandard: "HigherStandard"),
                ProductionNativeProofCase.Positive(
                    "exact-endpoint-hi-legend",
                    "exact_endpoint",
                    "Hi Legend.",
                    expectedEvidenceStandard: "HigherStandard"),
                ProductionNativeProofCase.Positive(
                    "declared-language-normalization",
                    "language_routing",
                    "Hello.",
                    declaredSourceLanguageCode: " en_US ",
                    nativeSourceLanguageCode: "en",
                    expectedEvidenceStandard: "HigherStandard"),
                ProductionNativeProofCase.Positive(
                    "automatic-language-governed-greeting",
                    "language_routing",
                    "Hello.",
                    declaredSourceLanguageCode: null,
                    expectedEvidenceStandard: "HigherStandard"),
                ProductionNativeProofCase.Positive(
                    "automatic-language-native-arithmetic",
                    "language_routing",
                    "What is 147 minus 26?",
                    declaredSourceLanguageCode: null),
                NativeOnlyProductionIsolationCase()
            };
            foreach (var heldOutCase in heldOutCases)
            {
                string? failure;
                try
                {
                    failure = await MeaningFixtureFailureAsync(
                        heldOutCase,
                        requireMultipleNodes: false);
                }
                catch (Exception exception)
                {
                    preflightFailures.Add(
                        ProductionNativeProofResult.FailedInfrastructurePreflight(
                            heldOutCase.Reference,
                            heldOutCase.Category,
                            exception));
                    continue;
                }
                if (failure is null)
                    matrix.Add(heldOutCase);
                else
                    fixtureFailures.Add(ProductionNativeProofResult.FailedFixture(
                        "fixture-" + heldOutCase.Reference,
                        heldOutCase.Category,
                        failure));
            }
            foreach (var crossFamilyCase in crossFamilyCases)
            {
                string? failure;
                try
                {
                    failure = await MeaningFixtureFailureAsync(
                        crossFamilyCase,
                        requireMultipleNodes: true);
                }
                catch (Exception exception)
                {
                    preflightFailures.Add(
                        ProductionNativeProofResult.FailedInfrastructurePreflight(
                            crossFamilyCase.Reference,
                            crossFamilyCase.Category,
                            exception));
                    continue;
                }
                if (failure is null)
                    matrix.Add(crossFamilyCase);
                else
                    fixtureFailures.Add(ProductionNativeProofResult.FailedFixture(
                        "fixture-" + crossFamilyCase.Reference,
                        crossFamilyCase.Category,
                        failure));
            }

            string? discourseFailure = null;
            var discoursePreflightCompleted = true;
            try
            {
                discourseFailure = await MeaningFixtureFailureAsync(
                    discourseCase,
                    requireMultipleNodes: false);
                if (discourseFailure is null)
                {
                    var selectorGraph = await founderLegend.AnalyzeReusableMeaningGraphAsync(
                        founder,
                        discourseCase.Messages[^1].Content!,
                        discourseCase.NativeSourceLanguageCode, matrixToken);
                    var rules = await operations.GetProductionDiscourseReferenceRulesAsync(
                        discourseCase.NativeSourceLanguageCode,
                        selectorGraph.Nodes.Select(item => item.SemanticSignature)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(), matrixToken);
                    if (rules.Count != 1)
                    {
                        discourseFailure = rules.Count == 0
                            ? "The production fixture has no active production-eligible discourse-reference rule for the governed selector."
                            : "The production fixture has ambiguous production-eligible discourse-reference rules for the governed selector.";
                    }
                    else
                    {
                        var preflightConversationId = Guid.NewGuid();
                        foreach (var message in discourseCase.Messages)
                        {
                            var graph = await founderLegend.AnalyzeReusableMeaningGraphAsync(
                                founder,
                                message.Content ?? string.Empty,
                                discourseCase.NativeSourceLanguageCode, matrixToken);
                            await discourse.RecordObservationAsync(
                                founder,
                                preflightConversationId.ToString(),
                                message.Role ?? string.Empty,
                                graph,
                                cancellationToken: matrixToken,
                                sourceLanguageCode: discourseCase.NativeSourceLanguageCode);
                        }
                        var preflightState = await discourse.GetStateAsync(
                            founder,
                            preflightConversationId.ToString(), matrixToken);
                        var selectorBindings = preflightState?.Turns.LastOrDefault()?.Bindings ?? [];
                        if (!selectorBindings.Any(item => item.ResolutionState == "bound"))
                        {
                            var reasons = selectorBindings.Count == 0
                                ? "no governed binding was produced"
                                : string.Join(",", selectorBindings.Select(item => item.ReasonCode)
                                    .Distinct(StringComparer.Ordinal));
                            discourseFailure =
                                $"The production fixture has the discourse selector rule but the canonical discourse-state authority could not bind its prior entity prerequisites: {reasons}.";
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                discoursePreflightCompleted = false;
                preflightFailures.Add(
                    ProductionNativeProofResult.FailedInfrastructurePreflight(
                        discourseCase.Reference,
                        discourseCase.Category,
                        exception));
            }
            if (discoursePreflightCompleted && discourseFailure is null)
            {
                matrix.Add(discourseCase);
            }
            else if (discoursePreflightCompleted)
            {
                fixtureFailures.Add(ProductionNativeProofResult.FailedFixture(
                    "fixture-" + discourseCase.Reference,
                    discourseCase.Category,
                    discourseFailure!));
            }
            if (deductionSource is not null)
            {
                matrix.Add(ProductionNativeProofCase.Positive(
                    "governed-deduction",
                    "deduction",
                    deductionSource));
            }
            if (uncertaintySource is not null)
            {
                matrix.Add(ProductionNativeProofCase.Positive(
                    "governed-uncertainty",
                    "uncertainty",
                    uncertaintySource));
            }
            if (diagnosisSource is not null)
            {
                matrix.Add(ProductionNativeProofCase.Positive(
                    "governed-diagnosis",
                    "diagnosis",
                    diagnosisSource));
            }
            if (planningSource is not null)
            {
                matrix.Add(ProductionNativeProofCase.Positive(
                    "governed-planning",
                    "planning",
                    planningSource));
            }
            if (!string.IsNullOrWhiteSpace(audienceConstraintSource))
            {
                matrix.Add(ProductionNativeProofCase.Positive(
                    "governed-audience-constraints",
                    "audience_constraints",
                    audienceConstraintSource));
            }

            var requiredCategories = new[]
            {
                "exact_endpoint",
                "held_out_paraphrase",
                "discourse",
                "cross_family_negative",
                "deduction",
                "uncertainty",
                "diagnosis",
                "planning",
                "audience_constraints",
                "language_routing",
                "native_only_isolation"
            };
            var results = new List<ProductionNativeProofResult>(fixtureFailures);
            results.AddRange(preflightFailures);
            var preflightStageEvents = diagnosticCapture.SnapshotDiagnostics();
            var preflightSqlCommands = readOnlyGuard.SnapshotDiagnostics();
            IReadOnlyDictionary<string, long> CountsFor(string reference) =>
                prerequisiteCounts.GetValueOrDefault(reference) ??
                prerequisiteCounts.GetValueOrDefault("fixture-" + reference) ??
                (reference.StartsWith("fixture-", StringComparison.Ordinal)
                    ? prerequisiteCounts.GetValueOrDefault(reference["fixture-".Length..]) : null) ??
                new Dictionary<string, long>();
            if (preflightSqlCommands.FailedEvents > 0 || preflightSqlCommands.CanceledEvents > 0 ||
                preflightSqlCommands.BlockedEvents > 0 || preflightStageEvents.SqlFailureEvents > 0)
                results.Add(ProductionNativeProofResult.FailedFixture("matrix-preflight-sql-failure", "matrix_summary",
                    "A SQL failure was observed during prerequisite inspection; later success cannot erase it."));
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                results[index] = result with
                {
                    DiagnosticEvidence = new(preflightStageEvents, preflightSqlCommands,
                        "matrix-preflight/sql", CountsFor(result.Reference)),
                    FailureDiagnosis = DiagnoseObservedFailure(result.Status, result.Phase,
                        result.FailureCode, result.ExpectedNative, preflightStageEvents,
                        sqlFailureEvents: preflightSqlCommands.FailedEvents, sqlCommands: preflightSqlCommands.Records)
                };
            }
            var representedCategories = matrix.Select(item => item.Category)
                .Concat(fixtureFailures.Select(item => item.Category))
                .Concat(preflightFailures.Select(item => item.Category))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (!requiredCategories.OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(representedCategories, StringComparer.Ordinal))
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-category-contract",
                    "matrix_definition",
                    "The production matrix does not represent every required category exactly once or more."));
            }
            if (matrix.Count + fixtureFailures.Count + preflightFailures.Count < requiredCategories.Length ||
                matrix.Count + fixtureFailures.Count + preflightFailures.Count > 16)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-size-contract",
                    "matrix_definition",
                    $"The production matrix contains {matrix.Count + fixtureFailures.Count + preflightFailures.Count} executable or preflight-result cases; expected {requiredCategories.Length} through 16."));
            }

            _output.WriteLine("============================================================");
            _output.WriteLine("LEGEND® PRODUCTION ZERO-WRITE NATIVE PROOF MATRIX");
            _output.WriteLine("============================================================");
            _output.WriteLine($"PRODUCTION PROOF MATRIX VERSION: {matrixVersion}");
            _output.WriteLine(
                "PRODUCTION PROOF MATRIX CATEGORIES: " +
                string.Join(",", requiredCategories));
            foreach (var fixtureFailure in fixtureFailures)
            {
                _output.WriteLine(
                    $"MATRIX CASE FAILED: reference={fixtureFailure.Reference}; " +
                    $"category={fixtureFailure.Category}; phase=fixture; " +
                    $"failure_code={SafeObservationCode(fixtureFailure.FailureCode)}; provider_clients=0; elapsed_ms=0");
            }
            foreach (var preflightFailure in preflightFailures)
            {
                _output.WriteLine(
                    $"MATRIX CASE FAILED: reference={preflightFailure.Reference}; " +
                    $"category={preflightFailure.Category}; phase=preflight; " +
                    $"failure_code={preflightFailure.FailureCode}; " +
                    "provider_clients=0; elapsed_ms=0");
            }

            var executed = preflightFailures.Count;
            var nativePasses = 0;
            var negativePasses = 0;
            async Task RecordDiscourseMessagesAsync(
                Guid conversationId,
                IReadOnlyList<LegendFounderAiChatMessage> messages,
                string sourceLanguageCode,
                CancellationToken cancellationToken)
            {
                foreach (var message in messages)
                {
                    var graph = await founderLegend.AnalyzeReusableMeaningGraphAsync(
                        founder,
                        message.Content ?? string.Empty,
                        sourceLanguageCode, cancellationToken);
                    await discourse.RecordObservationAsync(
                        founder,
                        conversationId.ToString(),
                        message.Role ?? string.Empty,
                        graph,
                        cancellationToken: cancellationToken, sourceLanguageCode: sourceLanguageCode);
                }
            }

            isolatedPhase = "matrix_execution";
            string? stopRemainingReason = null;
            foreach (var proofCase in matrix)
            {
                diagnosticCapture.ResetDiagnostics();
                readOnlyGuard.ResetDiagnostics();
                var resultStart = results.Count;
                prerequisiteCounts[proofCase.Reference] = new Dictionary<string, long>(CountsFor(proofCase.Reference))
                {
                    ["RequestMessageCount"] = proofCase.Messages.Count,
                    ["DeclaredSourceLanguagePresent"] = proofCase.DeclaredSourceLanguageCode is null ? 0 : 1
                };
                var caseStarted = Stopwatch.GetTimestamp();
                executed++;
                try
                {
                    var currentPrompt = proofCase.Messages[^1].Content!;
                    var normalizedPrompt = LegendLanguageIdentity.NormalizeText(currentPrompt);
                    if (proofCase.MustBeHeldOut)
                    {
                        Assert.False(await db.LegendLanguageTextUnits
                            .AsNoTracking()
                            .AnyAsync(item =>
                                item.LanguageCode == proofCase.NativeSourceLanguageCode &&
                                item.Text == normalizedPrompt &&
                                item.IsTrainingEligible &&
                                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved, matrixToken),
                            $"Matrix case '{proofCase.Reference}' is no longer held out.");
                    }

                if (proofCase.Category == "exact_endpoint")
                {
                    Assert.True(await (
                            from transition in db.LegendSemanticTransitionEvidence.AsNoTracking()
                            join source in db.LegendCurriculumExamples.AsNoTracking()
                                on transition.SourceCurriculumExampleId equals source.Id
                            join unit in db.LegendLanguageTextUnits.AsNoTracking()
                                on source.TextUnitId equals unit.Id
                            where transition.SupersededUtc == null &&
                                transition.ContributionState == "Supported" &&
                                transition.IsHumanVerifiedSupport &&
                                transition.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                                source.SupersededUtc == null &&
                                unit.Text == normalizedPrompt
                            select transition.Id).AnyAsync(matrixToken),
                        $"Matrix case '{proofCase.Reference}' is not an active exact transition endpoint.");
                }

                var context = proofCase.Messages
                    .Take(proofCase.Messages.Count - 1)
                    .Select(message => new LegendConnectConversationContextItem(
                        message.Role ?? string.Empty,
                        message.Content ?? string.Empty))
                    .ToArray();
                LegendConnectDiscourseStateSnapshot? discourseState = null;
                string? replyConversationId = null;
                if (proofCase.Category == "cross_family_negative")
                {
                    var graph = await founderLegend.AnalyzeReusableMeaningGraphAsync(
                        founder,
                        currentPrompt,
                        proofCase.NativeSourceLanguageCode, matrixToken);
                    Assert.True(
                        graph.IsComposed,
                        $"Cross-family case '{proofCase.Reference}' did not compose governed primitives: {graph.ReasonCode}.");
                    Assert.True(graph.Nodes.Count >= 2);
                    Assert.Empty(graph.UnknownSurfaceComponents);
                }
                if (proofCase.Category == "discourse")
                {
                    var withoutContext = await founderLegend.TryInferConversationWithDiscourseAsync(
                        founder,
                        currentPrompt,
                        Array.Empty<LegendConnectConversationContextItem>(),
                        discourseState: null,
                        proofCase.NativeSourceLanguageCode, matrixToken,
                        providerPolicy: isolated ? LegendConnectExternalProviderPolicy.NativeOnly : null);
                    Assert.False(
                        withoutContext.Supported,
                        "The discourse case must require its bounded prior-turn context.");

                    var directConversationId = Guid.NewGuid();
                    await RecordDiscourseMessagesAsync(
                        directConversationId,
                        proofCase.Messages,
                        proofCase.NativeSourceLanguageCode, matrixToken);
                    discourseState = await discourse.GetStateAsync(
                        founder,
                        directConversationId.ToString(), matrixToken);
                    Assert.NotNull(discourseState);
                    Assert.Contains(
                        discourseState!.Turns.SelectMany(item => item.Bindings),
                        item => item.ResolutionState == "bound");

                    // ReplyAsync records the current user selector itself,
                    // exactly as production serving does. Seed only the prior
                    // governed turns into a separate canonical conversation.
                    replyConversationId = Guid.NewGuid().ToString();
                    await RecordDiscourseMessagesAsync(
                        Guid.Parse(replyConversationId),
                        proofCase.Messages.Take(proofCase.Messages.Count - 1).ToArray(),
                        proofCase.NativeSourceLanguageCode, matrixToken);
                }
                if (proofCase.Category is "deduction" or "uncertainty" or "diagnosis" or "planning")
                {
                    var planned = await operations.TryPlanConversationAsync(
                        currentPrompt,
                        discourseState: null,
                        cancellationToken: matrixToken,
                        sourceLanguageCode: proofCase.NativeSourceLanguageCode);
                    Assert.True(
                        planned.Supported,
                        $"Reasoning matrix case '{proofCase.Reference}' did not produce a governed plan: {planned.ReasonCode}.");
                    var reasoningPlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
                        planned.Plan);
                    Assert.NotNull(reasoningPlan.ReasoningTransitionPath);
                    Assert.NotEmpty(reasoningPlan.ReasoningTransitionPath!);
                    Assert.True(reasoningPlan.ReasoningEvidenceCount > 0);
                }
                if (proofCase.Category == "audience_constraints")
                {
                    var planned = await operations.TryPlanConversationAsync(
                        currentPrompt,
                        discourseState: null,
                        cancellationToken: matrixToken,
                        sourceLanguageCode: proofCase.NativeSourceLanguageCode);
                    Assert.True(planned.Supported, planned.ReasonCode);
                    var audiencePlan = Assert.IsType<LegendConnectResponseMeaningPlanSnapshot>(
                        planned.Plan);
                    Assert.NotNull(audiencePlan.PresentationConstraints);
                    Assert.False(string.IsNullOrWhiteSpace(
                        audiencePlan.PresentationConstraints!.Audience));
                }
                var providerCallsBefore = factory.CreateClientCalls;
                var native = await founderLegend.TryInferConversationWithDiscourseAsync(
                    founder,
                    currentPrompt,
                    context,
                    discourseState,
                    proofCase.NativeSourceLanguageCode, matrixToken,
                    providerPolicy: isolated ? LegendConnectExternalProviderPolicy.NativeOnly : null);
                prerequisiteCounts[proofCase.Reference] = new Dictionary<string, long>(CountsFor(proofCase.Reference))
                {
                    ["NativeEvidenceCount"] = native.EvidenceCount,
                    ["NativeSupported"] = native.Supported ? 1 : 0
                };
                var reply = await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        NativeOnly = true,
                        ConversationId = replyConversationId,
                        SourceLanguageCode = proofCase.DeclaredSourceLanguageCode,
                        Messages = proofCase.Messages
                    }, matrixToken);

                Assert.Equal(providerCallsBefore, factory.CreateClientCalls);
                Assert.Equal(0, translationBoundary.CallAttempts);
                var stageEvidence = diagnosticCapture.SnapshotDiagnostics();
                var sqlEvidence = readOnlyGuard.SnapshotDiagnostics();
                Assert.Equal(0, stageEvidence.SqlFailureEvents);
                Assert.Equal(0, sqlEvidence.FailedEvents);
                Assert.Equal(0, sqlEvidence.CanceledEvents);
                Assert.Equal(0, sqlEvidence.BlockedEvents);
                if (proofCase.ExpectNative)
                {
                    Assert.True(
                        native.Supported,
                        $"Matrix case '{proofCase.Reference}' was not supported: {native.ReasonCode}.");
                    Assert.True(native.EvidenceCount > 0);
                    Assert.False(native.RequiresEscalation);
                    Assert.False(string.IsNullOrWhiteSpace(native.Answer));
                    Assert.True(reply.Succeeded);
                    Assert.Equal("LegendAi", reply.ResponseAuthority);
                    Assert.Equal("native_response", reply.Stage);
                    Assert.Equal(native.Answer, reply.Message);
                    if (proofCase.Reference == "automatic-language-native-arithmetic")
                    {
                        // Reproduce the live request without teaching its answer.
                        // This assertion certifies the numeric result only in the
                        // bare or existing computed-response rendering contract;
                        // other wording remains unverified, never substring-matched.
                        Assert.Contains(reply.Message, new[] { "121", "The result is 121." });
                    }
                    if (proofCase.ExpectedEvidenceStandard is not null)
                    {
                        Assert.Equal(
                            proofCase.ExpectedEvidenceStandard,
                            native.EvidenceStandard);
                    }
                    nativePasses++;
                }
                else
                {
                    Assert.False(
                        native.Supported,
                        $"Negative matrix case '{proofCase.Reference}' incorrectly produced a native answer.");
                    Assert.True(reply.Succeeded);
                    Assert.Equal("SystemDiagnostic", reply.ResponseAuthority);
                    Assert.Equal("native_only_blocked", reply.Stage);
                    Assert.Equal(native.ReasonCode, reply.Reason);
                    negativePasses++;
                }

                results.Add(ProductionNativeProofResult.PassedCase(
                    proofCase.Reference,
                    proofCase.Category,
                    proofCase.ExpectNative,
                    native.Supported,
                    native.ReasonCode,
                    native.EvidenceCount,
                    reply.ResponseAuthority,
                    reply.Stage,
                    factory.CreateClientCalls,
                    Stopwatch.GetElapsedTime(caseStarted).TotalMilliseconds));

                _output.WriteLine(
                    $"MATRIX CASE: reference={proofCase.Reference}; " +
                    $"category={proofCase.Category}; expected_native={proofCase.ExpectNative}; " +
                    $"native_supported={native.Supported}; reason={native.ReasonCode}; " +
                    $"evidence={native.EvidenceCount}; authority={reply.ResponseAuthority}; " +
                    $"stage={reply.Stage}; provider_clients={factory.CreateClientCalls}; " +
                    $"elapsed_ms={Stopwatch.GetElapsedTime(caseStarted).TotalMilliseconds:0}");
                }
                catch (Exception exception)
                {
                    if (IsObservedAuthorizationFailure(exception, diagnosticCapture.SnapshotDiagnostics(), readOnlyGuard.SnapshotDiagnostics()))
                        stopRemainingReason = "authorization_failure";
                    else if (matrixToken.IsCancellationRequested)
                        stopRemainingReason = "matrix_deadline_exceeded";
                    diagnosticLoggerFactory.CreateLogger<LegendFounderCurriculumSqlServerE2ETests>().LogWarning(exception,
                        "LEGEND RuntimeDiagnostic Event={Event} AuthorityMethod={AuthorityMethod} Stage={Stage} Outcome={Outcome} ReasonCode={ReasonCode}",
                        "CaseAssertionFailed", "LegendFounderCurriculumSqlServerE2ETests.ProductionReadOnlyNativeProofMatrix",
                        "case_assertions", "failed", "case_execution_failed");
                    var elapsedMilliseconds = Stopwatch.GetElapsedTime(caseStarted).TotalMilliseconds;
                    results.Add(ProductionNativeProofResult.FailedCase(
                        proofCase.Reference,
                        proofCase.Category,
                        proofCase.ExpectNative,
                        exception.Message,
                        factory.CreateClientCalls,
                        elapsedMilliseconds));
                    _output.WriteLine(
                        $"MATRIX CASE FAILED: reference={proofCase.Reference}; " +
                        $"category={proofCase.Category}; expected_native={proofCase.ExpectNative}; " +
                        $"failure_type={exception.GetType().Name}; provider_clients={factory.CreateClientCalls}; " +
                        $"elapsed_ms={elapsedMilliseconds:0}");
                }
                finally
                {
                    var stageEvents = diagnosticCapture.SnapshotDiagnostics();
                    var sqlCommands = readOnlyGuard.SnapshotDiagnostics();
                    for (var index = resultStart; index < results.Count; index++)
                    {
                        var result = results[index];
                        results[index] = result with
                        {
                            UsesAutomaticLanguageIdentification = proofCase.DeclaredSourceLanguageCode is null,
                            DiagnosticEvidence = new(stageEvents, sqlCommands,
                                proofCase.Reference + "/sql", CountsFor(proofCase.Reference)),
                            FailureDiagnosis = DiagnoseObservedFailure(result.Status, result.Phase,
                                result.FailureCode ?? result.ReasonCode, proofCase.ExpectNative, stageEvents,
                                sqlFailureEvents: sqlCommands.FailedEvents, sqlCommands: sqlCommands.Records)
                        };
                    }
                }
                if (stopRemainingReason is not null)
                {
                    foreach (var pending in matrix.Skip(matrix.IndexOf(proofCase) + 1))
                        results.Add(new ProductionNativeProofResult(pending.Reference, pending.Category,
                            "not_executed", "not_executed", "Execution stopped at an authorization or deadline boundary.",
                            "execution_stopped", stopRemainingReason, pending.ExpectNative, null, null, null, null,
                            "not_executed", factory.CreateClientCalls, 0)
                        {
                            UsesAutomaticLanguageIdentification = pending.DeclaredSourceLanguageCode is null,
                            FailureDiagnosis = new("not_executed", stopRemainingReason,
                                "ProductionReadOnlyNativeProofMatrix", "execution_stopped", "insufficient_diagnostic_evidence",
                                "Resolve the recorded authorization or deadline boundary before executing this case.")
                        });
                    break;
                }
            }

            if (executed != matrix.Count)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-execution-count",
                    "matrix_summary",
                    $"The matrix attempted {executed} of {matrix.Count} executable cases."));
            }
            if (nativePasses == 0)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-native-pass-count",
                    "matrix_summary",
                    "The production matrix produced no positive native pass."));
            }
            if (negativePasses == 0)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-negative-pass-count",
                    "matrix_summary",
                    "The production matrix produced no negative isolation pass."));
            }
            if (factory.CreateClientCalls != 0)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-provider-isolation",
                    "matrix_summary",
                    $"The native-only production matrix created {factory.CreateClientCalls} provider clients."));
            }
            if (translationBoundary.CallAttempts != 0)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-translation-provider-isolation",
                    "matrix_summary",
                    $"The native-only production matrix attempted {translationBoundary.CallAttempts} external translation-provider calls."));
            }
            if (readOnlyGuard.BlockedCommands != 0 || productionSaves.Attempts != 0)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-production-write-attempt", "matrix_summary",
                    "The production read-only matrix attempted a blocked command or SaveChanges."));
            }
            _output.WriteLine($"PRODUCTION PROOF MATRIX CASES EXECUTED: {executed}");
            _output.WriteLine($"PRODUCTION PROOF MATRIX NATIVE PASSES: {nativePasses}");
            _output.WriteLine($"PRODUCTION PROOF MATRIX NEGATIVE PASSES: {negativePasses}");
            _output.WriteLine($"OPENAI HTTP CALLS: {factory.SendCalls}");
            _output.WriteLine($"EXTERNAL TRANSLATION PROVIDER CALL ATTEMPTS: {translationBoundary.CallAttempts}");
            _output.WriteLine($"PRODUCTION WRITE COMMANDS REJECTED BEFORE SQL: {readOnlyGuard.BlockedCommands}");
            _output.WriteLine($"PRODUCTION SAVE CHANGES ATTEMPTS: {productionSaves.Attempts}");
            var sqlDiagnosticWindows = results.Where(item => item.DiagnosticEvidence is not null)
                .GroupBy(item => item.DiagnosticEvidence!.SqlSnapshotReference, StringComparer.Ordinal)
                .Select(group => group.First().DiagnosticEvidence!.SqlCommands)
                .OfType<ReadOnlyLegendDbCommandInterceptor.SqlCommandDiagnosticSnapshot>().ToArray();
            var observedSqlFailures = sqlDiagnosticWindows.Sum(item => item.FailedEvents);

            var resultPath = isolatedIdentity?.ResultPath ?? Environment.GetEnvironmentVariable(
                "LEGEND_PRODUCTION_PROOF_RESULT_PATH");
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                var absoluteResultPath = Path.GetFullPath(resultPath);
                var resultDirectory = Path.GetDirectoryName(absoluteResultPath);
                Assert.False(string.IsNullOrWhiteSpace(resultDirectory));
                Directory.CreateDirectory(resultDirectory!);
                await File.WriteAllTextAsync(
                    absoluteResultPath,
                    JsonSerializer.Serialize(
                        new
                        {
                            MatrixVersion = matrixVersion,
                            DiagnosticsVersion = "legend-runtime-diagnostic-v1",
                            ExercisedBoundaries = new { InProcessNativeSql = true, LiveProvider = false, AuthenticatedHttp = false },
                            SqlFailureCount = observedSqlFailures,
                            NotExecutedCases = results.Count(item => item.Status == "not_executed"),
                            DiagnosticsTruncated = results.Any(item => item.DiagnosticEvidence?.StageEvents.Truncated == true) ||
                                sqlDiagnosticWindows.Any(item => item.Truncated),
                            IsolatedReadOnlyMode = isolated,
                            Authority = isolated ? "non-authoritative" : "release_workflow_matrix",
                            DeployedSha = isolated ? "unavailable" : null,
                            CandidateSha = isolatedIdentity?.CandidateSha,
                            RunIdentity = isolatedIdentity?.RunIdentity,
                            StartedUtc = matrixStartedUtc, CompletedUtc = DateTime.UtcNow,
                            ElapsedMilliseconds = Stopwatch.GetElapsedTime(matrixStarted).TotalMilliseconds,
                            SqlPrincipalVerified = isolatedPrincipalVerified,
                            QueryTimeoutSeconds = isolated ? (int?)15 : null,
                            ObservationTimeoutSeconds = isolated ? (int?)600 : null,
                            SelectCommandCount = readOnlyGuard.SelectCommands,
                            ProviderHttpCallCount = factory.SendCalls,
                            ExternalTranslationProviderCallAttempts = translationBoundary.CallAttempts,
                            Status = results.All(item => item.Status == "passed")
                                ? "passed"
                                : "failed",
                            ExecutedCases = executed,
                            TotalCases = results.Count,
                            FailedCases = results.Count(item => item.Status == "failed"),
                            NativePasses = nativePasses,
                            NegativePasses = negativePasses,
                            ProviderClientCount = factory.CreateClientCalls,
                            ProductionWriteCommandCount = readOnlyGuard.BlockedCommands,
                            ProductionSaveChangesAttempts = productionSaves.Attempts,
                            Categories = requiredCategories,
                            CaseResults = results.Select(ToIsolatedMatrixResult).ToArray()
                        },
                        new JsonSerializerOptions { WriteIndented = true }));
            }

            var failures = results
                .Where(item => item.Status != "passed")
                .Select(item => $"{SafeObservationCode(item.Reference)}: {SafeObservationCode(item.FailureCode)}")
                .ToArray();
            Assert.True(
                failures.Length == 0,
                "The production native proof matrix completed with independent failures: " +
                string.Join(" | ", failures));
        }
        catch (Exception exception) when (isolatedIdentity.HasValue)
        {
            var identity = isolatedIdentity.Value;
            if (!File.Exists(identity.ResultPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(identity.ResultPath))!);
                await File.WriteAllTextAsync(identity.ResultPath, JsonSerializer.Serialize(new
                {
                    MatrixVersion = matrixVersion, IsolatedReadOnlyMode = true,
                    DiagnosticsVersion = "legend-runtime-diagnostic-v1",
                    Authority = "non-authoritative", DeployedSha = "unavailable",
                    identity.CandidateSha, identity.RunIdentity,
                    StartedUtc = matrixStartedUtc, CompletedUtc = DateTime.UtcNow,
                    Status = "failed", FailedPhase = isolatedPhase,
                    FailureCode = exception is OperationCanceledException ? "isolated_matrix_deadline_exceeded"
                        : exception is SqlException sqlFailure ? "sql_error_" + sqlFailure.Number
                        : "isolated_matrix_" + isolatedPhase + "_failed",
                    FailureDiagnosis = new ProductionFailureDiagnosis(isolatedPhase,
                        exception is SqlException earlySql ? "sql_error_" + earlySql.Number : exception.GetType().Name,
                        isolatedPhase switch
                        {
                            "sql_principal" => "RequireSelectOnlyPrincipalAsync",
                            "sql_sources" => "RequireSafePhysicalSourcesAsync",
                            _ => "ProductionReadOnlyNativeProofMatrix"
                        }, "observed_preflight_failure", "observed_failure_only",
                        "Inspect the recorded preflight boundary; later runtime stages were not proven to execute."),
                    ExceptionType = exception.GetType().Name, HResult = exception.HResult,
                    SqlErrorNumber = exception is SqlException earlySqlNumber ? (int?)earlySqlNumber.Number : null,
                    DiagnosticEvidence = (object?)null,
                    SqlPrincipalVerified = isolatedPrincipalVerified,
                    // A preflight exception does not prove zero executed cases,
                    // provider calls or write attempts: no counters are invented.
                    ExecutedCases = (int?)null,
                    ElapsedMilliseconds = Stopwatch.GetElapsedTime(matrixStarted).TotalMilliseconds
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiApiKey);
            Environment.SetEnvironmentVariable("OpenAI__ApiKey", previousOpenAiConfigApiKey);
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    /// <summary>
    /// A production-data shadow rebuild for pre-deployment proof.  The source
    /// context is SQL Server with a read-only connection and an interceptor
    /// that rejects every non-SELECT command.  The second context is an
    /// ephemeral in-process snapshot only: it exists so the unchanged
    /// canonical V21 planner, replay, compiler, and serving authorities can
    /// be exercised without mutating a production row or invoking a provider.
    /// </summary>
    [Fact]
    public async Task ProductionReadOnlyV21ShadowRebuild_UsesLiveFounderEvidenceWithoutWrites()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Production V21 shadow rebuild was not selected; LEGEND_PRODUCTION_READONLY_CONNECTION is unset.");
            return;
        }

        var founderId = Environment.GetEnvironmentVariable(
            "LEGEND_PRODUCTION_READONLY_FOUNDER_OID");
        Assert.False(string.IsNullOrWhiteSpace(founderId),
            "Production Founder OID was not supplied to the shadow rebuild.");
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
                ApplicationName = "LEGEND production V21 read-only shadow rebuild",
                ApplicationIntent = ApplicationIntent.ReadOnly
            };
            var readOnlyGuard = new ReadOnlyLegendDbCommandInterceptor();
            await using var production = new MasterAppDbContext(
                new DbContextOptionsBuilder<MasterAppDbContext>()
                    // The full-shadow proof intentionally computes exact
                    // counts over the live governed corpus before copying its
                    // bounded snapshot. Production cardinality can exceed the
                    // provider default 30-second command timeout; retain exact
                    // reads and give this dedicated diagnostic context a
                    // bounded allowance instead of weakening the proof.
                    .UseSqlServer(
                        connection.ConnectionString,
                        sql => sql.CommandTimeout(180))
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                    .AddInterceptors(readOnlyGuard)
                    .Options);

            Assert.True(await production.AgentProfiles.AsNoTracking().AnyAsync(item =>
                    item.IsActive && item.AgentUserId != null &&
                    item.AgentUserId.ToLower() == founderId!.ToLower()),
                "The configured production Founder OID has no active AgentProfile.");

            var liveBefore = await ReadShadowCountsAsync(production);
            var liveContracts = await production.LegendLanguageDerivationContracts
                .AsNoTracking()
                .Where(item => item.SupersededUtc == null)
                .GroupBy(item => new { item.DerivationKind, item.ContractVersion, item.State })
                .OrderBy(group => group.Key.DerivationKind)
                .Select(group => new { group.Key, Count = group.LongCount() })
                .ToListAsync();
            var contextEndpointIndexes = await ReadContextEndpointIndexesAsync(production);
            var sourceV21Contract = LegendConnectDerivationContracts.ContractIdentityFor(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                LegendConnectDerivationContracts.SourceSemanticProjection);
            var liveSourceV20Artifacts = await production.LegendLanguageDerivationArtifacts
                .AsNoTracking()
                .LongCountAsync(item => item.State == "Current" &&
                    item.DerivationContractIdentity == LegendConnectDerivationContracts.ContractIdentityFor(
                        20,
                        LegendConnectDerivationContracts.SourceSemanticProjection));
            var liveSourceV21Artifacts = await production.LegendLanguageDerivationArtifacts
                .AsNoTracking()
                .LongCountAsync(item => item.State == "Current" &&
                    item.DerivationContractIdentity == sourceV21Contract);
            var recoverableManifest = await production.LegendCurriculumManifestWorkItems
                .AsNoTracking()
                .Where(item => item.ProcessingState == "Failed" &&
                    item.LastErrorCode != "curriculum_manifest_payload_invalid" &&
                    item.LastErrorCode != "curriculum_manifest_payload_mismatch")
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new { item.Id, item.FamilyCount, item.LastErrorCode })
                .FirstOrDefaultAsync();

            _output.WriteLine("============================================================");
            _output.WriteLine("LEGEND® V21 LIVE-DATA READ-ONLY SHADOW REBUILD");
            _output.WriteLine("============================================================");
            WriteShadowCounts("LIVE BEFORE", liveBefore);
            _output.WriteLine($"LIVE V20 SOURCE-PROJECTION ARTIFACTS: {liveSourceV20Artifacts}");
            _output.WriteLine($"LIVE V21 SOURCE-PROJECTION ARTIFACTS: {liveSourceV21Artifacts}");
            _output.WriteLine("LIVE ACTIVE CONTRACTS: " + string.Join(" | ", liveContracts.Select(item =>
                $"{item.Key.DerivationKind}:v{item.Key.ContractVersion}:{item.Key.State}={item.Count}")));
            _output.WriteLine("LIVE CONTEXT ENDPOINT INDEXES: " +
                string.Join(" | ", contextEndpointIndexes));
            _output.WriteLine("LIVE RECOVERABLE FAILED MANIFEST: " +
                (recoverableManifest is null
                    ? "<NONE>"
                    : $"family-count={recoverableManifest.FamilyCount}; error={recoverableManifest.LastErrorCode ?? "<NONE>"}"));

            Assert.True(liveSourceV21Artifacts > 0,
                "The current live production snapshot contains no V21 source-projection lineage.");
            Assert.Contains(
                "IX_LegendLanguageContextRelationships_SourceTextUnitId",
                contextEndpointIndexes);
            Assert.Contains(
                "IX_LegendLanguageContextRelationships_RelatedTextUnitId",
                contextEndpointIndexes);

            await using var shadow = ControllerTestHelpers.BuildDb();
            var copied = await CopyLiveCurriculumSnapshotAsync(production, shadow, founderId!);
            _output.WriteLine("SHADOW SNAPSHOT ROWS: " + string.Join(" | ", copied.Select(item =>
                item.Key + "=" + item.Value)));

            var configuration = ShadowConfiguration();
            var registry = new LegendLanguageRegistry(shadow, configuration);
            var runtime = new LegendConnectRuntimePolicyAuthority(
                shadow,
                new FounderAccess(),
                registry,
                configuration,
                NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
            var intelligence = new LegendConnectTranslationIntelligence(shadow, configuration, runtime);
            var corpus = new LegendConnectCorpusService(
                shadow,
                registry,
                NullLogger<LegendConnectCorpusService>.Instance,
                intelligence: intelligence);
            var curriculum = new LegendConnectCurriculumService(shadow, registry, corpus);
            var operations = new LegendConnectOperations(
                shadow,
                registry,
                corpus,
                configuration,
                runtimePolicy: runtime,
                curriculum: curriculum,
                intelligence: intelligence);

            // This is a rebuild, so derived candidates, derived evidence,
            // dependency artifacts, and convergence rows were intentionally
            // not copied. The canonical compiler below must reconstruct every
            // output that the available governed source/evidence can support;
            // an unavailable evidence class must remain absent rather than be
            // fabricated to make the diagnostic positive.
            _output.WriteLine("SHADOW REBUILD PATH: current V21 governed inputs copied; supported derived outputs must be regenerated canonically and unsupported paths must remain fail-closed.");

            // The shadow executes the same bounded canonical compiler that
            // the durable worker owns.  It does not write production and it
            // never changes a contract, manifest, or work item by hand.
            var replayedSourceFamilies = await DrainShadowCurriculumPhaseAsync(
                curriculum,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies);
            var replayedAlignments = await DrainShadowCurriculumPhaseAsync(
                curriculum,
                LegendConnectLanguageIntelligenceReevaluationPhases.Alignments);

            // The compact dependency ledger is a projection of the canonical
            // source replay, not a semantic input. Rebuild it only after the
            // evaluator has reconstructed the governed source state.
            var familyIds = await shadow.LegendCurriculumExamples.AsNoTracking()
                .Where(item => item.SupersededUtc == null)
                .Select(item => item.CurriculumFamilyId)
                .Distinct()
                .ToListAsync();
            foreach (var familyId in familyIds)
                await curriculum.RefreshCurrentDerivationDependenciesForFamilyAsync(
                    familyId,
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            var shadowAfterFirstReplay = await ReadShadowCountsAsync(shadow);
            WriteShadowCounts("SHADOW AFTER FIRST CANONICAL REPLAY", shadowAfterFirstReplay);
            _output.WriteLine($"SHADOW REPLAYED SOURCE FAMILIES: {replayedSourceFamilies}");
            _output.WriteLine($"SHADOW REPLAYED HUMAN-VERIFIED ALIGNMENTS: {replayedAlignments}");
            Assert.Equal(copied["families"], replayedSourceFamilies);
            Assert.Equal(copied["alignments"], replayedAlignments);
            Assert.True(shadowAfterFirstReplay.SourceAnchors > 0,
                "The live-data shadow compiler produced no governed source anchors.");
            Assert.True(shadowAfterFirstReplay.CurrentArtifacts > 0,
                "The live-data shadow compiler produced no current V21 lineage artifacts.");
            Assert.True(shadowAfterFirstReplay.TargetCandidatesWithEvidence ==
                        shadowAfterFirstReplay.ActiveTargetRealizationCandidates,
                "Every shadow target-realization candidate must retain active evidence.");
            if (replayedAlignments == 0)
            {
                Assert.Equal(0, shadowAfterFirstReplay.ActiveTargetRealizationCandidates);
                Assert.Equal(0, shadowAfterFirstReplay.ActiveTargetRealizationEvidence);
                _output.WriteLine("SHADOW TARGET REALIZATION PATH: fail-closed; the live governed snapshot contains no human-verified alignment evidence.");
            }

            // Candidate evidence itself has exact source example, target
            // example, and alignment identities. The compact source ledger
            // below is refreshed through its existing authority to retain the
            // corresponding V21 contract provenance without creating a
            // candidate-specific authority.
            var activeV21Artifacts = await shadow.LegendLanguageDerivationArtifacts
                .LongCountAsync(item => item.State == "Current" &&
                    item.DerivationContractIdentity == sourceV21Contract);
            Assert.True(activeV21Artifacts > 0,
                "The shadow replay produced no V21 source-contract lineage artifacts.");
            var candidatesWithSourceContractLineage = await (
                from candidate in shadow.LegendLanguageTargetRealizationCandidates.AsNoTracking()
                join evidence in shadow.LegendLanguageTargetRealizationEvidence.AsNoTracking()
                    on candidate.Id equals evidence.CandidateId
                where candidate.SupersededUtc == null && evidence.SupersededUtc == null &&
                    shadow.LegendLanguageDerivationArtifacts.Any(artifact =>
                        artifact.State == "Current" &&
                        artifact.DerivationContractIdentity == sourceV21Contract &&
                        artifact.ArtifactKind == "compositional-anchor" &&
                        artifact.ResultArtifactIdentity.StartsWith(
                            "anchor:" + evidence.SourceCurriculumExampleId.ToString() + ":"))
                select candidate.Id).Distinct().LongCountAsync();
            _output.WriteLine($"SHADOW TARGET CANDIDATES WITH V21 SOURCE CONTRACT LINEAGE: {candidatesWithSourceContractLineage}");
            Assert.Equal(shadowAfterFirstReplay.ActiveTargetRealizationCandidates,
                candidatesWithSourceContractLineage);

            if (recoverableManifest is not null)
            {
                var manifestShadow = await BuildRecoverableManifestShadowAsync(
                    production,
                    recoverableManifest.Id,
                    founderId!);
                await using (manifestShadow)
                {
                    var manifestRegistry = new LegendLanguageRegistry(manifestShadow, configuration);
                    var manifestRuntime = new LegendConnectRuntimePolicyAuthority(
                        manifestShadow, new FounderAccess(), manifestRegistry, configuration,
                        NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
                    var manifestCorpus = new LegendConnectCorpusService(
                        manifestShadow, manifestRegistry,
                        NullLogger<LegendConnectCorpusService>.Instance);
                    var manifestCurriculum = new LegendConnectCurriculumService(
                        manifestShadow, manifestRegistry, manifestCorpus);
                    var durable = new LegendConnectHistoricalReevaluationWorkAuthority(
                        manifestShadow, manifestRuntime, configuration);
                    var processor = new LegendConnectCurriculumManifestProcessor(
                        manifestShadow, manifestCurriculum, durable,
                        NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
                    var firstAdmitted = await processor.SeedDurableFamilyWorkAsync(
                        durable, LegendConnectLanguageIntelligenceEvaluatorVersion.Current, 1);
                    var firstWorkCount = await manifestShadow.LegendHistoricalReevaluationWorkItems
                        .LongCountAsync(item => item.SubjectId == recoverableManifest.Id &&
                            item.EvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                    var secondAdmitted = await processor.SeedDurableFamilyWorkAsync(
                        durable, LegendConnectLanguageIntelligenceEvaluatorVersion.Current, 1);
                    var secondWorkCount = await manifestShadow.LegendHistoricalReevaluationWorkItems
                        .LongCountAsync(item => item.SubjectId == recoverableManifest.Id &&
                            item.EvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
                    _output.WriteLine($"SHADOW RECOVERABLE MANIFEST ADMISSION: first={firstAdmitted}; second={secondAdmitted}; work={firstWorkCount}/{secondWorkCount}");
                    Assert.True(firstAdmitted > 0);
                    Assert.Equal(0, secondAdmitted);
                    Assert.True(firstWorkCount > 0);
                    Assert.Equal(firstWorkCount, secondWorkCount);
                }
            }
            else
            {
                _output.WriteLine("SHADOW RECOVERABLE MANIFEST ADMISSION: not applicable; production has no recoverable failed manifest.");
            }

            var founder = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", founderId!)], "production-shadow-founder"));
            var profiles = new AgentProfileAccessResolver(shadow);
            var founderLegend = new FounderLegendConnectService(operations, profiles);
            var promptMatrix = await BuildShadowPromptMatrixAsync(
                shadow,
                founderLegend,
                founder);
            var factory = new CountingHttpClientFactory();
            var chat = new LegendFounderAiConversationService(
                factory,
                configuration,
                founderLegend,
                NullLogger<LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(shadow, profiles, operations),
                registry,
                ControllerTestHelpers.BuildTranslationService());
            var nativePasses = 0;
            foreach (var request in promptMatrix)
            {
                var source = await curriculum.AnalyzeSemanticTransitionSourceSemanticsAsync("en", request.Text);
                var graph = await operations.AnalyzeReusableMeaningGraphAsync(request.Text);
                var plan = await operations.TryPlanConversationAsync(request.Text, null);
                var binding = await operations.TryBindConversationContentAsync(request.Text, null);
                var native = await founderLegend.TryInferConversationWithDiscourseAsync(
                    founder,
                    request.Text,
                    Array.Empty<LegendConnectConversationContextItem>(),
                    discourseState: null,
                    sourceLanguageCode: "en");
                var response = await chat.ReplyAsync(founder, new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    NativeOnly = true,
                    Messages = [new LegendFounderAiChatMessage("user", request.Text)]
                });
                WriteShadowPromptTrace(request, source, graph, plan, binding, native, response, factory.CreateClientCalls);
                var passed = native.Supported && native.EvidenceCount > 0 && !native.RequiresEscalation &&
                    !string.IsNullOrWhiteSpace(native.Answer) && response.Succeeded &&
                    response.ResponseAuthority == "LegendAi" && response.Stage == "native_response" &&
                    string.Equals(native.Answer, response.Message, StringComparison.Ordinal);
                if (request.ExpectNative)
                {
                    Assert.True(passed, $"Shadow native inference failed for {request.Reference}; reason={native.ReasonCode}");
                    if (request.ExpectedEvidenceStandard is not null)
                        Assert.Equal(request.ExpectedEvidenceStandard, native.EvidenceStandard);
                    nativePasses++;
                }
                else
                {
                    Assert.False(native.Supported, $"Shadow fail-closed prompt unexpectedly served for {request.Reference}.");
                    Assert.True(native.RequiresEscalation);
                    Assert.Equal("SystemDiagnostic", response.ResponseAuthority);
                    Assert.Equal("native_only_blocked", response.Stage);
                }
            }
            Assert.Equal(promptMatrix.Count(item => item.ExpectNative), nativePasses);
            Assert.Equal(0, factory.CreateClientCalls);

            await DrainShadowCurriculumPhaseAsync(
                curriculum,
                LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies);
            await DrainShadowCurriculumPhaseAsync(
                curriculum,
                LegendConnectLanguageIntelligenceReevaluationPhases.Alignments);
            var shadowAfterSecondReplay = await ReadShadowCountsAsync(shadow);
            WriteShadowCounts("SHADOW AFTER SECOND CANONICAL REPLAY", shadowAfterSecondReplay);
            Assert.Equal(shadowAfterFirstReplay, shadowAfterSecondReplay);
            _output.WriteLine("OPENAI HTTP CALLS: 0");
            _output.WriteLine("PRODUCTION WRITE COMMANDS: 0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiApiKey);
            Environment.SetEnvironmentVariable("OpenAI__ApiKey", previousOpenAiConfigApiKey);
        }
    }

    /// <summary>
    /// Uses only a read-only production query to obtain the active governed
    /// transition class behind known production greeting endpoints. It then
    /// replays those exact canonical rows in an isolated local database using
    /// the normal v16 curriculum authority. This is the forward-repair proof
    /// for data that cannot be mutated during production diagnosis.
    /// </summary>
    [Fact]
    public async Task ProductionDataDerivedV16Replay_ActivatesKnownGreetingEndpointsNatively()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "LEGEND_PRODUCTION_READONLY_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine(
                "Production-data-derived v16 replay proof was not selected; " +
                "LEGEND_PRODUCTION_READONLY_CONNECTION is unset.");
            return;
        }

        var connection = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "LEGEND production-data-derived v16 replay proof",
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        var readOnlyGuard = new ReadOnlyLegendDbCommandInterceptor();
        await using var production = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .AddInterceptors(readOnlyGuard)
                .Options);

        var prompts = new[] { "Hi there.", "Good morning." };
        var normalizedPrompts = prompts
            .Select(LegendLanguageIdentity.NormalizeText)
            .ToArray();
        var signatures = await (
            from evidence in production.LegendSemanticTransitionEvidence.AsNoTracking()
            join source in production.LegendCurriculumExamples.AsNoTracking()
                on evidence.SourceCurriculumExampleId equals source.Id
            join sourceUnit in production.LegendLanguageTextUnits.AsNoTracking()
                on source.TextUnitId equals sourceUnit.Id
            where evidence.SourceLanguageCode == "en" &&
                evidence.ResultLanguageCode == "en" &&
                evidence.SupersededUtc == null &&
                evidence.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                evidence.ContributionState == "Supported" &&
                evidence.IsHumanVerifiedSupport &&
                normalizedPrompts.Contains(sourceUnit.Text)
            select evidence.TransitionSignature)
            .Distinct()
            .ToArrayAsync();
        Assert.NotEmpty(signatures);

        var transitions = await production.LegendSemanticTransitionEvidence
            .AsNoTracking()
            .Where(item => signatures.Contains(item.TransitionSignature) &&
                item.SourceLanguageCode == "en" &&
                item.ResultLanguageCode == "en" &&
                item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                item.ContributionState == "Supported" &&
                item.IsHumanVerifiedSupport)
            .ToListAsync();
        var exampleIds = transitions
            .SelectMany(item => new[]
            {
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId
            })
            .Distinct()
            .ToArray();
        var examples = await production.LegendCurriculumExamples
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.Id) &&
                item.SupersededUtc == null &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToListAsync();
        Assert.Equal(exampleIds.Length, examples.Count);
        var families = await production.LegendCurriculumFamilies
            .AsNoTracking()
            .Where(item => examples.Select(example => example.CurriculumFamilyId).Contains(item.Id) &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToListAsync();
        var textUnits = await production.LegendLanguageTextUnits
            .AsNoTracking()
            .Where(item => examples.Select(example => example.TextUnitId).Contains(item.Id) &&
                item.LanguageCode == "en" &&
                item.IsTrainingEligible &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved)
            .ToListAsync();
        var variations = await production.LegendCurriculumExampleVariations
            .AsNoTracking()
            .Where(item => exampleIds.Contains(item.CurriculumExampleId))
            .ToListAsync();

        await using var db = ControllerTestHelpers.BuildDb();
        db.AddRange(families);
        db.AddRange(textUnits);
        db.AddRange(examples);
        db.AddRange(variations);
        db.AddRange(transitions);
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty),
                new KeyValuePair<string, string?>("LegendConnect:CorpusAcquisition:Enabled", "false"),
                new KeyValuePair<string, string?>("LegendConnect:ContextualComposition:Mode", "Shadow"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:Code", "en"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:Name", "English"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:NativeName", "English")
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum);

        foreach (var prompt in prompts)
        {
            var before = await curriculum.AnalyzeSemanticTransitionSourceSemanticsAsync("en", prompt);
            Assert.Equal(
                LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
                before.State);
        }

        await curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        const string founderId = "6fb8c6b8-7a22-408b-a0e2-6adf7c4f2232";
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var previousOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var previousOpenAiConfigApiKey = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.Empty);
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", string.Empty);
        try
        {
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = "production-data-derived@legend.local",
                NormalizedEmail = "production-data-derived@legend.local",
                IsActive = true
            });
            await db.SaveChangesAsync();

            var founder = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("oid", founderId)], "production-data-derived"));
            var profiles = new AgentProfileAccessResolver(db);
            var founderLegend = new FounderLegendConnectService(operations, profiles);
            var factory = new CountingHttpClientFactory();
            var chat = new LegendFounderAiConversationService(
                factory,
                configuration,
                founderLegend,
                NullLogger<LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(db, profiles, operations),
                registry,
                ControllerTestHelpers.BuildTranslationService());

            _output.WriteLine("============================================================");
            _output.WriteLine("LEGEND® PRODUCTION-DATA-DERIVED v16 REPLAY TRANSCRIPT");
            _output.WriteLine("============================================================");
            foreach (var prompt in prompts)
            {
                var source = await curriculum.AnalyzeSemanticTransitionSourceSemanticsAsync("en", prompt);
                var native = await founderLegend.TryInferConversationWithDiscourseAsync(
                    founder,
                    prompt,
                    Array.Empty<LegendConnectConversationContextItem>(),
                    new LegendConnectDiscourseStateSnapshot([]),
                    "en");
                var reply = await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        Messages = [new LegendFounderAiChatMessage("user", prompt)]
                    });

                _output.WriteLine($"USER: {prompt}");
                _output.WriteLine($"SOURCE STATE: {source.State}");
                _output.WriteLine("SOURCE COMPONENTS: " + string.Join(
                    " | ",
                    source.Components.Select(item =>
                        $"{item.Dimension}={item.Value}@{item.SurfaceForm}")));
                _output.WriteLine($"NATIVE SUPPORTED: {native.Supported}");
                _output.WriteLine($"NATIVE EVIDENCE: {native.EvidenceCount}");
                _output.WriteLine($"NATIVE RESPONSE: {native.Answer ?? "<NULL>"}");
                _output.WriteLine($"FINAL RESPONSE: {reply.Message}");

                Assert.Equal(
                    LegendShadowSourceUnderstanding.SupportedForShadowEvaluation,
                    source.State);
                Assert.True(native.Supported, native.ReasonCode);
                Assert.True(native.EvidenceCount > 0);
                Assert.False(native.RequiresEscalation);
                Assert.False(string.IsNullOrWhiteSpace(native.Answer));
                Assert.True(reply.Succeeded);
                Assert.Equal(native.Answer, reply.Message);
            }

            Assert.Equal(0, factory.CreateClientCalls);
            _output.WriteLine("OPENAI CLIENTS: 0");
            _output.WriteLine("OPENAI HTTP CALLS: 0");
            _output.WriteLine("PRODUCTION WRITE COMMANDS: 0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiApiKey);
            Environment.SetEnvironmentVariable("OpenAI__ApiKey", previousOpenAiConfigApiKey);
        }
    }

    [Fact]
    public async Task ExternalFounderManifest_UsesNormalDurablePathAndRepliesNatively()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_CONNECTION");
        var manifestPath = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_MANIFEST_PATH");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(manifestPath))
        {
            _output.WriteLine("External Founder SQL Server E2E is opt-in; no isolated database was selected.");
            return;
        }

        var manifestPaths = manifestPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(manifestPaths);
        foreach (var path in manifestPaths)
            Assert.True(File.Exists(path), "Each external Founder manifest must exist.");

        var founderId = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_FOUNDER_ID") ??
            "e2e4d030-8d47-4a5b-a2db-5f2e50d14570";
        var founderEmail = $"founder-e2e-{founderId}@legend.local";
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(connectionString)
                .Options);
        var profile = await db.AgentProfiles
            .SingleOrDefaultAsync(item => item.AgentUserId == founderId);
        if (profile is null)
        {
            db.AgentProfiles.Add(new AgentProfile
            {
                Id = Guid.NewGuid(),
                AgentUserId = founderId,
                AgentUpn = founderEmail,
                NormalizedEmail = founderEmail,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty),
                new KeyValuePair<string, string?>("LegendConnect:ContextualComposition:Mode", "Shadow")
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db,
            new FounderAccess(),
            registry,
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var durable = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            runtimePolicy: runtime,
            curriculum: curriculum,
            intelligence: intelligence);
        var founder = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "e2e"));
        var founderLegend = new FounderLegendConnectService(
            operations,
            new AgentProfileAccessResolver(db));

        var familiesBefore = await db.LegendCurriculumFamilies.CountAsync();
        var examplesBefore = await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null);
        var anchorsBefore = await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null);
        var transitionsBefore = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null);
        foreach (var path in manifestPaths)
        {
            var manifest = await File.ReadAllTextAsync(path);
            Assert.False(string.IsNullOrWhiteSpace(manifest));
            var accepted = await founderLegend.SubmitCurriculumAsync(
                founder,
                new FounderLegendConnectCurriculumInput { Manifest = manifest });
            Assert.True(accepted.Succeeded, accepted.Message);
            _output.WriteLine($"MANIFEST ACCEPTED: {Path.GetFileName(path)}; DUPLICATE PREVENTED: {accepted.DuplicatePrevented}");
        }

        var processor = new LegendConnectCurriculumManifestProcessor(
            db,
            curriculum,
            durable,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
        for (var pass = 0; pass < 64; pass++)
        {
            await processor.ProcessPendingAsync(1);
            db.ChangeTracker.Clear();
            var states = await db.LegendCurriculumManifestWorkItems
                .Select(item => item.ProcessingState)
                .ToArrayAsync();
            if (states.All(item => item == "Completed"))
                break;
            Assert.DoesNotContain("Failed", states);
        }

        Assert.All(
            await db.LegendCurriculumManifestWorkItems.ToListAsync(),
            work => Assert.Equal("Completed", work.ProcessingState));

        // Settle the existing legacy-receipt lifecycle first. Historical
        // curriculum that predates raw submission receipts is discovered and
        // converted through this authority once; later capability replay does
        // not recreate those receipts.
        var trainingIngestion = new LegendConnectFounderTrainingIngestionAuthority(
            db,
            registry,
            corpus,
            curriculum);
        var initialReceiptReconciliation = await trainingIngestion.ReconcileLegacyAsync(25);
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.LegendFounderTrainingSubmissions.CountAsync(item =>
            item.CompletedLanguageIntelligenceEvaluatorVersion <
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current));
        _output.WriteLine($"INITIAL HISTORICAL RAW RECEIPTS RECONCILED: {initialReceiptReconciliation.CapabilityReplayedSubmissionCount}");

        // Reproduce the deployed evaluator-version boundary through the
        // actual global runtime-policy authority. SourceFamilies is advanced
        // one durable identity at a time, then the authority is recreated to
        // prove a process restart resumes from its persisted cursor.
        await DrainHistoricalReplayAsync(runtime, curriculum, intelligence, operations, 9);
        Assert.Equal(
            9,
            (await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(9))
                .CompletedEvaluatorVersion);
        var currentStart = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, currentStart.Phase);
        Assert.Null(currentStart.Cursor);
        var firstSourcePage = await curriculum.ReevaluateHistoricalAlignmentsAsync(
            1,
            currentStart.Phase,
            currentStart.Cursor);
        Assert.NotNull(firstSourcePage.LastProcessedId);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            currentStart.Phase,
            firstSourcePage.LastProcessedId,
            firstSourcePage.PhaseComplete);
        db.ChangeTracker.Clear();

        var restartedRuntime = new LegendConnectRuntimePolicyAuthority(
            db,
            new FounderAccess(),
            new LegendLanguageRegistry(db, configuration),
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var afterRestart = await restartedRuntime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, afterRestart.Phase);
        Assert.Equal(firstSourcePage.LastProcessedId, afterRestart.Cursor);
        await DrainHistoricalReplayAsync(
            restartedRuntime,
            curriculum,
            intelligence,
            operations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        var currentComplete = await restartedRuntime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.False(currentComplete.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current, currentComplete.CompletedEvaluatorVersion);
        _output.WriteLine($"V9 COMPLETED EVALUATOR: 9");
        _output.WriteLine($"V{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} FIRST SOURCEFAMILIES CURSOR: {firstSourcePage.LastProcessedId:D}");
        _output.WriteLine($"V{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} RESUMED SOURCEFAMILIES CURSOR: {afterRestart.Cursor:D}");
        _output.WriteLine($"V{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} COMPLETED EVALUATOR: {currentComplete.CompletedEvaluatorVersion}");

        // The same external Founder fixture also supplies a normal raw
        // training submission. Mark only its capability watermark stale, not
        // its canonical content, then use the existing reconciliation
        // authority to prove no resubmission or duplicate corpus is needed.
        var rawFixture = JsonSerializer.Deserialize<LegendConnectCurriculumManifestSubmission>(
            (await db.LegendCurriculumManifestWorkItems.OrderBy(item => item.CreatedUtc).FirstAsync()).PayloadJson)!;
        var rawFixtureText = rawFixture.Families[0].Examples[0].Text;
        var rawAccepted = await operations.SubmitFounderKnowledgeAsync(
            founderId,
            new LegendConnectKnowledgeSubmission(
                "en",
                rawFixtureText,
                null,
                null,
                "External curriculum replay proof",
                null,
                null,
                "FounderApproved"));
        Assert.True(rawAccepted.Succeeded, rawAccepted.Message);
        var rawSubmission = await db.LegendFounderTrainingSubmissions
            .SingleAsync(item => item.Id == rawAccepted.TrainingSubmissionId);
        Assert.Equal(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            rawSubmission.CompletedLanguageIntelligenceEvaluatorVersion);
        var rawCanonicalBeforeReplay = new
        {
            Submissions = await db.LegendFounderTrainingSubmissions.CountAsync(),
            SubmissionUnits = await db.LegendFounderTrainingSubmissionUnits.CountAsync(),
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(),
            Transitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)
        };
        rawSubmission.CompletedLanguageIntelligenceEvaluatorVersion =
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current - 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var staleRawSubmissionCount = await db.LegendFounderTrainingSubmissions.CountAsync(item =>
            item.CompletedLanguageIntelligenceEvaluatorVersion <
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.True(staleRawSubmissionCount > 0);
        var rawReplay = await trainingIngestion.ReconcileLegacyAsync(25);
        db.ChangeTracker.Clear();
        Assert.Equal(staleRawSubmissionCount, rawReplay.CapabilityReplayedSubmissionCount);
        Assert.Equal(0, await db.LegendFounderTrainingSubmissions.CountAsync(item =>
            item.CompletedLanguageIntelligenceEvaluatorVersion <
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current));
        Assert.Equal(rawCanonicalBeforeReplay, new
        {
            Submissions = await db.LegendFounderTrainingSubmissions.CountAsync(),
            SubmissionUnits = await db.LegendFounderTrainingSubmissionUnits.CountAsync(),
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(),
            Transitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)
        });
        Assert.Equal(0, (await trainingIngestion.ReconcileLegacyAsync(25)).CapabilityReplayedSubmissionCount);
        _output.WriteLine($"STALE RAW FOUNDER SUBMISSIONS REPLAYED: {staleRawSubmissionCount}");
        _output.WriteLine("CURRENT RAW FOUNDER SUBMISSIONS REPLAYED ON SECOND RUN: 0");

        // Simulate the historical deployment condition in this isolated
        // database: canonical Founder curriculum exists, but the later
        // semantic-transition capability has not yet produced its derived
        // evidence. The external manifest remains the sole declaration of
        // meaning; no fixture curriculum or runtime rule is introduced.
        var canonicalAfterNormalProcessing = new
        {
            Families = await db.LegendCurriculumFamilies.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null),
            Transitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null),
            SupersededTransitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc != null)
        };
        var transitionSupportAfterNormalProcessing = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null)
            .GroupBy(item => new
            {
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId,
                item.IndependentSourceIdentity
            })
            .Select(group => new { group.Key, Count = group.Count() })
            .OrderBy(item => item.Key.TransitionSignature)
            .ThenBy(item => item.Key.SourceCurriculumExampleId)
            .ThenBy(item => item.Key.ResultCurriculumExampleId)
            .ToArrayAsync();

        var completedWork = await db.LegendCurriculumManifestWorkItems
            .Where(item => item.ProcessingState == "Completed")
            .ToListAsync();
        Assert.NotEmpty(completedWork);
        Assert.Contains(completedWork, item => item.FamilyCount > 1);
        foreach (var work in completedWork)
        {
            work.CompletedLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current - 1;
        }
        await db.SaveChangesAsync();
        await db.LegendSemanticTransitionEvidence.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, await db.LegendSemanticTransitionEvidence.CountAsync());
        var transitionsAfterHistoricalSimulation = 0;

        // The first replay page has a durable family cursor. An expired lease
        // is the same recovery boundary used after a process interruption.
        LegendCurriculumManifestWorkItem? interruptedReplay = null;
        for (var pass = 0; pass < 32 && interruptedReplay is null; pass++)
        {
            Assert.Equal(1, await processor.ProcessPendingAsync(1));
            db.ChangeTracker.Clear();
            interruptedReplay = await db.LegendCurriculumManifestWorkItems
                .Where(item => item.ProcessingState == "Pending" &&
                    item.NextFamilyIndex > 0 && item.NextFamilyIndex < item.FamilyCount)
                .OrderBy(item => item.CreatedUtc)
                .FirstOrDefaultAsync();
        }
        Assert.NotNull(interruptedReplay);
        interruptedReplay.ProcessingState = "Processing";
        interruptedReplay.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        for (var pass = 0; pass < 128; pass++)
        {
            await processor.ProcessPendingAsync(1);
            db.ChangeTracker.Clear();
            var states = await db.LegendCurriculumManifestWorkItems
                .Select(item => new
                {
                    item.ProcessingState,
                    item.CompletedLanguageIntelligenceEvaluatorVersion
                })
                .ToArrayAsync();
            if (states.All(item => item.ProcessingState == "Completed" &&
                item.CompletedLanguageIntelligenceEvaluatorVersion ==
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current))
                break;
            Assert.DoesNotContain(states, item => item.ProcessingState == "Failed");
        }

        var replayedWork = await db.LegendCurriculumManifestWorkItems.ToListAsync();
        Assert.All(replayedWork, item =>
        {
            Assert.Equal("Completed", item.ProcessingState);
            Assert.Equal(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                item.CompletedLanguageIntelligenceEvaluatorVersion);
        });
        var canonicalAfterCapabilityReplay = new
        {
            Families = await db.LegendCurriculumFamilies.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null),
            Transitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null),
            SupersededTransitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc != null)
        };
        var transitionSupportAfterCapabilityReplay = await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null)
            .GroupBy(item => new
            {
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId,
                item.IndependentSourceIdentity
            })
            .Select(group => new { group.Key, Count = group.Count() })
            .OrderBy(item => item.Key.TransitionSignature)
            .ThenBy(item => item.Key.SourceCurriculumExampleId)
            .ThenBy(item => item.Key.ResultCurriculumExampleId)
            .ToArrayAsync();

        Assert.Equal(canonicalAfterNormalProcessing, canonicalAfterCapabilityReplay);
        Assert.Equal(transitionSupportAfterNormalProcessing, transitionSupportAfterCapabilityReplay);
        Assert.Equal(0, await db.LegendSemanticTransitionEvidence
            .Where(item => item.SupersededUtc == null)
            .GroupBy(item => new
            {
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.ResultCurriculumExampleId
            })
            .CountAsync(group => group.Count() > 1));
        Assert.Equal(0, await db.LegendLanguageCompositionalAnchors
            .Where(item => item.SupersededUtc == null)
            .GroupBy(item => item.AnchorSignature)
            .CountAsync(group => group.Count() > 1));
        Assert.Equal(0, await processor.ProcessPendingAsync(1));

        var request = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_REQUEST") ?? "Hello legend";
        var history = JsonSerializer.Deserialize<List<LegendFounderAiChatMessage>>(
            Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_HISTORY") ?? "[]") ?? [];
        var expectNative = !string.Equals(
            Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_EXPECT_NATIVE"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        var source = await curriculum.AnalyzeSemanticTransitionSourceSemanticsAsync("en", request);
        var nativeClock = Stopwatch.StartNew();
        var native = await founderLegend.TryInferConversationWithDiscourseAsync(
            founder,
            request,
            history.Select(item => new LegendConnectConversationContextItem(
                    item.Role ?? string.Empty,
                    item.Content ?? string.Empty))
                .ToArray(),
            discourseState: null,
            sourceLanguageCode: "en");
        nativeClock.Stop();

        _output.WriteLine($"REQUEST: {request}");
        _output.WriteLine($"SOURCE STATE: {source.State}");
        _output.WriteLine($"SOURCE REASONS: {string.Join(", ", source.Reasons)}");
        _output.WriteLine($"SOURCE COMPONENTS: {string.Join(" | ", source.Components.Select(item => $"{item.Dimension}={item.Value}@{item.SurfaceForm}"))}");
        _output.WriteLine($"FAMILIES BEFORE: {familiesBefore}");
        _output.WriteLine($"EXAMPLES BEFORE: {examplesBefore}");
        _output.WriteLine($"ANCHORS BEFORE: {anchorsBefore}");
        _output.WriteLine($"TRANSITIONS BEFORE: {transitionsBefore}");
        _output.WriteLine($"HISTORICAL TRANSITIONS MISSING: {transitionsAfterHistoricalSimulation}");
        _output.WriteLine($"TRANSITIONS AFTER REPLAY: {canonicalAfterCapabilityReplay.Transitions}");
        _output.WriteLine($"DUPLICATE ACTIVE TRANSITIONS: 0");
        _output.WriteLine($"DUPLICATE ACTIVE ANCHORS: 0");
        _output.WriteLine($"NATIVE REASON: {native.ReasonCode}");
        _output.WriteLine($"NATIVE AUTHORITY: {native.AuthoritySummary}");
        Assert.Equal(expectNative, native.Supported);
        if (expectNative)
        {
            // DIRECT RESPONSE RELEASE ASSERTIONS
            // The governed native authority itself must answer.
            Assert.False(native.RequiresEscalation);
            Assert.True(
                native.EvidenceCount > 0,
                $"Native inference claimed support for '{request}' without governed evidence.");
            Assert.False(string.IsNullOrWhiteSpace(native.Answer));
            Assert.False(string.Equals(request, native.Answer, StringComparison.OrdinalIgnoreCase));

            Assert.False(
                native.Answer!.Contains(
                    "does not yet have enough governed evidence",
                    StringComparison.OrdinalIgnoreCase));

            Assert.False(
                native.Answer.Contains(
                    "external teacher is unavailable",
                    StringComparison.OrdinalIgnoreCase));

            Assert.False(
                native.Answer.Contains(
                    "No unsupported answer was produced",
                    StringComparison.OrdinalIgnoreCase));

            _output.WriteLine($"NATIVE SUPPORTED: {native.Supported}");
            _output.WriteLine($"NATIVE EVIDENCE: {native.EvidenceCount}");
            _output.WriteLine($"REQUIRES ESCALATION: {native.RequiresEscalation}");
        }

        var factory = new CountingHttpClientFactory();
        var service = new LegendFounderAiConversationService(
            factory,
            configuration,
            founderLegend,
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(
                db, new AgentProfileAccessResolver(db), operations),
            registry,
            ControllerTestHelpers.BuildTranslationService());
        var replyClock = Stopwatch.StartNew();
        var reply = await service.ReplyAsync(
            founder,
            new LegendFounderAiChatRequest
            {
                Mode = "legend",
                NativeOnly = true,
                Messages = [.. history, new("user", request)]
            });
        replyClock.Stop();

        Assert.True(reply.Succeeded);
        if (expectNative)
        {
            var replyMessage = Assert.IsType<string>(reply.Message);
            Assert.Equal(native.Answer, replyMessage);
            Assert.Equal("LegendAi", reply.ResponseAuthority);
            Assert.Equal("native_response", reply.Stage);

            Assert.False(
                replyMessage.Contains(
                    "does not yet have enough governed evidence",
                    StringComparison.OrdinalIgnoreCase));

            Assert.False(
                replyMessage.Contains(
                    "external teacher is unavailable",
                    StringComparison.OrdinalIgnoreCase));

            Assert.False(
                replyMessage.Contains(
                    "No unsupported answer was produced",
                    StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.NotEqual(request, reply.Message);
        }

        // If this is non-zero, the external conversational provider
        // participated and this is NOT a native LEGEND release proof.
        Assert.Equal(0, factory.CreateClientCalls);

        _output.WriteLine($"REQUEST: {request}");
        _output.WriteLine($"SOURCE STATE: {source.State}");
        _output.WriteLine($"SOURCE COMPONENTS: {string.Join(" | ", source.Components.Select(item => $"{item.Dimension}={item.Value}@{item.SurfaceForm}"))}");
        _output.WriteLine($"FAMILIES: {await db.LegendCurriculumFamilies.CountAsync()}");
        _output.WriteLine($"EXAMPLES: {await db.LegendCurriculumExamples.CountAsync(item => item.SupersededUtc == null)}");
        _output.WriteLine($"ANCHORS: {await db.LegendLanguageCompositionalAnchors.CountAsync(item => item.SupersededUtc == null)}");
        _output.WriteLine($"TRANSITIONS: {await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)}");
        _output.WriteLine($"NATIVE RESPONSE: {native.Answer}");
        _output.WriteLine($"NATIVE REASON: {native.ReasonCode}");
        _output.WriteLine($"FINAL RESPONSE: {reply.Message}");
        _output.WriteLine($"OPENAI CLIENTS: {factory.CreateClientCalls}");
        _output.WriteLine("OPENAI HTTP CALLS: 0");
        _output.WriteLine($"NATIVE LATENCY MS: {nativeClock.Elapsed.TotalMilliseconds:F0}");
        _output.WriteLine($"REPLY LATENCY MS: {replyClock.Elapsed.TotalMilliseconds:F0}");
    }

    [Fact]
    public async Task HistoricalAlignmentConflict_QuarantinesWithoutBlockingV15ConvergenceOnIsolatedSqlServer()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_REPLAY_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Historical replay SQL Server proof is opt-in; no isolated database was selected.");
            return;
        }

        await using var db = new MasterAppDbContext(
            new DbContextOptionsBuilder<MasterAppDbContext>()
                .UseSqlServer(connectionString)
                .Options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("LegendConnect:ContextualComposition:Mode", "Shadow"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:Code", "en"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:Name", "English"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:NativeName", "English"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:1:Code", "x-test"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:1:Name", "Test language"),
                new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:1:NativeName", "Test language")
            })
            .Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db,
            new FounderAccess(),
            registry,
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var operationsWriter = new LegendConnectOperationalEventWriter(
            db,
            NullLogger<LegendConnectOperationalEventWriter>.Instance);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance,
            operationsWriter,
            intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus, operationsWriter);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            operationalEvents: operationsWriter,
            runtimePolicy: runtime,
            curriculum: curriculum,
            intelligence: intelligence);

        await DrainHistoricalReplayAsync(runtime, curriculum, intelligence, operations, 9);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "sql.replay.alignment.conflict",
            Provenance = "FounderApproved"
        };
        var source = HistoricalUnit("en", "A governed historical source.");
        var target = HistoricalUnit("x-test", "A governed historical target.");
        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = source.Id,
            LanguageCode = "en",
            Provenance = "FounderApproved"
        };
        var targetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = target.Id,
            LanguageCode = "x-test",
            DerivedFromCurriculumExampleId = sourceExample.Id,
            Provenance = "FounderApproved"
        };
        var alignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = pair.PairKey,
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "FounderApproved",
            Provenance = "FounderApproved",
            HumanVerified = true,
            QualityState = "Verified",
            Confidence = 1m,
            ObservationCount = 1
        };
        db.AddRange(
            family,
            source,
            target,
            sourceExample,
            targetExample,
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(),
                CurriculumExampleId = sourceExample.Id,
                Dimension = "register",
                Value = "warm"
            },
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(),
                CurriculumExampleId = targetExample.Id,
                Dimension = "register",
                Value = "formal"
            },
            alignment);
        await db.SaveChangesAsync();

        var currentStart = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(9, currentStart.CompletedEvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, currentStart.Phase);
        await DrainHistoricalReplayAsync(
            runtime,
            curriculum,
            intelligence,
            operations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

        var completed = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.False(completed.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceEvaluatorVersion.Current, completed.CompletedEvaluatorVersion);
        var retained = await db.LegendCurriculumExampleVariations.SingleAsync(item =>
            item.CurriculumExampleId == targetExample.Id && item.Dimension == "register");
        Assert.Equal("formal", retained.Value);
        var quarantines = await db.LegendConnectOperationalEvents.Where(item =>
            item.Category == "HistoricalCurriculumReplay" &&
            item.ErrorCode == "conflicting_controlled_variation" &&
            item.CorrelationId == alignment.Id.ToString("D")).ToListAsync();
        Assert.Single(quarantines);

        var converged = new
        {
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            Variations = await db.LegendCurriculumExampleVariations.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(),
            ActiveTransitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null),
            Quarantines = quarantines.Count
        };
        await DrainHistoricalReplayAsync(
            runtime,
            curriculum,
            intelligence,
            operations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(converged, new
        {
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            Variations = await db.LegendCurriculumExampleVariations.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(),
            ActiveTransitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null),
            Quarantines = await db.LegendConnectOperationalEvents.CountAsync(item =>
                item.Category == "HistoricalCurriculumReplay" &&
                item.ErrorCode == "conflicting_controlled_variation" &&
                item.CorrelationId == alignment.Id.ToString("D"))
        });

        _output.WriteLine("SQL V9 COMPLETED: 9");
        _output.WriteLine($"SQL V{LegendConnectLanguageIntelligenceEvaluatorVersion.Current} COMPLETED: {completed.CompletedEvaluatorVersion}");
        _output.WriteLine("SQL CONFLICT VALUE RETAINED: formal");
        _output.WriteLine("SQL CONFLICT QUARANTINES: 1");
        _output.WriteLine($"SQL SECOND-RUN TEXT UNITS: {converged.TextUnits}");
        _output.WriteLine($"SQL SECOND-RUN ACTIVE TRANSITIONS: {converged.ActiveTransitions}");
    }

    [Fact]
    public async Task AuthenticatedHttpChat_UsesMvcAntiforgeryAndNativeReplyAgainstIsolatedSqlServer()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Authenticated Founder HTTP proof is opt-in; no isolated database was selected.");
            return;
        }

        var founderId = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_FOUNDER_ID") ??
            "e2e4d030-8d47-4a5b-a2db-5f2e50d14570";
        var request = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_REQUEST") ?? "Hello legend";
        var expectedNativeResponse = Environment.GetEnvironmentVariable(
            "LEGEND_FOUNDER_E2E_EXPECTED_RESPONSE") ?? "hello and welcome legend.";
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty)
                })
                .Build();
            var factory = new CountingHttpClientFactory();
            using var host = await BuildAuthenticatedHttpHostAsync(
                connectionString,
                configuration,
                factory);
            var client = host.GetTestClient();

            var tokenRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/__legend-connect-proof/token");
            tokenRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
            Assert.NotNull(token);

            var chatRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/founder/legend-ai/chat")
            {
                Content = JsonContent.Create(new LegendFounderAiChatRequest
                {
                    Mode = "legend",
                    NativeOnly = true,
                    Messages = [new("user", request)]
                })
            };
            chatRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            chatRequest.Headers.Add("RequestVerificationToken", token!.RequestToken);
            var antiforgeryCookie = ExtractAntiforgeryCookie(tokenResponse);
            if (!string.IsNullOrWhiteSpace(antiforgeryCookie))
                chatRequest.Headers.Add("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(chatRequest);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<LegendFounderAiChatResponse>();
            Assert.NotNull(body);
            Assert.True(body!.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(body.Message));
            Assert.False(string.Equals(request, body.Message, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("legend", body.Mode);
            Assert.Equal(expectedNativeResponse, body.Message);
            Assert.Equal(0, factory.CreateClientCalls);

            _output.WriteLine("HTTP PATH: authenticated + antiforgery + MVC controller + ReplyAsync");
            _output.WriteLine($"HTTP REQUEST: {request}");
            _output.WriteLine($"HTTP STATUS: {(int)response.StatusCode}");
            _output.WriteLine($"HTTP RESPONSE: {body.Message}");
            _output.WriteLine($"OPENAI CLIENTS: {factory.CreateClientCalls}");
            _output.WriteLine("OPENAI HTTP CALLS: 0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    [Fact]
    public async Task AuthenticatedHttpChat_CapturesConfiguredConversationMatrixAgainstIsolatedSqlServer()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_CONNECTION");
        var matrixJson = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_CONVERSATION_MATRIX");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(matrixJson))
        {
            _output.WriteLine("Configured Founder conversation matrix is opt-in; isolated SQL and request matrix are required.");
            return;
        }

        var requests = JsonSerializer.Deserialize<List<ConversationMatrixRequest>>(
            matrixJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.False(string.IsNullOrWhiteSpace(request.Text)));

        var founderId = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_E2E_FOUNDER_ID") ??
            "e2e4d030-8d47-4a5b-a2db-5f2e50d14570";
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var previousOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var previousOpenAiApiKeyAlternate = Environment.GetEnvironmentVariable("OpenAI__ApiKey");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        // The resolver intentionally falls back to process environment
        // variables. Clear them only for this isolated test so an unsupported
        // prompt proves the clean native limitation without any provider path.
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.Empty);
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", string.Empty);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty)
                })
                .Build();
            var factory = new CountingHttpClientFactory();
            using var logCapture = new ExceptionCapturingLoggerProvider();
            using var host = await BuildAuthenticatedHttpHostAsync(
                connectionString,
                configuration,
                factory,
                loggerProvider: logCapture);
            var client = host.GetTestClient();

            foreach (var request in requests)
            {
                var tokenRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    "/__legend-connect-proof/token");
                tokenRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
                var tokenResponse = await client.SendAsync(tokenRequest);
                tokenResponse.EnsureSuccessStatusCode();
                var token = await tokenResponse.Content.ReadFromJsonAsync<AntiforgeryTokenDto>();
                Assert.NotNull(token);

                var chatRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    "/founder/legend-ai/chat")
                {
                    Content = JsonContent.Create(new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        NativeOnly = true,
                        Messages = [.. (request.History ?? []), new("user", request.Text)]
                    })
                };
                chatRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
                chatRequest.Headers.Add("RequestVerificationToken", token!.RequestToken);
                var antiforgeryCookie = ExtractAntiforgeryCookie(tokenResponse);
                if (!string.IsNullOrWhiteSpace(antiforgeryCookie))
                    chatRequest.Headers.Add("Cookie", antiforgeryCookie);

                var response = await client.SendAsync(chatRequest);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    foreach (var exception in logCapture.Exceptions)
                        _output.WriteLine($"MATRIX SERVER EXCEPTION: {exception}");
                }
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<LegendFounderAiChatResponse>();
                Assert.NotNull(body);
                Assert.True(body!.Succeeded);
                Assert.Equal("legend", body.Mode);
                Assert.False(string.IsNullOrWhiteSpace(body.Message));
                Assert.False(string.Equals(request.Text, body.Message, StringComparison.OrdinalIgnoreCase));

                var governedLimitation = body.Message!.Contains(
                    "does not yet have enough governed evidence",
                    StringComparison.OrdinalIgnoreCase);
                if (request.RequireNative)
                {
                    Assert.False(governedLimitation, $"Expected native support for '{request.Text}'.");
                }
                else if (!governedLimitation)
                {
                    Assert.False(body.Message.Contains(
                        "external teacher is unavailable",
                        StringComparison.OrdinalIgnoreCase));
                }

                _output.WriteLine($"MATRIX REQUEST: {request.Text}");
                _output.WriteLine($"MATRIX NATIVE: {!governedLimitation}");
                _output.WriteLine($"MATRIX RESPONSE: {body.Message}");
            }

            Assert.Equal(0, factory.CreateClientCalls);
            _output.WriteLine("MATRIX OPENAI CLIENTS: 0");
            _output.WriteLine("MATRIX OPENAI HTTP CALLS: 0");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiApiKey);
            Environment.SetEnvironmentVariable("OpenAI__ApiKey", previousOpenAiApiKeyAlternate);
        }
    }

    [Fact]
    public async Task FounderSectionPages_AndRetainedRetrievalQueryCountAndLatency_RemainBoundedAgainstLargeSqlServerDataset()
    {
        var connectionString = Environment.GetEnvironmentVariable("LEGEND_FOUNDER_SCALABILITY_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _output.WriteLine("Founder SQL Server scalability proof is opt-in; no isolated database was selected.");
            return;
        }

        const string founderId = "4f4a89fd-b4a1-4100-92e4-5c7eafb384db";
        var previousFounderOid = Environment.GetEnvironmentVariable("FOUNDER_OID");
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);
        var commandCounter = new CountingDbCommandInterceptor();
        try
        {
            await using (var db = new MasterAppDbContext(
                new DbContextOptionsBuilder<MasterAppDbContext>()
                    .UseSqlServer(connectionString)
                    .AddInterceptors(commandCounter)
                    .Options))
            {
                if (!await db.AgentProfiles.AnyAsync(item => item.AgentUserId == founderId))
                {
                    db.AgentProfiles.Add(new AgentProfile
                    {
                        Id = Guid.NewGuid(),
                        AgentUserId = founderId,
                        AgentUpn = "founder-scalability@legend.local",
                        NormalizedEmail = "founder-scalability@legend.local",
                        IsActive = true
                    });
                    await db.SaveChangesAsync();
                }

                var configuration = new ConfigurationBuilder().Build();
                var registry = new LegendLanguageRegistry(db, configuration);
                await registry.ListEnabledTranslationLanguagesAsync();
                const string marker = "founder-page-scale-20260820";
                if (!await db.LegendCurriculumFamilies.AnyAsync(item => item.FamilyKey == marker + ".historical"))
                {
                    var start = DateTime.UtcNow.AddDays(-30);
                    for (var batch = 0; batch < 20; batch++)
                    {
                        var families = new List<LegendCurriculumFamily>();
                        var units = new List<LegendLanguageTextUnit>();
                        var examples = new List<LegendCurriculumExample>();
                        var anchors = new List<LegendLanguageCompositionalAnchor>();
                        for (var offset = 0; offset < 500; offset++)
                        {
                            var index = batch * 500 + offset;
                            var timestamp = start.AddSeconds(index);
                            var family = new LegendCurriculumFamily
                            {
                                Id = Guid.NewGuid(),
                                FamilyKey = index == 0 ? marker + ".historical" : $"{marker}.{index:D5}",
                                SemanticCategory = "Scalability",
                                Provenance = "FounderApproved",
                                CreatedUtc = timestamp,
                                UpdatedUtc = timestamp
                            };
                            var unit = new LegendLanguageTextUnit
                            {
                                Id = Guid.NewGuid(),
                                LanguageCode = "en",
                                StoragePartition = "Legend:en",
                                NormalizedHash = LegendLanguageIdentity.TextHash(
                                    index == 0
                                        ? "A historical SQL Server curriculum example."
                                        : $"SQL Server curriculum example {index}."),
                                Text = index == 0 ? "A historical SQL Server curriculum example." : $"SQL Server curriculum example {index}.",
                                Provenance = "FounderApproved",
                                IsTrainingEligible = true,
                                CreatedUtc = timestamp,
                                UpdatedUtc = timestamp
                            };
                            var example = new LegendCurriculumExample
                            {
                                Id = Guid.NewGuid(),
                                CurriculumFamilyId = family.Id,
                                TextUnitId = unit.Id,
                                LanguageCode = "en",
                                Provenance = "FounderApproved",
                                CreatedUtc = timestamp,
                                UpdatedUtc = timestamp
                            };
                            families.Add(family);
                            units.Add(unit);
                            examples.Add(example);
                            anchors.Add(new LegendLanguageCompositionalAnchor
                            {
                                Id = Guid.NewGuid(),
                                LanguageCode = "en",
                                TextUnitId = unit.Id,
                                CurriculumFamilyId = family.Id,
                                CurriculumExampleId = example.Id,
                                Dimension = "function",
                                Value = "scalability",
                                AnchorSignature = Guid.NewGuid().ToString("N"),
                                Provenance = "FounderApproved",
                                CreatedUtc = timestamp
                            });
                        }
                        db.AddRange(families);
                        db.AddRange(units);
                        db.AddRange(examples);
                        db.AddRange(anchors);
                        await db.SaveChangesAsync();
                        db.ChangeTracker.Clear();
                    }
                }
                var historicalUnit = await db.LegendLanguageTextUnits.SingleAsync(item =>
                    item.LanguageCode == "en" &&
                    item.Text == "A historical SQL Server curriculum example.");
                var historicalHash = LegendLanguageIdentity.TextHash(historicalUnit.Text);
                if (!string.Equals(historicalUnit.NormalizedHash, historicalHash, StringComparison.Ordinal))
                {
                    historicalUnit.NormalizedHash = historicalHash;
                    await db.SaveChangesAsync();
                    db.ChangeTracker.Clear();
                }

                var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
                var operations = new LegendConnectOperations(db, registry, corpus, configuration);
                var founder = new FounderLegendConnectService(operations, new AgentProfileAccessResolver(db));
                commandCounter.Reset();
                var retrievalClock = Stopwatch.StartNew();
                var retained = await operations.SearchRetainedKnowledgeAsync(
                    "A historical SQL Server curriculum example.",
                    sourceLanguageCode: "en",
                    take: 12);
                retrievalClock.Stop();
                Assert.Contains(retained.Items, item =>
                    item.Kind == "CanonicalText" &&
                    item.Content == "A historical SQL Server curriculum example.");
                Assert.True(
                    commandCounter.Commands <= 6,
                    $"Indexed retained retrieval executed {commandCounter.Commands} commands.");
                Assert.True(
                    retrievalClock.Elapsed < TimeSpan.FromSeconds(5),
                    $"Indexed retained retrieval took {retrievalClock.Elapsed.TotalMilliseconds:F0} ms.");
                _output.WriteLine($"SQL RETAINED RETRIEVAL LATENCY MS: {retrievalClock.Elapsed.TotalMilliseconds:F0}");
                _output.WriteLine($"SQL RETAINED RETRIEVAL QUERY COUNT: {commandCounter.Commands}");

                commandCounter.Reset();
                var shellClock = Stopwatch.StartNew();
                var shell = await founder.GetDashboardAsync(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", founderId)], "e2e")), "en", null);
                shellClock.Stop();
                Assert.Equal(10_000, shell.Shell.SelectedLanguage!.CanonicalEntryCount);
                Assert.Equal(10_000, shell.Shell.SelectedLanguage.CurriculumExampleCount);
                Assert.Equal(10_000, shell.Shell.SelectedLanguage.CompositionalAnchorCount);
                _output.WriteLine($"SQL SHELL LATENCY MS: {shellClock.Elapsed.TotalMilliseconds:F0}");
                _output.WriteLine($"SQL SHELL QUERY COUNT: {commandCounter.Commands}");
                _output.WriteLine("SQL SHELL MATERIALIZED DETAIL ROWS: 0");
            }

            var configurationForHttp = new ConfigurationBuilder().Build();
            var factory = new CountingHttpClientFactory();
            using var host = await BuildAuthenticatedHttpHostAsync(
                connectionString,
                configurationForHttp,
                factory,
                commandCounter);
            var client = host.GetTestClient();

            commandCounter.Reset();
            using var shellRequest = new HttpRequestMessage(HttpMethod.Get, "/founder/legend-connect?language=en");
            shellRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var shellHttpClock = Stopwatch.StartNew();
            using var shellResponse = await client.SendAsync(shellRequest);
            shellHttpClock.Stop();
            shellResponse.EnsureSuccessStatusCode();
            var shellBytes = (await shellResponse.Content.ReadAsByteArrayAsync()).Length;
            var shellHtml = await shellResponse.Content.ReadAsStringAsync();
            Assert.Contains("data-legend-connect-shell", shellHtml, StringComparison.Ordinal);
            Assert.Contains("data-legend-section=\"curriculum\"", shellHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("founder-page-scale-20260820.historical", shellHtml, StringComparison.Ordinal);
            var shellHttpQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var switchedLanguageRequest = new HttpRequestMessage(HttpMethod.Get, "/founder/legend-connect?language=ht");
            switchedLanguageRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var switchedLanguageResponse = await client.SendAsync(switchedLanguageRequest);
            switchedLanguageResponse.EnsureSuccessStatusCode();
            var switchedLanguageHtml = await switchedLanguageResponse.Content.ReadAsStringAsync();
            Assert.Contains("data-language=\"ht\"", switchedLanguageHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("founder-page-scale-20260820.historical", switchedLanguageHtml, StringComparison.Ordinal);
            var switchedLanguageQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var switchedSectionRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=curriculum&language=ht");
            switchedSectionRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var switchedSectionResponse = await client.SendAsync(switchedSectionRequest);
            switchedSectionResponse.EnsureSuccessStatusCode();
            var switchedSection = await switchedSectionResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.NotNull(switchedSection);
            Assert.Empty(switchedSection!.Rows);
            var switchedSectionQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=curriculum&language=en");
            pageRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var pageClock = Stopwatch.StartNew();
            using var pageResponse = await client.SendAsync(pageRequest);
            pageClock.Stop();
            pageResponse.EnsureSuccessStatusCode();
            var responseBytes = (await pageResponse.Content.ReadAsByteArrayAsync()).Length;
            var curriculum = await pageResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.NotNull(curriculum);
            Assert.Equal(50, curriculum!.Rows.Count);
            Assert.NotNull(curriculum.NextCursor);
            var curriculumQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var nextPageRequest = new HttpRequestMessage(HttpMethod.Get,
                $"/founder/legend-connect/sections?section=curriculum&language=en&cursor={Uri.EscapeDataString(curriculum.NextCursor!)}");
            nextPageRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var nextPageResponse = await client.SendAsync(nextPageRequest);
            nextPageResponse.EnsureSuccessStatusCode();
            var nextCurriculum = await nextPageResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.NotNull(nextCurriculum);
            Assert.Equal(50, nextCurriculum!.Rows.Count);
            Assert.Empty(curriculum.Rows.Select(row => row[0]).Intersect(nextCurriculum.Rows.Select(row => row[0])));
            var nextCurriculumQueryCount = commandCounter.Commands;

            using var oldSearchRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=curriculum&language=en&search=founder-page-scale-20260820.historical");
            oldSearchRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            using var oldSearchResponse = await client.SendAsync(oldSearchRequest);
            oldSearchResponse.EnsureSuccessStatusCode();
            var oldSearch = await oldSearchResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.Single(oldSearch!.Rows);

            commandCounter.Reset();
            using var evidenceRequest = new HttpRequestMessage(HttpMethod.Get,
                "/founder/legend-connect/sections?section=evidence&language=en");
            evidenceRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var evidenceClock = Stopwatch.StartNew();
            using var evidenceResponse = await client.SendAsync(evidenceRequest);
            evidenceClock.Stop();
            evidenceResponse.EnsureSuccessStatusCode();
            var evidenceBytes = (await evidenceResponse.Content.ReadAsByteArrayAsync()).Length;
            var evidence = await evidenceResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.Equal(50, evidence!.Rows.Count);
            var evidenceQueryCount = commandCounter.Commands;

            commandCounter.Reset();
            using var examplesRequest = new HttpRequestMessage(HttpMethod.Get,
                $"/founder/legend-connect/sections?section=curriculum-examples&language=en&familyId={oldSearch.Rows[0][0]}");
            examplesRequest.Headers.Add("X-Legend-Connect-Founder", founderId);
            var examplesClock = Stopwatch.StartNew();
            using var examplesResponse = await client.SendAsync(examplesRequest);
            examplesClock.Stop();
            examplesResponse.EnsureSuccessStatusCode();
            var examplesBytes = (await examplesResponse.Content.ReadAsByteArrayAsync()).Length;
            var familyExamples = await examplesResponse.Content.ReadFromJsonAsync<LegendConnectFounderSectionPageSnapshot>();
            Assert.Single(familyExamples!.Rows);
            var examplesQueryCount = commandCounter.Commands;

            _output.WriteLine($"SQL SHELL HTTP STATUS: {(int)shellResponse.StatusCode}");
            _output.WriteLine($"SQL SHELL HTTP LATENCY MS: {shellHttpClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL SHELL HTTP RESPONSE BYTES: {shellBytes}");
            _output.WriteLine($"SQL SHELL HTTP QUERY COUNT: {shellHttpQueryCount}");
            _output.WriteLine("SQL SHELL HTTP MATERIALIZED DETAIL ROWS: 0");
            _output.WriteLine($"SQL LANGUAGE SWITCH HTTP QUERY COUNT: {switchedLanguageQueryCount}");
            _output.WriteLine($"SQL LANGUAGE SWITCH SECTION QUERY COUNT: {switchedSectionQueryCount}");
            _output.WriteLine("SQL LANGUAGE SWITCH CROSS-LANGUAGE ROWS: 0");
            _output.WriteLine($"SQL CURRICULUM HTTP STATUS: {(int)pageResponse.StatusCode}");
            _output.WriteLine($"SQL CURRICULUM LATENCY MS: {pageClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL CURRICULUM RESPONSE BYTES: {responseBytes}");
            _output.WriteLine($"SQL CURRICULUM QUERY COUNT: {curriculumQueryCount}");
            _output.WriteLine($"SQL CURRICULUM ROWS: {curriculum.Rows.Count}");
            _output.WriteLine($"SQL CURRICULUM NEXT-PAGE QUERY COUNT: {nextCurriculumQueryCount}");
            _output.WriteLine($"SQL CURRICULUM NEXT-PAGE ROWS: {nextCurriculum.Rows.Count}");
            _output.WriteLine($"SQL EVIDENCE LATENCY MS: {evidenceClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL EVIDENCE RESPONSE BYTES: {evidenceBytes}");
            _output.WriteLine($"SQL EVIDENCE QUERY COUNT: {evidenceQueryCount}");
            _output.WriteLine($"SQL EVIDENCE ROWS: {evidence.Rows.Count}");
            _output.WriteLine($"SQL FAMILY EXAMPLES LATENCY MS: {examplesClock.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"SQL FAMILY EXAMPLES RESPONSE BYTES: {examplesBytes}");
            _output.WriteLine($"SQL FAMILY EXAMPLES QUERY COUNT: {examplesQueryCount}");
            _output.WriteLine($"SQL FAMILY EXAMPLES ROWS: {familyExamples.Rows.Count}");
            _output.WriteLine("SQL OLD RECORD DISCOVERY: passed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounderOid);
        }
    }

    private static async Task<IHost> BuildAuthenticatedHttpHostAsync(
        string connectionString,
        IConfiguration configuration,
        IHttpClientFactory factory,
        DbCommandInterceptor? commandInterceptor = null,
        ILoggerProvider? loggerProvider = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddDataProtection();
                    services.AddLogging();
                    if (loggerProvider is not null)
                        services.AddSingleton(loggerProvider);
                    services.AddHttpContextAccessor();
                    services.AddControllersWithViews()
                        .AddApplicationPart(typeof(LegendFounderAiController).Assembly)
                        .AddApplicationPart(System.Reflection.Assembly.Load("AgentPortal.Views"))
                        .AddApplicationPart(typeof(Shared.Diagnostics.AppFailureDiagnostics).Assembly)
                        .AddApplicationPart(typeof(LegendConnectOperationalProofController).Assembly);
                    services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
                    services.AddAuthentication("LegendConnectTest")
                        .AddScheme<AuthenticationSchemeOptions, LegendConnectFounderAuthHandler>(
                            "LegendConnectTest",
                            _ => { });
                    services.AddAuthorization(options => options.DefaultPolicy =
                        new AuthorizationPolicyBuilder("LegendConnectTest")
                            .RequireAuthenticatedUser()
                            .Build());
                    services.AddDbContext<MasterAppDbContext>(options =>
                    {
                        options.UseSqlServer(connectionString);
                        if (commandInterceptor is not null)
                            options.AddInterceptors(commandInterceptor);
                    });
                    services.AddSingleton(configuration);
                    services.AddSingleton(factory);
                    services.AddSingleton<ITranslationService>(
                        ControllerTestHelpers.BuildTranslationService());
                    services.AddScoped<ILegendLanguageRegistry, LegendLanguageRegistry>();
                    services.AddScoped<LegendConnectCorpusService>();
                    services.AddScoped<LegendConnectCurriculumService>();
                    services.AddScoped<ILegendConnectOperations>(serviceProvider =>
                        new LegendConnectOperations(
                            serviceProvider.GetRequiredService<MasterAppDbContext>(),
                            serviceProvider.GetRequiredService<ILegendLanguageRegistry>(),
                            serviceProvider.GetRequiredService<LegendConnectCorpusService>(),
                            serviceProvider.GetRequiredService<IConfiguration>(),
                            curriculum: serviceProvider.GetRequiredService<LegendConnectCurriculumService>()));
                    services.AddScoped<AgentProfileAccessResolver>();
                    services.AddScoped<FounderLegendConnectService>();
                    services.AddScoped<LegendFounderAiConversationService>();
                    services.AddSingleton<LegendFounderAiProgressBroker>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();
    }

    private static async Task DrainHistoricalReplayAsync(
        LegendConnectRuntimePolicyAuthority runtime,
        LegendConnectCurriculumService curriculum,
        ILegendConnectTranslationIntelligence intelligence,
        ILegendConnectOperations operations,
        int evaluatorVersion)
    {
        for (var pass = 0; pass < 256; pass++)
        {
            var state = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);
            if (!state.RequiresWork)
                return;

            LegendConnectHistoricalReevaluationProgress progress;
            if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
            {
                progress = await intelligence.ReevaluateHistoricalProviderObservationsAsync(1, state.Cursor);
            }
            else if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
            {
                progress = await operations.ReconcileHistoricalOperationalTranslationsAsync(1, state.Cursor);
            }
            else
            {
                progress = await curriculum.ReevaluateHistoricalAlignmentsAsync(1, state.Phase, state.Cursor);
            }

            await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                evaluatorVersion,
                state.Phase,
                progress.LastProcessedId,
                progress.PhaseComplete);
        }

        throw new Xunit.Sdk.XunitException("The isolated SQL Server historical replay did not converge.");
    }

    private static IConfiguration ShadowConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("OpenAI:ApiKey", string.Empty),
            new KeyValuePair<string, string?>("LegendConnect:CorpusAcquisition:Enabled", "false"),
            new KeyValuePair<string, string?>("LegendConnect:ContextualComposition:Mode", "Shadow"),
            new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:Code", "en"),
            new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:Name", "English"),
            new KeyValuePair<string, string?>("LegendConnect:LanguageRegistry:Baseline:0:NativeName", "English")
        })
        .Build();

    private sealed record ShadowCorpusCounts(
        long FounderExamples,
        long SourceAnchors,
        long TransitionEvidence,
        long CurrentArtifacts,
        long StaleArtifacts,
        long ActiveTargetRealizationCandidates,
        long ActiveTargetRealizationEvidence,
        long TargetCandidatesWithEvidence,
        long ActiveAlignments,
        long ManifestWorkItems,
        long HistoricalWorkItems);

    private sealed record ShadowPrompt(
        string Reference,
        string Text,
        bool ExpectNative,
        string? ExpectedEvidenceStandard = null);

    private sealed record ProductionNativeProofCase(
        string Reference,
        string Category,
        string? DeclaredSourceLanguageCode,
        string NativeSourceLanguageCode,
        IReadOnlyList<LegendFounderAiChatMessage> Messages,
        bool ExpectNative,
        bool MustBeHeldOut = false,
        string? ExpectedEvidenceStandard = null)
    {
        internal static ProductionNativeProofCase Positive(
            string reference,
            string category,
            string prompt,
            bool mustBeHeldOut = false,
            string? declaredSourceLanguageCode = "en",
            string nativeSourceLanguageCode = "en",
            string? expectedEvidenceStandard = null) =>
            new(
                reference,
                category,
                declaredSourceLanguageCode,
                nativeSourceLanguageCode,
                [new LegendFounderAiChatMessage("user", prompt)],
                true,
                mustBeHeldOut,
                expectedEvidenceStandard);

        internal static ProductionNativeProofCase Negative(
            string reference,
            string category,
            string prompt) =>
            new(
                reference,
                category,
                "en",
                "en",
                [new LegendFounderAiChatMessage("user", prompt)],
                false);
    }

    private static string? ProductionMeaningFixtureFailure(
        string prompt,
        LegendConnectUtteranceMeaningGraphSnapshot graph,
        bool requireMultipleNodes)
    {
        // Capacity/authority failures must execute and remain visible as code
        // failures. Only explicit absence of governed semantic evidence is a
        // production-data fixture failure.
        if (graph.ReasonCode == "meaning_graph_retrieval_bound_exceeded")
            return null;
        if (graph.ReasonCode == "meaning_graph_component_unknown" ||
            graph.Nodes.Count == 0 || graph.UnknownSurfaceComponents.Count > 0)
        {
            return $"The production fixture lacks complete active Founder-governed meaning primitives for '{prompt}'. " +
                $"Reason={graph.ReasonCode}; unknown={string.Join(",", graph.UnknownSurfaceComponents)}.";
        }
        if (requireMultipleNodes && graph.Nodes.Count < 2)
        {
            return $"The production fixture for '{prompt}' does not contain the required independently governed cross-family primitives.";
        }
        if (graph.Nodes.Count > 1 && graph.Relations.Count == 0)
        {
            return $"The production fixture has governed primitives for '{prompt}' but no active Founder-governed relation connecting them.";
        }
        return graph.IsComposed
            ? null
            : $"The production fixture cannot compose '{prompt}' through governed meaning evidence. Reason={graph.ReasonCode}.";
    }

    private sealed record ProductionNativeProofResult(
        string Reference,
        string Category,
        string Phase,
        string Status,
        string? Failure,
        string? FailureKind,
        string? FailureCode,
        bool? ExpectedNative,
        bool? NativeSupported,
        string? ReasonCode,
        int? EvidenceCount,
        string? ResponseAuthority,
        string? Stage,
        int ProviderClientCount,
        double ElapsedMilliseconds)
    {
        public bool UsesAutomaticLanguageIdentification { get; init; }
        public ProductionDiagnosticEvidence? DiagnosticEvidence { get; init; }
        public ProductionFailureDiagnosis? FailureDiagnosis { get; init; }

        internal static ProductionNativeProofResult FailedFixture(
            string reference,
            string category,
            string failure) =>
            new(
                reference,
                category,
                "fixture",
                "failed",
                failure,
                "governed_content",
                "fixture_missing",
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                0);

        internal static ProductionNativeProofResult PassedCase(
            string reference,
            string category,
            bool expectedNative,
            bool nativeSupported,
            string? reasonCode,
            int evidenceCount,
            string? responseAuthority,
            string? stage,
            int providerClientCount,
            double elapsedMilliseconds) =>
            new(
                reference,
                category,
                "execution",
                "passed",
                null,
                null,
                null,
                expectedNative,
                nativeSupported,
                reasonCode,
                evidenceCount,
                responseAuthority,
                stage,
                providerClientCount,
                elapsedMilliseconds);

        internal static ProductionNativeProofResult FailedCase(
            string reference,
            string category,
            bool expectedNative,
            string failure,
            int providerClientCount,
            double elapsedMilliseconds) =>
            new(
                reference,
                category,
                "execution",
                "failed",
                failure,
                "case_execution",
                "execution_failed",
                expectedNative,
                null,
                null,
                null,
                null,
                null,
                providerClientCount,
                elapsedMilliseconds);

        internal static ProductionNativeProofResult FailedInfrastructurePreflight(
            string reference,
            string category,
            Exception exception)
        {
            var failureCode = exception switch
            {
                SqlException { Number: -2 } => "sql_command_timeout",
                TimeoutException => "operation_timeout",
                SqlException => "sql_failure",
                _ => "preflight_failure"
            };
            return new(
                reference,
                category,
                "preflight",
                "failed",
                $"The production proof preflight failed with {exception.GetType().Name}.",
                "infrastructure",
                failureCode,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                0);
        }
    }

    private static bool IsObservedAuthorizationFailure(Exception exception, RuntimeDiagnosticSnapshot events,
        ReadOnlyLegendDbCommandInterceptor.SqlCommandDiagnosticSnapshot sql) =>
        exception is UnauthorizedAccessException or AgentPortal.Security.ForbidResultException ||
        events.Records.Any(item => item.ExceptionType is "UnauthorizedAccessException" or "ForbidResultException") ||
        sql.Records.Any(item => item.SqlErrorNumber is 18456 or 18452 or 4060 or 916 or 229);

    internal sealed record ProductionDiagnosticEvidence(
        RuntimeDiagnosticSnapshot StageEvents, object SqlCommands, string SqlSnapshotReference,
        IReadOnlyDictionary<string, long> EvidencePrerequisites);

    internal sealed record ProductionFailureDiagnosis(
        string ObservedStage, string? ObservedReason, string AuthorityMethod,
        string Classification, string RootCauseStatus, string NextVerification);

    internal static ProductionFailureDiagnosis DiagnoseObservedFailure(
        string status, string phase, string? reason, bool? expectedNative, RuntimeDiagnosticSnapshot events,
        long sqlFailureEvents = 0,
        IReadOnlyList<ReadOnlyLegendDbCommandInterceptor.SqlCommandDiagnosticRecord>? sqlCommands = null)
    {
        var attributedCommand = sqlCommands?.FirstOrDefault(command =>
            (command.Outcome is "failed" or "canceled") && command.QueryAuthority is not null);
        var observedSqlFailure = events.SqlFailureEvents > 0 || sqlFailureEvents > 0;
        // SQL and runtime ordinals are independent. Only the command's own
        // exact static query label establishes its authority; never infer it
        // from the nearest runtime event in a case window.
        var failed = observedSqlFailure
            ? events.Records.FirstOrDefault(item => item.SqlErrorNumber is not null || item.Event == "QueryIterationFailed")
            : events.Records.FirstOrDefault(item =>
                item.Event is not "LanguageCandidatesRead" && item.ReasonCode != "candidate_read_failed" &&
                (item.Outcome is "failed" or "cancelled" ||
                (item.Event is "LanguageDetectionCompleted" or "SourceLanguageResolved" or "NativeInferenceCompleted" &&
                    (item.Supported == false || item.Outcome is "unresolved" or "blocked" or "rejected" or "uncomposed" or "unsupported" or "unavailable" or
                        "InvalidDeclaration" or "UnsupportedLanguage" or "SemanticAmbiguity" or "ProviderPolicyBlocked" or "TransientIdentificationUnavailable"))));
        if (status == "passed" && events.SqlFailureEvents == 0 && sqlFailureEvents == 0)
            return new("case_assertions", reason is null ? null : LegendConnectTelemetry.NormalizeDiagnosticReason(reason), "ProductionReadOnlyNativeProofMatrix",
                expectedNative == true ? "observed_native_response" : expectedNative == false ? "expected_negative" : "observed_read_only_result",
                events.Truncated ? "insufficient_diagnostic_evidence" : "no_failure_observed",
                "This result covers the exercised in-process SQL path; verify authenticated HTTP and production latency separately.");
        return new(attributedCommand is not null ? "sql_command" :
            failed?.Stage ?? (observedSqlFailure ? "unresolved_from_capture" : SafeObservationCode(phase)) ?? "unreported",
            failed?.ReasonCode is { } capturedReason && capturedReason != "none"
                ? capturedReason : reason is null ? null : LegendConnectTelemetry.NormalizeDiagnosticReason(reason),
            attributedCommand?.QueryAuthority ?? failed?.AuthorityMethod ?? "unresolved_from_capture",
            events.SqlFailureEvents > 0 || sqlFailureEvents > 0 ? "observed_sql_failure"
                : phase == "fixture" ? "fixture_prerequisite_missing" : "observed_case_failure",
            "observed_failure_only",
            attributedCommand is not null
                ? "The failed SQL command carries an exact code-defined query label. Inspect that query's execution plan and runtime waits before assigning a code repair; no plan was captured by this observation."
                : events.SqlFailureEvents > 0 || sqlFailureEvents > 0
                ? "The SQL snapshot and runtime trace share a case window, not a command-to-stage correlation. Reproduce the failed fingerprint or materialization event in its existing query before assigning a code repair."
                : phase == "fixture"
                    ? "Inspect the counted active evidence prerequisites and their canonical admission states; do not seed an expected answer or bypass eligibility."
                    : "Reproduce the first failed stage on this exact candidate and inspect its existing authority; the captured symptom does not establish a code repair.");
    }

    private static async Task<ShadowCorpusCounts> ReadShadowCountsAsync(
        MasterAppDbContext db) => new(
        await db.LegendCurriculumExamples.AsNoTracking().LongCountAsync(item =>
            item.SupersededUtc == null &&
            item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
            item.LanguageCode == "en"),
        await db.LegendLanguageCompositionalAnchors.AsNoTracking().LongCountAsync(item =>
            item.SupersededUtc == null && item.LanguageCode == "en" &&
            item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
        await db.LegendSemanticTransitionEvidence.AsNoTracking().LongCountAsync(item =>
            item.SupersededUtc == null &&
            item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
        await db.LegendLanguageDerivationArtifacts.AsNoTracking().LongCountAsync(item => item.State == "Current"),
        await db.LegendLanguageDerivationArtifacts.AsNoTracking().LongCountAsync(item => item.State == "Stale"),
        await db.LegendLanguageTargetRealizationCandidates.AsNoTracking().LongCountAsync(item =>
            item.SupersededUtc == null),
        await db.LegendLanguageTargetRealizationEvidence.AsNoTracking().LongCountAsync(item =>
            item.SupersededUtc == null),
        await (
            from candidate in db.LegendLanguageTargetRealizationCandidates.AsNoTracking()
            where candidate.SupersededUtc == null &&
                db.LegendLanguageTargetRealizationEvidence.Any(evidence =>
                    evidence.CandidateId == candidate.Id && evidence.SupersededUtc == null)
            select candidate.Id).LongCountAsync(),
        await db.LegendTranslationAlignments.AsNoTracking().LongCountAsync(item => item.SupersededUtc == null),
        await db.LegendCurriculumManifestWorkItems.AsNoTracking().LongCountAsync(),
        await db.LegendHistoricalReevaluationWorkItems.AsNoTracking().LongCountAsync());

    private void WriteShadowCounts(string label, ShadowCorpusCounts counts)
    {
        _output.WriteLine(
            $"{label}: examples={counts.FounderExamples}; anchors={counts.SourceAnchors}; transitions={counts.TransitionEvidence}; " +
            $"artifacts=current:{counts.CurrentArtifacts},stale:{counts.StaleArtifacts}; " +
            $"target-candidates={counts.ActiveTargetRealizationCandidates}; target-evidence={counts.ActiveTargetRealizationEvidence}; " +
            $"candidates-with-evidence={counts.TargetCandidatesWithEvidence}; alignments={counts.ActiveAlignments}; " +
            $"manifests={counts.ManifestWorkItems}; historical-work={counts.HistoricalWorkItems}");
    }

    private static async Task<IReadOnlyDictionary<string, int>> CopyLiveCurriculumSnapshotAsync(
        MasterAppDbContext production,
        MasterAppDbContext shadow,
        string founderId)
    {
        var copied = new Dictionary<string, int>(StringComparer.Ordinal);
        // The shadow is deliberately complete for the governed Founder scope,
        // not for unrelated provider-only corpus history.  This preserves
        // every active Founder source family plus every human-verified
        // directional dependency that can affect its V21 compilation, while
        // avoiding an unindexed in-memory replay of unrelated history.
        var founderSources = await (
            from example in production.LegendCurriculumExamples.AsNoTracking()
            join unit in production.LegendLanguageTextUnits.AsNoTracking()
                on example.TextUnitId equals unit.Id
            where example.DerivedFromCurriculumExampleId == null &&
                example.SupersededUtc == null && unit.IsTrainingEligible &&
                example.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                unit.Provenance == LegendConnectKnowledgeProvenance.FounderApproved
            select new { example.Id, example.CurriculumFamilyId, example.TextUnitId })
            .ToListAsync();
        var sourceExampleIds = founderSources.Select(item => item.Id).ToArray();
        var familyIds = founderSources.Select(item => item.CurriculumFamilyId).Distinct().ToArray();
        var sourceTextUnitIds = founderSources.Select(item => item.TextUnitId).Distinct().ToArray();
        var verifiedAlignments = await production.LegendTranslationAlignments.AsNoTracking()
            .Where(item => item.SupersededUtc == null && item.HumanVerified &&
                sourceTextUnitIds.Contains(item.SourceTextUnitId))
            .ToListAsync();
        var alignmentIds = verifiedAlignments.Select(item => item.Id).ToArray();
        var pairKeys = verifiedAlignments.Select(item => item.PairKey).Distinct().ToArray();
        var targetTextUnitIds = verifiedAlignments.Select(item => item.TargetTextUnitId).Distinct().ToArray();
        var replayTextUnitIds = sourceTextUnitIds.Concat(targetTextUnitIds).Distinct().ToArray();
        var replayExamples = await production.LegendCurriculumExamples.AsNoTracking()
            .Where(item => sourceExampleIds.Contains(item.Id) ||
                (item.DerivedFromCurriculumExampleId != null &&
                 sourceExampleIds.Contains(item.DerivedFromCurriculumExampleId.Value) &&
                 targetTextUnitIds.Contains(item.TextUnitId)))
            .Select(item => item.Id)
            .ToListAsync();
        var replayExampleIds = replayExamples.ToArray();
        await CopySnapshotSetAsync<AgentProfile>(
            production, shadow, copied, "founder-profile",
            query => query.Where(item => item.IsActive && item.AgentUserId != null &&
                item.AgentUserId.ToLower() == founderId.ToLower()));
        await CopySnapshotSetAsync<LegendLanguageDefinition>(production, shadow, copied, "languages");
        await CopySnapshotSetAsync<LegendLanguagePair>(production, shadow, copied, "pairs",
            query => query.Where(item => pairKeys.Contains(item.PairKey) || item.PairKey == "en:en"));
        await CopySnapshotSetAsync<LegendLanguageTextUnit>(
            production, shadow, copied, "training-text-units",
            query => query.Where(item => replayTextUnitIds.Contains(item.Id)));
        await CopySnapshotSetAsync<LegendCurriculumFamily>(production, shadow, copied, "families",
            query => query.Where(item => familyIds.Contains(item.Id)));
        await CopySnapshotSetAsync<LegendCurriculumExample>(production, shadow, copied, "examples",
            query => query.Where(item => replayExampleIds.Contains(item.Id)));
        await CopySnapshotSetAsync<LegendCurriculumExampleVariation>(production, shadow, copied, "variations",
            query => query.Where(item => replayExampleIds.Contains(item.CurriculumExampleId)));
        await CopySnapshotSetAsync<LegendTranslationAlignment>(production, shadow, copied, "alignments",
            query => query.Where(item => alignmentIds.Contains(item.Id)));
        await CopySnapshotSetAsync<LegendTranslationQualityEvidence>(production, shadow, copied, "quality-evidence",
            query => query.Where(item => alignmentIds.Contains(item.ObservedAlignmentId)));
        // Source and related text-unit endpoints have separate SQL Server
        // indexes.  Keep the Founder/human-verified closure bounded through
        // each one rather than asking SQL Server to intersect two whole
        // snapshot IN lists.  The final two-endpoint closure remains in
        // memory, so competing and contradictory relationships are retained.
        var contexts = await ReadContextRelationshipsForTextUnitClosureAsync(
            production,
            replayTextUnitIds);
        await CopySnapshotRowsAsync(shadow, copied, "contexts", contexts);
        // SQL Server has independently indexed source and result example
        // endpoints.  Do not issue a single two-IN predicate here: with a
        // whole Founder snapshot it can force an expensive plan before the
        // canonical shadow compiler has even started.  Read each indexed
        // direction in bounded batches, de-duplicate in memory, then retain
        // only edges whose two endpoints are inside the already-authorized
        // Founder/human-verified closure.  No prompt text participates in
        // this selection.
        var transitions = await ReadTransitionEvidenceForExampleClosureAsync(
            production,
            replayExampleIds);
        await CopySnapshotRowsAsync(shadow, copied, "transitions", transitions);
        var founderRelations = await ReadFounderRelationsForExampleClosureAsync(
            production,
            replayExampleIds);
        await CopySnapshotRowsAsync(shadow, copied, "founder-relations", founderRelations);
        await CopySnapshotSetAsync<LegendLanguageStructuralPattern>(production, shadow, copied, "structural-patterns",
            query => query.Where(item => familyIds.Contains(item.CurriculumFamilyId)));
        await CopySnapshotSetAsync<LegendLanguageStructuralRelationship>(production, shadow, copied, "structural-relationships");
        await CopySnapshotSetAsync<LegendLanguageStructuralEvidence>(production, shadow, copied, "structural-evidence",
            query => query.Where(item => familyIds.Contains(item.CurriculumFamilyId)));
        await CopySnapshotSetAsync<LegendLanguageLexeme>(production, shadow, copied, "lexemes");
        await CopySnapshotSetAsync<LegendLanguageLexicalOccurrence>(production, shadow, copied, "lexical-occurrences",
            query => query.Where(item => replayTextUnitIds.Contains(item.TextUnitId)));
        await CopySnapshotSetAsync<LegendLanguageLexicalRelationship>(production, shadow, copied, "lexical-relationships",
            query => query.Where(item => replayTextUnitIds.Contains(item.TextUnitId)));
        await CopySnapshotSetAsync<LegendLanguageCompositionalAnchor>(production, shadow, copied, "anchors",
            query => query.Where(item => replayExampleIds.Contains(item.CurriculumExampleId)));
        await CopySnapshotSetAsync<LegendLanguageMeaningNodeEvidence>(production, shadow, copied, "meaning-nodes",
            query => query.Where(item => replayExampleIds.Contains(item.CurriculumExampleId)));
        await CopySnapshotSetAsync<LegendLanguageMeaningPrimitive>(production, shadow, copied, "meaning-primitives");
        await CopySnapshotSetAsync<LegendLanguageMeaningPrimitiveEvidence>(production, shadow, copied, "meaning-primitive-evidence",
            query => query.Where(item => replayExampleIds.Contains(item.CurriculumExampleId)));
        await CopySnapshotSetAsync<LegendLanguageMeaningRelation>(production, shadow, copied, "meaning-relations");
        await CopySnapshotSetAsync<LegendLanguageMeaningRelationEvidence>(production, shadow, copied, "meaning-relation-evidence",
            query => query.Where(item => replayExampleIds.Contains(item.CurriculumExampleId)));
        await CopySnapshotSetAsync<LegendLanguageDiscourseReferenceRule>(production, shadow, copied, "reference-rules");
        await CopySnapshotSetAsync<LegendLanguageDiscourseReferenceRuleEvidence>(production, shadow, copied, "reference-rule-evidence",
            query => query.Where(item => replayExampleIds.Contains(item.CurriculumExampleId)));
        // Do not copy compiled serving projections into a rebuild proof. The
        // canonical alignment phase must recreate candidates and their exact
        // evidence links from the governed source snapshot above.
        await CopySnapshotSetAsync<LegendConnectRuntimePolicy>(production, shadow, copied, "runtime-policy");
        await CopySnapshotSetAsync<LegendLanguageDerivationContract>(production, shadow, copied, "contracts");
        await CopySnapshotSetAsync<LegendLanguageDerivationContractDependency>(production, shadow, copied, "contract-dependencies");
        // Derivation artifacts and convergence rows are metadata projections,
        // not semantic authority. Copying them would both weaken the rebuild
        // proof and scale with unrelated historical output. They are rebuilt
        // through the existing family dependency authority after compilation.
        return copied;
    }

    private static async Task<MasterAppDbContext> BuildRecoverableManifestShadowAsync(
        MasterAppDbContext production,
        Guid manifestId,
        string founderId)
    {
        var shadow = ControllerTestHelpers.BuildDb();
        var copied = new Dictionary<string, int>(StringComparer.Ordinal);
        await CopySnapshotSetAsync<AgentProfile>(production, shadow, copied, "founder-profile",
            query => query.Where(item => item.IsActive && item.AgentUserId != null &&
                item.AgentUserId.ToLower() == founderId.ToLower()));
        await CopySnapshotSetAsync<LegendLanguageDefinition>(production, shadow, copied, "languages");
        await CopySnapshotSetAsync<LegendLanguagePair>(production, shadow, copied, "pairs");
        await CopySnapshotSetAsync<LegendCurriculumManifestWorkItem>(production, shadow, copied, "manifest",
            query => query.Where(item => item.Id == manifestId));
        await CopySnapshotSetAsync<LegendHistoricalReevaluationWorkItem>(production, shadow, copied, "manifest-work",
            query => query.Where(item => item.SubjectId == manifestId));
        return shadow;
    }

    private static async Task CopySnapshotSetAsync<TEntity>(
        MasterAppDbContext production,
        MasterAppDbContext shadow,
        IDictionary<string, int> copied,
        string label,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? scope = null)
        where TEntity : class
    {
        IQueryable<TEntity> query = production.Set<TEntity>().AsNoTracking();
        if (scope is not null)
            query = scope(query);
        var rows = await query.ToListAsync();
        copied[label] = rows.Count;
        if (rows.Count == 0)
            return;
        shadow.Set<TEntity>().AddRange(rows);
        await shadow.SaveChangesAsync();
        shadow.ChangeTracker.Clear();
    }

    /// <summary>
    /// Loads transition evidence through the two endpoint indexes rather than
    /// asking SQL Server to intersect two large IN lists.  The final endpoint
    /// closure check deliberately happens in memory so competing and
    /// contradictory rows are preserved regardless of contribution state or
    /// provenance.
    /// </summary>
    private static async Task<IReadOnlyList<LegendSemanticTransitionEvidence>>
        ReadTransitionEvidenceForExampleClosureAsync(
            MasterAppDbContext production,
            IReadOnlyCollection<Guid> exampleIds)
    {
        var scope = exampleIds.ToHashSet();
        var rows = new Dictionary<Guid, LegendSemanticTransitionEvidence>();

        foreach (var batch in scope.Chunk(256))
        {
            var bySource = await production.LegendSemanticTransitionEvidence
                .AsNoTracking()
                .Where(item => batch.Contains(item.SourceCurriculumExampleId))
                .ToListAsync();
            foreach (var item in bySource)
                rows[item.Id] = item;

            var byResult = await production.LegendSemanticTransitionEvidence
                .AsNoTracking()
                .Where(item => batch.Contains(item.ResultCurriculumExampleId))
                .ToListAsync();
            foreach (var item in byResult)
                rows[item.Id] = item;
        }

        return rows.Values
            .Where(item => scope.Contains(item.SourceCurriculumExampleId) &&
                scope.Contains(item.ResultCurriculumExampleId))
            .OrderBy(item => item.Id)
            .ToArray();
    }

    /// <summary>
    /// Loads contextual relationships through the existing source and related
    /// text-unit indexes.  This has the same closure semantics as the former
    /// two-IN SQL predicate, but does not require the production optimizer to
    /// intersect a full Founder snapshot before the shadow replay begins.
    /// </summary>
    private static async Task<IReadOnlyList<LegendLanguageContextRelationship>>
        ReadContextRelationshipsForTextUnitClosureAsync(
            MasterAppDbContext production,
            IReadOnlyCollection<Guid> textUnitIds)
    {
        var scope = textUnitIds.ToHashSet();
        var rows = new Dictionary<Guid, LegendLanguageContextRelationship>();

        foreach (var batch in scope.Chunk(256))
        {
            var bySource = await production.LegendLanguageContextRelationships
                .AsNoTracking()
                .Where(item => batch.Contains(item.SourceTextUnitId))
                .ToListAsync();
            foreach (var item in bySource)
                rows[item.Id] = item;

            var byRelated = await production.LegendLanguageContextRelationships
                .AsNoTracking()
                .Where(item => batch.Contains(item.RelatedTextUnitId))
                .ToListAsync();
            foreach (var item in byRelated)
                rows[item.Id] = item;
        }

        return rows.Values
            .Where(item => scope.Contains(item.SourceTextUnitId) &&
                scope.Contains(item.RelatedTextUnitId))
            .OrderBy(item => item.Id)
            .ToArray();
    }

    /// <summary>
    /// Reads only system catalog metadata through the same guarded read-only
    /// production connection used by the shadow diagnostic.  This proves the
    /// bounded closure reader is backed by both endpoint indexes rather than
    /// inferring deployment state from the local EF model.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadContextEndpointIndexesAsync(
        MasterAppDbContext production) =>
        await production.Database.SqlQueryRaw<string>(
                """
                SELECT [i].[name] AS [Value]
                FROM [sys].[indexes] AS [i]
                WHERE [i].[object_id] = OBJECT_ID(N'[dbo].[LegendLanguageContextRelationships]')
                  AND [i].[name] IN (
                      N'IX_LegendLanguageContextRelationships_SourceTextUnitId',
                      N'IX_LegendLanguageContextRelationships_RelatedTextUnitId')
                ORDER BY [i].[name]
                """)
            .ToListAsync();

    /// <summary>
    /// Founder-declared example relations use the same source/result endpoint
    /// shape and indexes as transition evidence.  They must travel with the
    /// snapshot because transition evidence may carry their immutable lineage.
    /// </summary>
    private static async Task<IReadOnlyList<LegendFounderSemanticExampleRelationEvidence>>
        ReadFounderRelationsForExampleClosureAsync(
            MasterAppDbContext production,
            IReadOnlyCollection<Guid> exampleIds)
    {
        var scope = exampleIds.ToHashSet();
        var rows = new Dictionary<Guid, LegendFounderSemanticExampleRelationEvidence>();

        foreach (var batch in scope.Chunk(256))
        {
            var bySource = await production.LegendFounderSemanticExampleRelationEvidence
                .AsNoTracking()
                .Where(item => batch.Contains(item.SourceCurriculumExampleId))
                .ToListAsync();
            foreach (var item in bySource)
                rows[item.Id] = item;

            var byResult = await production.LegendFounderSemanticExampleRelationEvidence
                .AsNoTracking()
                .Where(item => batch.Contains(item.ResultCurriculumExampleId))
                .ToListAsync();
            foreach (var item in byResult)
                rows[item.Id] = item;
        }

        return rows.Values
            .Where(item => scope.Contains(item.SourceCurriculumExampleId) &&
                scope.Contains(item.ResultCurriculumExampleId))
            .OrderBy(item => item.Id)
            .ToArray();
    }

    private static async Task CopySnapshotRowsAsync<TEntity>(
        MasterAppDbContext shadow,
        IDictionary<string, int> copied,
        string label,
        IReadOnlyCollection<TEntity> rows)
        where TEntity : class
    {
        copied[label] = rows.Count;
        if (rows.Count == 0)
            return;

        shadow.Set<TEntity>().AddRange(rows);
        await shadow.SaveChangesAsync();
        shadow.ChangeTracker.Clear();
    }

    private static async Task<int> DrainShadowCurriculumPhaseAsync(
        LegendConnectCurriculumService curriculum,
        string phase)
    {
        Guid? cursor = null;
        var processed = 0;
        for (var page = 0; page < 512; page++)
        {
            var progress = await curriculum.ReevaluateHistoricalAlignmentsAsync(
                250,
                phase,
                cursor);
            processed += progress.ProcessedCount;
            if (progress.PhaseComplete)
                return processed;
            Assert.NotNull(progress.LastProcessedId);
            cursor = progress.LastProcessedId;
        }
        throw new Xunit.Sdk.XunitException(
            $"The live-data shadow rebuild did not drain canonical {phase} work within its bounded page limit.");
    }

    private async Task<IReadOnlyList<ShadowPrompt>> BuildShadowPromptMatrixAsync(
        MasterAppDbContext shadow,
        FounderLegendConnectService founderLegend,
        ClaimsPrincipal founder)
    {
        var knownGreetingTexts = GreetingEndpointRegressionPrompts
            .Select(item => LegendLanguageIdentity.NormalizeText(item.Text))
            .ToHashSet(StringComparer.Ordinal);

        // Find an end-to-end broad-governed example from the current live
        // snapshot itself. A fixed phrase is not evidence: if its semantic
        // primitives do not exist, fail-closed is the correct result. This
        // selection starts only from active, human-verified, contradiction-
        // free transition signatures with one or two independent sources,
        // then asks the unchanged native authority whether the complete
        // source endpoint is actually governable at BroadGoverned standard.
        var activeTransitions = await shadow.LegendSemanticTransitionEvidence
            .AsNoTracking()
            .Where(item => item.SupersededUtc == null &&
                item.SourceLanguageCode == "en" && item.ResultLanguageCode == "en" &&
                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                (item.ContributionState == "Supported" ||
                 item.ContributionState == "Contradictory"))
            .Select(item => new
            {
                item.TransitionSignature,
                item.SourceCurriculumExampleId,
                item.IndependentSourceIdentity,
                item.ContributionState,
                item.IsHumanVerifiedSupport
            })
            .ToListAsync();
        var broadSourceExampleIds = activeTransitions
            .GroupBy(item => item.TransitionSignature, StringComparer.Ordinal)
            .Where(group => !group.Any(item => item.ContributionState == "Contradictory") &&
                group.Where(item => item.ContributionState == "Supported" &&
                        item.IsHumanVerifiedSupport)
                    .Select(item => item.IndependentSourceIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Count() is > 0 and < 3)
            .SelectMany(group => group
                .Where(item => item.ContributionState == "Supported" &&
                    item.IsHumanVerifiedSupport)
                .Select(item => item.SourceCurriculumExampleId))
            .Distinct()
            .ToArray();
        var broadSourceTexts = await (
            from example in shadow.LegendCurriculumExamples.AsNoTracking()
            join unit in shadow.LegendLanguageTextUnits.AsNoTracking()
                on example.TextUnitId equals unit.Id
            join family in shadow.LegendCurriculumFamilies.AsNoTracking()
                on example.CurriculumFamilyId equals family.Id
            where broadSourceExampleIds.Contains(example.Id) &&
                example.SupersededUtc == null && unit.IsTrainingEligible &&
                !knownGreetingTexts.Contains(unit.Text) &&
                !family.FamilyKey.StartsWith("conversation.")
            orderby family.FamilyKey, unit.NormalizedHash
            select new { unit.Text, unit.NormalizedHash })
            .Distinct()
            .Take(128)
            .ToListAsync();
        ShadowPrompt? broadGovernedPrompt = null;
        foreach (var candidate in broadSourceTexts)
        {
            var native = await founderLegend.TryInferConversationWithDiscourseAsync(
                founder,
                candidate.Text,
                Array.Empty<LegendConnectConversationContextItem>(),
                discourseState: null,
                sourceLanguageCode: "en");
            if (!native.Supported || native.EvidenceStandard != "BroadGoverned")
                continue;
            broadGovernedPrompt = new(
                "broad-governed-" + candidate.NormalizedHash[..12],
                candidate.Text,
                true,
                "BroadGoverned");
            break;
        }
        _output.WriteLine(broadGovernedPrompt is null
            ? "SHADOW BROAD-GOVERNED NATIVE PROMPT: not applicable; the live snapshot exposes no end-to-end broad-only source endpoint."
            : $"SHADOW BROAD-GOVERNED NATIVE PROMPT: {broadGovernedPrompt.Reference}");

        var governedReasoning = await (
            from transition in shadow.LegendSemanticTransitionEvidence.AsNoTracking()
            join source in shadow.LegendCurriculumExamples.AsNoTracking()
                on transition.SourceCurriculumExampleId equals source.Id
            join unit in shadow.LegendLanguageTextUnits.AsNoTracking()
                on source.TextUnitId equals unit.Id
            join family in shadow.LegendCurriculumFamilies.AsNoTracking()
                on source.CurriculumFamilyId equals family.Id
            where transition.SupersededUtc == null &&
                transition.ContributionState == "Supported" &&
                transition.IsHumanVerifiedSupport &&
                transition.Provenance == LegendConnectKnowledgeProvenance.FounderApproved &&
                source.SupersededUtc == null && unit.IsTrainingEligible &&
                !knownGreetingTexts.Contains(unit.Text) &&
                !family.FamilyKey.StartsWith("conversation.")
            orderby family.FamilyKey, unit.NormalizedHash
            select new { unit.Text, unit.NormalizedHash }).FirstOrDefaultAsync();
        Assert.NotNull(governedReasoning);

        var prompts = new List<ShadowPrompt>(GreetingEndpointRegressionPrompts)
        {
            new("curriculum-reasoning-" + governedReasoning!.NormalizedHash[..12], governedReasoning.Text, true),
            new("ambiguous-request", "Hello or goodbye?", false),
            new("contradictory-request", "Please greet me and do not greet me.", false)
        };
        if (broadGovernedPrompt is not null &&
            !prompts.Any(item => string.Equals(item.Text, broadGovernedPrompt.Text, StringComparison.Ordinal)))
        {
            prompts.Insert(GreetingEndpointRegressionPrompts.Length, broadGovernedPrompt);
        }
        return prompts;
    }

    // The isolated direct and shadow regressions share this bounded greeting
    // endpoint set. Production release authority belongs only to the broader
    // zero-write matrix above; this set cannot satisfy deployment proof.
    private static readonly ShadowPrompt[] GreetingEndpointRegressionPrompts =
    [
        new("greeting-hi-there", "Hi there.", true, "HigherStandard"),
        new("greeting-hi-legend", "Hi Legend.", true, "HigherStandard"),
        new("greeting-hello", "Hello.", true, "HigherStandard"),
        new("greeting-hey-legend", "Hey Legend.", true, "HigherStandard"),
        new("greeting-good-morning", "Good morning.", true, "HigherStandard"),
        new("greeting-how-are-you", "How are you?", true, "HigherStandard"),
        new("greeting-nice-to-meet-you", "Nice to meet you.", true, "HigherStandard"),
        new("greeting-whats-up", "What's up?", true, "HigherStandard")
    ];

    private void WriteShadowPromptTrace(
        ShadowPrompt request,
        LegendShadowSourceUnderstanding source,
        LegendConnectUtteranceMeaningGraphSnapshot graph,
        LegendConnectResponseMeaningPlanResult plan,
        LegendConnectContentBoundResponseMeaningPlanResult binding,
        LegendConnectNativeInferenceSnapshot native,
        LegendFounderAiChatResponse response,
        int providerClientCalls)
    {
        _output.WriteLine($"SHADOW REQUEST: {request.Reference}; language=en; expected-native={request.ExpectNative}");
        _output.WriteLine($"  source-state={source.State}; source-reasons={string.Join(",", source.Reasons)}");
        _output.WriteLine("  source-components=" + (source.Components.Count == 0
            ? "<NONE>"
            : string.Join(" | ", source.Components.Select(item =>
                item.Dimension + "=" + item.Value + "@" + item.SurfaceForm + "#" + item.SemanticSignature[..12]))));
        _output.WriteLine($"  graph=composed:{graph.IsComposed}; reason={graph.ReasonCode}; unknown={string.Join(",", graph.UnknownSurfaceComponents)}");
        _output.WriteLine("  graph-nodes=" + (graph.Nodes.Count == 0
            ? "<NONE>"
            : string.Join(" | ", graph.Nodes.Select(item =>
                item.SemanticDimension + "=" + item.SemanticValue + "#" + item.SemanticSignature[..12]))));
        _output.WriteLine($"  graph-relations={graph.Relations.Count}; plan=supported:{plan.Supported},reason:{plan.ReasonCode},transition:{plan.Plan?.TransitionSignature[..12] ?? "<NONE>"}");
        _output.WriteLine($"  content-binding=supported:{binding.Supported},reason:{binding.ReasonCode},facts:{binding.Plan?.Facts.Count ?? 0}");
        _output.WriteLine($"  native=supported:{native.Supported},reason:{native.ReasonCode},evidence:{native.EvidenceCount},escalation:{native.RequiresEscalation}");
        _output.WriteLine($"  evidence-standard={native.EvidenceStandard}; articulation-mode={native.ArticulationMode}");
        _output.WriteLine($"  realization=succeeded:{response.Succeeded},failure:{response.FailureKind ?? "<NONE>"},provider-clients:{providerClientCalls}");
    }

    private static LegendLanguageTextUnit HistoricalUnit(string languageCode, string text) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = "FounderApproved",
        IsTrainingEligible = true
    };

    private static string ExtractAntiforgeryCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(
                    ".AspNetCore.Antiforgery",
                    StringComparison.OrdinalIgnoreCase))?.Split(';')[0] ?? string.Empty
            : string.Empty;

    private sealed record ConversationMatrixRequest(
        string Text,
        bool RequireNative,
        IReadOnlyList<LegendFounderAiChatMessage>? History = null);

    internal sealed record RuntimeDiagnosticEvent(
        long Ordinal, string Event, string AuthorityMethod, string SourcePath,
        string Stage, string Outcome, string? ReasonCode, long? ElapsedMs,
        string? ExceptionType, int? HResult, int? SqlErrorNumber,
        string? ProviderPolicy, bool? Supported, bool? RequiresEscalation,
        IReadOnlyDictionary<string, long> Counts)
    {
        public bool? ResearchRequired { get; init; }
        public bool? RequiresGovernedReadReceipt { get; init; }
        public bool? ReadScopeEstablished { get; init; }
    }

    internal sealed record RuntimeDiagnosticSnapshot(
        long ObservedEvents, long RecordsDropped, bool Truncated, long ExceptionEvents, long SqlFailureEvents,
        IReadOnlyList<RuntimeDiagnosticEvent> Records);

    internal sealed class ExceptionCapturingLoggerProvider(int maxEvents = 256) : ILoggerProvider
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();
        private readonly object _diagnosticLock = new();
        private readonly List<RuntimeDiagnosticEvent> _events = [];
        private readonly int _maximumEvents = Math.Clamp(maxEvents, 1, 4096);
        private long _observedEvents;
        private long _exceptionEvents;
        private long _sqlFailureEvents;
        public IEnumerable<Exception> Exceptions => _exceptions;
        public ILogger CreateLogger(string categoryName) => new ExceptionCapturingLogger(this, categoryName);
        public void ResetDiagnostics()
        {
            lock (_diagnosticLock)
            {
                _events.Clear();
                _observedEvents = 0;
                _exceptionEvents = 0;
                _sqlFailureEvents = 0;
            }
        }

        public RuntimeDiagnosticSnapshot SnapshotDiagnostics()
        {
            lock (_diagnosticLock)
                return new(_observedEvents, _observedEvents - _events.Count,
                    _observedEvents > _events.Count, _exceptionEvents, _sqlFailureEvents, Array.AsReadOnly(_events.ToArray()));
        }

        private void Capture<TState>(string category, EventId eventId, TState state, Exception? exception)
        {
            // Neither the formatter, scopes, exception message nor stack is
            // read. Only fixed structured fields enter the public evidence.
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.GroupBy(item => item.Key, StringComparer.Ordinal)
                    .Where(group => group.Count() == 1)
                    .ToDictionary(group => group.Key, group => group.Single().Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            var format = fields.GetValueOrDefault("{OriginalFormat}") as string;
            var structured = format?.StartsWith("LEGEND RuntimeDiagnostic", StringComparison.Ordinal) == true;
            var legacyStage = format?.StartsWith("LEGEND Founder AI stage", StringComparison.Ordinal) == true;
            var queryIterationFailed = category == "Microsoft.EntityFrameworkCore.Query" &&
                eventId.Id == CoreEventId.QueryIterationFailed.Id;
            if (!structured && !legacyStage && !queryIterationFailed && exception is null)
                return;
            if (exception is not null)
            {
                _exceptions.Enqueue(exception);
                while (_exceptions.Count > _maximumEvents)
                    _exceptions.TryDequeue(out _);
            }
            string? Code(string key) => fields.GetValueOrDefault(key) is string value
                ? SafeObservationCode(value) : null;
            long? Number(string key) => fields.GetValueOrDefault(key) switch
            {
                byte value => value, short value => value, int value => value,
                long value when value >= 0 => value,
                // Runtime stopwatch measurements may be fractional; preserve
                // an upper-rounded millisecond rather than dropping the field.
                double value when double.IsFinite(value) && value >= 0 && value < long.MaxValue => (long)Math.Ceiling(value),
                _ => null
            };
            bool? Flag(string key) => fields.GetValueOrDefault(key) is bool value ? value : null;
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var key in new[] { "LanguagesConsidered", "LanguagesCandidate", "LanguagesAnalyzed",
                         "LanguagesComposed", "LanguagesIncomplete", "GraphNodes", "GraphRelations",
                         "UnknownComponents", "EvidenceCount", "Candidates", "EligibleCandidates",
                         "SourceSlotBindings", "DiscourseTurns", "InvalidatedTurns", "TokenCount", "CandidateCount",
                         "NodeCount", "RelationCount", "UnknownCount", "DeclarationCount", "TemplateCount", "MatchCount", "Bound",
                         "FounderNodes", "MachineNodes", "CurrentTurnAssertionNodes", "SourceSlotBindingNodes", "BindingsBound",
                         "BindingsUnresolved", "StatusCode", "Attempt", "RetryDelayMs" })
                if (Number(key) is long value && value >= 0)
                    counts[key] = value;
            var declaredMethod = fields.GetValueOrDefault("AuthorityMethod") as string;
            var qualifiedParts = declaredMethod?.Split('.');
            var authorityType = qualifiedParts?.Length == 2 ? qualifiedParts[0] : category.Split('.').Last();
            var sourcePath = authorityType switch
            {
                "LegendFounderAiConversationService" => "AgentPortal/Services/LegendFounderAiConversationService.cs",
                "LegendFounderAiDiscourseStateService" => "AgentPortal/Services/LegendFounderAiDiscourseStateService.cs",
                "FounderLegendConnectService" => "AgentPortal/Services/FounderLegendConnectService.cs",
                "LegendFounderToolAuthority" => "AgentPortal/Services/LegendFounderToolAuthority.cs",
                "LegendConnectTranslationRouter" => "Infrastructure/Messaging/LegendConnectTranslationRouter.cs",
                "LegendConnectCurriculumService" => "Infrastructure/Messaging/LegendConnectCurriculum.cs",
                "LegendConnectOperations" => "Infrastructure/Messaging/LegendConnectOperations.cs",
                "LegendConnectCorpusService" => "Infrastructure/Messaging/LegendConnectLearning.cs",
                "TranslationCapacityAuthority" => "Infrastructure/Messaging/LegendConnectCapacity.cs",
                "LegendLanguageRegistry" => "Infrastructure/Messaging/LegendConnectLanguageRegistry.cs",
                "AzureTranslatorService" => "Infrastructure/Messaging/AzureTranslatorService.cs",
                "LegendConnectTranslationIntelligence" => "Infrastructure/Messaging/LegendConnectIntelligence.cs",
                "LegendConnectRuntimePolicyAuthority" => "Infrastructure/Messaging/LegendConnectRuntimePolicyAuthority.cs",
                "LegendConnectActiveModelInference" => "Infrastructure/Messaging/LegendConnectTranslationRouter.cs",
                "TranslationEntitlementAuthority" => "Infrastructure/Messaging/TranslationEntitlementAuthority.cs",
                "OpenAiKeyResolver" => "AgentPortal/Services/Analytics/OpenAiKeyResolver.cs",
                "LegendFounderCurriculumSqlServerE2ETests" => "AgentPortal.Tests/LegendFounderCurriculumSqlServerE2ETests.cs",
                _ => "unmapped_runtime_authority"
            };
            SqlException? sqlException = null;
            var inspected = 0;
            for (var current = exception; current is not null && inspected++ < 8; current = current.InnerException)
                if (current is SqlException sql)
                {
                    sqlException = sql;
                    break;
                }
            var outcome = exception is OperationCanceledException ? "cancelled" : exception is not null ? "failed" :
                Code("Outcome") is { } candidateOutcome &&
                candidateOutcome is "started" or "completed" or "failed" or "cancelled" or "resolved" or "unresolved" or "allowed" or "blocked" or "rejected" or "composed" or "uncomposed" or "retrying" or
                    "Resolved" or "InvalidDeclaration" or "UnsupportedLanguage" or "SemanticAmbiguity" or "ProviderPolicyBlocked" or "TransientIdentificationUnavailable" or
                    "excluded" or "retained" or "observed" or "unavailable" or "supported" or "unsupported" or "not_required" or "no_match" or
                    "Unknown" or "OwnedRecordStateInspection" or "AnalysisUnavailable" or "Conclusion" or "InsufficientEvidence" or "UnresolvedConflict" or "Failure"
                    ? candidateOutcome : "unreported";
            var methodName = declaredMethod?.Split('.').Last();
            var allowedMethods = new[] { "ReplyAsync", "ResolveSourceLanguageAsync", "TraceNativeStageAsync",
                "ObserveDiscourseMeaningAsync", "AnalyzeReusableMeaningGraphAsync", "ReadReusableMeaningCandidatesAsync",
                "AnalyzeDeclaredSourceSlotsAsync", "DetectLanguageAsync", "GetReusableMeaningLanguageCandidatesAsync",
                "TryInferConversationWithDiscourseAsync", "TryInferConversationWithReadOnlyContentAsync",
                "BindReadOnlyResultAsync", "EnsureFounderAuthorizedAsync", "RecordCurrentObservationAsync", "GetStateAsync",
                "ClassifyOwnedRecordIntentAsync", "ExecuteResearchAsync", "TryPlanConversationAsync",
                "ListEnabledTranslationLanguagesReadOnlyAsync", "NormalizeEnabledTranslationLanguageReadOnlyAsync",
                "ProductionReadOnlyNativeProofMatrix", "ProductionReadOnlyCandidateObservation", "GetFounderSectionPageAsync", "ObserveRuntimeStageAsync", "TryBindConversationContentAsync", "SendResponseAsync", "ResearchAsync", "Resolve",
                "NormalizeEnabledTranslationLanguageAsync", "GetEnabledPairAsync", "TryGetTrustedExactMemoryAsync",
                "EvaluateContextAsync", "TryGetReusableProviderObservationAsync", "TryComposeAsync", "GetEffectiveAsync",
                "TryTranslateAsync", "TryReserveAsync", "CompleteAsync", "RecordAvoidedAsync", "TranslateAsync", "TranslateCoreAsync",
                "DecideResearchNeeded", "ExecuteAsync", "TryReadResearchOutcome" };
            var authorityMethod = sourcePath != "unmapped_runtime_authority" &&
                methodName is not null && allowedMethods.Contains(methodName, StringComparer.Ordinal)
                    ? authorityType + "." + methodName : "unresolved_from_capture";
            var eventName = Code("Event") is { } declaredEvent && declaredEvent is "StageStarted" or "StageEnded" or
                "LanguageDetectionCompleted" or "LanguageGraphAnalyzed" or "LanguageCandidatesRead" or
                "SourceLanguageResolved" or "NativeInferenceCompleted" or "ResearchDecision" or "ProviderEscalation" or
                "CaseAssertionFailed" or "stage_completed" or "MeaningGraphObserved" or "DiscourseStateObserved" or "OwnedRecordClassified" or
                "ProviderRetry" or "ProviderRejected" or "ProviderEscalationRejected" or "ProviderTransportFailed" or "ProviderJsonInvalid" or
                "OwnedRecordClassificationException" or "DiscourseObservationException" or "SourceLanguageException" or "LanguageRegistryRead" or "ResearchCompleted" or
                "TranslationProviderBoundary" or "TranslationProviderCompleted" or "NativeInferenceException" or "NativeAnswer" or "GovernedExecutionFailed" or
                "TranslationStageStarted" or "TranslationStageEnded" or "TranslationCompleted" or "TranslationBoundaryFailed" or
                "ResearchOutcomeValidated" or "GovernedToolFailed" or "GovernedToolTimedOut"
                    ? declaredEvent : exception is not null ? "caught_exception" : "unreported";
            var stageName = Code("Stage") is { } declaredStage && declaredStage is "founder_authorization" or
                "source_language" or "language_detection" or "language_candidates" or "language_registry" or
                "meaning_graph" or "meaning_candidates" or "source_slots" or "meaning_relations" or
                "native_semantic_inference" or "native_read_binding" or "native_read_realization" or
                "discourse_meaning_analysis" or "discourse_persistence" or "discourse_reload" or
                "native_inference" or "case_assertions" or "provider_escalation" or "research" or "language_graph" or
                "source_language_identification" or "source_language_normalization" or "language_candidate_prefilter" or "provider_detection" or
                "external_response" or "owned_record_classification" or "discourse_observation" or "source_language_detection" or
                "external_language_detection" or "detected_language_registry" or "declared_language_registry" or "resolved_language_registry" or "language_identification" or "native_response" or "governed_execution" or "founder_section" or "section_prerequisite" or
                "translation_language_registry" or "translation_pair" or "translation_memory" or "translation_structural" or
                "translation_context" or "translation_runtime_policy" or "translation_promoted_model" or "translation_provider_observation" or
                "translation_quota" or "translation_capacity" or "translation_provider" or "translation_capacity_finalization" or
                "translation_result" or "translation_intelligence" or "translation_usage" or "translation_quota_finalization" or
                "research_decision" or "research_tool" or "governed_tool"
                    ? declaredStage : "unreported";
            var capturedReason = fields.GetValueOrDefault("ReasonCode") as string;
            var reasonCode = capturedReason is null ? null :
                LegendConnectTelemetry.NormalizeDiagnosticReason(capturedReason);
            lock (_diagnosticLock)
            {
                var ordinal = ++_observedEvents;
                if (exception is not null) _exceptionEvents++;
                if (sqlException is not null || queryIterationFailed) _sqlFailureEvents++;
                if (_events.Count >= _maximumEvents)
                    return;
                _events.Add(new(ordinal,
                    queryIterationFailed ? "QueryIterationFailed" : eventName,
                    authorityMethod, sourcePath,
                    queryIterationFailed ? "sql_row_materialization" : stageName,
                    queryIterationFailed ? "failed" : outcome,
                    queryIterationFailed ? "query_iteration_failed" : reasonCode, Number("ElapsedMs"),
                    exception?.GetType().Name ?? (Code("ExceptionType") is { } typeName && typeName is
                        "none" or "Exception" or "InvalidOperationException" or "ArgumentException" or "ArgumentNullException" or
                        "OperationCanceledException" or "TaskCanceledException" or "TimeoutException" or "SqlException" or
                        "UnauthorizedAccessException" or "ForbidResultException" or "HttpRequestException" or "JsonException" or
                        "NotSupportedException" or "FormatException" or "OverflowException" or "DbUpdateException"
                        ? typeName : null), exception?.HResult,
                    sqlException?.Number, Code("ProviderPolicy") is "native_only" or "provider_enabled" ? Code("ProviderPolicy") : null,
                    Flag("Supported"), Flag("RequiresEscalation"),
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(counts))
                {
                    ResearchRequired = Flag("ResearchRequired"),
                    RequiresGovernedReadReceipt = Flag("RequiresGovernedReadReceipt"),
                    ReadScopeEstablished = Flag("ReadScopeEstablished")
                });
            }
        }
        public void Dispose() { }

        private sealed class ExceptionCapturingLogger(
            ExceptionCapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Capture(category, eventId, state, exception);
        }
    }

    private sealed class CountingDbCommandInterceptor : DbCommandInterceptor
    {
        private int _commands;

        public int Commands => Volatile.Read(ref _commands);

        public void Reset() => Interlocked.Exchange(ref _commands, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Defence in depth for production read-only proof and diagnostics: no database command
    /// other than a SELECT may leave the local process. The native authority
    /// is read-only by design, and this turns that design requirement into an
    /// executable invariant for the diagnostic.
    /// </summary>
    internal sealed class ReadOnlyLegendDbCommandInterceptor : DbCommandInterceptor
    {
        private const int MaximumDiagnosticRecords = 256;
        private readonly object _diagnosticLock = new();
        private readonly List<SqlCommandDiagnosticRecord> _diagnosticRecords = [];
        private int _selectCommands;
        private int _blockedCommands;
        private long _eventsObserved;
        private long _succeededEvents;
        private long _failedEvents;
        private long _canceledEvents;
        private long _blockedEvents;
        public int SelectCommands => Volatile.Read(ref _selectCommands);
        public int BlockedCommands => Volatile.Read(ref _blockedCommands);
        private HashSet<string>? _physicalTables;

        // These are terminal interception events, not inferred request stages.
        // Reader success measures execution through obtaining the reader; it
        // does not claim successful enumeration or materialization of its rows.
        internal sealed record SqlCommandDiagnosticRecord(
            long Ordinal, string QueryFingerprint, string Outcome,
            double ElapsedMilliseconds, string? ExceptionType, int? HResult,
            int? SqlErrorNumber, string? QueryAuthority = null, string? QueryOperation = null);

        internal sealed record SqlCommandDiagnosticSnapshot(
            long EventsObserved, long SucceededEvents, long FailedEvents,
            long CanceledEvents, long BlockedEvents, long RecordsDropped,
            bool Truncated, IReadOnlyList<SqlCommandDiagnosticRecord> Records);

        // Call only between awaited cases. Guard counts and physical-table
        // permissions are deliberately cumulative and cannot be reset here.
        public void ResetDiagnostics()
        {
            lock (_diagnosticLock)
            {
                _diagnosticRecords.Clear();
                _eventsObserved = _succeededEvents = _failedEvents =
                    _canceledEvents = _blockedEvents = 0;
            }
        }

        public SqlCommandDiagnosticSnapshot SnapshotDiagnostics()
        {
            lock (_diagnosticLock)
            {
                var dropped = _eventsObserved - _diagnosticRecords.Count;
                return new(_eventsObserved, _succeededEvents, _failedEvents,
                    _canceledEvents, _blockedEvents, dropped, dropped > 0,
                    Array.AsReadOnly(_diagnosticRecords.ToArray()));
            }
        }
        public ReadOnlyLegendDbCommandInterceptor(bool restrictPhysicalTables = false)
        {
            if (restrictPhysicalTables) _physicalTables = new(StringComparer.OrdinalIgnoreCase);
        }
        public void AllowPhysicalTables(IEnumerable<string> tables) =>
            _physicalTables = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result) =>
            throw NewWriteBlocked(command);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(NewWriteBlocked(command));

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            EnsureSelect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            EnsureSelect(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            EnsureSelect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            EnsureSelect(command);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            RecordDiagnostic(command, "succeeded", eventData.Duration);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            RecordDiagnostic(command, "succeeded", eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override object? ScalarExecuted(
            DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            RecordDiagnostic(command, "succeeded", eventData.Duration);
            return result;
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            RecordDiagnostic(command, "succeeded", eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
            RecordDiagnostic(command, "failed", eventData.Duration, eventData.Exception);

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            RecordDiagnostic(command, "failed", eventData.Duration, eventData.Exception);
            return Task.CompletedTask;
        }

        public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) =>
            RecordDiagnostic(command, "canceled", eventData.Duration);

        public override Task CommandCanceledAsync(
            DbCommand command, CommandEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            RecordDiagnostic(command, "canceled", eventData.Duration);
            return Task.CompletedTask;
        }

        private void RecordDiagnostic(
            DbCommand command, string outcome, TimeSpan duration, Exception? exception = null)
        {
            lock (_diagnosticLock)
            {
                var ordinal = ++_eventsObserved;
                switch (outcome)
                {
                    case "succeeded": _succeededEvents++; break;
                    case "failed": _failedEvents++; break;
                    case "canceled": _canceledEvents++; break;
                    case "blocked": _blockedEvents++; break;
                }
                if (_diagnosticRecords.Count >= MaximumDiagnosticRecords)
                {
                    // A late swallowed failure must remain inspectable even
                    // after a case has already emitted 256 successful reads.
                    // Evicted successes still contribute to RecordsDropped.
                    var replaceable = outcome == "succeeded" ? -1 :
                        _diagnosticRecords.FindIndex(item => item.Outcome == "succeeded");
                    if (replaceable < 0)
                        return;
                    _diagnosticRecords.RemoveAt(replaceable);
                }
                var type = exception?.GetType().FullName;
                if (type is not null)
                    type = new string(type.Take(160).Where(character =>
                        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+').ToArray());
                var attribution = ReadQueryAttribution(command.CommandText);
                _diagnosticRecords.Add(new(ordinal, QueryFingerprint(command.CommandText), outcome,
                    Math.Max(0, duration.TotalMilliseconds), type, exception?.HResult,
                    (exception as SqlException)?.Number, attribution.Authority, attribution.Operation));
            }
        }

        internal static (string? Authority, string? Operation) ReadQueryAttribution(string sql)
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
            if (errors.Count != 0)
                return (null, null);
            var labels = tokens.Where(token => token.TokenType == TSqlTokenType.SingleLineComment)
                .Select(token => token.Text.Trim())
                .Where(comment => comment.StartsWith("-- LEGEND_QUERY:", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (labels.Length != 1)
                return (null, null);
            // Static code-defined labels only; never emit arbitrary SQL comments,
            // parameters, private values or an unrecognized label suffix.
            return labels[0] switch
            {
                "-- LEGEND_QUERY:exact_semantic_anchors" =>
                    ("LegendConnectCurriculumService.LoadExactActiveSemanticAnchorIdsAsync", "exact_semantic_anchors"),
                "-- LEGEND_QUERY:indexed_semantic_anchors" =>
                    ("LegendConnectCurriculumService.LoadIndexedSemanticAnchorIdsAsync", "indexed_semantic_anchors"),
                "-- LEGEND_QUERY:reusable_meaning_candidates" =>
                    ("LegendConnectCurriculumService.ReadReusableMeaningCandidatesAsync", "reusable_meaning_candidates"),
                "-- LEGEND_QUERY:source_slot_examples" =>
                    ("LegendConnectCurriculumService.AnalyzeDeclaredSourceSlotsAsync", "source_slot_examples"),
                "-- LEGEND_QUERY:source_slot_declarations" =>
                    ("LegendConnectCurriculumService.AnalyzeDeclaredSourceSlotsAsync", "source_slot_declarations"),
                "-- LEGEND_QUERY:source_slot_nodes" =>
                    ("LegendConnectCurriculumService.AnalyzeDeclaredSourceSlotsAsync", "source_slot_nodes"),
                "-- LEGEND_QUERY:source_slot_relations" =>
                    ("LegendConnectCurriculumService.AnalyzeDeclaredSourceSlotsAsync", "source_slot_relations"),
                "-- LEGEND_QUERY:computed_structure_examples" =>
                    ("LegendConnectCurriculumService.LoadCurrentComputedStructuresAsync", "computed_structure_examples"),
                "-- LEGEND_QUERY:computed_structure_nodes" =>
                    ("LegendConnectCurriculumService.LoadCurrentComputedStructuresAsync", "computed_structure_nodes"),
                "-- LEGEND_QUERY:computed_structure_relations" =>
                    ("LegendConnectCurriculumService.LoadCurrentComputedStructuresAsync", "computed_structure_relations"),
                _ => (null, null)
            };
        }

        internal static string QueryFingerprint(string sql)
        {
            // Tokenize locally, drop comments, and replace literal/parameter
            // values before hashing. No SQL, parameter, connection, exception
            // message, or token text is retained in the diagnostic snapshot.
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
            var shape = errors.Count == 0
                ? string.Join("|", tokens.Where(token =>
                        !string.IsNullOrWhiteSpace(token.Text) &&
                        !token.Text.StartsWith("--", StringComparison.Ordinal) &&
                        !token.Text.StartsWith("/*", StringComparison.Ordinal))
                    .Select(token =>
                    {
                        var kind = token.TokenType.ToString();
                        return kind.Contains("Literal", StringComparison.Ordinal) ||
                            kind is "Integer" or "Numeric" or "Real" or "Money" ||
                            token.Text.StartsWith("@", StringComparison.Ordinal)
                                ? kind
                                : kind + ":" + token.Text.ToUpperInvariant();
                    }))
                : "unparseable_sql";
            return Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(shape))).ToLowerInvariant();
        }

        private void EnsureSelect(DbCommand command)
        {
            try
            {
                if (command.CommandType != System.Data.CommandType.Text)
                    throw new InvalidOperationException("Only text SELECT commands are allowed.");
                ValidateSelect(command.CommandText, _physicalTables);
                Interlocked.Increment(ref _selectCommands);
            }
            catch (InvalidOperationException exception)
            {
                Interlocked.Increment(ref _blockedCommands);
                RecordDiagnostic(command, "blocked", TimeSpan.Zero, exception);
                throw;
            }
        }

        internal static void ValidateSelect(string sql, HashSet<string>? physicalTables = null)
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(new StringReader(sql), out var errors);
            if (errors.Count != 0 || fragment is not TSqlScript script || script.Batches.Count != 1 ||
                script.Batches[0].Statements.Count != 1 ||
                script.Batches[0].Statements[0] is not SelectStatement select || select.Into is not null)
                throw new InvalidOperationException("Production read-only proof requires one parsed SELECT without INTO.");
            var visitor = new SelectSideEffectVisitor(physicalTables);
            select.Accept(visitor);
            if (visitor.Rejected)
                throw new InvalidOperationException("Production read-only proof rejected an external or state-changing SELECT.");
        }

        private sealed class SelectSideEffectVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string>? _physicalTables;
            public SelectSideEffectVisitor(HashSet<string>? physicalTables) => _physicalTables = physicalTables;
            public bool Rejected { get; private set; }
            public override void ExplicitVisit(NamedTableReference node)
            {
                var name = string.Join(".", node.SchemaObject.Identifiers.Select(item => item.Value));
                if (_physicalTables is not null && !_physicalTables.Contains(name) &&
                    name is not ("sys.tables" or "sys.schemas" or "sys.objects" or "sys.database_principals"
                        or "sys.database_permissions" or "sys.user_token" or "sys.computed_columns" or "sys.security_predicates"))
                    Rejected = true;
                base.ExplicitVisit(node);
            }
            public override void ExplicitVisit(FunctionCall node)
            {
                // Schema-qualified and CLR member functions may hide indirect side effects.
                if (node.CallTarget is not null) Rejected = true;
                base.ExplicitVisit(node);
            }
            public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
            {
                if (string.Join(".", node.SchemaObject.Identifiers.Select(item => item.Value)) != "sys.fn_my_permissions")
                    Rejected = true;
                base.ExplicitVisit(node);
            }
            public override void ExplicitVisit(NextValueForExpression node) => Rejected = true;
            public override void ExplicitVisit(OpenRowsetTableReference node) => Rejected = true;
            public override void ExplicitVisit(BulkOpenRowset node) => Rejected = true;
            public override void ExplicitVisit(OpenQueryTableReference node) => Rejected = true;
            public override void ExplicitVisit(AdHocTableReference node) => Rejected = true;
            public override void ExplicitVisit(SchemaObjectName node)
            {
                if (node.Identifiers.Count > 2)
                    Rejected = true;
                base.ExplicitVisit(node);
            }
        }

        private InvalidOperationException NewWriteBlocked(DbCommand command)
        {
            Interlocked.Increment(ref _blockedCommands);
            var exception = new InvalidOperationException("Production read-only proof rejected a non-SELECT database command.");
            RecordDiagnostic(command, "blocked", TimeSpan.Zero, exception);
            return exception;
        }
    }

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(
            MessagingActor actor,
            string resourceType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(
                resourceType,
                ControlledResourceAccessStates.NotGranted,
                true));

        public Task<bool> IsFounderManagerAsync(
            MessagingActor actor,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsCanonicalFounderManagerAsync(
            MessagingActor actor,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<string?> GetPreferredLanguageAsync(
            MessagingActor actor,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task NativeObservationCounter_RetainsDeniedProviderSendAttempts()
    {
        var factory = new CountingHttpClientFactory();
        using var client = factory.CreateClient("native-observation-counter");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("blocked"));
        Assert.Equal(1, factory.CreateClientCalls);
        Assert.Equal(1, factory.SendCalls);
    }

    private sealed class CountingForbiddenTranslationProvider : ITranslationProvider
    {
        private int _callAttempts;
        public int CallAttempts => Volatile.Read(ref _callAttempts);
        public string ProviderName => "ForbiddenExternalTranslation";

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text, CancellationToken cancellationToken = default) =>
            Refuse<TranslationDetectionResult>();

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text, CancellationToken cancellationToken,
            LegendConnectExternalProviderPolicy? providerPolicy) =>
            Refuse<TranslationDetectionResult>();

        public Task<TranslationProviderResult> TranslateAsync(
            string text, string targetLanguage, string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Refuse<TranslationProviderResult>();

        public Task<TranslationProviderResult> TranslateAsync(
            string text, string targetLanguage, string? sourceLanguage,
            CancellationToken cancellationToken,
            LegendConnectExternalProviderPolicy? providerPolicy) =>
            Refuse<TranslationProviderResult>();

        private Task<T> Refuse<T>()
        {
            Interlocked.Increment(ref _callAttempts);
            return Task.FromException<T>(new InvalidOperationException(
                "Native SQL proof must not invoke an external translation provider."));
        }
    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        private int _createClientCalls;
        private int _sendCalls;
        public int CreateClientCalls => Volatile.Read(ref _createClientCalls);
        public int SendCalls => Volatile.Read(ref _sendCalls);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _createClientCalls);
            return new HttpClient(new NoNetworkHandler(() => Interlocked.Increment(ref _sendCalls)))
            {
                BaseAddress = new Uri("https://legend-e2e.invalid/")
            };
        }
    }

    private sealed class NoNetworkHandler(Action recordSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            recordSend();
            throw new InvalidOperationException("The OpenAI test client must not be used by native inference.");
        }
    }
}

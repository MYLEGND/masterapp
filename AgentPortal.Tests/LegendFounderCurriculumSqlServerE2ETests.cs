using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
                elapsedMilliseconds: 2)
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
        Assert.Equal(2, root.GetProperty("FailedCases").GetInt32());
        var serializedResults = root.GetProperty("CaseResults");
        Assert.Equal(3, serializedResults.GetArrayLength());
        Assert.Equal("fixture", serializedResults[0].GetProperty("Phase").GetString());
        Assert.Equal("execution", serializedResults[1].GetProperty("Phase").GetString());
        Assert.Equal("execution", serializedResults[2].GetProperty("Phase").GetString());
    }

    [Fact]
    public async Task ProductionReadOnlyNativeProofMatrix()
    {
        const string matrixVersion = "lai-027-029-v1";
        var proofRequired = string.Equals(
            Environment.GetEnvironmentVariable("LEGEND_PRODUCTION_PROOF_REQUIRED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
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
            var readOnlyGuard = new ReadOnlyLegendDbCommandInterceptor();
            await using var db = new MasterAppDbContext(
                new DbContextOptionsBuilder<MasterAppDbContext>()
                    .UseSqlServer(connection.ConnectionString)
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                    .AddInterceptors(readOnlyGuard)
                    .Options);

            var founderId = Environment.GetEnvironmentVariable(
                "LEGEND_PRODUCTION_READONLY_FOUNDER_OID");
            Assert.False(
                string.IsNullOrWhiteSpace(founderId),
                "Production Founder OID was not supplied to the read-only serving proof.");
            Assert.True(await db.AgentProfiles
                    .AsNoTracking()
                    .AnyAsync(item => item.IsActive &&
                        item.AgentUserId != null &&
                        item.AgentUserId.ToLower() == founderId!.ToLower()),
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
                NullLogger<LegendConnectCorpusService>.Instance);
            var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
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
            var chat = new LegendFounderAiConversationService(
                factory,
                configuration,
                founderLegend,
                NullLogger<LegendFounderAiConversationService>.Instance,
                new LegendFounderAiDiscourseStateService(db, profiles, operations),
                registry,
                ControllerTestHelpers.BuildTranslationService());

            async Task<string?> FindReasoningSourceAsync(string operatorPrefix)
            {
                var text = await (
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
                    select unit.Text).FirstOrDefaultAsync();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            var fixtureFailures = new List<ProductionNativeProofResult>();

            async Task<string?> FindReasoningFixtureAsync(
                string reference,
                string category,
                string operatorPrefix)
            {
                var source = await FindReasoningSourceAsync(operatorPrefix);
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

            var audienceConstraintSource = await (
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
                select sourceUnit.Text).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(audienceConstraintSource))
            {
                fixtureFailures.Add(ProductionNativeProofResult.FailedFixture(
                    "fixture-governed-audience-constraints",
                    "audience_constraints",
                    "The production matrix has no active Founder-governed audience-constrained response source."));
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
                    "held-out-competing-hypotheses",
                    "held_out_paraphrase",
                    "Keep both hypotheses; plan an experiment.",
                    mustBeHeldOut: true),
                ProductionNativeProofCase.Positive(
                    "held-out-discriminating-check",
                    "held_out_paraphrase",
                    "Retain the competing explanations; devise a discriminating check.",
                    mustBeHeldOut: true),
                new(
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
                    true),
                ProductionNativeProofCase.Negative(
                    "cross-family-handoff-inventory",
                    "cross_family_negative",
                    "handoff failure"),
                ProductionNativeProofCase.Negative(
                    "cross-family-capacity-scheduling",
                    "cross_family_negative",
                    "capacity shortage"),
                ProductionNativeProofCase.Positive(
                    "declared-language-normalization",
                    "language_routing",
                    "Hello.",
                    declaredSourceLanguageCode: " en_US ",
                    nativeSourceLanguageCode: "en",
                    expectedEvidenceStandard: "HigherStandard"),
                ProductionNativeProofCase.Negative(
                    "native-only-provider-isolation",
                    "native_only_isolation",
                    "Uncatalogued zephyr request.")
            };
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
            var representedCategories = matrix.Select(item => item.Category)
                .Concat(fixtureFailures.Select(item => item.Category))
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
            if (matrix.Count + fixtureFailures.Count < requiredCategories.Length ||
                matrix.Count + fixtureFailures.Count > 16)
            {
                results.Add(ProductionNativeProofResult.FailedFixture(
                    "matrix-size-contract",
                    "matrix_definition",
                    $"The production matrix contains {matrix.Count + fixtureFailures.Count} executable or fixture-failure cases; expected {requiredCategories.Length} through 16."));
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
                    $"failure={fixtureFailure.Failure}; provider_clients=0; elapsed_ms=0");
            }

            var executed = 0;
            var nativePasses = 0;
            var negativePasses = 0;
            foreach (var proofCase in matrix)
            {
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
                                item.Provenance == LegendConnectKnowledgeProvenance.FounderApproved),
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
                            select transition.Id).AnyAsync(),
                        $"Matrix case '{proofCase.Reference}' is not an active exact transition endpoint.");
                }

                var context = proofCase.Messages
                    .Take(proofCase.Messages.Count - 1)
                    .Select(message => new LegendConnectConversationContextItem(
                        message.Role ?? string.Empty,
                        message.Content ?? string.Empty))
                    .ToArray();
                if (proofCase.Category == "cross_family_negative")
                {
                    var graph = await founderLegend.AnalyzeReusableMeaningGraphAsync(
                        founder,
                        currentPrompt,
                        proofCase.NativeSourceLanguageCode);
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
                        proofCase.NativeSourceLanguageCode);
                    Assert.False(
                        withoutContext.Supported,
                        "The discourse case must require its bounded prior-turn context.");
                }
                if (proofCase.Category is "deduction" or "uncertainty" or "diagnosis" or "planning")
                {
                    var planned = await operations.TryPlanConversationAsync(
                        currentPrompt,
                        discourseState: null,
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
                    discourseState: null,
                    proofCase.NativeSourceLanguageCode);
                var reply = await chat.ReplyAsync(
                    founder,
                    new LegendFounderAiChatRequest
                    {
                        Mode = "legend",
                        NativeOnly = true,
                        SourceLanguageCode = proofCase.DeclaredSourceLanguageCode,
                        Messages = proofCase.Messages
                    });

                Assert.Equal(providerCallsBefore, factory.CreateClientCalls);
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
                        $"failure={exception.Message}; provider_clients={factory.CreateClientCalls}; " +
                        $"elapsed_ms={elapsedMilliseconds:0}");
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
            _output.WriteLine($"PRODUCTION PROOF MATRIX CASES EXECUTED: {executed}");
            _output.WriteLine($"PRODUCTION PROOF MATRIX NATIVE PASSES: {nativePasses}");
            _output.WriteLine($"PRODUCTION PROOF MATRIX NEGATIVE PASSES: {negativePasses}");
            _output.WriteLine("OPENAI HTTP CALLS: 0");
            _output.WriteLine("PRODUCTION WRITE COMMANDS: 0");

            var resultPath = Environment.GetEnvironmentVariable(
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
                            Status = results.All(item => item.Status == "passed")
                                ? "passed"
                                : "failed",
                            ExecutedCases = executed,
                            TotalCases = results.Count,
                            FailedCases = results.Count(item => item.Status == "failed"),
                            NativePasses = nativePasses,
                            NegativePasses = negativePasses,
                            ProviderClientCount = factory.CreateClientCalls,
                            ProductionWriteCommandCount = 0,
                            Categories = requiredCategories,
                            CaseResults = results
                        },
                        new JsonSerializerOptions { WriteIndented = true }));
            }

            var failures = results
                .Where(item => item.Status == "failed")
                .Select(item => $"{item.Reference}: {item.Failure}")
                .ToArray();
            Assert.True(
                failures.Length == 0,
                "The production native proof matrix completed with independent failures: " +
                string.Join(" | ", failures));
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
        string DeclaredSourceLanguageCode,
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
            string declaredSourceLanguageCode = "en",
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

    private sealed record ProductionNativeProofResult(
        string Reference,
        string Category,
        string Phase,
        string Status,
        string? Failure,
        bool? ExpectedNative,
        bool? NativeSupported,
        string? ReasonCode,
        int? EvidenceCount,
        string? ResponseAuthority,
        string? Stage,
        int ProviderClientCount,
        double ElapsedMilliseconds)
    {
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
                expectedNative,
                null,
                null,
                null,
                null,
                null,
                providerClientCount,
                elapsedMilliseconds);
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

    private sealed class ExceptionCapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IEnumerable<Exception> Exceptions => _exceptions;

        public ILogger CreateLogger(string categoryName) => new ExceptionCapturingLogger(_exceptions);

        public void Dispose() { }
    }

    private sealed class ExceptionCapturingLogger(
        ConcurrentQueue<Exception> exceptions) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                exceptions.Enqueue(exception);
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
    private sealed class ReadOnlyLegendDbCommandInterceptor : DbCommandInterceptor
    {
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

        private static void EnsureSelect(DbCommand command)
        {
            if (!command.CommandText.TrimStart().StartsWith(
                    "SELECT",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw NewWriteBlocked(command);
            }
        }

        private static InvalidOperationException NewWriteBlocked(DbCommand command) =>
            new("Production read-only proof rejected a non-SELECT database command: " +
                command.CommandText.TrimStart().Split(
                    new[] { '\r', '\n', ' ' },
                    StringSplitOptions.RemoveEmptyEntries)[0]);
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

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return new HttpClient(new NoNetworkHandler())
            {
                BaseAddress = new Uri("https://legend-e2e.invalid/")
            };
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The OpenAI test client must not be used by native inference.");
    }
}

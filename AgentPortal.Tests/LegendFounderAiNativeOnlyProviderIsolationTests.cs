using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using System.Security.Claims;
using AgentPortal.Services;
using Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Native-only isolation proved against the real production dependency graph.
///
/// These tests deliberately do not hand-construct <see cref="LegendConnectOperations"/>.
/// A manually constructed instance silently omits the promoted model-inference
/// authority, so an external transport that production would supply is absent
/// from the object under test and a counting double can never observe it. Every
/// case here resolves the authority from the same registration production uses,
/// then replaces only the outermost external boundary — the
/// <see cref="IHttpClientFactory"/> through which the conversation provider,
/// OpenAI model inference, Azure detection/translation, research search, and
/// research page retrieval all leave the process — with a double that counts
/// and refuses every attempt.
///
/// Zero counted attempts under a native-only policy is therefore evidence that
/// no external provider was reachable, not merely that none answered.
/// </summary>
public sealed class LegendFounderAiNativeOnlyProviderIsolationTests
{
    /// <summary>
    /// Counts and refuses every external boundary. Any client construction is
    /// recorded even if the caller never sends, so a provider path that is
    /// merely prepared is still observed.
    /// </summary>

    /// <summary>
    /// Records every external client by name and, unlike the refusing factory,
    /// can answer a named boundary with a governed canned payload. This is what
    /// makes the paired provider-enabled controls non-vacuous: a boundary that
    /// only ever throws can never demonstrate preserved provider provenance.
    /// </summary>
    private sealed class TestScope : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required IServiceScope Scope { get; init; }
        public required MasterAppDbContext Db { get; init; }
        public required ScriptedHttpClientFactory External { get; init; }

        public T Resolve<T>() where T : notnull =>
            Scope.ServiceProvider.GetRequiredService<T>();

        /// <summary>
        /// A fresh request scope over the same provider, the same in-memory
        /// database and the same counting client factory. Production resolves
        /// one scope per request; concurrent requests must therefore be
        /// modelled as concurrent scopes, not as concurrent use of a single
        /// scoped DbContext (which is not thread safe by design).
        /// </summary>
        public IServiceScope NewRequestScope() => Provider.CreateScope();

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Db.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private sealed class ScriptedHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, string> _responses;
        private readonly List<string> _clients = new();

        public ScriptedHttpClientFactory(
            Dictionary<string, string>? responses = null) =>
            _responses = responses ?? new Dictionary<string, string>();

        public IReadOnlyList<string> CreatedClients
        {
            get
            {
                lock (_clients)
                    return _clients.ToArray();
            }
        }

        public int SendAttempts;

        public int CallsTo(string clientName) =>
            CreatedClients.Count(name =>
                string.Equals(name, clientName, StringComparison.Ordinal));

        public HttpClient CreateClient(string name)
        {
            lock (_clients)
                _clients.Add(name);

            _responses.TryGetValue(name, out var body);
            return new HttpClient(new ScriptedHandler(this, body))
            {
                BaseAddress = new Uri("https://external.invalid/")
            };
        }

        private sealed class ScriptedHandler(
            ScriptedHttpClientFactory owner,
            string? body)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.SendAttempts);
                if (body is null)
                {
                    throw new InvalidOperationException(
                        "An external provider boundary was reached: " +
                        request.RequestUri);
                }

                return Task.FromResult(new HttpResponseMessage(
                    System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        body,
                        System.Text.Encoding.UTF8,
                        "application/json")
                });
            }
        }
    }

    /// <summary>
    /// A minimal OpenAI Responses payload carrying one completed output text.
    /// </summary>
    private static string ResponsesPayload(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new
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


    /// <summary>
    /// Builds the production registration graph. Provider credentials are
    /// configured so that no boundary can be reported as "unreachable" merely
    /// because it was unconfigured; the only thing standing between the code
    /// and an external call is the policy under test.
    /// </summary>
    private static TestScope BuildProductionEquivalentScope(
        Dictionary<string, string>? scriptedResponses = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-openai-key",
                // The promoted governed-reasoning model transport reads its own
                // credential prefix. Without it that boundary reports
                // "model_inference_provider_unavailable" and is never reached,
                // which would silently hide the promoted-model leak.
                ["LegendConnect:ModelEvaluation:ApiKey"] = "test-model-key",
                ["LegendConnect:ModelEvaluation:Endpoint"] =
                    "https://external.invalid/responses",
                ["LegendConnect:ModelEvaluation:CodeSha"] =
                    "0123456789abcdef0123456789abcdef01234567",
                ["AzureTranslator:Endpoint"] = "https://external.invalid/",
                ["AzureTranslator:Key"] = "test-azure-key",
                ["AzureTranslator:Region"] = "eastus",
                ["LegendConnect:InternetResearch:SearchEndpoint"] =
                    "https://external.invalid/search",
                ["LegendConnect:InternetResearch:SearchApiKey"] = "test-search-key"
            })
            .Build();

        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterAppDbContext>(options => options
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(warnings =>
                warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddMasterAppMessaging(configuration);

        var external = new ScriptedHttpClientFactory(scriptedResponses);
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(external);

        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new TestScope
        {
            Provider = provider,
            Scope = scope,
            Db = scope.ServiceProvider
                .GetRequiredService<MasterAppDbContext>(),
            External = external
        };
    }

    private static void AssertNoExternalProviderWasReached(
        ScriptedHttpClientFactory external,
        string because)
    {
        Assert.True(
            external.SendAttempts == 0,
            because + " but an external provider request was attempted.");
        Assert.True(
            external.CreatedClients.Count == 0,
            because + " but an external provider client was constructed: " +
            string.Join(", ", external.CreatedClients));
    }

    /// <summary>
    /// The precondition for every other case in this file. If the resolved
    /// authority did not actually receive the promoted model-inference
    /// dependency, a "zero external calls" result would be vacuous.
    /// </summary>
    [Fact]
    public void ResolvedOperations_ActuallyCarryThePromotedModelInferenceAuthority()
    {
        using var scope = BuildProductionEquivalentScope().Scope;
        var operations = scope.ServiceProvider
            .GetRequiredService<ILegendConnectOperations>();

        var concrete = Assert.IsType<LegendConnectOperations>(operations);
        var field = typeof(LegendConnectOperations).GetField(
            "_activeModelInference",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        Assert.True(
            field!.GetValue(concrete) is not null,
            "Production DI must supply the promoted model-inference authority. " +
            "Without it this suite would prove nothing, because the OpenAI-backed " +
            "reasoning transport would simply be absent from the object under test.");
    }

    /// <summary>
    /// The conversation inference entry point itself must reach no provider
    /// under native-only.
    ///
    /// This deliberately claims no more than that. The in-memory corpus is
    /// empty, so this request is not proven to reach a *supported* symbolic
    /// result, and it therefore does not on its own prove that the
    /// promoted-model boundary was exercised. That vulnerable path is proven
    /// separately and deterministically by
    /// <see cref="PromotedModelInference_IsReachedWhenAllowedAndNeverWhenNativeOnly"/>,
    /// which supplies an admitted promoted model and a snapshot satisfying
    /// every authorization gate.
    /// </summary>
    [Fact]
    public async Task NativeOnly_ConversationInference_ReachesNoExternalProvider()
    {
        await using var scope = BuildProductionEquivalentScope();
        var operations = scope.Resolve<ILegendConnectOperations>();

        var inference = await operations.TryInferConversationWithDiscourseAsync(
            "Summarize the current governed position for this account.",
            Array.Empty<LegendConnectConversationContextItem>(),
            discourseState: null,
            CancellationToken.None,
            "en",
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.NotNull(inference);
        AssertNoExternalProviderWasReached(
            scope.External,
            "A native-only conversation inference must be served or fail closed natively,");
    }

    /// <summary>
    /// The undeclared-language path previously reached Azure detection through
    /// the router's fall-through. Native-only must resolve the language from
    /// governed authority or fail closed with its own reason.
    /// </summary>
    [Theory]
    [InlineData("Konbyen dosye ki poko fini?")]
    [InlineData("¿Cuántos expedientes siguen abiertos?")]
    [InlineData("Which files are still open?")]
    public async Task NativeOnly_UndeclaredLanguage_FailsClosedWithoutExternalDetection(
        string utterance)
    {
        await using var scope = BuildProductionEquivalentScope();
        var translation = scope.Resolve<ITranslationService>();

        var detection = await translation.DetectLanguageAsync(
            utterance,
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(detection.Succeeded);
        Assert.Equal(
            "native_only_governed_source_language_undetermined",
            detection.ErrorCode);
        AssertNoExternalProviderWasReached(
            scope.External,
            "Native-only language identification must use governed authority only,");
    }

    [Fact]
    public async Task NativeOnly_Translation_FailsClosedWithoutExternalProvider()
    {
        await using var scope = BuildProductionEquivalentScope();
        var translation = scope.Resolve<ITranslationService>();

        var translated = await translation.TranslateAsync(
            "The review is complete.",
            "ht",
            "en",
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(translated.Succeeded);
        Assert.Equal(
            "external_provider_forbidden_by_native_only_policy",
            translated.ErrorCode);
        AssertNoExternalProviderWasReached(
            scope.External,
            "Native-only translation must not contact a provider,");
    }

    [Fact]
    public async Task NativeOnly_ResearchDecision_IsRefusedBeforeAnyTransport()
    {
        await using var scope = BuildProductionEquivalentScope();
        var operations = scope.Resolve<ILegendConnectOperations>();

        var decision = await operations.DecideResearchNeededAsync(
            "What did the federal register publish about this program yesterday?",
            "en",
            internalInference: null,
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(decision.ResearchRequired);
        Assert.Equal(
            "native_only_external_research_forbidden",
            decision.ReasonCode);
        AssertNoExternalProviderWasReached(
            scope.External,
            "A native-only request must not be routed to internet research,");
    }

    /// <summary>
    /// Research execution is gated independently of the decision, so a caller
    /// that already holds a research-required decision still cannot reach the
    /// search or page transports under a native-only policy.
    /// </summary>
    [Fact]
    public async Task NativeOnly_ResearchExecution_ReachesNeitherSearchNorPageTransport()
    {
        await using var scope = BuildProductionEquivalentScope();
        var operations = scope.Resolve<ILegendConnectOperations>();

        var decision = await operations.DecideResearchNeededAsync(
            "What did the federal register publish about this program yesterday?",
            "en",
            internalInference: null,
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.ProviderEnabled);

        var request = LegendConnectResearchRequestFactory.Create(
            "What did the federal register publish about this program yesterday?",
            decision,
            new LegendConnectResearchAuthorization(
                FounderAuthorized: true,
                "test_founder_authorization",
                Guid.NewGuid().ToString("N"),
                decision.AccessClass,
                IsReadOnly: true,
                ZeroWrite: true),
            internalAnswer: null,
            internalReasonCode: null,
            internalEvidenceCount: 0,
            presentationConstraints: null);

        var outcome = await operations.ExecuteResearchAsync(
            request,
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.Equal(
            LegendConnectResearchOutcomeState.Failure,
            outcome.State);
        Assert.Equal(
            "native_only_external_research_forbidden",
            outcome.Failure?.ReasonCode);
        AssertNoExternalProviderWasReached(
            scope.External,
            "Native-only research execution must not reach a transport,");
    }

    [Fact]
    public async Task NativeOnly_UnknownRequest_FailsClosedWithoutExternalRescue()
    {
        await using var scope = BuildProductionEquivalentScope();
        var operations = scope.Resolve<ILegendConnectOperations>();

        var inference = await operations.TryInferConversationWithDiscourseAsync(
            "Qwltz frbn mxupd, treschen volaby?",
            Array.Empty<LegendConnectConversationContextItem>(),
            discourseState: null,
            CancellationToken.None,
            "en",
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(inference.Supported);
        AssertNoExternalProviderWasReached(
            scope.External,
            "An unknown native-only request must fail closed,");
    }

    [Fact]
    public async Task NativeOnly_CancellationStillPropagates()
    {
        await using var scope = BuildProductionEquivalentScope();
        var operations = scope.Resolve<ILegendConnectOperations>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operations.TryInferConversationWithDiscourseAsync(
                "Summarize the current governed position for this account.",
                Array.Empty<LegendConnectConversationContextItem>(),
                discourseState: null,
                cancelled.Token,
                "en",
                LegendConnectExternalProviderPolicy.NativeOnly));

        AssertNoExternalProviderWasReached(
            scope.External,
            "Cancellation must not open an external path,");
    }

    /// <summary>
    /// The control. Provider-enabled mode must still route to the external
    /// boundary, otherwise the zero counts above would prove only that the
    /// boundary is dead in this fixture rather than that the policy closed it.
    /// </summary>
    [Fact]
    public async Task ProviderEnabled_StillRoutesToTheExternalBoundary()
    {
        await using var scope = BuildProductionEquivalentScope();
        var translation = scope.Resolve<ITranslationService>();

        // The double refuses loudly, so reaching the boundary surfaces as the
        // refusal itself. That is the point: provider-enabled detection really
        // does leave the process here.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translation.DetectLanguageAsync(
                "Which files are still open?",
                CancellationToken.None,
                LegendConnectExternalProviderPolicy.ProviderEnabled));

        Assert.True(
            scope.External.SendAttempts > 0,
            "Provider-enabled detection must still reach the external boundary; " +
            "otherwise the native-only zero counts would be vacuous.");
        Assert.Contains("AzureTranslator", scope.External.CreatedClients);
    }


    private const string SymbolicRequestText =
        "Give the governed model answer.";

    private const string SymbolicAnswer =
        "Founder governed model answer.";

    /// <summary>
    /// Seeds a genuinely supported governed symbolic transition through the
    /// real curriculum authority, so the request under test actually reaches a
    /// supported symbolic result and therefore actually reaches the
    /// promoted-model boundary. Without this the request fails earlier and any
    /// zero-provider count would be vacuous.
    ///
    /// SCOPE, stated exactly: the request exercised by these tests is the
    /// admitted curriculum surface itself. These are therefore
    /// ADMITTED-REQUEST PROVIDER-BOUNDARY REACHABILITY proofs. They are NOT
    /// held-out or generalized-composition evidence and must not be counted as
    /// such.
    ///
    /// OPEN DEFECT, recorded for the next repair group rather than claimed as
    /// a P0 pass: held-out semantic composition is unproven here. An attempt to
    /// drive an unseen paraphrase through a self-invented metadata dimension
    /// was refused at curriculum submission, which was a defect in that test
    /// fixture, not evidence about the product. A valid held-out proof must
    /// reuse an already-governed component with a real oracle (see
    /// LegendConnectCompositionalUnderstandingTests and
    /// LegendConnectGovernedReasoningExecutorTests) and is not attempted here.
    /// </summary>
    private static async Task SeedSupportedSymbolicTransitionAsync(
        TestScope scope)
    {
        ControllerTestHelpers.SeedGovernedLanguageBaseline(scope.Db);
        var curriculum = scope.Resolve<LegendConnectCurriculumService>();
        var submitted = await curriculum.SubmitFounderBatchAsync(
            new LegendConnectCurriculumBatchSubmission(
                "response.model-serving.reasoning",
                "Founder-governed symbolic authority before model realization",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        $"Founder model serving request: {SymbolicRequestText}",
                        new Dictionary<string, string>
                        {
                            ["request_surface"] = SymbolicRequestText,
                            ["conversation_function"] = "governed_model_request"
                        },
                        new LegendConnectMeaningGraphSubmission(
                        [
                            new LegendConnectMeaningNodeSubmission(
                                "function",
                                "conversation_function",
                                "governed_model_request",
                                SymbolicRequestText.TrimEnd('.'))
                        ],
                        [])),
                    new LegendConnectCurriculumExampleSubmission(
                        SymbolicAnswer,
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = "governed_model_answer"
                        })
                ],
                [
                    new LegendConnectSemanticTransitionSubmission(
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] = "governed_model_request"
                            }),
                        new LegendConnectSemanticFrameSubmission(
                            new Dictionary<string, string>
                            {
                                ["conversation_function"] = "governed_model_answer"
                            }))
                ],
                [
                    new LegendConnectSemanticSpanGroundingSubmission(
                        "conversation_function",
                        "request_surface")
                ]));

        Assert.True(submitted.Succeeded, submitted.Message);
    }

    /// <summary>
    /// The end-to-end model-state pair on a genuinely supported symbolic
    /// request.
    ///
    /// Native-only must serve Legend's own symbolic answer, hold the promoted
    /// model Dormant with the policy reason, and create neither the
    /// LegendModelEvaluation client nor the conversation OpenAI client. The
    /// provider-enabled control proves the same request really does reach the
    /// model boundary exactly once and keeps Applied provider provenance, so
    /// the native-only zeros are a decision, not an inert fixture.
    /// </summary>
    [Fact]
    public async Task ReplyAsync_NativeOnly_AdmittedRequest_ServesSymbolicAnswerWithDormantModelAndNoExternalClient()
    {
        using var founderEnvironment = new FounderEnvironmentScope(FounderId);
        await using var scope = BuildProductionEquivalentScope();
        var founder = await SeedFounderAsync(scope.Db);
        await SeedSupportedSymbolicTransitionAsync(scope);
        await SeedPromotedReasoningModelAsync(scope.Db);

        var inference = await scope.Resolve<ILegendConnectOperations>()
            .TryInferConversationWithDiscourseAsync(
                SymbolicRequestText,
                Array.Empty<LegendConnectConversationContextItem>(),
                discourseState: null,
                CancellationToken.None,
                "en",
                LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.True(inference.Supported, inference.ReasonCode);
        Assert.Equal(SymbolicAnswer, inference.Answer);
        Assert.Equal(
            "Dormant",
            inference.ModelAssistance?.State);
        Assert.Equal(
            "native_only_external_model_inference_forbidden",
            inference.ModelAssistance?.ReasonCode);
        Assert.Null(inference.ModelAssistance?.ModelVersion);

        var response = await BuildConversationService(scope).ReplyAsync(
            founder,
            NativeRequest(SymbolicRequestText, nativeOnly: true));

        // The real serving response, not merely "not the provider": LEGEND
        // must succeed under its own authority and return the exact governed
        // conclusion. A SystemDiagnostic, a wrong native authority, an empty
        // message or different text now fails this test.
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal("LegendAi", response.ResponseAuthority);
        Assert.Contains(
            SymbolicAnswer,
            response.Message,
            StringComparison.Ordinal);

        Assert.Equal(0, scope.External.CallsTo("LegendModelEvaluation"));
        Assert.Equal(0, scope.External.CallsTo("OpenAI"));
        AssertNoExternalProviderWasReached(
            scope.External,
            "A native-only supported symbolic reply must reach no provider,");
    }

    [Fact]
    public async Task ProviderEnabled_AdmittedRequest_MakesExactlyOneModelCallAndKeepsProvenance()
    {
        await using var scope = BuildProductionEquivalentScope(
            new Dictionary<string, string>
            {
                ["LegendModelEvaluation"] =
                    ResponsesPayload(SymbolicAnswer)
            });
        await SeedSupportedSymbolicTransitionAsync(scope);
        await SeedPromotedReasoningModelAsync(scope.Db);

        var inference = await scope.Resolve<ILegendConnectOperations>()
            .TryInferConversationWithDiscourseAsync(
                SymbolicRequestText,
                Array.Empty<LegendConnectConversationContextItem>(),
                discourseState: null,
                CancellationToken.None,
                "en",
                LegendConnectExternalProviderPolicy.ProviderEnabled);

        Assert.True(inference.Supported, inference.ReasonCode);
        Assert.Equal(
            1,
            scope.External.CallsTo("LegendModelEvaluation"));
        Assert.Equal("Applied", inference.ModelAssistance?.State);
        Assert.Equal(
            "ft:legend:reasoning-active",
            inference.ModelAssistance?.ModelVersion);
        Assert.NotNull(inference.ModelAssistance?.ModelTrainingRunId);
    }

    /// <summary>
    /// The unresolved-request pair. Native-only must still create no
    /// conversation OpenAI client; the provider-enabled control must reach the
    /// provider and remain attributed to it, never to Legend.
    /// </summary>
    [Fact]
    public async Task ReplyAsync_UnknownRequest_NativeOnlyMakesNoOpenAiCallAndProviderEnabledStaysAttributed()
    {
        const string Unknown =
            "Reconcile the unfamiliar governed position for this account.";

        using var founderEnvironment = new FounderEnvironmentScope(FounderId);

        await using var nativeScope = BuildProductionEquivalentScope();
        var nativeFounder = await SeedFounderAsync(nativeScope.Db);
        await nativeScope.Resolve<ILegendLanguageRegistry>()
            .NormalizeEnabledTranslationLanguageAsync("en", CancellationToken.None);

        var nativeResponse = await BuildConversationService(nativeScope)
            .ReplyAsync(
                nativeFounder,
                NativeRequest(Unknown, nativeOnly: true));

        Assert.NotNull(nativeResponse);
        Assert.NotEqual("OpenAITeacher", nativeResponse.ResponseAuthority);
        Assert.Equal(0, nativeScope.External.CallsTo("OpenAI"));
        AssertNoExternalProviderWasReached(
            nativeScope.External,
            "A native-only unresolved reply must reach no provider,");

        await using var providerScope = BuildProductionEquivalentScope(
            new Dictionary<string, string>
            {
                ["OpenAI"] = ResponsesPayload(
                    "The provider answered this unresolved request.")
            });
        var providerFounder = await SeedFounderAsync(providerScope.Db);

        var providerResponse = await BuildConversationService(providerScope)
            .ReplyAsync(
                providerFounder,
                NativeRequest(Unknown, nativeOnly: false));

        Assert.NotNull(providerResponse);
        Assert.True(
            providerScope.External.CallsTo("OpenAI") > 0,
            "Provider-enabled serving must reach the conversation provider; " +
            "otherwise the native-only zero above proves nothing.");
        Assert.Equal("OpenAITeacher", providerResponse.ResponseAuthority);
    }

    private static LegendFounderAiChatRequest NativeRequest(
        string prompt,
        bool nativeOnly) =>
        new()
        {
            Mode = "legend",
            NativeOnly = nativeOnly,
            SourceLanguageCode = "en",
            Messages = [new LegendFounderAiChatMessage("user", prompt)]
        };

    private const string FounderId = "6b3c1d70-2f5a-49f2-9a2b-1f4a4a2d77b1";

    /// <summary>
    /// Seeds the Founder identity and the governed language baseline so the
    /// serving path is authorized and language identity is resolvable from
    /// governed data alone.
    /// </summary>
    private static async Task<ClaimsPrincipal> SeedFounderAsync(
        MasterAppDbContext db)
    {
        db.AgentProfiles.Add(new AgentProfile
        {
            Id = Guid.NewGuid(),
            AgentUserId = FounderId,
            AgentUpn = "native-only-isolation@legend.test",
            NormalizedEmail = "native-only-isolation@legend.test",
            IsActive = true
        });
        await db.SaveChangesAsync();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        return ControllerTestHelpers.BuildUser(FounderId);
    }

    /// <summary>
    /// Builds the real conversation service over the DI-resolved operations
    /// authority. The conversation provider client, the promoted model
    /// transport, Azure detection/translation and both research transports all
    /// obtain their clients from the same counting/refusing factory, so one
    /// counter observes every external boundary of a real reply.
    /// </summary>
    private static LegendFounderAiConversationService BuildConversationService(
        TestScope scope)
    {
        var operations = scope.Resolve<ILegendConnectOperations>();
        var profiles = new AgentProfileAccessResolver(scope.Db);
        return new LegendFounderAiConversationService(
            scope.External,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = "test-openai-key",
                    ["OpenAI:LegendFounderAiTimeoutSeconds"] = "120"
                })
                .Build(),
            new FounderLegendConnectService(operations, profiles),
            NullLogger<LegendFounderAiConversationService>.Instance,
            new LegendFounderAiDiscourseStateService(
                scope.Db,
                profiles,
                operations),
            scope.Resolve<ILegendLanguageRegistry>(),
            scope.Resolve<ITranslationService>());
    }

    /// <summary>
    /// The end-to-end serving boundary. A real native-only
    /// <see cref="LegendFounderAiConversationService.ReplyAsync"/> must create
    /// the policy, carry it through the Founder wrapper and tool authority, and
    /// reach no external provider at all — including the conversation OpenAI
    /// client the service resolves for itself.
    /// </summary>
    [Theory]
    [InlineData("A queue holds 38 cases; nine close and seven arrive. How many remain?")]
    [InlineData("Rewrite this note as a concise professional update without changing facts.")]
    [InlineData("Konbyen dosye ki poko fini nan lis la?")]
    public async Task NativeOnly_ReplyAsync_ReachesNoExternalProviderEndToEnd(
        string prompt)
    {
        using var founderEnvironment = new FounderEnvironmentScope(FounderId);
        await using var scope = BuildProductionEquivalentScope();
        var founder = await SeedFounderAsync(scope.Db);
        var service = BuildConversationService(scope);

        var response = await service.ReplyAsync(
            founder,
            new LegendFounderAiChatRequest
            {
                Mode = "legend",
                NativeOnly = true,
                SourceLanguageCode = "en",
                Messages =
                [
                    new LegendFounderAiChatMessage("user", prompt)
                ]
            });

        Assert.NotNull(response);

        // The response must never be attributed to a provider under
        // native-only, whether it succeeded or failed closed.
        Assert.NotEqual("OpenAITeacher", response.ResponseAuthority);

        AssertNoExternalProviderWasReached(
            scope.External,
            "A native-only ReplyAsync must reach no external provider,");
    }

    /// <summary>
    /// The provider-enabled control for the same end-to-end boundary. Without
    /// it, the zero counts above could mean the conversation path is simply
    /// inert in this fixture rather than closed by the policy.
    /// </summary>
    [Fact]
    public async Task ProviderEnabled_ReplyAsync_StillReachesTheConversationProvider()
    {
        using var founderEnvironment = new FounderEnvironmentScope(FounderId);
        await using var scope = BuildProductionEquivalentScope();
        var founder = await SeedFounderAsync(scope.Db);
        var service = BuildConversationService(scope);

        var response = await service.ReplyAsync(
            founder,
            new LegendFounderAiChatRequest
            {
                Mode = "legend",
                NativeOnly = false,
                SourceLanguageCode = "en",
                Messages =
                [
                    new LegendFounderAiChatMessage(
                        "user",
                        "A queue holds 38 cases; nine close and seven arrive. How many remain?")
                ]
            });

        Assert.NotNull(response);
        Assert.True(
            scope.External.SendAttempts > 0,
            "Provider-enabled serving must still reach the conversation " +
            "provider; otherwise the native-only zero counts prove nothing.");
    }

    /// <summary>
    /// Proves the vulnerable promoted-model path itself, without depending on
    /// an admitted corpus.
    ///
    /// The empty in-memory corpus cannot produce a supported symbolic answer,
    /// so a full-request test would fail before ever reaching
    /// <c>TryApplyPromotedReasoningModelAsync</c> and would prove nothing about
    /// it. This drives that method directly with a symbolic snapshot that
    /// satisfies every one of its authorization gates — supported, non-blank
    /// answer, no escalation, evidence present, higher-standard evidence, the
    /// composed reason code, and no content-binding provenance — so the call
    /// genuinely reaches the model authority.
    ///
    /// The same snapshot is then run under both policies. Provider-enabled
    /// reaches the external transport; native-only does not, and is reported
    /// dormant with its exact reason. Identical input, opposite outcome,
    /// decided only by the policy.
    /// </summary>
    [Fact]
    public async Task PromotedModelInference_IsReachedWhenAllowedAndNeverWhenNativeOnly()
    {
        await using var scope = BuildProductionEquivalentScope();
        await SeedPromotedReasoningModelAsync(scope.Db);
        var operations = scope.Resolve<ILegendConnectOperations>();

        var symbolic = new LegendConnectNativeInferenceSnapshot(
            Supported: true,
            Confidence: 1m,
            "The governed symbolic authority already established this answer.",
            "semantic_transition_governed_composed",
            EvidenceCount: 3,
            "LEGEND composed governed meaning and selected higher-standard evidence.",
            RequiresEscalation: false,
            EvidenceStandard: "HigherStandard",
            ArticulationMode: "OriginalComposition");

        var apply = typeof(LegendConnectOperations).GetMethod(
            "TryApplyPromotedReasoningModelAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(apply);

        async Task<LegendConnectNativeInferenceSnapshot> ApplyAsync(
            LegendConnectExternalProviderPolicy policy)
        {
            var task = (Task)apply!.Invoke(
                operations,
                [
                    "A held-out governed request.",
                    "en",
                    symbolic,
                    policy,
                    CancellationToken.None
                ])!;
            await task;
            return (LegendConnectNativeInferenceSnapshot)task
                .GetType()
                .GetProperty("Result")!
                .GetValue(task)!;
        }

        // Provider-enabled: the path must genuinely reach the transport. The
        // transport catches its own transport failures, so reaching it is
        // observed through the counting factory rather than through a throw.
        string? allowedReason = null;
        try
        {
            allowedReason =
                (await ApplyAsync(
                    LegendConnectExternalProviderPolicy.ProviderEnabled))
                .ModelAssistance?.ReasonCode;
        }
        catch (InvalidOperationException)
        {
            // The refusing double reached the boundary and refused it. That is
            // the outcome being proven, not a failure.
        }

        Assert.True(
            scope.External.SendAttempts > 0,
            "The promoted-model path must actually reach the external model " +
            "transport when providers are allowed; otherwise the native-only " +
            "result below would be vacuous. Model assistance reported: " +
            (allowedReason ?? "<none>"));

        // Identical governed state: the same admitted promoted model and the
        // same governed language baseline. Only the policy differs.
        await using var nativeScope = BuildProductionEquivalentScope();
        await SeedPromotedReasoningModelAsync(nativeScope.Db);
        var nativeOperations = nativeScope.Resolve<ILegendConnectOperations>();

        var nativeTask = (Task)apply!.Invoke(
            nativeOperations,
            [
                "A held-out governed request.",
                "en",
                symbolic,
                LegendConnectExternalProviderPolicy.NativeOnly,
                CancellationToken.None
            ])!;
        await nativeTask;
        var served = (LegendConnectNativeInferenceSnapshot)nativeTask
            .GetType()
            .GetProperty("Result")!
            .GetValue(nativeTask)!;

        // The governed symbolic answer is served unchanged and unrelabelled.
        Assert.True(served.Supported);
        Assert.Equal(symbolic.Answer, served.Answer);
        Assert.Equal(
            "native_only_external_model_inference_forbidden",
            served.ModelAssistance?.ReasonCode);
        AssertNoExternalProviderWasReached(
            nativeScope.External,
            "Native-only promoted-model inference must not reach a transport,");
    }

    /// <summary>
    /// Seeds the governed language baseline and one genuinely admitted
    /// promoted governed-reasoning model, so promoted-model assistance is
    /// actually eligible. Without this the model authority reports
    /// <c>active_reasoning_model_unavailable</c> and never reaches a
    /// transport, which would make any native-only zero count vacuous.
    /// </summary>
    private static async Task SeedPromotedReasoningModelAsync(
        MasterAppDbContext db)
    {
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db);
        var now = DateTime.UtcNow;
        db.Add(new LegendConnectModelTrainingRun
        {
            Id = Guid.NewGuid(),
            RunKey = "native-only-isolation-realization",
            ScopeKey =
                $"capability:{LegendModelCapabilityKeys.GovernedReasoning}",
            Generation = 1,
            DatasetIdentity = "governed-reasoning-dataset",
            DatasetEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TrainingProvider = "OpenAI",
            BaseModel = "reasoning-base",
            ChallengerModelVersion = "ft:legend:reasoning-active",
            State = "TrainingCompleted",
            EvaluationState = "Passed",
            PromotionState = "Promoted",
            TrainingExampleCount = 12,
            ValidationExampleCount = 4,
            HeldOutScore = 1m,
            RegressionScore = 1m,
            FailureDetail = RealizationRuntimeProof,
            CompletedUtc = now.AddMinutes(-1),
            PromotedUtc = now,
            UpdatedUtc = now
        });
        await db.SaveChangesAsync();
    }

    private const string RealizationRuntimeProof =
        "evaluated=1;reference=1.000000;blocking=0;protected=0;leakage=0;prompt_set=test-v1;code_sha=0123456789abcdef0123456789abcdef01234567;runtime_mode=LockedHeldOutEvaluation;response_authority=LegendConnectActiveModelInference;settings=responses-v1,store=false,max_output_tokens=1200;criteria=governed-reference-policy-v1,held_out>=0.950000,regression>=1.000000,protected>=0.980000,blocking=0,leakage=0,runtime_model=exact;proof_set=abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789;latency_us=1;cost_micro=1";

    private sealed class FounderEnvironmentScope : IDisposable
    {
        private readonly string? _previous =
            Environment.GetEnvironmentVariable("FOUNDER_OID");

        public FounderEnvironmentScope(string founderId) =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", founderId);

        public void Dispose() =>
            Environment.SetEnvironmentVariable("FOUNDER_OID", _previous);
    }

    /// <summary>
    /// The policy is a value carried per request, not ambient state. Running
    /// native-only and provider-enabled requests *concurrently* on the same
    /// scope is the real test: a sequential pair could pass even if the policy
    /// were stored in shared mutable state, because the writes would not
    /// overlap. Here they do overlap, and every native-only request must still
    /// refuse while every provider-enabled request still reaches the boundary.
    /// </summary>
    [Fact]
    public async Task Policy_IsPerRequestUnderConcurrentMixedTraffic()
    {
        await using var scope = BuildProductionEquivalentScope();

        const int Pairs = 24;
        var nativeResults =
            new TranslationDetectionResult[Pairs];
        var providerReachedBoundary = new bool[Pairs];

        var work = new List<Task>();
        for (var index = 0; index < Pairs; index++)
        {
            var slot = index;
            work.Add(Task.Run(async () =>
            {
                using var requestScope = scope.NewRequestScope();
                nativeResults[slot] = await requestScope.ServiceProvider
                    .GetRequiredService<ITranslationService>()
                    .DetectLanguageAsync(
                        "Which files are still open?",
                        CancellationToken.None,
                        LegendConnectExternalProviderPolicy.NativeOnly);
            }));

            work.Add(Task.Run(async () =>
            {
                using var requestScope = scope.NewRequestScope();
                try
                {
                    await requestScope.ServiceProvider
                        .GetRequiredService<ITranslationService>()
                        .DetectLanguageAsync(
                            "Which files are still open?",
                            CancellationToken.None,
                            LegendConnectExternalProviderPolicy.ProviderEnabled);
                }
                catch (InvalidOperationException)
                {
                    // Reaching the refusing boundary is the expected outcome.
                    providerReachedBoundary[slot] = true;
                }
            }));
        }

        await Task.WhenAll(work);

        // Every native-only request failed closed with its own governed reason.
        Assert.All(nativeResults, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Equal(
                "native_only_governed_source_language_undetermined",
                result.ErrorCode);
        });

        // Every provider-enabled request still reached the boundary.
        Assert.All(
            providerReachedBoundary,
            reached => Assert.True(
                reached,
                "A provider-enabled request was wrongly refused while " +
                "native-only requests ran concurrently."));

        // The external boundary was reached exactly as many times as there
        // were provider-enabled requests: the native-only ones added nothing.
        Assert.Equal(Pairs, scope.External.CallsTo("AzureTranslator"));
    }

    /// <summary>
    /// The stage-preservation repair.
    ///
    /// Native-only forbids the external boundary, not Legend's own translation
    /// authority. A same-language request is resolved by Legend's internal
    /// same-language stage, so it must still SUCCEED under native-only with no
    /// external call at all. Before the repair this returned a bare refusal,
    /// because the policy short-circuited the whole router ahead of every
    /// internal stage.
    /// </summary>
    [Fact]
    public async Task NativeOnly_SameLanguageTranslation_IsServedInternallyWithNoExternalCall()
    {
        await using var scope = BuildProductionEquivalentScope();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(scope.Db);
        var translation = scope.Resolve<ITranslationService>();

        var result = await translation.TranslateAsync(
            "The governed position is unchanged.",
            "en",
            "en",
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Equal("The governed position is unchanged.", result.TranslatedText);
        Assert.Equal("LegendConnectSameLanguage", result.Provider);
        AssertNoExternalProviderWasReached(
            scope.External,
            "Legend's own same-language stage must reach no provider,");
    }

    /// <summary>
    /// The cross-language counterpart. No internal stage can serve it, so
    /// native-only must fail closed at the external boundary claiming no
    /// provider identity, while the paired control still reaches Azure and
    /// keeps the AzureTranslator identity.
    /// </summary>
    [Fact]
    public async Task CrossLanguageTranslation_FailsClosedNativelyAndKeepsAzureIdentityWhenAllowed()
    {
        await using var scope = BuildProductionEquivalentScope();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(scope.Db);
        var translation = scope.Resolve<ITranslationService>();

        var native = await translation.TranslateAsync(
            "The governed position is unchanged.",
            "ht",
            "en",
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(native.Succeeded);
        Assert.Equal(
            "external_provider_forbidden_by_native_only_policy",
            native.ErrorCode);
        Assert.Equal("None", native.Provider);
        AssertNoExternalProviderWasReached(
            scope.External,
            "Native-only cross-language translation must reach no provider,");

        await using var providerScope = BuildProductionEquivalentScope();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(providerScope.Db);

        var allowed = await providerScope.Resolve<ITranslationService>()
            .TranslateAsync(
                "The governed position is unchanged.",
                "ht",
                "en",
                CancellationToken.None,
                LegendConnectExternalProviderPolicy.ProviderEnabled);

        // The same request, with providers allowed, advances past the exact
        // point at which native-only stopped and enters the external
        // quota/capacity/Azure region, where it is attributed to Azure.
        //
        // Stated exactly: this fixture provisions no translation capacity, so
        // the request is stopped by the capacity gate that sits inside that
        // region rather than by the Azure HTTP call itself. That is still
        // decisive for this test, because reaching a distinctly external-region
        // outcome under AzureTranslator identity is only possible if the
        // native-only return point above is what stopped the first request.
        // Azure's own HTTP boundary being genuinely reachable in this fixture
        // is proven separately by
        // <see cref="Policy_IsPerRequestUnderConcurrentMixedTraffic"/>, which
        // observes real AzureTranslator client creation.
        Assert.False(allowed.Succeeded);
        Assert.Equal("AzureTranslator", allowed.Provider);
        Assert.Equal("translation_capacity_unavailable", allowed.ErrorCode);
        Assert.NotEqual(
            "external_provider_forbidden_by_native_only_policy",
            allowed.ErrorCode);
    }

    /// <summary>
    /// The two research boundaries, observed through independent counting
    /// doubles rather than through the HTTP factory, so the search transport
    /// and the page retriever are counted separately and cannot mask each
    /// other.
    ///
    /// Native-only must call neither. Provider-enabled must call each exactly
    /// once and carry the transport provenance through to the outcome.
    /// </summary>
    [Fact]
    public async Task ResearchTransports_AreNeverCalledNativeOnlyAndCalledExactlyOnceWhenAllowed()
    {
        var nativeSearch = new CountingResearchSearchTransport();
        var nativePages = new CountingResearchPageRetriever();
        await using var nativeScope = BuildProductionEquivalentScope(
            configureServices: services =>
            {
                services.RemoveAll<ILegendConnectResearchSearchTransport>();
                services.RemoveAll<ILegendConnectResearchPageRetriever>();
                services.AddSingleton<ILegendConnectResearchSearchTransport>(
                    nativeSearch);
                services.AddSingleton<ILegendConnectResearchPageRetriever>(
                    nativePages);
            });

        var nativeOutcome = await nativeScope.Resolve<ILegendConnectOperations>()
            .ExecuteResearchAsync(
                ResearchRequest(),
                CancellationToken.None,
                LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.Equal(
            LegendConnectResearchOutcomeState.Failure,
            nativeOutcome.State);
        Assert.Equal(0, nativeSearch.CallCount);
        Assert.Equal(0, nativePages.CallCount);
        AssertNoExternalProviderWasReached(
            nativeScope.External,
            "Native-only research must reach no transport,");

        var allowedSearch = new CountingResearchSearchTransport();
        var allowedPages = new CountingResearchPageRetriever();
        await using var allowedScope = BuildProductionEquivalentScope(
            configureServices: services =>
            {
                services.RemoveAll<ILegendConnectResearchSearchTransport>();
                services.RemoveAll<ILegendConnectResearchPageRetriever>();
                services.AddSingleton<ILegendConnectResearchSearchTransport>(
                    allowedSearch);
                services.AddSingleton<ILegendConnectResearchPageRetriever>(
                    allowedPages);
            });

        await allowedScope.Resolve<ILegendConnectOperations>()
            .ExecuteResearchAsync(
                ResearchRequest(),
                CancellationToken.None,
                LegendConnectExternalProviderPolicy.ProviderEnabled);

        // Counted independently, so neither boundary can mask the other.
        Assert.Equal(1, allowedSearch.CallCount);
        Assert.Equal(1, allowedPages.CallCount);

        // The page retriever received the search transport's own governed
        // output, so provenance crosses the boundary rather than being
        // regenerated.
        Assert.Equal("source-1", allowedPages.ObservedSourceIdentity);
        Assert.Equal(
            "search-result-1",
            allowedPages.ObservedSearchResultIdentity);
    }

    private static LegendConnectResearchRequest ResearchRequest()
    {
        const string Question = "Verify the current public evidence.";
        var decidedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var decision = new LegendConnectResearchNeededDecision(
            true,
            LegendConnectResearchNeed.InternalKnowledgeGap,
            "external_factual_internal_knowledge_gap",
            LegendConnectResearchAccessClass.PublicReadOnly,
            "en",
            false,
            false,
            false,
            null,
            decidedUtc);

        return new LegendConnectResearchRequest(
            Guid.NewGuid(),
            Question,
            decision,
            [
                new LegendConnectBoundedSearchQuery(
                    "query-1",
                    1,
                    Question,
                    "en",
                    4)
            ],
            4,
            4,
            8,
            2_000,
            1,
            new LegendConnectResearchAuthorization(
                true,
                LegendConnectResearchContracts.PublicAuthorizationProvenance,
                Guid.NewGuid().ToString("N"),
                LegendConnectResearchAccessClass.PublicReadOnly,
                true,
                true),
            null,
            "meaning_graph_component_unknown",
            0,
            decidedUtc);
    }

    /// <summary>
    /// The two-pass read-only governed receipt continuity matrix, executed
    /// entirely under one immutable NativeOnly policy.
    ///
    /// The valid row is end to end: pass one refuses to answer and returns the
    /// governed read request; that request is then handed to the real
    /// registered <see cref="LegendFounderToolAuthority"/>, composed exactly as
    /// production composes it, which executes the canonical read-only tool
    /// <c>legend_translation_quality</c> against the authenticated database and
    /// issues the receipt itself. The test never fabricates a valid receipt, so
    /// this proves the whole request/execute/bind loop rather than only the
    /// second-pass validator.
    ///
    /// The negative rows mutate that authority-issued receipt: a wrong request
    /// identity, a receipt declaring it did not guarantee zero writes, and one
    /// executed outside the governed freshness window must each fail closed
    /// with no answer and no escalation.
    ///
    /// Both passes use the same NativeOnly policy instance, the governed read
    /// executes exactly once, no row may construct or contact an external
    /// provider, and no row may write to the database.
    /// </summary>
    [Theory]
    [InlineData("valid", true, "semantic_transition_governed_composed")]
    [InlineData("wrong-identity", false, "read_only_content_binding_receipt_malformed")]
    [InlineData("malformed-zero-write", false, "read_only_content_binding_receipt_malformed")]
    [InlineData("stale", false, "read_only_content_binding_stale")]
    public async Task NativeOnly_TwoPassReadOnlyContentBinding_AnswersOnlyForTheExactGovernedReceipt(
        string receiptCase,
        bool expectSupported,
        string expectedReasonCode)
    {
        using var founderEnvironment = new FounderEnvironmentScope(FounderId);
        await using var scope = BuildProductionEquivalentScope();
        var founder = await SeedFounderAsync(scope.Db);
        var curriculum = scope.Resolve<LegendConnectCurriculumService>();
        for (var support = 1; support <= 3; support++)
        {
            var submitted = await curriculum.SubmitFounderBatchAsync(
                ReadOnlyContentBindingFamily(support));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        // One immutable policy object is used for both passes, so continuity of
        // the native-only decision across the tool round trip is proved rather
        // than assumed.
        var policy = LegendConnectExternalProviderPolicy.NativeOnly;
        var realOperations = scope.Resolve<ILegendConnectOperations>();

        // Counts the governed read at the canonical operations boundary the
        // tool actually reads through, so "executed exactly once" is observed,
        // not inferred from the number of test calls.
        var countingOperations =
            CountingOperationsProxy.Wrap(realOperations, out var counter);

        var toolAuthority = new LegendFounderToolAuthority(
            new FounderLegendConnectService(
                countingOperations,
                new AgentProfileAccessResolver(scope.Db)),
            null);

        var pending = await realOperations.TryInferConversationWithDiscourseAsync(
            ReadOnlyContentRequestText,
            Array.Empty<LegendConnectConversationContextItem>(),
            new LegendConnectDiscourseStateSnapshot([]),
            CancellationToken.None,
            "en",
            policy);

        Assert.False(pending.Supported);
        Assert.False(pending.RequiresEscalation);
        Assert.Equal("read_only_content_binding_required", pending.ReasonCode);
        var readRequest = Assert.IsType<LegendConnectReadOnlyContentBindingRequest>(
            pending.ReadOnlyContentRequest);
        Assert.Equal("legend_translation_quality", readRequest.ToolName);
        Assert.Equal("needsReviewCount", readRequest.ValuePath);
        AssertNoExternalProviderWasReached(
            scope.External,
            "native-only pass one must resolve the governed read request internally");

        var writesBeforeRead = scope.Db.ChangeTracker.Entries().Count();
        var bound = await toolAuthority.BindReadOnlyResultAsync(
            founder,
            readRequest,
            CancellationToken.None);

        Assert.True(bound.Succeeded, bound.ReasonCode);
        Assert.Equal("read_only_content_binding_receipt_governed", bound.ReasonCode);
        var issued = bound.Receipt!;

        // The governed read ran exactly once, through the canonical authority.
        Assert.Equal(1, counter.TranslationQualityReads);

        // The receipt carries real, complete lineage back to the request the
        // curriculum authority produced.
        Assert.Equal(readRequest.RequestIdentity, issued.RequestIdentity);
        Assert.Equal(readRequest.TransitionSignature, issued.TransitionSignature);
        Assert.Equal(
            readRequest.ResultSemanticFrameSignature,
            issued.ResultSemanticFrameSignature);
        Assert.Equal("legend_translation_quality", issued.ToolName);
        Assert.Equal(
            LegendLanguageIdentity.TextHash(readRequest.ArgumentsJson),
            issued.ArgumentsHash);
        Assert.Equal(readRequest.ValuePath, issued.ValuePath);
        Assert.Equal(readRequest.SemanticVariable, issued.SemanticVariable);
        Assert.Equal(readRequest.ResultDimension, issued.ResultDimension);
        Assert.Equal(
            LegendConnectReadOnlyContentBindingContracts.Provenance,
            issued.Provenance);
        Assert.True(issued.IsReadOnly);
        Assert.True(issued.ZeroWrite);
        Assert.False(string.IsNullOrWhiteSpace(issued.OutputHash));

        // The bound scalar is the value the canonical operations authority
        // actually reports, not a value chosen by this test.
        var expectedScalar = (await realOperations
                .GetTranslationQualityAsync(CancellationToken.None))
            .NeedsReviewCount
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expectedScalar, issued.SemanticValue);

        var receipt = receiptCase switch
        {
            "wrong-identity" => issued with
            {
                RequestIdentity = Guid.NewGuid().ToString("N")
            },
            "malformed-zero-write" => issued with { ZeroWrite = false },
            "stale" => issued with
            {
                ExecutedUtc = DateTime.UtcNow.AddMinutes(-10),
                ObservedUtc = DateTime.UtcNow.AddMinutes(-10)
            },
            _ => issued
        };

        var completed = await realOperations.TryInferConversationWithReadOnlyContentAsync(
            ReadOnlyContentRequestText,
            Array.Empty<LegendConnectConversationContextItem>(),
            new LegendConnectDiscourseStateSnapshot([]),
            receipt,
            CancellationToken.None,
            "en",
            policy);

        Assert.Equal(expectedReasonCode, completed.ReasonCode);
        Assert.Equal(expectSupported, completed.Supported);

        if (expectSupported)
        {
            // The exact governed conclusion realized from the governed scalar,
            // not merely a non-empty answer.
            Assert.Equal($"Open issues {expectedScalar}.", completed.Answer);
            var provenance = Assert.Single(completed.ContentBindingProvenance!);
            Assert.Equal(
                LegendConnectReadOnlyContentBindingContracts.Provenance,
                provenance.Provenance);
            Assert.Equal(readRequest.RequestIdentity, provenance.RequestIdentity);
            Assert.True(provenance.IsReadOnly);
            Assert.True(provenance.ZeroWrite);
        }
        else
        {
            Assert.Null(completed.Answer);
            // A rejected governed receipt must not become a reason to leave the
            // native boundary.
            Assert.False(completed.RequiresEscalation);
            Assert.True(
                completed.ContentBindingProvenance is null ||
                completed.ContentBindingProvenance.Count == 0);
        }

        // The second pass revalidates; it must never execute the tool again.
        Assert.Equal(1, counter.TranslationQualityReads);
        Assert.Equal(writesBeforeRead, scope.Db.ChangeTracker.Entries().Count());
        AssertNoExternalProviderWasReached(
            scope.External,
            "the whole two-pass read-only exchange is native-only");
    }

    /// <summary>
    /// Counts governed reads at the real operations boundary while delegating
    /// every call to the production instance. Nothing is stubbed; this only
    /// observes.
    /// </summary>
    private class CountingOperationsProxy : DispatchProxy
    {
        private ILegendConnectOperations _inner = null!;

        public int TranslationQualityReads { get; private set; }

        public static ILegendConnectOperations Wrap(
            ILegendConnectOperations inner,
            out CountingOperationsProxy counter)
        {
            var proxy = Create<ILegendConnectOperations, CountingOperationsProxy>();
            counter = (CountingOperationsProxy)(object)proxy;
            counter._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                throw new ArgumentNullException(nameof(targetMethod));

            if (string.Equals(
                    targetMethod.Name,
                    nameof(ILegendConnectOperations.GetTranslationQualityAsync),
                    StringComparison.Ordinal))
            {
                TranslationQualityReads++;
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException exception)
                when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }

    private const string ReadOnlyContentRequestText =
        "What is the current open issue count?";

    /// <summary>
    /// The canonical read-only content-binding curriculum family, matching the
    /// established fixture in
    /// <see cref="LegendConnectSemanticSpanGroundingTests"/>. It declares the
    /// governed tool, arguments, value path and freshness window on the result
    /// frame, so the read request under test is produced by the curriculum
    /// authority rather than by this test.
    /// </summary>
    private static LegendConnectCurriculumBatchSubmission ReadOnlyContentBindingFamily(
        int support)
    {
        var countSurface = support switch
        {
            1 => "two",
            2 => "four",
            _ => "six"
        };
        var countValue = (support * 2).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var resultVariations = new Dictionary<string, string>
        {
            ["response_kind"] = "current_issue_count",
            ["current_issue_count"] = countValue,
            ["content_binding_authority"] = "legend_founder_tool_authority",
            ["content_binding_access"] = "read_only",
            ["content_binding_tool"] = "legend_translation_quality",
            ["content_binding_arguments"] = "{}",
            ["content_binding_value_path"] = "needsReviewCount",
            ["content_binding_max_age_seconds"] = "60"
        };
        var resultFrame = new Dictionary<string, string>(resultVariations)
        {
            ["current_issue_count"] = "$IssueCount"
        };

        return new LegendConnectCurriculumBatchSubmission(
            $"response.read-only-content.{support}",
            "Founder-governed read-only operational content binding",
            [
                new LegendConnectCurriculumExampleSubmission(
                    $"Founder current issue request {support}: {ReadOnlyContentRequestText}",
                    new Dictionary<string, string>
                    {
                        ["request_surface"] = ReadOnlyContentRequestText,
                        ["conversation_function"] = "current_issue_count_request"
                    },
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "function",
                            "conversation_function",
                            "current_issue_count_request",
                            "What is the current open issue count")
                    ],
                    [])),
                new LegendConnectCurriculumExampleSubmission(
                    $"Open issues {countSurface}.",
                    resultVariations,
                    new LegendConnectMeaningGraphSubmission(
                    [
                        new LegendConnectMeaningNodeSubmission(
                            "label",
                            "response_kind",
                            "current_issue_count",
                            "Open issues"),
                        new LegendConnectMeaningNodeSubmission(
                            "count",
                            "current_issue_count",
                            countValue,
                            countSurface)
                    ],
                    [new LegendConnectMeaningRelationSubmission(
                        "label", "reports", "count")]))
            ],
            [
                new LegendConnectSemanticTransitionSubmission(
                    new LegendConnectSemanticFrameSubmission(
                        new Dictionary<string, string>
                        {
                            ["conversation_function"] = "current_issue_count_request"
                        }),
                    new LegendConnectSemanticFrameSubmission(resultFrame))
            ],
            support == 1
                ? [new LegendConnectSemanticSpanGroundingSubmission(
                    "conversation_function", "request_surface")]
                : []);
    }

    private sealed class CountingResearchSearchTransport
        : ILegendConnectResearchSearchTransport
    {
        public int CallCount;

        public Task<LegendConnectResearchSearchTransportResult> SearchAsync(
            LegendConnectResearchSearchTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            var retrievedUtc =
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var source = new LegendConnectResearchSourceIdentity(
                "source-1",
                "https://records.example/evidence",
                "Source One",
                "Publisher One",
                LegendConnectResearchSourceClass.PrimaryOfficialRecord,
                retrievedUtc,
                retrievedUtc,
                DocumentLanguageCode: "en",
                Author: "Record Custodian One",
                ProvenanceComplete: true,
                LineageKind:
                    LegendConnectResearchSourceLineageKind.Original,
                AuthorityScopes:
                    [LegendConnectResearchAuthorityScope.GeneralRecord]);

            var searchResult = new LegendConnectSearchResult(
                "search-result-1",
                request.Queries[0].QueryIdentity,
                1,
                source.SourceIdentity,
                "Source One",
                source.CanonicalUri,
                "A bounded public snippet.",
                "en",
                "en");

            return Task.FromResult(
                new LegendConnectResearchSearchTransportResult(
                    true,
                    "CountingSearchTransport",
                    "CountingSearchProvider",
                    null,
                    "counting-settings",
                    request.Queries,
                    [
                        new LegendConnectResearchSearchQueryReceipt(
                            "receipt-1",
                            request.Queries[0].QueryIdentity,
                            request.Queries[0].Query,
                            "en",
                            retrievedUtc,
                            "CountingSearchTransport",
                            "CountingSearchProvider",
                            1,
                            null,
                            "Recorded",
                            true,
                            true)
                    ],
                    [searchResult],
                    [source],
                    [],
                    [],
                    1,
                    null,
                    null,
                    false));
        }
    }

    private sealed class CountingResearchPageRetriever
        : ILegendConnectResearchPageRetriever
    {
        public int CallCount;

        public string? ObservedSourceIdentity { get; private set; }

        public string? ObservedSearchResultIdentity { get; private set; }

        public Task<LegendConnectResearchPageRetrievalResult> RetrieveAsync(
            LegendConnectResearchPageRetrievalRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            ObservedSourceIdentity =
                request.Sources.Count > 0
                    ? request.Sources[0].SourceIdentity
                    : null;
            ObservedSearchResultIdentity =
                request.SearchResults.Count > 0
                    ? request.SearchResults[0].SearchResultIdentity
                    : null;
            return Task.FromResult(
                new LegendConnectResearchPageRetrievalResult(
                    false,
                    "CountingPageRetriever",
                    "counting-settings",
                    request.SearchResults,
                    request.Sources,
                    [],
                    [],
                    [],
                    [],
                    1,
                    "counting_double_returns_no_documents",
                    false));
        }
    }
}

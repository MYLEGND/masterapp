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
    private sealed class RefusingHttpClientFactory : IHttpClientFactory
    {
        private readonly List<string> _clients = new();

        public IReadOnlyList<string> CreatedClients
        {
            get
            {
                lock (_clients)
                    return _clients.ToArray();
            }
        }

        public int SendAttempts;

        public HttpClient CreateClient(string name)
        {
            lock (_clients)
                _clients.Add(name);
            return new HttpClient(new RefusingHandler(this))
            {
                BaseAddress = new Uri("https://external.invalid/")
            };
        }

        private sealed class RefusingHandler : HttpMessageHandler
        {
            private readonly RefusingHttpClientFactory _owner;

            public RefusingHandler(RefusingHttpClientFactory owner) =>
                _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _owner.SendAttempts);
                throw new InvalidOperationException(
                    "An external provider boundary was reached: " +
                    request.RequestUri);
            }
        }
    }

    private sealed class TestScope : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required IServiceScope Scope { get; init; }
        public required MasterAppDbContext Db { get; init; }
        public required RefusingHttpClientFactory External { get; init; }

        public T Resolve<T>() where T : notnull =>
            Scope.ServiceProvider.GetRequiredService<T>();

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Db.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds the production registration graph. Provider credentials are
    /// configured so that no boundary can be reported as "unreachable" merely
    /// because it was unconfigured; the only thing standing between the code
    /// and an external call is the policy under test.
    /// </summary>
    private static TestScope BuildProductionEquivalentScope()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-openai-key",
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

        var external = new RefusingHttpClientFactory();
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(external);

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
        RefusingHttpClientFactory external,
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

    [Fact]
    public async Task NativeOnly_SupportedSymbolicRequest_ReachesNoExternalProvider()
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
            "A native-only symbolic request must be served or fail closed natively,");
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

    /// <summary>
    /// The policy is a value carried per request, not ambient state. A
    /// native-only request must not close the boundary for a subsequent
    /// provider-enabled request resolved from the same scope, and vice versa.
    /// </summary>
    [Fact]
    public async Task Policy_IsPerRequestAndDoesNotLeakBetweenRequests()
    {
        await using var scope = BuildProductionEquivalentScope();
        var translation = scope.Resolve<ITranslationService>();

        var native = await translation.DetectLanguageAsync(
            "Which files are still open?",
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.False(native.Succeeded);
        AssertNoExternalProviderWasReached(
            scope.External,
            "The native-only request must not reach a provider,");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translation.DetectLanguageAsync(
                "Which files are still open?",
                CancellationToken.None,
                LegendConnectExternalProviderPolicy.ProviderEnabled));

        Assert.True(
            scope.External.SendAttempts > 0,
            "The later provider-enabled request must be unaffected by the " +
            "earlier native-only request.");
    }
}

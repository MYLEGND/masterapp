using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectTranslationServingReconciliationTests
{
    private const string RuntimeProof =
        "evaluated=1;reference=1.000000;blocking=0;protected=0;leakage=0;prompt_set=test-v1;code_sha=0123456789abcdef0123456789abcdef01234567;runtime_mode=LockedHeldOutEvaluation;response_authority=LegendConnectActiveModelInference;settings=responses-v1,store=false,max_output_tokens=1200;criteria=governed-reference-policy-v1,held_out>=0.950000,regression>=1.000000,protected>=0.980000,blocking=0,leakage=0,runtime_model=exact;proof_set=abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789;latency_us=1;cost_micro=1";

    [Theory]
    [InlineData("memory", "LegendConnectTranslationMemory")]
    [InlineData("structural", "LegendConnectStructuralComposition")]
    [InlineData("contextual", "LegendConnectContextualComposition")]
    [InlineData("promoted-model", "LegendConnectPromotedTranslationModel")]
    [InlineData("provider-observation", "LegendConnectProviderObservation")]
    [InlineData("azure", "AzureTranslator")]
    public async Task Router_SelectsExactlyOneTerminalStageInCanonicalOrder(
        string selectedStage,
        string expectedProvider)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        var calls = new List<string>();

        var intelligence = new Mock<ILegendConnectTranslationIntelligence>();
        intelligence.SetupGet(item => item.IsContextualCompositionActive).Returns(true);
        intelligence.Setup(item => item.TryGetTrustedExactMemoryAsync(
                "en", "ht", "source", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("memory"))
            .ReturnsAsync(selectedStage == "memory"
                ? new LegendTranslationMemoryMatch("memory result", 1m, LegendConnectKnowledgeProvenance.FounderApproved, "Verified")
                : null);
        intelligence.Setup(item => item.EvaluateContextAsync(
                "en", "ht", "source", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("contextual"))
            .ReturnsAsync(selectedStage == "contextual"
                ? new LegendContextualTranslationSuggestion("context result", 1m)
                : null);
        intelligence.Setup(item => item.TryGetReusableProviderObservationAsync(
                "en", "ht", "source", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("provider-observation"))
            .ReturnsAsync(selectedStage == "provider-observation"
                ? new LegendTranslationMemoryMatch("observed result", 1m, LegendConnectKnowledgeProvenance.ProviderDerived, "Observation")
                : null);

        var structural = new Mock<ILegendConnectStructuralCompositionGate>();
        structural.Setup(item => item.TryComposeAsync(
                "en", "ht", "source", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("structural"))
            .ReturnsAsync(selectedStage == "structural"
                ? new LegendContextualTranslationSuggestion("structural result", 1m)
                : null);

        var activeModel = new Mock<ILegendConnectActiveModelInference>();
        activeModel.Setup(item => item.TryTranslateAsync(
                "en", "ht", "source", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("promoted-model"))
            .ReturnsAsync(selectedStage == "promoted-model"
                ? new LegendConnectActiveModelInferenceResult(true, "model result", "ft:test", null)
                : new LegendConnectActiveModelInferenceResult(false, null, null, "active_model_unavailable"));

        var provider = new RecordingProvider(calls);
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            intelligence: intelligence.Object,
            structuralComposition: structural.Object,
            activeModelInference: activeModel.Object);

        var result = await router.TranslateAsync("source", "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal(expectedProvider, result.Provider);
        var expectedCalls = selectedStage switch
        {
            "memory" => new[] { "memory" },
            "structural" => new[] { "memory", "structural" },
            "contextual" => new[] { "memory", "structural", "contextual" },
            "promoted-model" => new[] { "memory", "structural", "contextual", "promoted-model" },
            "provider-observation" => new[] { "memory", "structural", "contextual", "promoted-model", "provider-observation" },
            _ => new[] { "memory", "structural", "contextual", "promoted-model", "provider-observation", "azure" }
        };
        Assert.Equal(expectedCalls, calls);

        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(1, demand.TranslationRequestCount);
        Assert.Equal(1,
            demand.TranslationMemoryHitCount +
            demand.StructuralInternalServeCount +
            demand.ContextualInternalServeCount +
            demand.NeuralModelServeCount +
            demand.ProviderObservationReuseCount +
            demand.AzureFallbackCount);
    }

    [Fact]
    public async Task ProviderObservationReuse_IsAccountedSeparatelyAndNeverAsNativeMemory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        AddAlignment(
            db,
            "Provider source",
            "Provider target",
            LegendConnectKnowledgeProvenance.ProviderDerived,
            "Observation",
            0.99m);
        await db.SaveChangesAsync();

        var entitlements = new TranslationEntitlementAuthority(
            db,
            Mock.Of<IControlledResourceAccessService>(),
            configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            entitlements: entitlements);

        var result = await router.TranslateForAccountAsync(
            "Provider source",
            "ht",
            "en",
            new MessagingActor("account", MessagingParticipantTypes.Client),
            TranslationUsageReference.ForMessage(Guid.NewGuid(), "ht"));

        Assert.True(result.Succeeded);
        Assert.Equal("LegendConnectProviderObservation", result.Provider);
        Assert.Equal(0, provider.TranslateCalls);
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(0, demand.TranslationMemoryHitCount);
        Assert.Equal(0, demand.NeuralModelServeCount);
        Assert.Equal(1, demand.ProviderObservationReuseCount);
        Assert.Equal(0, demand.AzureFallbackCount);
        var system = await db.LegendTranslationSystemUsages.SingleAsync();
        Assert.Equal(0, system.TranslationMemoryCharactersAvoided);
        Assert.Equal("Provider source".Length, system.ProviderObservationCharactersAvoided);
        var account = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal(0, account.TranslationMemoryCharactersAvoided);
        Assert.Equal("Provider source".Length, account.ProviderObservationCharactersAvoided);
    }

    [Fact]
    public async Task LowQualityMachineAlignment_DoesNotServeAndFallsBackWithoutPromotion()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        AddAlignment(
            db,
            "Unqualified source",
            "Unqualified target",
            LegendConnectKnowledgeProvenance.SystemValidatedMachine,
            "SystemValidated",
            0.50m);
        await db.SaveChangesAsync();

        var provider = new RecordingProvider();
        var router = RouterWithRealIntelligence(db, registry, configuration, provider);
        var result = await router.TranslateAsync("Unqualified source", "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("AzureTranslator", result.Provider);
        Assert.Equal(1, provider.TranslateCalls);
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(0, demand.TranslationMemoryHitCount);
        Assert.Equal(0, demand.ProviderObservationReuseCount);
        Assert.Equal(1, demand.AzureFallbackCount);
    }

    [Fact]
    public async Task DisabledPair_BypassesAllInternalCandidatesAndKeepsAzureFallbackVisible()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        var pair = await db.LegendLanguagePairs.SingleAsync();
        pair.IsEnabled = false;
        await db.SaveChangesAsync();

        var intelligence = new Mock<ILegendConnectTranslationIntelligence>(MockBehavior.Strict);
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: intelligence.Object);

        var result = await router.TranslateAsync("unsupported pair", "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("AzureTranslator", result.Provider);
        Assert.Equal(1, provider.TranslateCalls);
        intelligence.VerifyNoOtherCalls();
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(1, demand.AzureFallbackCount);
        Assert.Equal(1, (await db.LegendTranslationSystemUsages.SingleAsync()).ProviderOperationCount);
    }

    [Fact]
    public async Task SameLanguageBypass_RemainsOutsideDirectionalRoutingAndAccountQuota()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var entitlements = new TranslationEntitlementAuthority(
            db,
            Mock.Of<IControlledResourceAccessService>(),
            configuration,
            NullLogger<TranslationEntitlementAuthority>.Instance);
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            entitlements: entitlements);

        var result = await router.TranslateForAccountAsync(
            "unchanged",
            "en",
            "en-US",
            new MessagingActor("account", MessagingParticipantTypes.Client),
            TranslationUsageReference.ForMessage(Guid.NewGuid(), "en"));

        Assert.True(result.Succeeded);
        Assert.Equal("unchanged", result.TranslatedText);
        Assert.Equal("LegendConnectSameLanguage", result.Provider);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Empty(await db.LegendTranslationPairDemands.ToListAsync());
        Assert.Equal(1, (await db.LegendTranslationSystemUsages.SingleAsync()).SameLanguageBypassCount);
        var account = await db.LegendTranslationUsagePeriods.SingleAsync();
        Assert.Equal("unchanged".Length, account.SameLanguageCharactersAvoided);
        Assert.Equal(0, account.ConsumedCharacters);
    }

    [Fact]
    public async Task RolledBackPromotedTranslationModel_CannotServeAndFallsThroughToAzure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        var pair = await db.LegendLanguagePairs.SingleAsync();
        pair.ActiveModelVersion = "ft:legend:active";
        var run = new LegendConnectModelTrainingRun
        {
            Id = Guid.NewGuid(),
            RunKey = "serving-rollback",
            ScopeKey = "Global",
            Generation = 1,
            DatasetIdentity = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            DatasetEvaluatorVersion = 13,
            TrainingProvider = "OpenAI",
            BaseModel = "base",
            ChallengerModelVersion = "ft:legend:active",
            State = "TrainingCompleted",
            EvaluationState = "Passed",
            PromotionState = "Promoted",
            HeldOutScore = 1m,
            RegressionScore = 1m,
            FailureDetail = RuntimeProof,
            CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
            PromotedUtc = DateTime.UtcNow
        };
        var lineage = new LegendConnectModelPromotionPair
        {
            Id = Guid.NewGuid(),
            ModelTrainingRunId = run.Id,
            PairKey = pair.PairKey,
            PromotedModelVersion = run.ChallengerModelVersion!,
            PromotedUtc = run.PromotedUtc!.Value
        };
        db.AddRange(run, lineage);
        await db.SaveChangesAsync();

        var intelligence = EmptyIntelligence();
        var transport = new RecordingModelTransport("promoted result");
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            intelligence: intelligence.Object,
            activeModelInference: new LegendConnectActiveModelInference(db, transport));

        var promoted = await router.TranslateAsync("first", "ht", "en");
        Assert.Equal("LegendConnectPromotedTranslationModel", promoted.Provider);

        pair.ActiveModelVersion = null;
        lineage.RolledBackUtc = DateTime.UtcNow;
        run.PromotionState = "RolledBack";
        await db.SaveChangesAsync();

        var afterRollback = await router.TranslateAsync("second", "ht", "en");

        Assert.Equal("AzureTranslator", afterRollback.Provider);
        Assert.Equal(1, provider.TranslateCalls);
        Assert.Equal(1, transport.Calls);
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(1, demand.NeuralModelServeCount);
        Assert.Equal(0, demand.NeuralModelFailureCount);
        Assert.Equal(1, demand.AzureFallbackCount);
    }

    [Fact]
    public async Task ProviderFailure_AfterInternalMissesRemainsExplicitInBothRoutingAndProviderAccounting()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        var intelligence = EmptyIntelligence();
        var activeModel = new Mock<ILegendConnectActiveModelInference>();
        activeModel.Setup(item => item.TryTranslateAsync(
                "en", "ht", "provider failure", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegendConnectActiveModelInferenceResult(
                false,
                null,
                "ft:failed",
                "active_model_inference_failed"));
        var provider = new RecordingProvider(calls: null, succeeds: false);
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: intelligence.Object,
            activeModelInference: activeModel.Object);

        var result = await router.TranslateAsync("provider failure", "ht", "en");

        Assert.False(result.Succeeded);
        Assert.Equal("translation_provider_failed", result.ErrorCode);
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(1, demand.TranslationRequestCount);
        Assert.Equal(1, demand.NeuralModelFailureCount);
        Assert.Equal(1, demand.AzureFallbackCount);
        var system = await db.LegendTranslationSystemUsages.SingleAsync();
        Assert.Equal(1, system.ProviderOperationCount);
        Assert.Equal(1, system.ProviderFailureCount);
        Assert.Equal(0, system.ProviderBillableCharacters);
    }

    [Fact]
    public async Task Dashboard_ReconcilesEveryTerminalStageAndSeparatesProviderReuseFromNativeCoverage()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            TranslationRequestCount = 5,
            TranslationMemoryHitCount = 1,
            StructuralInternalServeCount = 1,
            ContextualInternalServeCount = 0,
            NeuralModelServeCount = 1,
            NeuralModelFailureCount = 1,
            ProviderObservationReuseCount = 1,
            AzureFallbackCount = 1
        });
        db.LegendTranslationSystemUsages.Add(new LegendTranslationSystemUsage
        {
            Id = Guid.NewGuid(),
            UsageDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ProviderOperationCount = 1,
            PromotedTranslationModelCharactersAvoided = 10,
            ProviderObservationCharactersAvoided = 20
        });
        await db.SaveChangesAsync();

        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration);
        var dashboard = await operations.GetDashboardAsync();

        Assert.Equal("translation", dashboard.TranslationServingCapability);
        Assert.Equal(5, dashboard.CrossLanguageTranslationRequestCount);
        Assert.Equal(3, dashboard.NativeTranslationIntelligenceServeCount);
        Assert.Equal(1, dashboard.ProviderObservationReuseCount);
        Assert.Equal(4, dashboard.ReconciledTerminalRouteCount - dashboard.AzureFallbackCount);
        Assert.Equal(5, dashboard.ReconciledTerminalRouteCount);
        Assert.Equal(0, dashboard.TranslationRoutingReconciliationGap);
        Assert.Equal(0.6m, dashboard.InternalCoverageRate);
        Assert.Equal(0.8m, dashboard.ProviderAvoidanceRate);
        Assert.Equal(0.2m, dashboard.AzureDependencyRate);
        Assert.Equal(1, dashboard.ProviderOperationCount);
        Assert.Equal(10, dashboard.PromotedTranslationModelCharactersAvoided);
        Assert.Equal(20, dashboard.ProviderObservationCharactersAvoided);
        Assert.Equal(1, dashboard.Pairs[0].ProviderObservationReuseCount);
        Assert.Equal(0, dashboard.Pairs[0].RoutingReconciliationGap);
    }

    private static LegendConnectTranslationRouter RouterWithRealIntelligence(
        MasterAppDbContext db,
        LegendLanguageRegistry registry,
        IConfiguration configuration,
        RecordingProvider provider) =>
        new(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance),
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));

    private static Mock<ILegendConnectTranslationIntelligence> EmptyIntelligence()
    {
        var intelligence = new Mock<ILegendConnectTranslationIntelligence>();
        intelligence.SetupGet(item => item.IsContextualCompositionActive).Returns(false);
        intelligence.Setup(item => item.TryGetTrustedExactMemoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendTranslationMemoryMatch?)null);
        intelligence.Setup(item => item.EvaluateContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendContextualTranslationSuggestion?)null);
        intelligence.Setup(item => item.TryGetReusableProviderObservationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendTranslationMemoryMatch?)null);
        return intelligence;
    }

    private static void AddAlignment(
        MasterAppDbContext db,
        string sourceText,
        string targetText,
        string provenance,
        string qualityState,
        decimal confidence)
    {
        var source = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            StoragePartition = "/en",
            NormalizedHash = LegendLanguageIdentity.TextHash(sourceText),
            Text = sourceText,
            Provenance = provenance,
            IsTrainingEligible = true
        };
        var target = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(),
            LanguageCode = "ht",
            StoragePartition = "/ht",
            NormalizedHash = LegendLanguageIdentity.TextHash(targetText),
            Text = targetText,
            Provenance = provenance,
            IsTrainingEligible = true
        };
        db.AddRange(source, target, new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = provenance,
            Provenance = provenance,
            QualityState = qualityState,
            Confidence = confidence,
            HumanVerified = false,
            UpdatedUtc = DateTime.UtcNow
        });
    }

    private static TranslationCapacityAuthority Capacity(
        MasterAppDbContext db,
        IConfiguration configuration) =>
        new(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance);

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "1000"
        }).Build();

    private sealed class RecordingProvider : ITranslationProvider
    {
        private readonly ICollection<string>? _calls;
        private readonly bool _succeeds;

        public RecordingProvider(
            ICollection<string>? calls = null,
            bool succeeds = true)
        {
            _calls = calls;
            _succeeds = succeeds;
        }

        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null)
        {
            TranslateCalls++;
            _calls?.Add("azure");
            return Task.FromResult(new TranslationProviderResult(
                _succeeds,
                _succeeds ? "azure result" : null,
                sourceLanguage,
                ProviderName,
                _succeeds ? null : "translation_provider_failed"));
        }
    }

    private sealed class RecordingModelTransport : ILegendConnectModelInferenceTransport
    {
        private readonly string _text;

        public RecordingModelTransport(string text) => _text = text;

        public int Calls { get; private set; }

        public Task<LegendModelEvaluationGenerationResult> GenerateAsync(
            string model,
            LegendModelTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LegendModelEvaluationGenerationResult(true, _text));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectFoundationTests
{
    [Fact]
    public async Task Registry_NormalizesRegionalAndDisplayInputs_AndKeepsDatasetBoundaries()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);

        Assert.Equal("en", await registry.NormalizeEnabledTranslationLanguageAsync("EN-us"));
        Assert.Equal("ht", await registry.NormalizeEnabledTranslationLanguageAsync("Haitian Creole"));

        var spanish = Assert.IsType<LegendLanguageDefinitionSnapshot>(
            await registry.GetLanguageAsync("es"));
        var french = Assert.IsType<LegendLanguageDefinitionSnapshot>(
            await registry.GetLanguageAsync("fr"));
        Assert.Equal("/es", spanish.StoragePartition);
        Assert.Equal("/fr", french.StoragePartition);
        Assert.NotEqual(spanish.StoragePartition, french.StoragePartition);

        var enToHt = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("en", "ht"));
        var htToEn = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("ht", "en"));
        Assert.Equal("en:ht", enToHt.PairKey);
        Assert.Equal("ht:en", htToEn.PairKey);
        Assert.NotEqual(enToHt.TranslationMemoryPartition, htToEn.TranslationMemoryPartition);
    }

    [Fact]
    public async Task Registry_RejectsDisabledLanguageWithoutLanguageSpecificServiceCode()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        _ = await registry.ListEnabledTranslationLanguagesAsync();
        var spanish = await db.LegendLanguageDefinitions.SingleAsync(item => item.LanguageCode == "es");
        spanish.IsTranslationEnabled = false;
        await db.SaveChangesAsync();

        Assert.Null(await registry.NormalizeEnabledTranslationLanguageAsync("Spanish"));
        Assert.NotNull(await registry.NormalizeEnabledTranslationLanguageAsync("French"));
    }

    [Fact]
    public async Task Router_SameLanguageBypass_DoesNotInvokeAzureOrReserveCapacity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, Configuration(), NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance);

        var result = await router.TranslateAsync("Hello", "en", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("Hello", result.TranslatedText);
        Assert.Equal("LegendConnectSameLanguage", result.Provider);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Empty(await db.LegendTranslationProviderCapacities.ToListAsync());
    }

    [Fact]
    public async Task Router_RecordsPrivacySafeDirectionalDemandWithoutMessageText()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, Configuration(), NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance));

        var result = await router.TranslateAsync("Hello", "ht", "en");

        Assert.True(result.Succeeded);
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal("en:ht", demand.PairKey);
        Assert.Equal(1, demand.TranslationRequestCount);
        Assert.Equal(5, demand.ProviderCharacterCount);
        Assert.DoesNotContain("Hello", demand.PairKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "ht")]
    [InlineData("es", "fr")]
    public async Task Router_NativeOnlyMiss_DoesNotRecordExternalDemandOrReserveCapacity(
        string sourceLanguage,
        string targetLanguage)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        Assert.NotNull(await registry.GetOrCreateEnabledPairAsync(sourceLanguage, targetLanguage));
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, Configuration(), NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            intelligence: new LegendConnectTranslationIntelligence(db, Configuration()));

        var result = await router.TranslateAsync(
            "Unretained translation content",
            targetLanguage,
            sourceLanguage,
            CancellationToken.None,
            LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.False(result.Succeeded);
        Assert.Equal("None", result.Provider);
        Assert.Equal("external_provider_forbidden_by_native_only_policy", result.ErrorCode);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Null(provider.LastProviderPolicy);
        Assert.Empty(await db.LegendTranslationPairDemands.ToListAsync());
        Assert.Empty(await db.LegendTranslationProviderCapacities.ToListAsync());
        Assert.Empty(await db.LegendTranslationProviderReservations.ToListAsync());
    }

    [Fact]
    public async Task Router_ExternalFallback_PreservesRequestPolicyAtProviderBoundary()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            CreateRegistry(db),
            new TranslationCapacityAuthority(db, Configuration(), NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance));
        var policy = new LegendConnectExternalProviderPolicy(AllowExternalProviders: true);

        var result = await router.TranslateAsync(
            "Translation content",
            "ht",
            "en",
            CancellationToken.None,
            policy);

        Assert.True(result.Succeeded);
        Assert.Equal(provider.ProviderName, result.Provider);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1, provider.TranslateCalls);
        Assert.Same(policy, provider.LastProviderPolicy);
        var demand = await db.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(1, demand.AzureFallbackCount);
        Assert.Equal("Translation content".Length, demand.ProviderCharacterCount);
    }

    [Theory]
    [InlineData(true, "en", false)]
    [InlineData(false, "en", true)]
    [InlineData(false, "fr", false)]
    public async Task Router_LanguageDetection_ReadsPartialRegistryWithoutProvisioning(
        bool nativeOnly,
        string detectedLanguage,
        bool expectedSuccess)
    {
        await using var db = ControllerTestHelpers.BuildDb();
        ControllerTestHelpers.SeedGovernedLanguageBaseline(db, "en");
        var saveAttempts = 0;
        db.SavingChanges += (_, _) =>
        {
            saveAttempts++;
            throw new InvalidOperationException("Language detection cannot provision registry rows.");
        };
        var registry = CreateRegistry(db);
        var provider = new RecordingProvider { DetectedLanguage = detectedLanguage };
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, Configuration(), NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            structuralComposition: new LegendConnectCurriculumService(db, registry, corpus));

        var result = await router.DetectLanguageAsync(
            "Unretained source content",
            CancellationToken.None,
            nativeOnly
                ? LegendConnectExternalProviderPolicy.NativeOnly
                : LegendConnectExternalProviderPolicy.ProviderEnabled);

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(expectedSuccess ? "en" : null, result.Language);
        Assert.Equal(
            nativeOnly
                ? "native_only_governed_source_language_undetermined"
                : expectedSuccess ? null : "translation_language_unsupported",
            result.ErrorCode);
        Assert.Equal(nativeOnly ? 0 : 1, provider.DetectionCalls);
        Assert.Equal(0, saveAttempts);
        Assert.Equal("en", (await db.LegendLanguageDefinitions.SingleAsync()).LanguageCode);
        Assert.Empty(await db.LegendTranslationProviderCapacities.ToListAsync());
    }

    [Fact]
    public async Task Capacity_LiveReservationHasPriorityOverBootstrapReserve()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "20",
                ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "10"
            })
            .Build();
        var capacity = new TranslationCapacityAuthority(
            db,
            configuration,
            NullLogger<TranslationCapacityAuthority>.Instance);

        var live = Assert.IsType<TranslationCapacityReservation>(
            await capacity.TryReserveAsync("AzureTranslator", 15, TranslationCapacityPurpose.Live));
        Assert.Null(await capacity.TryReserveAsync("AzureTranslator", 1, TranslationCapacityPurpose.Bootstrap));
        await capacity.CompleteAsync(live, providerMayHaveConsumed: true);

        var ledger = await db.LegendTranslationProviderCapacities.SingleAsync();
        Assert.Equal(15, ledger.LiveCharactersConsumed);
        Assert.Equal(0, ledger.ReservedLiveCharacters);
    }

    [Fact]
    public async Task PrivateMessageLearning_IsRecordedWithoutRetainingMessageText()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        var publisher = new LegendTranslationLearningPublisher(
            db,
            registry,
            NullLogger<LegendTranslationLearningPublisher>.Instance);

        await publisher.TryPublishAsync(new TranslationLearningCandidate(
            Guid.NewGuid(),
            "en",
            "ht",
            "Private source message",
            "Private translated message",
            "AzureTranslator"));

        var item = await db.LegendTranslationLearningEvents.SingleAsync();
        Assert.Equal("IneligiblePrivateMessage", item.EligibilityState);
        Assert.Equal("Skipped", item.ProcessingState);
        Assert.Null(item.SourceText);
        Assert.Null(item.TargetText);
        Assert.NotEmpty(item.SourceTextHash);
        Assert.NotEmpty(item.TargetTextHash);
    }

    [Fact]
    public async Task EligibleEvent_DeduplicatesTextUnitsAndDirectionalAlignment()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = CreateRegistry(db);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("en", "ht"));
        var eventOne = EligibleEvent(pair, "Hello friend", "Bonjou zanmi", "first");
        var eventTwo = EligibleEvent(pair, "Hello friend", "Bonjou zanmi", "retry");
        db.AddRange(eventOne, eventTwo);
        await db.SaveChangesAsync();
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);

        await corpus.ProcessAsync(eventOne);
        await corpus.ProcessAsync(eventTwo);

        Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync());
        var alignment = await db.LegendTranslationAlignments.SingleAsync();
        Assert.Equal("en:ht", alignment.PairKey);
        Assert.Equal(2, alignment.ObservationCount);
        Assert.Equal(2, await db.LegendTranslationLearningEvents.CountAsync(item => item.ProcessingState == "Processed"));
        Assert.True(
            LegendCorpusCandidateScoring.Score(
                new LegendCorpusCandidate { Priority = 1 }, pairDemand: 10, pairCoverage: 0) >
            LegendCorpusCandidateScoring.Score(
                new LegendCorpusCandidate { Priority = 0 }, pairDemand: 0, pairCoverage: 100_000));
    }

    private static LegendTranslationLearningEvent EligibleEvent(
        LegendLanguagePairSnapshot pair,
        string source,
        string target,
        string suffix) => new()
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = "test:" + suffix,
        SourceLanguageCode = pair.SourceLanguageCode,
        TargetLanguageCode = pair.TargetLanguageCode,
        PairKey = pair.PairKey,
        SourceTextHash = LegendLanguageIdentity.TextHash(source),
        TargetTextHash = LegendLanguageIdentity.TextHash(target),
        SourceText = source,
        TargetText = target,
        Provider = "AzureTranslator",
        Provenance = "ApprovedTestCorpus",
        EligibilityState = "Eligible",
        ProcessingState = "Pending",
        CreatedUtc = DateTime.UtcNow
    };

    private static LegendLanguageRegistry CreateRegistry(Infrastructure.Data.MasterAppDbContext db) =>
        new(db, Configuration());

    private static IConfiguration Configuration() => new ConfigurationBuilder().Build();

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public string DetectedLanguage { get; init; } = "en";
        public int DetectionCalls { get; private set; }
        public int TranslateCalls { get; private set; }
        public LegendConnectExternalProviderPolicy? LastProviderPolicy { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        {
            DetectionCalls++;
            return Task.FromResult(new TranslationDetectionResult(true, DetectedLanguage));
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(true, text, sourceLanguage, ProviderName));
        }

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage,
            CancellationToken cancellationToken,
            LegendConnectExternalProviderPolicy? providerPolicy)
        {
            LastProviderPolicy = providerPolicy;
            return TranslateAsync(text, targetLanguage, sourceLanguage, cancellationToken);
        }
    }
}

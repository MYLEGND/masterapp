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
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default,
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
            return Task.FromResult(new TranslationProviderResult(true, text, sourceLanguage, ProviderName));
        }
    }
}

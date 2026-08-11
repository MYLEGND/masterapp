using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Multi-context checks for the aggregate-only Legend Connect telemetry
/// authorities. These use the relational execution path, not the in-memory
/// test-provider fallback.
/// </summary>
public sealed class LegendConnectSystemHardeningTests
{
    [Fact]
    public async Task ConcurrentTelemetryWrites_AccumulateEveryDeltaThroughTheCanonicalRows()
    {
        var databaseName = "legend-connect-telemetry-" + Guid.NewGuid().ToString("N");
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared;Default Timeout=30";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var setup = new MasterAppDbContext(options))
            await setup.Database.EnsureCreatedAsync();

        await using var first = new MasterAppDbContext(options);
        await using var second = new MasterAppDbContext(options);
        var firstDemand = new TranslationDemandRecorder(first, NullLogger<TranslationDemandRecorder>.Instance);
        var secondDemand = new TranslationDemandRecorder(second, NullLogger<TranslationDemandRecorder>.Instance);
        var firstUsage = new TranslationSystemUsageRecorder(first, NullLogger<TranslationSystemUsageRecorder>.Instance);
        var secondUsage = new TranslationSystemUsageRecorder(second, NullLogger<TranslationSystemUsageRecorder>.Instance);

        await Task.WhenAll(
            firstDemand.TryRecordAsync("en:ht", 0, translationMemoryHit: true),
            secondDemand.TryRecordAsync("en:ht", 0, azureFallback: true));
        await Task.WhenAll(
            firstUsage.TryRecordSameLanguageBypassAsync(5),
            secondUsage.TryRecordAsync(new TranslationSystemUsageDelta(QuotaDeniedRequests: 1)));

        await using var verification = new MasterAppDbContext(options);
        var demand = await verification.LegendTranslationPairDemands.SingleAsync();
        Assert.Equal(2, demand.TranslationRequestCount);
        Assert.Equal(1, demand.TranslationMemoryHitCount);
        Assert.Equal(1, demand.AzureFallbackCount);

        var usage = await verification.LegendTranslationSystemUsages.SingleAsync();
        Assert.Equal(1, usage.SameLanguageBypassCount);
        Assert.Equal(5, usage.SameLanguageCharactersAvoided);
        Assert.Equal(1, usage.QuotaDeniedRequestCount);
    }

    [Fact]
    public async Task AutonomousProviderFailure_UsesTheExistingLeaseClaimForBoundedRecovery()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "1000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "100"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        db.LegendCorpusCandidates.Add(new LegendCorpusCandidate
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "provider-failure-retry",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ht",
            SourceText = "Retry only after the existing lease expires.",
            SourceTextHash = LegendLanguageIdentity.TextHash("Retry only after the existing lease expires."),
            Category = "ApprovedTestCorpus",
            Provenance = "ApprovedTestCorpus",
            IsApproved = true,
            ProcessingState = "Pending",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new FailingProvider();
        var service = new LegendConnectAutonomousLearningService(
            db,
            registry,
            provider,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance),
            new LegendConnectAutonomousGapPlanner(db, registry),
            configuration);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        var candidate = await db.LegendCorpusCandidates.SingleAsync();
        Assert.Equal(1, provider.TranslateCalls);
        Assert.Equal("Processing", candidate.ProcessingState);
        Assert.Equal("translation_provider_failed", candidate.FailureCode);
        Assert.Equal(1, candidate.AttemptCount);
        Assert.True(candidate.LeaseExpiresUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task ExpiredProviderCapacityLease_IsReleasedExactlyOnceBeforeTheCanonicalRetry()
    {
        var databaseName = "legend-connect-capacity-" + Guid.NewGuid().ToString("N");
        var connectionString = $"Data Source=file:{databaseName}?mode=memory&cache=shared;Default Timeout=30";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var setup = new MasterAppDbContext(options))
            await setup.Database.EnsureCreatedAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "10",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:Providers:AzureTranslator:ReservationLeaseSeconds"] = "15"
        }).Build();
        await using var first = new MasterAppDbContext(options);
        var firstAuthority = new TranslationCapacityAuthority(
            first,
            configuration,
            NullLogger<TranslationCapacityAuthority>.Instance);
        var abandoned = Assert.IsType<TranslationCapacityReservation>(
            await firstAuthority.TryReserveAsync(
                "AzureTranslator",
                10,
                TranslationCapacityPurpose.Live,
                reservationReference: "message:capacity-recovery"));

        await using (var expire = new MasterAppDbContext(options))
        {
            var durable = await expire.LegendTranslationProviderReservations.SingleAsync();
            durable.ReservationExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
            await expire.SaveChangesAsync();
        }

        await using var retry = new MasterAppDbContext(options);
        var retryAuthority = new TranslationCapacityAuthority(
            retry,
            configuration,
            NullLogger<TranslationCapacityAuthority>.Instance);
        var recovered = Assert.IsType<TranslationCapacityReservation>(
            await retryAuthority.TryReserveAsync(
                "AzureTranslator",
                6,
                TranslationCapacityPurpose.Live,
                reservationReference: "message:capacity-recovery"));

        Assert.Equal(abandoned.ReservationId, recovered.ReservationId);
        await retryAuthority.CompleteAsync(recovered, providerSucceeded: false);

        await using var verification = new MasterAppDbContext(options);
        var ledger = await verification.LegendTranslationProviderCapacities.SingleAsync();
        var reservation = await verification.LegendTranslationProviderReservations.SingleAsync();
        Assert.Equal(0, ledger.ReservedLiveCharacters);
        Assert.Equal(0, ledger.LiveCharactersConsumed);
        Assert.Equal("Released", reservation.State);
        Assert.Equal(6, reservation.Characters);
    }

    [Fact]
    public async Task ProductionEligibleContext_ServesInternallyWithoutQuotaCapacityOrProviderWork()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:ContextualComposition:Mode"] = "Active",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        var sourceText = "A verified contextual phrase";
        var source = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(), LanguageCode = "en", StoragePartition = "/en",
            NormalizedHash = LegendLanguageIdentity.TextHash(sourceText), Text = sourceText,
            Provenance = "FounderApproved", IsTrainingEligible = true
        };
        var target = new LegendLanguageTextUnit
        {
            Id = Guid.NewGuid(), LanguageCode = "ht", StoragePartition = "/ht",
            NormalizedHash = LegendLanguageIdentity.TextHash("Yon fraz kontèks verifye"), Text = "Yon fraz kontèks verifye",
            Provenance = "FounderApproved", IsTrainingEligible = true
        };
        db.AddRange(source, target);
        db.LegendLanguageContextRelationships.Add(new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(), PairKey = "en:ht", SourceTextUnitId = source.Id, RelatedTextUnitId = target.Id,
            RelationshipKind = "ContextualExample", ContextSignature = "approved||",
            SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(sourceText),
            Confidence = 0.99m, QualityState = "Verified", Provenance = "FounderApproved", ObservationCount = 1
        });
        await db.SaveChangesAsync();

        var provider = new FailingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));

        var result = await router.TranslateAsync(sourceText, "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("LegendConnectContextualComposition", result.Provider);
        Assert.Equal("Yon fraz kontèks verifye", result.TranslatedText);
        Assert.Equal(0, provider.TranslateCalls);
        Assert.Empty(await db.LegendTranslationProviderCapacities.ToListAsync());
    }

    private sealed class FailingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(
                false,
                null,
                sourceLanguage,
                ProviderName,
                "translation_provider_failed"));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectDashboardCountersTests
{
    [Fact]
    public async Task ScalarProjectionMatchesFullDashboardWithoutLoadingCorpusRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new Commands();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseSqlite(connection).AddInterceptors(commands).Options);
        await db.Database.EnsureCreatedAsync();
        var operations = Operations(db);
        var source = new LegendLanguageTextUnit { LanguageCode = "en", Text = "Hello world", NormalizedHash = "SOURCE", IsTrainingEligible = true, Provenance = "FounderApproved" };
        var target = new LegendLanguageTextUnit { LanguageCode = "fr", Text = "Bonjour", NormalizedHash = "TARGET", IsTrainingEligible = true };
        var retired = new LegendLanguageTextUnit { LanguageCode = "en", Text = "retired", NormalizedHash = "RETIRED", IsTrainingEligible = false };
        db.AddRange(source, target, retired);
        db.AddRange(new LegendLanguageDefinition { LanguageCode = "en", CanonicalName = "English", StoragePartition = "/en", IsEnabled = true },
            new LegendLanguageDefinition { LanguageCode = "fr", CanonicalName = "French", StoragePartition = "/fr", IsEnabled = true },
            new LegendLanguagePair { PairKey = "en:fr", SourceLanguageCode = "en", TargetLanguageCode = "fr", IsEnabled = true });
        db.AddRange(new LegendTranslationAlignment { PairKey = "en:fr", SourceTextUnitId = source.Id, TargetTextUnitId = target.Id },
            new LegendTranslationAlignment { PairKey = "en:fr", SourceTextUnitId = retired.Id, TargetTextUnitId = target.Id });
        db.AddRange(
            new LegendTranslationLearningEvent { IdempotencyKey = "pending", SourceLanguageCode = "en", SourceTextHash = "SOURCE", TargetLanguageCode = "fr", TargetTextHash = "missing", EligibilityState = "Eligible", ProcessingState = "Pending", FailureCode = "retry", Provenance = "ConsentedLiveTranslation" },
            new LegendTranslationLearningEvent { IdempotencyKey = "retired", SourceLanguageCode = "en", SourceTextHash = "RETIRED", EligibilityState = "Eligible", ProcessingState = "Pending", FailureCode = "retired" },
            new LegendTranslationLearningEvent { IdempotencyKey = "privacy", EligibilityState = "NotEligible", ProcessingState = "Processed", FailureCode = "privacy", Provenance = "ConsentedLiveTranslation", PromotionOutcome = "Reused" });
        db.AddRange(
            new LegendCorpusCandidate { IdempotencyKey = "valid", SourceLanguageCode = "en", TargetLanguageCode = "fr", SourceTextHash = "SOURCE", SourceText = "Hello\tworld", IsApproved = true, FailureCode = "retry" },
            new LegendCorpusCandidate { IdempotencyKey = "mismatch", SourceLanguageCode = "en", TargetLanguageCode = "fr", SourceTextHash = "SOURCE", SourceText = "Different text", IsApproved = true, FailureCode = "invalid" });
        db.Add(new LegendTranslationPairDemand { PairKey = "en:fr", TranslationRequestCount = 19, TranslationMemoryHitCount = 3, StructuralInternalServeCount = 2, ContextualInternalServeCount = 1, NeuralModelServeCount = 2, NeuralModelFailureCount = 1, ProviderObservationReuseCount = 4, AzureFallbackCount = 7 });
        db.Add(new LegendTranslationSystemUsage { UsageDate = DateOnly.FromDateTime(DateTime.UtcNow), SameLanguageBypassCount = 7, ProviderOperationCount = 11, ProviderBillableCharacters = 900, StructuralCompositionCharactersAvoided = 40 });
        await db.SaveChangesAsync();
        var legacy = LegendConnectDashboardCounters.FromDashboard(await operations.GetDashboardAsync());
        commands.Sql.Clear();
        var counters = await operations.GetDashboardCountersAsync();
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(counters));
        Assert.Equal(1, counters.ActiveDirectionalAtomicAlignmentCount);
        Assert.Equal(3, counters.FailedLearningJobCount);
        Assert.Equal(1, counters.LearningJobCount);
        Assert.Equal(0, counters.TranslationRoutingReconciliationGap);
        Assert.DoesNotContain(commands.Sql, sql => sql.Contains("LegendLanguageContextRelationships", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Sql, sql => sql.Contains("\"TargetText\"", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Sql, sql => sql.Contains("\"IdempotencyKey\"", StringComparison.Ordinal));
        Assert.DoesNotContain(commands.Sql, sql => sql.Contains("\"TranslationContext\"", StringComparison.Ordinal));
        Assert.True(commands.Sql.Count < 30, $"Expected scalar queries, observed {commands.Sql.Count} commands.");
    }

    [Fact]
    public async Task EmptyProjectionKeepsUnknownProviderCapacityAndNativeOnlyRegistryBoundary()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        registry.Setup(item => item.ListEnabledTranslationLanguagesReadOnlyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LegendLanguageDefinitionSnapshot>());
        var operations = Operations(db, registry.Object);
        var counters = await operations.GetDashboardCountersAsync(providerPolicy: LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.Null(counters.ProviderCapacity);
        Assert.Equal(0, counters.ReconciledTerminalRouteCount);
        Assert.Equal(0m, counters.InternalCoverageRate);
        registry.VerifyAll();
    }

    [Fact]
    public async Task AmbiguousNormalizedSourceIdentityFailsClosedLikeTheOriginalDashboard()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        db.AddRange(
            new LegendLanguageTextUnit { LanguageCode = "en", NormalizedHash = "ABC", Text = "one", IsTrainingEligible = true },
            new LegendLanguageTextUnit { LanguageCode = " EN ", NormalizedHash = "abc", Text = "one", IsTrainingEligible = true },
            new LegendCorpusCandidate { IdempotencyKey = "failure", SourceLanguageCode = "en", SourceTextHash = "ABC", SourceText = "one", FailureCode = "retry" });
        await db.SaveChangesAsync();
        var operations = Operations(db);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.GetDashboardAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.GetDashboardCountersAsync());
    }

    [Fact]
    public async Task CancelledCounterReadDoesNotReturnAnEmptySuccess()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations(db).GetDashboardCountersAsync(cancellation.Token));
    }

    private static LegendConnectOperations Operations(MasterAppDbContext db, ILegendLanguageRegistry? registry = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        if (registry is null)
        {
            var mock = new Mock<ILegendLanguageRegistry>();
            mock.Setup(item => item.ListEnabledTranslationLanguagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<LegendLanguageDefinitionSnapshot>());
            registry = mock.Object;
        }
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectOperations(db, registry, corpus, configuration);
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

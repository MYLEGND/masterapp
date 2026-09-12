using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AzureTranslatorSubscriptionCapacityTests
{
    [Fact]
    public async Task PaidTier_ObservesMonitorWithoutAddingProviderTotalsToLedger()
    {
        var handler = new JsonHandler("""{"name":"translator","sku":{"name":"S1"}}""",
            """{"value":[{"name":{"value":"TextCharactersTranslated"},"timeseries":[{"data":[{"total":4500}]}]}]}""");
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(factory.Object, Configuration(),
            NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance, new StaticTokenCredential());
        await using var db = ControllerTestHelpers.BuildDb();
        var authority = new TranslationCapacityAuthority(db, Configuration(),
            NullLogger<TranslationCapacityAuthority>.Instance, azureSubscriptionCapacity: source);
        var reservation = await authority.TryReserveAsync("AzureTranslator", 12, TranslationCapacityPurpose.Live, "monitor-ledger");
        Assert.NotNull(reservation);
        await authority.CompleteAsync(reservation!, providerMayHaveConsumed: true);
        var snapshot = await authority.GetSnapshotAsync("AzureTranslator");
        Assert.Equal(4500L, snapshot.MonthlyAzureReportedCharacters);
        Assert.Equal(12L, snapshot.MonthlyCharactersConsumed);
        Assert.False(snapshot.IsAzureUsageVerified);
        Assert.True(snapshot.RemainingIsEstimate);
        Assert.NotNull(snapshot.AzureUsageRetrievedUtc);
        Assert.Equal(2, handler.SendAttempts);
    }

    [Theory]
    [InlineData("{\"value\":[]}", null)]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"TextCharactersTranslated\"},\"timeseries\":[{\"data\":[{\"total\":null}]}]}]}", null)]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"TextCharactersTranslated\"},\"timeseries\":[{\"data\":[{\"total\":0}]}]}]}", 0L)]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"TextCharactersTranslated\"},\"timeseries\":[{\"data\":[{\"total\":12},{\"total\":30}]}]}]}", 42L)]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"TextCharactersTranslated\"},\"timeseries\":[{\"data\":[{\"total\":12},{\"total\":null}]}]}]}", null)]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"TextCharactersTranslated\"},\"timeseries\":[{\"data\":[{\"total\":12}]},{}]}]}", null)]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"TextCharactersTranslated\"},\"timeseries\":[{\"data\":[{\"total\":12},{\"total\":-1}]}]}]}", null)]
    public void MonitorUsage_DistinguishesMissingObservationsFromMeasuredZero(string json, long? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AzureTranslatorSubscriptionCapacitySource.ReadMonthlyTranslatedCharacters(document.RootElement));
    }

    [Fact]
    public async Task Snapshot_ExcludesFutureDatedConsumptionAndMarksLedgerAsEstimate()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.LegendTranslationProviderReservations.Add(new LegendTranslationProviderReservation
        {
            Id = Guid.NewGuid(), Provider = "AzureTranslator", ReservationReference = "future-consumption",
            BillingPeriodStart = new DateOnly(now.Year, now.Month, 1),
            Purpose = TranslationCapacityPurpose.Live.ToString(), Characters = 100,
            State = "Completed", CreatedUtc = now.AddHours(1), CompletedUtc = now.AddHours(1),
            ReservationExpiresUtc = now.AddHours(2)
        });
        await db.SaveChangesAsync();
        var authority = new TranslationCapacityAuthority(db, Configuration(),
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(Available("F0", 2_000_000)));
        var snapshot = await authority.GetSnapshotAsync("AzureTranslator");
        Assert.Equal(0, snapshot.MonthlyCharactersConsumed);
        Assert.Equal(0, snapshot.HourlyCharactersConsumed);
        Assert.True(snapshot.RemainingIsEstimate);
        Assert.False(snapshot.IsAzureUsageVerified);
        Assert.Null(snapshot.MonthlyAzureReportedCharacters);
        Assert.True(snapshot.UsageRefreshedUtc >= now);
    }

    [Fact]
    public async Task NativeCapacity_ColdFreshAndExpiredCacheNeverRefreshesOrReplacesProviderState()
    {
        var clock = new CapacityTimeProvider();
        var credential = new StaticTokenCredential();
        var handler = new JsonHandler("""{"name":"translator","sku":{"name":"F0"}}""");
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object, Configuration(), NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance,
            credential, timeProvider: clock);

        var cold = await source.GetCurrentAsync(CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.False(cold.IsAvailable);
        Assert.Contains("native_only_capacity_refresh_forbidden", cold.Detail, StringComparison.Ordinal);
        Assert.Equal(0, credential.TokenRequests);
        Assert.Equal(0, handler.SendAttempts);
        factory.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Never);

        var synchronized = await source.GetCurrentAsync();
        Assert.True(synchronized.IsAvailable);
        Assert.Equal(1, credential.TokenRequests);
        Assert.Equal(1, handler.SendAttempts);
        await using var db = ControllerTestHelpers.BuildDb();
        var authority = new TranslationCapacityAuthority(db, Configuration(),
            NullLogger<TranslationCapacityAuthority>.Instance, azureSubscriptionCapacity: source);
        var available = await authority.GetSnapshotAsync("AzureTranslator", CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.Equal(2_000_000, available.HourlyRemainingCharacters);
        Assert.True(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            CapacityBindingRequest("hourlyRemainingCharacters"),
            JsonSerializer.Serialize(available, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            DateTime.UtcNow, out var availableReceipt, out _));
        Assert.Equal("2000000", availableReceipt!.SemanticValue);
        var local = await source.GetCurrentAsync(CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.True(local.IsAvailable);
        Assert.Equal("Cached", local.Status);
        Assert.Equal(synchronized.RefreshedUtc, local.RefreshedUtc);
        Assert.Equal(synchronized.HourlyCharacterLimit, local.HourlyCharacterLimit);
        Assert.Same(synchronized, await source.GetCurrentAsync());
        Assert.Equal(1, credential.TokenRequests);
        Assert.Equal(1, handler.SendAttempts);
        factory.Verify(item => item.CreateClient("AzureResourceManager"), Times.Once);

        clock.UtcNow = clock.UtcNow.AddMinutes(3);
        var expired = await source.GetCurrentAsync(CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);
        Assert.False(expired.IsAvailable);
        Assert.Null(expired.HourlyCharacterLimit);
        Assert.Equal(1, credential.TokenRequests);
        Assert.Equal(1, handler.SendAttempts);
        factory.Verify(item => item.CreateClient("AzureResourceManager"), Times.Once);

        var refreshed = await source.GetCurrentAsync();
        Assert.True(refreshed.IsAvailable);
        Assert.Equal("Synchronized", refreshed.Status);
        Assert.Equal(clock.UtcNow.UtcDateTime, refreshed.RefreshedUtc);
        Assert.Equal(2, credential.TokenRequests);
        Assert.Equal(2, handler.SendAttempts);
        factory.Verify(item => item.CreateClient("AzureResourceManager"), Times.Exactly(2));
    }

    [Fact]
    public async Task NativeCapacity_DoesNotWaitForProviderRefreshAndHonorsCancellation()
    {
        var credential = new StaticTokenCredential();
        var handler = new HeldCapacityHandler();
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object, Configuration(), NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance,
            credential, refreshTimeout: TimeSpan.FromSeconds(30));
        var providerRefresh = source.GetCurrentAsync();
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var local = await source.GetCurrentAsync(CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(local.IsAvailable);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                source.GetCurrentAsync(canceled.Token, LegendConnectExternalProviderPolicy.NativeOnly));
            Assert.Equal(1, credential.TokenRequests);
            Assert.Equal(1, handler.SendAttempts);
            factory.Verify(item => item.CreateClient("AzureResourceManager"), Times.Once);
        }
        finally
        {
            handler.Release.TrySetResult();
        }
        Assert.True((await providerRefresh.WaitAsync(TimeSpan.FromSeconds(5))).IsAvailable);
    }

    [Fact]
    public async Task NativeReadinessAndCapacity_ColdRegistryAndCacheAttemptNoWritesOrExternalCalls()
    {
        var sentinel = new NoWriteCapacitySentinel();
        await using var db = new MasterAppDbContext(new DbContextOptionsBuilder<MasterAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(sentinel).Options);
        var credential = new StaticTokenCredential();
        var handler = new JsonHandler("""{"name":"translator","sku":{"name":"F0"}}""");
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var configuration = Configuration();
        configuration["LegendConnect:ContextualComposition:Mode"] = "Shadow";
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object, configuration, NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance, credential);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, Mock.Of<IControlledResourceAccessService>(), new LegendLanguageRegistry(db, configuration),
            configuration, NullLogger<LegendConnectRuntimePolicyAuthority>.Instance, source);
        var capacity = new TranslationCapacityAuthority(
            db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, runtime, source);

        var readiness = await runtime.GetReadinessAsync(CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);
        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator", CancellationToken.None, LegendConnectExternalProviderPolicy.NativeOnly);

        Assert.Equal("BLOCKED", Assert.Single(readiness.Checks, item => item.Name == "Language Registry").State);
        Assert.False(snapshot.IsSynchronized);
        Assert.Null(snapshot.HourlyCharacterLimit);
        Assert.Null(snapshot.HourlyRemainingCharacters);
        Assert.Null(snapshot.SafeAcquisitionCharacters);
        Assert.Contains("native_only_capacity_refresh_forbidden", snapshot.Detail, StringComparison.Ordinal);
        Assert.Equal(0, sentinel.Attempts);
        Assert.Equal(0, credential.TokenRequests);
        Assert.Equal(0, handler.SendAttempts);
        factory.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Never);
        Assert.Empty(await db.Set<LegendLanguageDefinition>().ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries());

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var directOutput = JsonSerializer.Serialize(snapshot, jsonOptions);
        var nestedOutput = JsonSerializer.Serialize(new
        {
            providerCapacity = snapshot,
            stages = new[] { new { name = "provider_capacity", state = "available" } }
        }, jsonOptions);
        foreach (var path in new[] { "hourlyRemainingCharacters", "safeAcquisitionCharacters" })
        {
            var request = CapacityBindingRequest(path);
            Assert.False(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
                request, directOutput, DateTime.UtcNow, out _, out _));
            Assert.False(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
                request with { ToolName = "legend_operational_diagnostics", ValuePath = "providerCapacity." + path },
                nestedOutput, DateTime.UtcNow, out _, out _));
        }
        Assert.True(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            CapacityBindingRequest("status"), directOutput, DateTime.UtcNow, out var statusReceipt, out _));
        Assert.Equal("Unavailable", statusReceipt!.SemanticValue);
        Assert.True(LegendFounderToolAuthority.TryCreateReadOnlyContentBindingReceipt(
            CapacityBindingRequest("monthlyCharactersConsumed"), directOutput, DateTime.UtcNow, out var usageReceipt, out _));
        Assert.Equal("0", usageReceipt!.SemanticValue);
    }

    private static LegendConnectReadOnlyContentBindingRequest CapacityBindingRequest(string path) =>
        new("capacity-request", "capacity-transition", "capacity-result", "legend_provider_capacity",
            "{}", path, null, 60, "$capacity", "capacity");

    [Theory]
    [InlineData("F0", 2_000_000)]
    [InlineData("S1", 40_000_000)]
    [InlineData("S3", 120_000_000)]
    public async Task AzureResourceSku_DerivesTheCurrentTranslatorCapacityContract(string sku, long expectedLimit)
    {
        var handler = new JsonHandler($"{{\"name\":\"masterapp-translator-1221\",\"sku\":{{\"name\":\"{sku}\"}}}}");
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object,
            Configuration(),
            NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance,
            new StaticTokenCredential());

        var capacity = await source.GetCurrentAsync();

        Assert.True(capacity.IsAvailable);
        Assert.Equal("Synchronized", capacity.Status);
        Assert.Equal(sku, capacity.Tier);
        Assert.Equal(expectedLimit, capacity.HourlyCharacterLimit);
        Assert.Equal(sku == "F0" ? expectedLimit : null, capacity.MonthlyIncludedCharacterAllowance);
        Assert.Equal(expectedLimit / 20, capacity.HourlyLiveReserveCharacters);
        Assert.Equal(expectedLimit - expectedLimit / 20, capacity.MaximumSafeHourlyCorpusCharacters);
        Assert.Equal(sku == "F0" ? expectedLimit / 20 : null, capacity.MonthlyLiveReserveCharacters);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Contains(sku == "F0" ? "api-version=2024-10-01" : "api-version=2023-10-01", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(sku == "F0" ? 1 : 2, handler.SendAttempts);
        Assert.Null(capacity.MonthlyAzureReportedCharacters);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
    }

    [Fact]
    public async Task UnknownAzureTier_FailsClosedInsteadOfInventingACapacity()
    {
        var handler = new JsonHandler("{\"name\":\"masterapp-translator-1221\",\"sku\":{\"name\":\"S9\"}}");
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object,
            Configuration(),
            NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance,
            new StaticTokenCredential());

        var capacity = await source.GetCurrentAsync();

        Assert.False(capacity.IsAvailable);
        Assert.Null(capacity.HourlyCharacterLimit);
        Assert.Contains("S9", capacity.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzureLookupTimeout_FailsClosedWithoutBlockingTheCaller()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object,
            Configuration(),
            NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance,
            new StaticTokenCredential());

        var capacity = await source.GetCurrentAsync();

        Assert.False(capacity.IsAvailable);
        Assert.Equal("Unavailable", capacity.Status);
        Assert.Equal("Azure capacity synchronization timed out.", capacity.Detail);
    }

    [Fact]
    public async Task SlowAzureLookup_IsBoundedAndFailsClosed()
    {
        var handler = new WaitingHandler();
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("AzureResourceManager"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") });
        var source = new AzureTranslatorSubscriptionCapacitySource(
            factory.Object,
            Configuration(),
            NullLogger<AzureTranslatorSubscriptionCapacitySource>.Instance,
            new StaticTokenCredential(),
            refreshTimeout: TimeSpan.FromMilliseconds(50));

        var capacity = await source.GetCurrentAsync();

        Assert.False(capacity.IsAvailable);
        Assert.Equal("Azure capacity synchronization timed out.", capacity.Detail);
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task CapacityAuthority_EnforcesF0MonthlyBudgetAndHourlyVelocityFromAzure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var capacity = new TranslationCapacityAuthority(
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(Available("F0", 2_000_000)));

        var corpusOverReserve = await capacity.TryReserveAsync(
            "AzureTranslator", 1_900_001, TranslationCapacityPurpose.Bootstrap, "corpus-over-reserve");
        Assert.Null(corpusOverReserve);

        var live = await capacity.TryReserveAsync(
            "AzureTranslator", 100, TranslationCapacityPurpose.Live, "rolling-live");
        Assert.NotNull(live);
        await capacity.CompleteAsync(live!, providerMayHaveConsumed: true);

        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator");
        Assert.True(snapshot.IsSynchronized);
        Assert.Equal("F0", snapshot.Tier);
        Assert.Equal(2_000_000, snapshot.MonthlyIncludedCharacterAllowance);
        Assert.Equal(100_000, snapshot.MonthlyLiveReserveCharacters);
        Assert.Equal(1_900_000, snapshot.MaximumSafeCorpusConsumptionCharacters);
        Assert.Equal(100, snapshot.MonthlyCharactersConsumed);
        Assert.Equal(1_999_900, snapshot.MonthlyRemainingCharacters);
        Assert.Equal(2_000_000, snapshot.HourlyCharacterLimit);
        Assert.Equal(100, snapshot.HourlyCharactersConsumed);
        Assert.Equal(1_999_900, snapshot.HourlyRemainingCharacters);
        Assert.Equal(1_899_900, snapshot.SafeAcquisitionCharacters);
        Assert.Equal(60, snapshot.HourlyCapacityWindowMinutes);
    }

    [Fact]
    public async Task CapacityAuthority_HoldsAzureWorkWhenSubscriptionSyncIsUnavailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var capacity = new TranslationCapacityAuthority(
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(new AzureTranslatorSubscriptionCapacity(
                false, "Unavailable", null, null, null, null, null, DateTime.UtcNow, "Reader role is missing.")));

        Assert.Null(await capacity.TryReserveAsync(
            "AzureTranslator", 1, TranslationCapacityPurpose.Live, "no-sync"));

        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator");
        Assert.False(snapshot.IsSynchronized);
        Assert.Null(snapshot.HourlyCharacterLimit);
        Assert.Equal("Reader role is missing.", snapshot.Detail);
        Assert.Empty(db.LegendTranslationProviderReservations);
    }

    [Fact]
    public async Task Readiness_DoesNotReviveLegacyCapacityWhenAzureSynchronizationIsUnavailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureTranslator:ResourceId"] = "/subscriptions/test/resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/translator",
            ["AzureTranslator:Endpoint"] = "https://translator.example.test",
            ["AzureTranslator:Key"] = "test-key",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "1000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "100",
            ["LegendConnect:CorpusAcquisition:MaximumSafeCorpusConsumptionCharacters"] = "900",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98"
        }).Build();
        var policy = new LegendConnectRuntimePolicyAuthority(
            db,
            Mock.Of<IControlledResourceAccessService>(),
            new LegendLanguageRegistry(db, configuration),
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance,
            new StaticAzureCapacitySource(new AzureTranslatorSubscriptionCapacity(
                false, "Unavailable", null, null, null, null, null, DateTime.UtcNow, "Reader role is missing.")));

        var readiness = await policy.GetReadinessAsync();

        var capacityCheck = Assert.Single(readiness.Checks, item => item.Name == "Capacity Policy");
        Assert.Equal("BLOCKED", capacityCheck.State);
        Assert.Equal("Reader role is missing.", capacityCheck.Detail);
    }

    [Fact]
    public async Task Router_RetainsAttemptedAzureCharactersWhenTheResponseIsUnavailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var capacity = new TranslationCapacityAuthority(
            db,
            configuration,
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(Available("F0", 2_000_000)));
        var router = new LegendConnectTranslationRouter(
            new UnavailableProvider(),
            new LegendLanguageRegistry(db, configuration),
            capacity,
            NullLogger<LegendConnectTranslationRouter>.Instance);

        var result = await router.TranslateAsync("Hello", "ht", "en");

        Assert.False(result.Succeeded);
        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator");
        Assert.Equal(5, snapshot.MonthlyCharactersConsumed);
        Assert.Equal(1_999_995, snapshot.MonthlyRemainingCharacters);
        Assert.Equal(5, snapshot.HourlyCharactersConsumed);
        Assert.Equal(1_999_995, snapshot.HourlyRemainingCharacters);
    }

    [Fact]
    public async Task F0MonthlyAllowance_BlocksCorpusAfterPriorMonthWindowUsageEvenWhenHourlyCapacityRemains()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.LegendTranslationProviderReservations.Add(new LegendTranslationProviderReservation
        {
            Id = Guid.NewGuid(),
            Provider = "AzureTranslator",
            BillingPeriodStart = new DateOnly(now.Year, now.Month, 1),
            ReservationReference = "monthly-prior-hour",
            Purpose = TranslationCapacityPurpose.Bootstrap.ToString(),
            Characters = 100_000,
            State = "Completed",
            CreatedUtc = now.AddHours(-2),
            CompletedUtc = now.AddHours(-2),
            ReservationExpiresUtc = now.AddHours(-2).AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var capacity = new TranslationCapacityAuthority(
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(Available("F0", 2_000_000)));

        Assert.Null(await capacity.TryReserveAsync(
            "AzureTranslator", 1_900_000, TranslationCapacityPurpose.Bootstrap, "monthly-overage"));

        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator");
        Assert.Equal(100_000, snapshot.MonthlyCharactersConsumed);
        Assert.Equal(0, snapshot.HourlyCharactersConsumed);
        Assert.Equal(1_900_000, snapshot.MonthlyRemainingCharacters);
        Assert.Equal(2_000_000, snapshot.HourlyRemainingCharacters);
        Assert.Equal(1_800_000, snapshot.SafeAcquisitionCharacters);
    }

    [Fact]
    public async Task F0MonthlyAllowance_BlocksLiveTrafficAfterTheHourWindowHasRolledPast()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.LegendTranslationProviderReservations.Add(new LegendTranslationProviderReservation
        {
            Id = Guid.NewGuid(),
            Provider = "AzureTranslator",
            BillingPeriodStart = new DateOnly(now.Year, now.Month, 1),
            ReservationReference = "monthly-live-prior-hour",
            Purpose = TranslationCapacityPurpose.Live.ToString(),
            Characters = 2_000_000,
            State = "Completed",
            CreatedUtc = now.AddHours(-2),
            CompletedUtc = now.AddHours(-2),
            ReservationExpiresUtc = now.AddHours(-2).AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var capacity = new TranslationCapacityAuthority(
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(Available("F0", 2_000_000)));

        Assert.Null(await capacity.TryReserveAsync(
            "AzureTranslator", 1, TranslationCapacityPurpose.Live, "monthly-live-overage"));

        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator");
        Assert.Equal(0, snapshot.HourlyCharactersConsumed);
        Assert.Equal(2_000_000, snapshot.MonthlyCharactersConsumed);
        Assert.Equal(0, snapshot.MonthlyRemainingCharacters);
    }

    [Fact]
    public async Task MeteredTier_HourlyVelocityStillBlocksTrafficWithoutAMonthlyAllowance()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var now = DateTime.UtcNow;
        db.LegendTranslationProviderReservations.Add(new LegendTranslationProviderReservation
        {
            Id = Guid.NewGuid(),
            Provider = "AzureTranslator",
            BillingPeriodStart = new DateOnly(now.Year, now.Month, 1),
            ReservationReference = "hourly-velocity",
            Purpose = TranslationCapacityPurpose.Live.ToString(),
            Characters = 39_999_999,
            State = "Completed",
            CreatedUtc = now.AddMinutes(-1),
            CompletedUtc = now.AddMinutes(-1),
            ReservationExpiresUtc = now.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var capacity = new TranslationCapacityAuthority(
            db,
            new ConfigurationBuilder().Build(),
            NullLogger<TranslationCapacityAuthority>.Instance,
            azureSubscriptionCapacity: new StaticAzureCapacitySource(Available("S1", 40_000_000)));

        Assert.Null(await capacity.TryReserveAsync(
            "AzureTranslator", 2, TranslationCapacityPurpose.Live, "hourly-overage"));

        var snapshot = await capacity.GetSnapshotAsync("AzureTranslator");
        Assert.Null(snapshot.MonthlyIncludedCharacterAllowance);
        Assert.Null(snapshot.MonthlyRemainingCharacters);
        Assert.Equal(39_999_999, snapshot.HourlyCharactersConsumed);
        Assert.Equal(1, snapshot.HourlyRemainingCharacters);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureTranslator:ResourceId"] = "/subscriptions/test/resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/translator"
        })
        .Build();

    private static AzureTranslatorSubscriptionCapacity Available(string tier, long limit) => new(
        true, "Synchronized", "/subscriptions/test/resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/translator",
        "masterapp-translator-1221", tier, tier == "F0" ? limit : null, limit, DateTime.UtcNow, "Synchronized for test.");

    private sealed class StaticAzureCapacitySource : IAzureTranslatorSubscriptionCapacitySource
    {
        private readonly AzureTranslatorSubscriptionCapacity _capacity;
        public StaticAzureCapacitySource(AzureTranslatorSubscriptionCapacity capacity) => _capacity = capacity;
        public Task<AzureTranslatorSubscriptionCapacity> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_capacity);
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public int TokenRequests { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            TokenRequests++;
            return new("unit-test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class UnavailableProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(false, null, "translation_provider_failed"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationProviderResult(
                false, null, sourceLanguage, ProviderName, "translation_provider_timeout"));
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly string? _metricsJson;
        public JsonHandler(string json, string? metricsJson = null)
        {
            _json = json;
            _metricsJson = metricsJson;
        }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public int SendAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendAttempts++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/metrics", StringComparison.Ordinal) ? _metricsJson ?? _json : _json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapacityTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class HeldCapacityHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendAttempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendAttempts++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"translator","sku":{"name":"F0"}}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NoWriteCapacitySentinel : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Attempts++;
            throw new InvalidOperationException("Native diagnostic attempted a database write.");
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Native diagnostic attempted a database write.");
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Azure Resource Manager timeout.");
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The timeout cancellation token must end this request.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}

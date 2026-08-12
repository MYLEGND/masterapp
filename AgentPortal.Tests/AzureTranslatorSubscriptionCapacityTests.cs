using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class AzureTranslatorSubscriptionCapacityTests
{
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
        Assert.Contains("api-version=2024-10-01", handler.RequestUri!.Query, StringComparison.Ordinal);
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
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("unit-test-token", DateTimeOffset.UtcNow.AddHours(1));

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
        public JsonHandler(string json) => _json = json;
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Azure Resource Manager timeout.");
    }
}

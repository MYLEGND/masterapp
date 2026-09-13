using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ApplicationLocalizationArchitectureTests
{
    [Theory]
    [InlineData("translation_pending", "Pending", true)]
    [InlineData("translation_provider_timeout", "RetryableFailure", true)]
    [InlineData("translation_provider_transient_failure", "RetryableFailure", true)]
    [InlineData("translation_provider_rate_limited", "RetryableFailure", true)]
    [InlineData("translation_capacity_temporarily_unavailable", "RetryableFailure", true)]
    [InlineData("translation_capacity_reservation_pending", "RetryableFailure", true)]
    [InlineData("translation_provider_failed", "Blocked", false)]
    [InlineData("translation_provider_authentication_failed", "Blocked", false)]
    [InlineData("translation_provider_access_denied", "Blocked", false)]
    [InlineData("translation_capacity_configuration_unavailable", "Blocked", false)]
    [InlineData("translation_output_invalid", "Blocked", false)]
    public void CatalogContinuation_TypedFailureCannotBeConcealedByPendingEntries(string failure, string disposition, bool retry)
    {
        var entries = new[] { FailedCopy(failure), FailedCopy("translation_pending") };
        var result = ApplicationLocalizationService.BuildContinuation(entries);
        Assert.Equal(disposition, result.Disposition);
        Assert.Equal(2, result.RemainingEntries);
        Assert.Equal(retry, result.RetryAfterSeconds is > 0);
        Assert.InRange(result.MaximumRequestsPerPass, 1, 64);
        Assert.InRange(result.MaximumDurationSeconds, 1, 180);
    }

    [Fact]
    public void CatalogContinuation_UsesCapacityResetAndStopsAtApprovalOnly()
    {
        var reset = DateTime.UtcNow.AddDays(3);
        var result = ApplicationLocalizationService.BuildContinuation(new[] {
            FailedCopy("translation_capacity_monthly_exhausted") with { RetryAfterUtc = reset } });
        Assert.Equal("RetryableFailure", result.Disposition);
        Assert.InRange(result.RetryAfterSeconds!.Value, 259_199, 259_201);
        Assert.Null(ApplicationLocalizationService.BuildContinuation(new[] { FailedCopy("approved_translation_unavailable") }).RetryAfterSeconds);
        Assert.Equal("Complete", ApplicationLocalizationService.BuildContinuation(Array.Empty<ApplicationLocalizedCopy>()).Disposition);
    }

    private static ApplicationLocalizedCopy FailedCopy(string code) => new(
        Guid.NewGuid().ToString("N"), "Source", "Source", "visual interface copy", "revision",
        Array.Empty<string>(), "SourceFallback", "Source", "Fallback", DateTime.UtcNow, false, code);

    [Fact]
    public async Task QuotaNotice_EveryBaselineLanguage_IsPresetAndNeverInvokesTranslation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var languages = await registry.ListEnabledTranslationLanguagesAsync();
        var manifest = new EmbeddedApplicationCopyManifestSource();
        var entries = manifest.Manifest.Entries.Where(item => item.PresetTranslations is not null).ToArray();
        Assert.Equal(2, entries.Length);
        foreach (var entry in entries)
        {
        Assert.Equal("ApprovedOnly", entry.TranslationPolicy);
        var preferences = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        var translations = new Mock<IRetainedTranslationService>(MockBehavior.Strict);
        var intelligence = new Mock<ILegendConnectTranslationIntelligence>(MockBehavior.Strict);
        var actor = new MessagingActor("client-1", MessagingParticipantTypes.Client);
        var service = new ApplicationLocalizationService(manifest, preferences.Object, registry,
            translations.Object, intelligence.Object, NullLogger<ApplicationLocalizationService>.Instance);
        foreach (var language in languages)
        {
            preferences.Setup(item => item.GetCanonicalPreferredLanguageAsync(actor, It.IsAny<CancellationToken>()))
                .ReturnsAsync(language.Code);
            Assert.True(entry.PresetTranslations!.ContainsKey(language.Code));
            for (var delivery = 0; delivery < 2; delivery++)
            {
                var notice = await service.LocalizeAsync(actor, entry.Source, entry.Context);
                Assert.Equal(entry.PresetTranslations[language.Code], notice.Text);
                Assert.True(notice.Reused);
                Assert.Null(notice.FailureCode);
            }
        }
        translations.VerifyNoOtherCalls();
        intelligence.VerifyNoOtherCalls();
        }
    }

    [Theory]
    [InlineData("Ringing")]
    [InlineData("The call status could not be confirmed. Please try again.")]
    [InlineData("The recipient could not be reached. Their device did not confirm receiving the call.")]
    [InlineData("The call was declined.")]
    public void CallingStatusAndFailureCopy_UsesTheSharedRetainedCatalog(string source)
    {
        var entry = Assert.Single(new EmbeddedApplicationCopyManifestSource().Manifest.Entries,
            entry => entry.Source == source && entry.Context == "visual interface copy");
        Assert.Equal("Global", entry.ReuseScope);
        Assert.Equal("AzureAllowed", entry.TranslationPolicy);
    }

    [Fact]
    public async Task EveryEnabledLanguage_RetainsAndReusesTheSameSourceIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        var languages = await new LegendLanguageRegistry(db, Configuration()).ListEnabledTranslationLanguagesAsync();
        foreach (var language in languages)
        {
            var first = await router.TranslateRetainedAsync(Request(target: language.Code));
            var operations = provider.TranslateOperations;
            var second = await router.TranslateRetainedAsync(Request(target: language.Code));
            Assert.True(first.Succeeded);
            Assert.True(second.Reused);
            Assert.Equal(language.Code, second.TargetLanguageCode);
            Assert.Equal(first.Text, second.Text);
            Assert.Equal(operations, provider.TranslateOperations);
        }
    }

    [Fact]
    public async Task RetainedTranslation_FirstMissPersists_AndSecondRequestReusesWithoutProvider()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        var request = Request(target: "ht");

        var first = await router.TranslateRetainedAsync(request);
        var second = await router.TranslateRetainedAsync(request);

        Assert.True(first.Succeeded);
        Assert.False(first.Reused);
        Assert.Equal("[ht] Welcome, {name}.", first.Text);
        Assert.True(second.Succeeded);
        Assert.True(second.Reused);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(1, provider.TranslateOperations);
        Assert.Single(db.Set<Domain.Entities.LegendTranslationAlignment>()
            .Where(item => item.RetainedTranslationIdentity != null));
    }

    [Fact]
    public async Task RetainedReuse_IsDurablyCountedAcrossRouterInstancesInBothDirections()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        var forward = Request("ht");
        var reverse = Request("en") with { SourceLanguageCode = "ht", SourceText = "Byenveni, {name}." };
        foreach (var request in new[] { forward, reverse })
            Assert.True((await router.TranslateRetainedAsync(request)).Succeeded);
        db.ChangeTracker.Clear();
        var anotherDeviceRequest = await BuildRouterAsync(db, provider);
        foreach (var request in new[] { forward, reverse })
            Assert.True((await anotherDeviceRequest.TranslateRetainedAsync(request)).Reused);
        Assert.Equal(2, provider.TranslateOperations);
        var pairs = db.Set<LegendTranslationPairDemand>().ToArray();
        Assert.Equal(2, pairs.Length);
        Assert.All(pairs, pair => {
            Assert.Equal(2, pair.TranslationRequestCount);
            Assert.Equal(1, pair.AzureFallbackCount);
            Assert.Equal(1, pair.ProviderObservationReuseCount);
            Assert.Equal(0, pair.TranslationMemoryHitCount);
        });
        var usage = Assert.Single(db.Set<LegendTranslationSystemUsage>());
        Assert.Equal(2, usage.ProviderOperationCount);
        Assert.Equal(forward.SourceText.Length + reverse.SourceText.Length, usage.ProviderObservationCharactersAvoided);
        var registry = new LegendLanguageRegistry(db, Configuration());
        var dashboard = await new LegendConnectOperations(db, registry,
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance), Configuration())
            .GetDashboardCountersAsync();
        Assert.Equal(2, dashboard.ProviderObservationReuseCount);
        Assert.Equal(2, dashboard.ProviderOperationCount);
        Assert.Equal(0, dashboard.TranslationRoutingReconciliationGap);
        Assert.Equal(usage.ProviderObservationCharactersAvoided, dashboard.ProviderObservationCharactersAvoided);
    }

    [Fact]
    public async Task RetainedBatch_RecordsProviderAndReuseInTheSameDurableLedger()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        var requests = new[] { Request("ht"), Request("ht") with { StableSourceContentId = "other", SourceText = "Hello, {name}." } };
        Assert.All(await router.TranslateRetainedBatchAsync(requests), result => Assert.True(result.Succeeded));
        var reuseWrites = 0;
        db.SavingChanges += (_, _) => reuseWrites++;
        Assert.All(await router.TranslateRetainedBatchAsync(requests), result => Assert.True(result.Reused));
        Assert.Equal(2, reuseWrites); // One pair delta and one avoided-character delta, independent of batch size.
        Assert.Equal(1, provider.BatchOperations);
        Assert.Equal(0, provider.TranslateOperations);
        var pair = Assert.Single(db.Set<LegendTranslationPairDemand>());
        Assert.Equal(4, pair.TranslationRequestCount);
        Assert.Equal(2, pair.AzureFallbackCount);
        Assert.Equal(2, pair.ProviderObservationReuseCount);
        var usage = Assert.Single(db.Set<LegendTranslationSystemUsage>());
        Assert.Equal(1, usage.ProviderOperationCount);
        Assert.Equal(requests.Sum(request => request.SourceText.Length), usage.ProviderObservationCharactersAvoided);
    }

    [Fact]
    public async Task DuplicateBatchEntries_TranslateOnceAndRecordTheOtherDeliveryAsReuse()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        var request = Request("ht");
        var results = await router.TranslateRetainedBatchAsync(new[] { request, request });
        Assert.False(results[0].Reused);
        Assert.True(results[1].Reused);
        Assert.Equal(1, provider.BatchOperations);
        var demand = Assert.Single(db.Set<LegendTranslationPairDemand>());
        Assert.Equal(2, demand.TranslationRequestCount);
        Assert.Equal(1, demand.AzureFallbackCount);
        Assert.Equal(1, demand.ProviderObservationReuseCount);
        Assert.Single(db.Set<LegendTranslationAlignment>().Where(row => row.RetainedTranslationIdentity != null));
    }

    [Fact]
    public async Task NewlyRegisteredLanguage_ReusesWithoutChangingTheRouter()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        db.Add(new LegendLanguageDefinition { LanguageCode = "sw", CanonicalName = "Swahili", NativeName = "Kiswahili",
            StoragePartition = "/sw", IsEnabled = true, IsTranslationEnabled = true });
        await db.SaveChangesAsync();
        var request = Request("sw");
        Assert.True((await router.TranslateRetainedAsync(request)).Succeeded);
        Assert.True((await router.TranslateRetainedAsync(request)).Reused);
        Assert.Equal(1, provider.TranslateOperations);
    }

    [Fact]
    public async Task RetainedIdentity_SeparatesTargetRevisionContextAndPrivacyScope()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);

        var global = Request(target: "ht");
        var differentTarget = Request(target: "es");
        var differentRevision = Request(target: "ht") with { SourceRevision = "2" };
        var differentContext = Request(target: "ht") with { TranslationContext = "account welcome email" };
        var privateA = Request(target: "ht") with
        {
            ReuseScope = TranslationReuseScopes.User,
            ScopeIdentityHash = new string('a', 64)
        };
        var privateB = privateA with { ScopeIdentityHash = new string('b', 64) };

        await router.TranslateRetainedAsync(global);
        await router.TranslateRetainedAsync(differentTarget);
        await router.TranslateRetainedAsync(differentRevision);
        await router.TranslateRetainedAsync(differentContext);
        await router.TranslateRetainedAsync(privateA);
        await router.TranslateRetainedAsync(privateB);
        await router.TranslateRetainedAsync(global);
        await router.TranslateRetainedAsync(privateA);

        Assert.Equal(6, provider.TranslateOperations);
        Assert.Equal(6, db.Set<Domain.Entities.LegendTranslationAlignment>()
            .Count(item => item.RetainedTranslationIdentity != null));
    }

    [Fact]
    public async Task RetainedTranslation_PreservesTargetLanguageAndRejectsInvalidStructure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);

        var sameLanguage = await router.TranslateRetainedAsync(Request(target: "en"));
        Assert.True(sameLanguage.Succeeded);
        Assert.True(sameLanguage.Reused);
        Assert.Equal("Welcome, {name}.", sameLanguage.Text);
        Assert.Equal(0, provider.TranslateOperations);

        provider.Output = "Bonjou.";
        var invalidPlaceholder = await router.TranslateRetainedAsync(Request(target: "ht"));
        Assert.False(invalidPlaceholder.Succeeded);
        Assert.Equal("Welcome, {name}.", invalidPlaceholder.Text);
        Assert.Equal("translation_output_invalid", invalidPlaceholder.ErrorCode);

        provider.Output = "[ht] Read <b>{count}</b> at https://mylegnd.com\nNow";
        var structured = await router.TranslateRetainedAsync(new RetainedTranslationRequest(
            "test.structured",
            "Read <b>{count}</b> at https://mylegnd.com\nNow",
            "en",
            "ht",
            "1",
            "structured accessibility instruction",
            "count",
            TranslationReuseScopes.Global));
        Assert.True(structured.Succeeded);
        Assert.Null(structured.ErrorCode);
    }

    [Fact]
    public async Task CorruptedRetainedTranslation_IsSupersededAndRepopulatedSafely()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);
        var request = Request(target: "ht");

        var first = await router.TranslateRetainedAsync(request);
        Assert.True(first.Succeeded);
        var active = db.Set<Domain.Entities.LegendTranslationAlignment>()
            .Single(item => item.RetainedTranslationIdentity != null && item.SupersededUtc == null);
        var target = db.Set<Domain.Entities.LegendLanguageTextUnit>()
            .Single(item => item.Id == active.TargetTextUnitId);
        target.Text = "Bonjou.";
        target.NormalizedHash = LegendLanguageIdentity.TextHash(target.Text);
        await db.SaveChangesAsync();
        provider.Output = "Byenveni, {name}.";

        var repaired = await router.TranslateRetainedAsync(request);

        Assert.True(repaired.Succeeded);
        Assert.Equal("Byenveni, {name}.", repaired.Text);
        Assert.Equal(2, provider.TranslateOperations);
        Assert.Single(db.Set<Domain.Entities.LegendTranslationAlignment>()
            .Where(item => item.RetainedTranslationIdentity != null && item.SupersededUtc == null));
        Assert.Single(db.Set<Domain.Entities.LegendTranslationAlignment>()
            .Where(item => item.RetainedTranslationIdentity != null && item.SupersededUtc != null));
    }

    [Fact]
    public async Task RetainedTranslation_ProtectsBrandNamesFromProviderMutation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var provider = new RecordingTranslationProvider();
        var router = await BuildRouterAsync(db, provider);

        var result = await router.TranslateRetainedAsync(new RetainedTranslationRequest(
            "test.brand",
            "Continue with Legend® Ai and OpenAI.",
            "en",
            "ht",
            "1",
            "authentication instruction",
            "",
            TranslationReuseScopes.Global));

        Assert.True(result.Succeeded);
        Assert.Contains("Legend® Ai", result.Text, StringComparison.Ordinal);
        Assert.Contains("OpenAI", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Legend® Ai", provider.LastText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", provider.LastText, StringComparison.Ordinal);
        Assert.Contains("{legendBrand1}", provider.LastText, StringComparison.Ordinal);
        Assert.Contains("{legendBrand2}", provider.LastText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentIdenticalMisses_UseOneProviderOperation()
    {
        var provider = new RecordingTranslationProvider(delay: TimeSpan.FromMilliseconds(80));
        var language = new Mock<ILegendLanguageRegistry>(MockBehavior.Strict);
        language.Setup(item => item.NormalizeEnabledTranslationLanguageAsync(
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? value, CancellationToken _) => value);
        var capacity = new AlwaysAvailableCapacity();
        var coalescer = new TranslationRequestCoalescer();
        var retained = new ConcurrentDictionary<string, LegendRetainedTranslationMemoryMatch>();
        var intelligence = new Mock<ILegendConnectTranslationIntelligence>(MockBehavior.Strict);
        intelligence.SetupGet(item => item.IsContextualCompositionActive).Returns(false);
        intelligence.Setup(item => item.TryGetTrustedExactMemoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendTranslationMemoryMatch?)null);
        intelligence.Setup(item => item.TryGetTrustedScopedMemoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendTranslationMemoryMatch?)null);
        intelligence.Setup(item => item.TryGetRetainedTranslationAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string identity, CancellationToken _) =>
                retained.TryGetValue(identity, out var value) ? value : null);
        intelligence.Setup(item => item.EvaluateContextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendContextualTranslationSuggestion?)null);
        intelligence.Setup(item => item.RetainProviderTranslationAsync(
                It.IsAny<LegendRetainedTranslationWrite>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegendRetainedTranslationWrite write, CancellationToken _) =>
                retained.GetOrAdd(write.Identity, _ => new LegendRetainedTranslationMemoryMatch(
                    write.TargetText,
                    write.Provider,
                    LegendConnectKnowledgeProvenance.ProviderDerived,
                    "Observation",
                    DateTime.UtcNow)));

        LegendConnectTranslationRouter MakeRouter() => new(
            provider,
            language.Object,
            capacity,
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: intelligence.Object,
            coalescer: coalescer);

        var first = MakeRouter().TranslateRetainedAsync(Request(target: "ht"));
        var second = MakeRouter().TranslateRetainedAsync(Request(target: "ht"));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, provider.TranslateOperations);
        Assert.Equal(1, retained.Count);
    }

    [Fact]
    public async Task BatchCatalog_UsesCanonicalPreference_AndReusesAcrossActors()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        await registry.ListEnabledTranslationLanguagesAsync();
        var provider = new RecordingTranslationProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new AlwaysAvailableCapacity(),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance));
        var preferences = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        preferences.Setup(item => item.GetCanonicalPreferredLanguageAsync(
                It.IsAny<MessagingActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ht");
        var service = new ApplicationLocalizationService(
            new EmbeddedApplicationCopyManifestSource(),
            preferences.Object,
            registry,
            router,
            new LegendConnectTranslationIntelligence(db, configuration),
            NullLogger<ApplicationLocalizationService>.Instance);

        var first = await service.GetCatalogAsync(new MessagingActor("user-1", "Client"));
        Assert.InRange(provider.BatchOperations, 1, 2);
        for (var attempt = 0; first.Entries.Any(entry => entry.FailureCode == "translation_pending") && attempt < 60; attempt++)
            first = await service.GetCatalogAsync(new MessagingActor("user-1", "Client"));
        Assert.DoesNotContain(first.Entries, entry => entry.FailureCode == "translation_pending");
        var second = await service.GetCatalogAsync(new MessagingActor("user-2", "Client"));

        Assert.Equal("ht", first.LanguageCode);
        Assert.False(first.IsComplete);
        Assert.Equal(new EmbeddedApplicationCopyManifestSource().Manifest.Entries.Count(entry => entry.TranslationPolicy == "ApprovedOnly" && entry.PresetTranslations?.ContainsKey("ht") != true),
            first.Entries.Count(item => item.FailureCode == "approved_translation_unavailable"));
        Assert.Contains(first.Entries, item =>
            item.Source == "Secure sign in" && item.Text.StartsWith("[ht]", StringComparison.Ordinal));
        Assert.Equal(first.Entries.Select(item => item.Text), second.Entries.Select(item => item.Text));
        var providerEntryCount = first.Entries.Count(item =>
            string.Equals(item.Provider, provider.ProviderName, StringComparison.Ordinal));
        Assert.Equal((providerEntryCount + 99) / 100, provider.BatchOperations);
        Assert.Equal(0, provider.TranslateOperations);
    }

    [Fact]
    public async Task ServerOwnedNotificationTemplate_LocalizesBeforeInterpolation_AndReusesGlobally()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        await registry.ListEnabledTranslationLanguagesAsync();
        var provider = new RecordingTranslationProvider();
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new AlwaysAvailableCapacity(),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: intelligence);
        var preferences = new Mock<IControlledResourceAccessService>(MockBehavior.Strict);
        preferences.Setup(item => item.GetCanonicalPreferredLanguageAsync(
                It.IsAny<MessagingActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ht");
        var service = new ApplicationLocalizationService(
            new EmbeddedApplicationCopyManifestSource(),
            preferences.Object,
            registry,
            router,
            intelligence,
            NullLogger<ApplicationLocalizationService>.Instance);

        var first = await service.LocalizeAsync(
            new MessagingActor("user-1", "Client"),
            "{resourceName} approved",
            "visual interface copy",
            new Dictionary<string, string> { ["resourceName"] = "Verifikasyon Legend" });
        var second = await service.LocalizeAsync(
            new MessagingActor("user-2", "Client"),
            "{resourceName} approved",
            "visual interface copy",
            new Dictionary<string, string> { ["resourceName"] = "Verifikasyon Legend" });

        Assert.True(first.Text.StartsWith("[ht]", StringComparison.Ordinal));
        Assert.Contains("Verifikasyon Legend", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{resourceName}", first.Text, StringComparison.Ordinal);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(1, provider.TranslateOperations);
    }

    private static RetainedTranslationRequest Request(string target) => new(
        "test.welcome",
        "Welcome, {name}.",
        "en",
        target,
        "1",
        "authenticated home welcome heading",
        "name",
        TranslationReuseScopes.Global);

    private static async Task<LegendConnectTranslationRouter> BuildRouterAsync(
        MasterAppDbContext db,
        ITranslationProvider provider)
    {
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        await registry.ListEnabledTranslationLanguagesAsync();
        return new LegendConnectTranslationRouter(
            provider,
            registry,
            new AlwaysAvailableCapacity(),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            demand: new TranslationDemandRecorder(db, NullLogger<TranslationDemandRecorder>.Instance),
            systemUsage: new TranslationSystemUsageRecorder(db, NullLogger<TranslationSystemUsageRecorder>.Instance));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98"
        })
        .Build();

    private sealed class AlwaysAvailableCapacity : ITranslationCapacityAuthority
    {
        public Task<Domain.Messaging.LegendConnectProviderCapacitySnapshot> GetSnapshotAsync(
            string provider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TranslationCapacityReservationResult> TryReserveAsync(
            string provider,
            int characters,
            TranslationCapacityPurpose purpose,
            string? reservationReference = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new TranslationCapacityReservationResult(new TranslationCapacityReservation(
                provider,
                DateOnly.FromDateTime(DateTime.UtcNow),
                characters,
                purpose,
                Guid.NewGuid())));

        public Task CompleteAsync(
            TranslationCapacityReservation reservation,
            bool providerMayHaveConsumed,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingTranslationProvider : ITranslationProvider
    {
        private readonly TimeSpan _delay;
        private int _translateOperations;
        private int _batchOperations;

        public RecordingTranslationProvider(TimeSpan? delay = null) =>
            _delay = delay ?? TimeSpan.Zero;

        public string ProviderName => "AzureTranslator";
        public string ProviderVersion => "test-v1";
        public int TranslateOperations => Volatile.Read(ref _translateOperations);
        public int BatchOperations => Volatile.Read(ref _batchOperations);
        public string? Output { get; set; }
        public string LastText { get; private set; } = string.Empty;

        public Task<TranslationDetectionResult> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public async Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _translateOperations);
            LastText = text;
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken);
            return new TranslationProviderResult(
                true,
                Output ?? $"[{targetLanguage}] {text}",
                sourceLanguage,
                ProviderName);
        }

        public Task<IReadOnlyList<TranslationProviderResult>> TranslateBatchAsync(
            IReadOnlyList<string> texts,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _batchOperations);
            return Task.FromResult<IReadOnlyList<TranslationProviderResult>>(texts
                .Select(text => new TranslationProviderResult(
                    true,
                    $"[{targetLanguage}] {text}",
                    sourceLanguage,
                    ProviderName))
                .ToArray());
        }
    }
}

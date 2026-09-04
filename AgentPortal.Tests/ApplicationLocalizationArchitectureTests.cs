using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentPortal.Tests;

public sealed class ApplicationLocalizationArchitectureTests
{
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
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));
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
        var second = await service.GetCatalogAsync(new MessagingActor("user-2", "Client"));

        Assert.Equal("ht", first.LanguageCode);
        Assert.False(first.IsComplete);
        Assert.Equal(2, first.Entries.Count(item => item.FailureCode == "approved_translation_unavailable"));
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
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));
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

        public Task<TranslationCapacityReservation?> TryReserveAsync(
            string provider,
            int characters,
            TranslationCapacityPurpose purpose,
            string? reservationReference = null,
            CancellationToken cancellationToken = default) => Task.FromResult<TranslationCapacityReservation?>(new(
                provider,
                DateOnly.FromDateTime(DateTime.UtcNow),
                characters,
                purpose,
                Guid.NewGuid()));

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
            CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public async Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default,
        LegendConnectExternalProviderPolicy? providerPolicy = null)
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

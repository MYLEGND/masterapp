using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

public sealed class LegendConnectRuntimePolicyTests
{
    [Fact]
    public async Task FounderRuntimePolicy_PersistsAcrossAuthorityRecreation_AndAuditsChanges()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var first = Policy(db, registry, configuration);

        var saved = await first.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(
            1_000, 250, 750, true, "Shadow", 0.99m));

        Assert.True(saved.IsPersisted);
        Assert.Equal(1_000, saved.MonthlyProviderCapacityCharacters);
        Assert.Equal(250, saved.LiveTranslationReserveCharacters);
        var recreated = Policy(db, new LegendLanguageRegistry(db, configuration), configuration);
        var loaded = await recreated.GetEffectiveAsync();
        Assert.Equal(750, loaded.MaximumSafeCorpusConsumptionCharacters);
        Assert.Contains(await recreated.GetRecentAuditAsync(), item => item.Action == "RuntimePolicyChanged" && item.FounderUserId == "founder");
    }

    [Fact]
    public async Task RuntimePolicy_RejectsNonFounderAndInvalidProtectedReserve()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var nonFounder = new LegendConnectRuntimePolicyAuthority(
            db, new TestAccess(founder: false), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nonFounder.UpdateAsync("member",
            new LegendConnectRuntimePolicyMutation(100, 10, 90, true, "Shadow", 0.98m)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nonFounder.ConfigurePriorityOverrideAsync("member",
            new LegendConnectPriorityOverrideMutation("ht", null, null)));

        var policy = Policy(db, registry, configuration);
        await Assert.ThrowsAsync<ArgumentException>(() => policy.UpdateAsync("founder",
            new LegendConnectRuntimePolicyMutation(100, 100, 0, true, "Shadow", 0.98m)));
        await Assert.ThrowsAsync<ArgumentException>(() => policy.UpdateAsync("founder",
            new LegendConnectRuntimePolicyMutation(100, 20, 81, true, "Shadow", 0.98m)));
    }

    [Fact]
    public async Task Activation_IsBlockedUntilReadinessGatesPass_ThenCanBePausedWithoutStoppingLiveTranslation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);

        var initiallyBlocked = await policy.ActivateAsync("founder");
        Assert.Equal("BLOCKED", initiallyBlocked.State);

        await policy.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(
            100, 20, 80, true, "Shadow", 0.98m));
        await policy.RecordWorkerHeartbeatAsync("Learning");
        await policy.RecordWorkerHeartbeatAsync("Acquisition");

        var idle = await policy.ActivateAsync("founder");
        Assert.Equal("ACTIVE — NO ELIGIBLE WORK", idle.State);
        Assert.Equal("IDLE", Assert.Single(idle.Checks, item => item.Name == "Approved Corpus").State);
        Assert.True((await policy.GetEffectiveAsync()).CorpusAcquisitionEnabled);

        db.LegendCorpusCandidates.Add(Candidate("activation", "en", "ht", "Approved activation candidate"));
        await db.SaveChangesAsync();

        var active = await policy.ActivateAsync("founder");
        Assert.Equal("ACTIVE", active.State);
        var paused = await policy.PauseAsync("founder");
        Assert.False(paused.CorpusAcquisitionEnabled);

        var provider = new RecordingProvider();
        var live = await new LegendConnectTranslationRouter(
            provider, registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, policy),
            NullLogger<LegendConnectTranslationRouter>.Instance)
            .TranslateAsync("Live translation remains available", "ht", "en");
        Assert.True(live.Succeeded);
        Assert.Equal(1, provider.TranslateCalls);
    }

    [Fact]
    public async Task IdleActivation_FounderMonolingualSeed_UsesTheExistingPlannerAndWorker()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        await policy.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(
            1_000, 250, 750, true, "Shadow", 0.98m));
        await policy.RecordWorkerHeartbeatAsync("Learning");
        await policy.RecordWorkerHeartbeatAsync("Acquisition");

        Assert.Equal("ACTIVE — NO ELIGIBLE WORK", (await policy.ActivateAsync("founder")).State);

        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration);
        var seed = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "Please bring the approved meeting agenda.", null, null,
            "Founder operations", "Formal", null, "FounderApproved"));

        Assert.True(seed.Succeeded);
        Assert.NotEmpty(await db.LegendCorpusCandidates
            .Where(item => item.IsApproved && item.ProcessingState == "Pending")
            .ToListAsync());

        var provider = new RecordingProvider();
        await Autonomous(db, configuration, provider).ProcessOneAsync();

        Assert.Equal(1, provider.TranslateCalls);
        Assert.Contains(await db.LegendCorpusCandidates.ToListAsync(), item => item.ProcessingState == "Queued");
        Assert.NotEmpty(await db.LegendTranslationAlignments.ToListAsync());
    }

    [Fact]
    public async Task AutonomousAcquisition_CannotConsumeProtectedLiveReserve()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        await policy.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(
            100, 80, 20, true, "Shadow", 0.98m));
        var capacity = new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, policy);

        Assert.Null(await capacity.TryReserveAsync("AzureTranslator", 21, TranslationCapacityPurpose.Bootstrap));
        Assert.NotNull(await capacity.TryReserveAsync("AzureTranslator", 20, TranslationCapacityPurpose.Bootstrap));
    }

    [Fact]
    public async Task FounderLanguageOverride_PrioritizesHaitianCreoleWithoutChangingDefaultScoringOrCreatingDuplicateWork()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        _ = await registry.GetOrCreateEnabledPairAsync("en", "fr");
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            Id = Guid.NewGuid(), PairKey = "en:fr", TranslationRequestCount = 500, LastRequestedUtc = DateTime.UtcNow
        });
        var haitian = Candidate("haitian", "en", "ht", "Approved Haitian coverage");
        var ineligibleHaitian = Candidate("ineligible-haitian", "en", "ht", "Unapproved Haitian coverage");
        ineligibleHaitian.IsApproved = false;
        var french = Candidate("french", "en", "fr", "Higher demand French coverage");
        db.AddRange(haitian, ineligibleHaitian, french);
        await db.SaveChangesAsync();

        var planner = new LegendConnectAutonomousGapPlanner(db, registry);
        Assert.Equal(french.Id, await planner.SelectApprovedGapAsync());

        await policy.ConfigurePriorityOverrideAsync("founder", new LegendConnectPriorityOverrideMutation("Haitian Creole", null, null));
        Assert.Equal(haitian.Id, await planner.SelectApprovedGapAsync(await policy.GetEffectiveAsync()));
        Assert.Contains(await policy.GetRecentAuditAsync(), item => item.Action == "FounderPriorityOverrideEnabled" && item.LanguageCode == "ht");

        await policy.DisablePriorityOverrideAsync("founder");
        Assert.Equal(french.Id, await planner.SelectApprovedGapAsync(await policy.GetEffectiveAsync()));
    }

    [Fact]
    public async Task PairSpecificOverride_AffectsOnlyTheSelectedDirection_AndCompletionDoesNotCallAzure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        var forward = Assert.IsType<LegendLanguagePairSnapshot>(await registry.GetOrCreateEnabledPairAsync("en", "ht"));
        _ = await registry.GetOrCreateEnabledPairAsync("ht", "en");
        var completed = Candidate("completed", "en", "ht", "Existing exact source");
        var reverse = Candidate("reverse", "ht", "en", "Konesans ranvèse");
        db.AddRange(completed, reverse);
        var eventItem = new LegendTranslationLearningEvent
        {
            Id = Guid.NewGuid(), IdempotencyKey = "existing-forward", SourceLanguageCode = "en", TargetLanguageCode = "ht", PairKey = forward.PairKey,
            SourceTextHash = completed.SourceTextHash, TargetTextHash = LegendLanguageIdentity.TextHash("Knowledge exists"),
            SourceText = completed.SourceText, TargetText = "Knowledge exists", Provider = "AzureTranslator",
            Provenance = "ApprovedTestCorpus", EligibilityState = "Eligible", ProcessingState = "Pending", CreatedUtc = DateTime.UtcNow
        };
        db.Add(eventItem);
        await db.SaveChangesAsync();
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        await corpus.ProcessAsync(eventItem);

        await policy.ConfigurePriorityOverrideAsync("founder", new LegendConnectPriorityOverrideMutation(null, "en:ht", null));
        var planner = new LegendConnectAutonomousGapPlanner(db, registry);
        Assert.Null(await planner.SelectApprovedGapAsync(await policy.GetEffectiveAsync()));

        var progress = await policy.GetPriorityProgressAsync();
        Assert.Equal("PRIORITY COMPLETE — NO ELIGIBLE MISSING WORK", progress.Status);
        Assert.Equal("Pending", (await db.LegendCorpusCandidates.SingleAsync(item => item.Id == reverse.Id)).ProcessingState);
    }

    [Fact]
    public async Task RuntimePolicy_ReportsActualReadinessAndSelfRelianceExcludesShadowObservations()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            Id = Guid.NewGuid(), PairKey = "en:ht", TranslationRequestCount = 10,
            TranslationMemoryHitCount = 3, ContextualCompositionObservationCount = 4,
            ContextualInternalServeCount = 0, AzureFallbackCount = 7, LastRequestedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var operations = new LegendConnectOperations(db, registry,
            new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance), configuration);

        var pair = Assert.IsType<LegendConnectPairHealthSnapshot>(await operations.GetPairHealthAsync("en:ht"));
        Assert.Equal(0, pair.ContextualInternalServeCount);
        Assert.Equal(0.3m, pair.ProviderAvoidanceRate);
        Assert.Equal(0.7m, pair.AzureDependencyRate);
    }

    [Fact]
    public async Task TwoAutonomousWorkerInstances_ClaimOneCandidateOnce_AndUseTheExistingPipeline()
    {
        var databaseName = "legend-connect-runtime-" + Guid.NewGuid().ToString("N");
        await using var keeper = new SqliteConnection($"Data Source=file:{databaseName}?mode=memory&cache=shared");
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(keeper).Options;
        var configuration = Configuration();
        await using (var seeded = new MasterAppDbContext(options))
        {
            await seeded.Database.EnsureCreatedAsync();
            var registry = new LegendLanguageRegistry(seeded, configuration);
            _ = await registry.ListEnabledTranslationLanguagesAsync();
            var policy = Policy(seeded, registry, configuration);
            await policy.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(100, 20, 80, true, "Shadow", 0.98m));
            await policy.RecordWorkerHeartbeatAsync("Learning");
            await policy.RecordWorkerHeartbeatAsync("Acquisition");
            seeded.LegendCorpusCandidates.Add(Candidate("shared-candidate", "en", "ht", "One approved candidate"));
            await seeded.SaveChangesAsync();
            Assert.Equal("ACTIVE", (await policy.ActivateAsync("founder")).State);
        }

        await using var dbOne = new MasterAppDbContext(options);
        await using var dbTwo = new MasterAppDbContext(options);
        var provider = new RecordingProvider();
        var first = Autonomous(dbOne, configuration, provider);
        var second = Autonomous(dbTwo, configuration, provider);

        await Task.WhenAll(first.ProcessOneAsync(), second.ProcessOneAsync());

        await using var verification = new MasterAppDbContext(options);
        Assert.Equal(1, provider.TranslateCalls);
        Assert.Equal("Queued", (await verification.LegendCorpusCandidates.SingleAsync()).ProcessingState);
        Assert.Single(await verification.LegendTranslationAlignments.ToListAsync());
    }

    private static LegendConnectRuntimePolicyAuthority Policy(
        MasterAppDbContext db,
        ILegendLanguageRegistry registry,
        IConfiguration configuration) =>
        new(db, new TestAccess(founder: true), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

    private static LegendConnectAutonomousLearningService Autonomous(
        MasterAppDbContext db,
        IConfiguration configuration,
        ITranslationProvider provider)
    {
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        var capacity = new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, policy);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        return new LegendConnectAutonomousLearningService(
            db, registry, provider, capacity, corpus, new LegendConnectAutonomousGapPlanner(db, registry),
            configuration, runtimePolicy: policy);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "0",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98",
            ["AzureTranslator:Endpoint"] = "https://translator.example.test",
            ["AzureTranslator:Key"] = "test-key"
        }).Build();

    private static LegendCorpusCandidate Candidate(string key, string source, string target, string text) => new()
    {
        Id = Guid.NewGuid(), IdempotencyKey = key, SourceLanguageCode = source, TargetLanguageCode = target,
        SourceText = text, SourceTextHash = LegendLanguageIdentity.TextHash(text), Category = "ApprovedTestCorpus",
        Provenance = "ApprovedTestCorpus", IsApproved = true, ProcessingState = "Pending", CreatedUtc = DateTime.UtcNow
    };

    private sealed class TestAccess : IControlledResourceAccessService
    {
        private readonly bool _founder;
        public TestAccess(bool founder) => _founder = founder;
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, _founder));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(_founder);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(_founder);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        private int _translateCalls;
        public int TranslateCalls => _translateCalls;
        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));
        public Task<TranslationProviderResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref _translateCalls);
            return Task.FromResult(new TranslationProviderResult(true, "Translated", sourceLanguage, ProviderName));
        }
    }
}

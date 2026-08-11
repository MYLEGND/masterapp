using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Controllers;
using AgentPortal.Models;
using AgentPortal.Security;
using AgentPortal.Services;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

[Collection("LegendConnectFounderEnvironment")]
public sealed class LegendConnectContinuationTests
{
    [Fact]
    public async Task AutonomousApprovedGapSelection_UsesDemandAndWritesTheExistingDatasetAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration(enabled: true);
        var registry = new LegendLanguageRegistry(db, configuration);
        var highDemandPair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("en", "ht"));
        _ = await registry.GetOrCreateEnabledPairAsync("en", "fr");
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            Id = Guid.NewGuid(),
            PairKey = highDemandPair.PairKey,
            TranslationRequestCount = 50,
            LastRequestedUtc = DateTime.UtcNow
        });
        db.LegendCorpusCandidates.AddRange(
            Candidate("low", "en", "fr", "Low demand approved example"),
            Candidate("high", "en", "ht", "High demand approved example"));
        await db.SaveChangesAsync();

        var provider = new RecordingProvider();
        var corpus = Corpus(db, registry);
        var service = new LegendConnectAutonomousLearningService(
            db,
            registry,
            provider,
            Capacity(db, configuration),
            corpus,
            new LegendConnectAutonomousGapPlanner(db, registry),
            configuration);

        await service.ProcessOneAsync();

        Assert.Equal(1, provider.TranslateCalls);
        Assert.Equal("High demand approved example", provider.LastText);
        Assert.Contains(await db.LegendTranslationAlignments.ToListAsync(), item => item.PairKey == "en:ht");
        Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en" || item.LanguageCode == "ht"));
    }

    [Fact]
    public async Task DisabledCorpusAcquisition_MakesNoAutonomousAzureCall_AndLiveTranslationRemainsAvailable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var disabled = Configuration(enabled: false);
        var registry = new LegendLanguageRegistry(db, disabled);
        db.LegendCorpusCandidates.Add(Candidate("approved", "en", "ht", "Approved but disabled"));
        await db.SaveChangesAsync();
        var provider = new RecordingProvider();
        var autonomous = new LegendConnectAutonomousLearningService(
            db, registry, provider, Capacity(db, disabled), Corpus(db, registry),
            new LegendConnectAutonomousGapPlanner(db, registry), disabled);

        await autonomous.ProcessOneAsync();
        Assert.Equal(0, provider.TranslateCalls);

        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, disabled),
            NullLogger<LegendConnectTranslationRouter>.Instance);
        var live = await router.TranslateAsync("Live translation", "ht", "en");

        Assert.True(live.Succeeded);
        Assert.Equal(1, provider.TranslateCalls);
    }

    [Fact]
    public async Task ManualKnowledge_BlocksExactDuplicates_AllowsSimilarVariants_AndCreatesContextualRelationships()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var operations = Operations(db, registry, configuration);
        var input = new LegendConnectKnowledgeSubmission(
            "en", "Good morning", "ht", "Bonjou", "Greeting", "Formal", null, "IgnoredByFounderAuthority");

        var first = await operations.SubmitFounderKnowledgeAsync("founder", input);
        var duplicate = await operations.SubmitFounderKnowledgeAsync("founder", input);
        var variant = await operations.SubmitFounderKnowledgeAsync("founder", input with { SourceText = "Good morning!", TargetText = "Bonjou!" });

        Assert.True(first.Succeeded);
        Assert.True(duplicate.DuplicatePrevented);
        Assert.Equal("This exact entry already exists in this language.", duplicate.Message);
        Assert.True(variant.Succeeded);
        Assert.Equal(2, await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en"));
        Assert.Equal(2, await db.LegendLanguageContextRelationships.CountAsync(item => item.PairKey == "en:ht"));
        Assert.Equal(2, await db.LegendTranslationAlignments.CountAsync(item => item.PairKey == "en:ht"));
        Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), item => item.Result == "DuplicatePrevented");
        var languageHealth = Assert.IsType<LegendConnectLanguageHealthSnapshot>(await operations.GetLanguageHealthAsync("en"));
        Assert.Equal("/en", languageHealth.StoragePartition);
        Assert.Equal(2, languageHealth.ContextRelationshipCount);
    }

    [Fact]
    public async Task TrustedExactTranslationMemory_PrecedesAzureWithoutCreatingAnotherProvider()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = Corpus(db, registry);
        var inserted = await corpus.SubmitApprovedKnowledgeAsync(new LegendConnectKnowledgeSubmission(
            "en", "Approved exact phrase", "ht", "Fraz egzak apwouve", "Phrase", null, null, "FounderApproved"));
        Assert.True(inserted.Succeeded);

        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            Capacity(db, configuration),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));
        var result = await router.TranslateAsync("Approved exact phrase", "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("Fraz egzak apwouve", result.TranslatedText);
        Assert.Equal("LegendConnectTranslationMemory", result.Provider);
        Assert.Equal(0, provider.TranslateCalls);
    }

    [Fact]
    public async Task AutomatedLearning_DeduplicatesAnExistingCanonicalAlignmentWithoutCallingAzure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration(enabled: true);
        var registry = new LegendLanguageRegistry(db, configuration);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(await registry.GetOrCreateEnabledPairAsync("en", "ht"));
        var corpus = Corpus(db, registry);
        var existing = EligibleEvent(pair, "Approved source", "Approved target", "existing");
        db.LegendTranslationLearningEvents.Add(existing);
        await db.SaveChangesAsync();
        await corpus.ProcessAsync(existing);
        db.LegendCorpusCandidates.Add(Candidate("duplicate", "en", "ht", "Approved source"));
        await db.SaveChangesAsync();
        var provider = new RecordingProvider();
        var service = new LegendConnectAutonomousLearningService(
            db, registry, provider, Capacity(db, configuration), corpus,
            new LegendConnectAutonomousGapPlanner(db, registry), configuration);

        await service.ProcessOneAsync();

        Assert.Equal(0, provider.TranslateCalls);
        Assert.Equal("Deduplicated", (await db.LegendCorpusCandidates.SingleAsync()).ProcessingState);
        Assert.Single(await db.LegendTranslationAlignments.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentManualDuplicates_CreateOneCanonicalEntry()
    {
        var databaseName = "legend-connect-" + Guid.NewGuid().ToString("N");
        await using var keeper = new SqliteConnection($"Data Source=file:{databaseName}?mode=memory&cache=shared");
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<MasterAppDbContext>().UseSqlite(keeper).Options;
        await using (var seeded = new MasterAppDbContext(options))
        {
            await seeded.Database.EnsureCreatedAsync();
            _ = await new LegendLanguageRegistry(seeded, Configuration()).ListEnabledTranslationLanguagesAsync();
        }

        await using var dbOne = new MasterAppDbContext(options);
        await using var dbTwo = new MasterAppDbContext(options);
        var input = new LegendConnectKnowledgeSubmission("en", "Concurrent canonical entry", null, null, "Vocabulary", null, null, "FounderApproved");
        var first = Corpus(dbOne, new LegendLanguageRegistry(dbOne, Configuration())).SubmitApprovedKnowledgeAsync(input);
        var second = Corpus(dbTwo, new LegendLanguageRegistry(dbTwo, Configuration())).SubmitApprovedKnowledgeAsync(input);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results.Where(item => item.Succeeded));
        Assert.Single(results.Where(item => item.DuplicatePrevented));
        await using var verification = new MasterAppDbContext(options);
        Assert.Equal(1, await verification.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en"));
    }

    [Fact]
    public async Task ContextualIntelligence_IsLanguageIsolated_AndLowConfidenceFallsBackToAzure()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:ContextualComposition:Mode"] = "Active",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98"
        }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        var source = new LegendLanguageTextUnit { Id = Guid.NewGuid(), LanguageCode = "en", StoragePartition = "/en", NormalizedHash = LegendLanguageIdentity.TextHash("Many friends"), Text = "Many friends", Provenance = "Approved", IsTrainingEligible = true };
        var target = new LegendLanguageTextUnit { Id = Guid.NewGuid(), LanguageCode = "ht", StoragePartition = "/ht", NormalizedHash = LegendLanguageIdentity.TextHash("Anpil zanmi"), Text = "Anpil zanmi", Provenance = "Approved", IsTrainingEligible = true };
        db.AddRange(source, target);
        db.LegendLanguageContextRelationships.Add(new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(), PairKey = "en:ht", SourceTextUnitId = source.Id, RelatedTextUnitId = target.Id,
            RelationshipKind = "ContextualExample", ContextSignature = "greeting||",
            SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(source.Text),
            Confidence = 0.5m, QualityState = "Observation", Provenance = "Approved", ObservationCount = 1
        });
        await db.SaveChangesAsync();

        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider, registry, Capacity(db, configuration), NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration));
        var result = await router.TranslateAsync("Good people", "ht", "en");

        Assert.True(result.Succeeded);
        Assert.Equal("AzureTranslator", result.Provider);
        Assert.Equal(1, provider.TranslateCalls);
        Assert.DoesNotContain(await db.LegendLanguageContextRelationships.ToListAsync(), item => item.PairKey == "en:fr");
    }

    [Fact]
    public async Task FounderPageAndMutations_AreFounderOnly_AndCorrectionIsAuditable()
    {
        var previousFounder = Environment.GetEnvironmentVariable("FOUNDER_OID");
        var founderOid = Guid.NewGuid().ToString();
        Environment.SetEnvironmentVariable("FOUNDER_OID", founderOid);
        try
        {
            await using var db = ControllerTestHelpers.BuildDb();
            var configuration = Configuration();
            var registry = new LegendLanguageRegistry(db, configuration);
            var service = new FounderLegendConnectService(Operations(db, registry, configuration));
            var founder = ControllerTestHelpers.BuildUser(founderOid);
            var stranger = ControllerTestHelpers.BuildUser(Guid.NewGuid().ToString());
            var controller = new LegendConnectController(service, NullLogger<LegendConnectController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = founder } }
            };

            Assert.IsType<ViewResult>(await controller.Index(null, null, CancellationToken.None));
            var submitted = await service.SubmitAsync(founder, new FounderLegendConnectKnowledgeInput
            {
                SourceLanguageCode = "en", SourceText = "Correction source", TargetLanguageCode = "ht", TargetText = "Correction target", ContextCategory = "Phrase"
            });
            Assert.True(submitted.Succeeded);
            var corrected = await service.CorrectAsync(founder, new FounderLegendConnectCorrectionInput
            {
                SupersededAlignmentId = submitted.AlignmentId!.Value,
                SourceLanguageCode = "en", SourceText = "Replacement source", TargetLanguageCode = "ht", TargetText = "Replacement target", ContextCategory = "Phrase"
            });
            Assert.True(corrected.Succeeded);
            Assert.NotNull((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == submitted.AlignmentId)).SupersededUtc);
            Assert.Contains(await db.LegendConnectKnowledgeAuditEntries.ToListAsync(), item => item.Action == "FounderKnowledgeCorrected");

            var forbiddenController = new LegendConnectController(service, NullLogger<LegendConnectController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = stranger } }
            };
            Assert.IsType<ForbidResult>(await forbiddenController.Index(null, null, CancellationToken.None));
            Assert.IsType<ForbidResult>(await forbiddenController.SubmitKnowledge(
                new FounderLegendConnectKnowledgeInput { SourceLanguageCode = "en", SourceText = "Denied" },
                CancellationToken.None));
            await Assert.ThrowsAsync<ForbidResultException>(() => service.SubmitAsync(stranger, new FounderLegendConnectKnowledgeInput { SourceLanguageCode = "en", SourceText = "Denied" }));
            Assert.IsType<FounderOnlyAttribute>(typeof(LegendConnectController).GetCustomAttributes(typeof(FounderOnlyAttribute), inherit: true).Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOUNDER_OID", previousFounder);
        }
    }

    [Fact]
    public async Task HealthRemainsDirectional_AndSurfacesPairBoundedFailures()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        _ = await registry.GetOrCreateEnabledPairAsync("ht", "en");
        db.LegendConnectOperationalEvents.Add(new LegendConnectOperationalEvent
        {
            Id = Guid.NewGuid(), Category = "AzureProvider", Severity = "Error", Status = "Failed",
            PairKey = "en:ht", LanguageCode = "en", ErrorCode = "translation_provider_failed",
            Summary = "Bounded test event", OccurredUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var operations = Operations(db, registry, configuration);

        var forward = Assert.IsType<LegendConnectPairHealthSnapshot>(await operations.GetPairHealthAsync("en:ht"));
        var reverse = Assert.IsType<LegendConnectPairHealthSnapshot>(await operations.GetPairHealthAsync("ht:en"));
        var language = Assert.IsType<LegendConnectLanguageHealthSnapshot>(await operations.GetLanguageHealthAsync("en"));

        Assert.Equal("en:ht", forward.PairKey);
        Assert.Equal("ht:en", reverse.PairKey);
        Assert.NotEqual(forward.RecentErrors.Count, reverse.RecentErrors.Count);
        Assert.Contains(language.RecentErrors, item => item.PairKey == "en:ht");
    }

    private static IConfiguration Configuration(bool enabled = false) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = enabled ? "true" : "false",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "10000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "100",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:ContextualComposition:MinimumConfidence"] = "0.98"
        }).Build();

    private static LegendConnectCorpusService Corpus(MasterAppDbContext db, ILegendLanguageRegistry registry) =>
        new(db, registry, NullLogger<LegendConnectCorpusService>.Instance);

    private static TranslationCapacityAuthority Capacity(MasterAppDbContext db, IConfiguration configuration) =>
        new(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance);

    private static LegendConnectOperations Operations(MasterAppDbContext db, ILegendLanguageRegistry registry, IConfiguration configuration) =>
        new(db, registry, Corpus(db, registry), configuration);

    private static LegendCorpusCandidate Candidate(string key, string source, string target, string text) => new()
    {
        Id = Guid.NewGuid(), IdempotencyKey = key, SourceLanguageCode = source, TargetLanguageCode = target,
        SourceText = text, SourceTextHash = LegendLanguageIdentity.TextHash(text), Category = "ApprovedTestCorpus",
        Provenance = "ApprovedTestCorpus", IsApproved = true, ProcessingState = "Pending", CreatedUtc = DateTime.UtcNow
    };

    private static LegendTranslationLearningEvent EligibleEvent(LegendLanguagePairSnapshot pair, string source, string target, string suffix) => new()
    {
        Id = Guid.NewGuid(), IdempotencyKey = "continuation:" + suffix,
        SourceLanguageCode = pair.SourceLanguageCode, TargetLanguageCode = pair.TargetLanguageCode, PairKey = pair.PairKey,
        SourceTextHash = LegendLanguageIdentity.TextHash(source), TargetTextHash = LegendLanguageIdentity.TextHash(target),
        SourceText = source, TargetText = target, Provider = "AzureTranslator", Provenance = "ApprovedTestCorpus",
        EligibilityState = "Eligible", ProcessingState = "Pending", CreatedUtc = DateTime.UtcNow
    };

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }
        public string? LastText { get; private set; }
        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));
        public Task<TranslationProviderResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            LastText = text;
            return Task.FromResult(new TranslationProviderResult(true, "Approved translation", sourceLanguage, ProviderName));
        }
    }
}

[CollectionDefinition("LegendConnectFounderEnvironment", DisableParallelization = true)]
public sealed class LegendConnectFounderEnvironmentCollection { }

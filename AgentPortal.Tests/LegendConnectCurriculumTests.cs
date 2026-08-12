using System;
using System.Collections.Generic;
using System.Linq;
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

public sealed class LegendConnectCurriculumTests
{
    [Fact]
    public async Task FounderEnglishCurriculum_CreatesFamilyEnglishEvidenceAndIsIdempotent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);

        var first = await curriculum.SubmitFounderEnglishBatchAsync(Batch("possession.basic"));
        var second = await curriculum.SubmitFounderEnglishBatchAsync(Batch("possession.basic"));

        Assert.True(first.Succeeded, first.Message);
        Assert.False(first.DuplicatePrevented);
        Assert.True(second.Succeeded, second.Message);
        Assert.True(second.DuplicatePrevented);
        var family = Assert.Single(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Equal("possession.basic", family.FamilyKey);
        var englishExamples = await db.LegendCurriculumExamples
            .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == "en")
            .ToListAsync();
        Assert.Equal(4, englishExamples.Count);
        Assert.Equal(12, await db.LegendCurriculumExampleVariations
            .CountAsync(item => englishExamples.Select(example => example.Id).Contains(item.CurriculumExampleId)));
        Assert.All(await db.LegendLanguageStructuralEvidence.ToListAsync(), evidence =>
            Assert.Equal("en", evidence.LanguageCode));
        Assert.NotEmpty(await db.LegendLanguageStructuralPatterns
            .Where(item => item.LanguageCode == "en" && item.CurriculumFamilyId == family.Id)
            .ToListAsync());
        Assert.Equal(4, await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en"));
        Assert.Equal(
            await db.LegendCorpusCandidates.Select(item => item.IdempotencyKey).Distinct().CountAsync(),
            await db.LegendCorpusCandidates.CountAsync());
    }

    [Fact]
    public async Task AzureExpandedCurriculum_TargetLanguagesLearnFromTheirOwnExamples_AndPatternsRemainGated()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var submitted = await curriculum.SubmitFounderEnglishBatchAsync(Batch("possession.basic"));
        Assert.True(submitted.Succeeded, submitted.Message);

        await ProcessQueuedCurriculumAsync(db, configuration, registry, corpus, curriculum, new ShapePreservingProvider());

        var family = Assert.Single(await db.LegendCurriculumFamilies.ToListAsync());
        foreach (var language in new[] { "es", "ar", "ja" })
        {
            var examples = await db.LegendCurriculumExamples
                .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == language)
                .ToListAsync();
            Assert.Equal(4, examples.Count);
            Assert.All(examples, item => Assert.NotNull(item.DerivedFromCurriculumExampleId));
            Assert.Equal(12, await db.LegendCurriculumExampleVariations
                .CountAsync(item => examples.Select(example => example.Id).Contains(item.CurriculumExampleId)));

            var evidence = await db.LegendLanguageStructuralEvidence
                .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == language)
                .ToListAsync();
            Assert.NotEmpty(evidence);
            var exampleIds = examples.Select(item => item.Id).ToHashSet();
            Assert.All(evidence, item =>
            {
                Assert.Contains(item.BaselineCurriculumExampleId, exampleIds);
                Assert.Contains(item.ComparedCurriculumExampleId, exampleIds);
            });

            var patterns = await db.LegendLanguageStructuralPatterns
                .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == language)
                .ToListAsync();
            Assert.Contains(patterns, item => item.MaturityState == "Supported" && item.SupportCount >= 3);
            Assert.All(patterns, item => Assert.False(item.IsProductionEligible));
        }

        var fallbackProvider = new CountingProvider();
        var router = new LegendConnectTranslationRouter(
            fallbackProvider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration),
            structuralComposition: curriculum);
        var unseen = await router.TranslateAsync("An unseen curriculum sentence.", "es", "en");
        Assert.True(unseen.Succeeded);
        Assert.Equal(1, fallbackProvider.TranslateCalls);

        Assert.All(await db.LegendLanguageStructuralEvidence.ToListAsync(), evidence =>
        {
            var baseline = db.LegendCurriculumExamples.Single(item => item.Id == evidence.BaselineCurriculumExampleId);
            var compared = db.LegendCurriculumExamples.Single(item => item.Id == evidence.ComparedCurriculumExampleId);
            Assert.Equal(evidence.LanguageCode, baseline.LanguageCode);
            Assert.Equal(evidence.LanguageCode, compared.LanguageCode);
        });
    }

    [Fact]
    public async Task ContradictoryTargetEvidence_DoesNotPromoteOrValidateAStructuralPattern()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var submitted = await curriculum.SubmitFounderEnglishBatchAsync(Batch("possession.contradiction"));
        Assert.True(submitted.Succeeded, submitted.Message);

        await ProcessQueuedCurriculumAsync(db, configuration, registry, corpus, curriculum, new ContradictoryShapeProvider());

        var family = await db.LegendCurriculumFamilies.SingleAsync(item => item.FamilyKey == "possession.contradiction");
        var pattern = await db.LegendLanguageStructuralPatterns
            .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == "es" && item.VariationDimension == "person")
            .OrderByDescending(item => item.ContradictionCount)
            .FirstAsync();
        Assert.True(pattern.ContradictionCount > 0);
        Assert.Equal("Observation", pattern.MaturityState);
        Assert.False(await curriculum.TryValidatePatternAsync(pattern.Id));
        Assert.False((await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == pattern.Id)).IsProductionEligible);
    }

    [Fact]
    public async Task ExistingSingleEntryFounderTraining_RemainsTheCanonicalCorpusPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);

        var result = await corpus.SubmitApprovedKnowledgeAsync(new LegendConnectKnowledgeSubmission(
            "en", "A single Founder seed.", null, null, "EverydayConversation", null, null, "FounderApproved"));

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.SourceTextUnitId);
        Assert.NotEmpty(await db.LegendCorpusCandidates.ToListAsync());
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
    }

    private static LegendConnectCurriculumBatchSubmission Batch(string familyKey) => new(
        familyKey,
        "Possession",
        [
            new LegendConnectCurriculumExampleSubmission("I have red.", Variations("first", "red")),
            new LegendConnectCurriculumExampleSubmission("She has blue.", Variations("third", "blue")),
            new LegendConnectCurriculumExampleSubmission("You have gold.", Variations("second", "gold")),
            new LegendConnectCurriculumExampleSubmission("They have green.", Variations("plural", "green"))
        ]);

    private static IReadOnlyDictionary<string, string> Variations(string person, string @object) =>
        new Dictionary<string, string>
        {
            ["person"] = person,
            ["tense"] = "present",
            ["object"] = @object
        };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
        }).Build();

    private static async Task ProcessQueuedCurriculumAsync(
        Infrastructure.Data.MasterAppDbContext db,
        IConfiguration configuration,
        ILegendLanguageRegistry registry,
        LegendConnectCorpusService corpus,
        LegendConnectCurriculumService curriculum,
        ITranslationProvider provider)
    {
        var worker = new LegendConnectAutonomousLearningService(
            db,
            registry,
            provider,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            corpus,
            new LegendConnectAutonomousGapPlanner(db, registry),
            configuration,
            curriculum: curriculum);
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (!await db.LegendCorpusCandidates.AnyAsync(item => item.ProcessingState == "Pending"))
                return;
            await worker.ProcessOneAsync();
        }
        Assert.False(await db.LegendCorpusCandidates.AnyAsync(item => item.ProcessingState == "Pending"));
    }

    private sealed class ShapePreservingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationProviderResult(true, $"{targetLanguage} form {Suffix(text)}", sourceLanguage, ProviderName));
    }

    private sealed class ContradictoryShapeProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            var translated = text.Contains("red", StringComparison.Ordinal)
                ? $"{targetLanguage} x a"
                : text.Contains("blue", StringComparison.Ordinal)
                    ? $"{targetLanguage} x y b"
                    : text.Contains("gold", StringComparison.Ordinal)
                        ? $"{targetLanguage} x y z c"
                        : $"{targetLanguage} x y z d";
            return Task.FromResult(new TranslationProviderResult(true, translated, sourceLanguage, ProviderName));
        }
    }

    private sealed class CountingProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            return Task.FromResult(new TranslationProviderResult(true, "Azure fallback result", sourceLanguage, ProviderName));
        }
    }

    private static string Suffix(string text) => text.Contains("red", StringComparison.Ordinal)
        ? "a"
        : text.Contains("blue", StringComparison.Ordinal)
            ? "b"
            : text.Contains("gold", StringComparison.Ordinal)
                ? "c"
                : "d";
}

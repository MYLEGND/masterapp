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
            Assert.All(examples, item => Assert.Equal("ProviderDerived", item.Provenance));
            var targetUnitIds = examples.Select(item => item.TextUnitId).ToArray();
            Assert.All(await db.LegendLanguageTextUnits
                .Where(item => targetUnitIds.Contains(item.Id))
                .ToListAsync(), item => Assert.Equal("ProviderDerived", item.Provenance));
            Assert.Equal(12, await db.LegendCurriculumExampleVariations
                .CountAsync(item => examples.Select(example => example.Id).Contains(item.CurriculumExampleId)));

            var evidence = await db.LegendLanguageStructuralEvidence
                .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == language)
                .ToListAsync();
            Assert.NotEmpty(evidence);
            Assert.All(evidence, item => Assert.Equal("ProviderDerived", item.Provenance));
            var exampleIds = examples.Select(item => item.Id).ToHashSet();
            Assert.All(evidence, item =>
            {
                Assert.Contains(item.BaselineCurriculumExampleId, exampleIds);
                Assert.Contains(item.ComparedCurriculumExampleId, exampleIds);
            });

            var patterns = await db.LegendLanguageStructuralPatterns
                .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == language)
                .ToListAsync();
            Assert.Contains(patterns, item => item.ProviderOnlySupportCount > 0);
            Assert.All(patterns, item =>
            {
                Assert.Equal(0, item.SupportCount);
                Assert.NotEqual("Supported", item.MaturityState);
                Assert.NotEqual("Validated", item.MaturityState);
            });
            Assert.All(patterns, item => Assert.False(item.IsProductionEligible));
            Assert.All(patterns, item => Assert.Equal("ProviderDerived", item.Provenance));
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
    public async Task ProviderOnlyTargetEvidence_DoesNotPromoteOrValidateAStructuralPattern()
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
        var patterns = await db.LegendLanguageStructuralPatterns
            .Where(item => item.CurriculumFamilyId == family.Id && item.LanguageCode == "es" && item.VariationDimension == "person")
            .ToListAsync();
        Assert.NotEmpty(patterns);
        Assert.All(patterns, pattern =>
        {
            Assert.Equal(0, pattern.SupportCount);
            Assert.True(pattern.ProviderOnlySupportCount > 0);
            Assert.NotEqual("Supported", pattern.MaturityState);
            Assert.False(pattern.IsProductionEligible);
        });
        foreach (var pattern in patterns)
            Assert.False(await curriculum.TryValidatePatternAsync(pattern.Id));
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

    [Fact]
    public async Task FounderCurriculumManifest_MultipleFamiliesCommitThroughExistingAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);

        var result = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-test",
            new LegendConnectCurriculumManifestSubmission(
            [
                ConversationBatch(
                    "conversation.greeting.basic",
                    "Conversation greeting",
                    ("Hi.", "greeting"),
                    ("Hello.", "greeting")),
                ConversationBatch(
                    "conversation.clarification.basic",
                    "Conversation clarification",
                    ("What do you mean?", "clarification_request"),
                    ("Can you explain that?", "clarification_request"))
            ]));

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("durably queued", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Empty(await db.LegendCurriculumExamples.ToListAsync());

        var processor = new LegendConnectCurriculumManifestProcessor(
            db,
            curriculum,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);

        Assert.Equal(1, await processor.ProcessPendingAsync(1));
        Assert.Single(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Equal(2, await db.LegendCurriculumExamples.CountAsync(item => item.LanguageCode == "en"));

        Assert.Equal(1, await processor.ProcessPendingAsync(1));
        Assert.Equal(2, await db.LegendCurriculumFamilies.CountAsync());
        Assert.Equal(4, await db.LegendCurriculumExamples.CountAsync(item => item.LanguageCode == "en"));

        var work = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal("Completed", work.ProcessingState);
        Assert.Equal(2, work.NextFamilyIndex);
    }

    [Fact]
    public async Task FounderCurriculumManifest_InvalidLaterFamilyCausesZeroPartialMutation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);

        var result = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-test",
            new LegendConnectCurriculumManifestSubmission(
            [
                ConversationBatch(
                    "conversation.greeting.basic",
                    "Conversation greeting",
                    ("Hi.", "greeting"),
                    ("Hello.", "greeting")),
                ConversationBatch(
                    "conversation.invalid.single",
                    "Invalid single example",
                    ("Only one example.", "statement"))
            ]));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_curriculum_examples", result.ErrorCode);
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Empty(await db.LegendCurriculumExamples.ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());
    }

    [Fact]
    public async Task FounderCurriculumManifest_RejectsFamilyOverOneHundredExamplesBeforeMutation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);

        var oversized = new LegendConnectCurriculumBatchSubmission(
            "conversation.oversized",
            "Conversation test",
            Enumerable.Range(1, 101)
                .Select(index => new LegendConnectCurriculumExampleSubmission(
                    $"Utterance {index}.",
                    new Dictionary<string, string> { ["function"] = $"variation_{index}" }))
                .ToArray());

        var result = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-test",
            new LegendConnectCurriculumManifestSubmission([oversized]));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_curriculum_examples", result.ErrorCode);
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());
    }

    [Fact]
    public async Task FounderCurriculumManifest_RejectsConflictingSemanticCategoryForExistingFamily()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);

        var first = await curriculum.SubmitFounderEnglishBatchAsync(
            ConversationBatch(
                "conversation.greeting.basic",
                "Conversation greeting",
                ("Hi.", "greeting"),
                ("Hello.", "greeting")));
        Assert.True(first.Succeeded, first.Message);
        var originalTextUnitCount = await db.LegendLanguageTextUnits.CountAsync();

        var result = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-test",
            new LegendConnectCurriculumManifestSubmission(
            [
                ConversationBatch(
                    "conversation.greeting.basic",
                    "Unrelated apology category",
                    ("Hey.", "greeting"),
                    ("Hey there.", "greeting"))
            ]));

        Assert.False(result.Succeeded);
        Assert.Equal("curriculum_family_category_conflict", result.ErrorCode);
        Assert.Equal(originalTextUnitCount, await db.LegendLanguageTextUnits.CountAsync());
        Assert.Single(await db.LegendCurriculumFamilies.ToListAsync());
    }


    [Fact]
    public async Task FounderCurriculumManifest_LargeShapeQueuesWithoutSynchronousLearning()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);

        var families = new List<LegendConnectCurriculumBatchSubmission>();
        var remaining = 719;
        for (var familyIndex = 0; familyIndex < 42; familyIndex++)
        {
            var familiesLeft = 42 - familyIndex;
            var count = Math.Min(100, Math.Max(2, (int)Math.Ceiling(remaining / (double)familiesLeft)));
            remaining -= count;
            families.Add(new LegendConnectCurriculumBatchSubmission(
                $"conversation.scale.family_{familyIndex:D2}",
                "Scale regression",
                Enumerable.Range(1, count)
                    .Select(exampleIndex => new LegendConnectCurriculumExampleSubmission(
                        $"Family {familyIndex} example {exampleIndex}.",
                        new Dictionary<string, string>
                        {
                            ["function"] = $"function_{familyIndex}",
                            ["variation"] = $"value_{exampleIndex}"
                        }))
                    .ToArray()));
        }

        Assert.Equal(0, remaining);
        Assert.Equal(719, families.Sum(item => item.Examples.Count));

        var result = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-scale",
            new LegendConnectCurriculumManifestSubmission(families));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(719, result.EnglishExampleCount);
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Empty(await db.LegendCurriculumExamples.ToListAsync());
        Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());

        var work = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal("Pending", work.ProcessingState);
        Assert.Equal(42, work.FamilyCount);
        Assert.Equal(719, work.ExampleCount);
        Assert.Equal(0, work.NextFamilyIndex);
    }

    [Fact]
    public async Task FounderCurriculumManifest_ExactResubmissionIsOneDurableWorkItem()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);
        var manifest = new LegendConnectCurriculumManifestSubmission(
        [
            ConversationBatch(
                "conversation.retry.basic",
                "Retry safety",
                ("Please try again.", "retry"),
                ("Let's try again.", "retry"))
        ]);

        var first = await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest);
        var second = await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(second.DuplicatePrevented);
        Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
    }

    [Fact]
    public async Task FounderCurriculumManifest_ExpiredLeaseResumesWithoutDuplicateFamilyKnowledge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);
        var processor = new LegendConnectCurriculumManifestProcessor(
            db,
            curriculum,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);

        var manifest = new LegendConnectCurriculumManifestSubmission(
        [
            ConversationBatch(
                "conversation.resume.first",
                "Resume first",
                ("I understand.", "understood"),
                ("I don't understand.", "not_understood")),
            ConversationBatch(
                "conversation.resume.second",
                "Resume second",
                ("Can you repeat that?", "repeat"),
                ("Could you say that again?", "repeat"))
        ]);

        Assert.True((await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest)).Succeeded);
        Assert.Equal(1, await processor.ProcessPendingAsync(1));

        var work = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal(1, work.NextFamilyIndex);

        work.ProcessingState = "Processing";
        work.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.Equal(1, await processor.ProcessPendingAsync(1));
        Assert.Equal(2, await db.LegendCurriculumFamilies.CountAsync());

        var completed = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal("Completed", completed.ProcessingState);
        Assert.Equal(2, completed.NextFamilyIndex);

        var duplicate = await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest);
        Assert.True(duplicate.Succeeded);
        Assert.True(duplicate.DuplicatePrevented);
        Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal(2, await db.LegendCurriculumFamilies.CountAsync());
    }

    private static LegendConnectCurriculumBatchSubmission ConversationBatch(
        string familyKey,
        string category,
        params (string Text, string Function)[] examples) =>
        new(
            familyKey,
            category,
            examples.Select(item => new LegendConnectCurriculumExampleSubmission(
                item.Text,
                new Dictionary<string, string>
                {
                    ["function"] = item.Function,
                    ["intent"] = item.Function
                })).ToArray());

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

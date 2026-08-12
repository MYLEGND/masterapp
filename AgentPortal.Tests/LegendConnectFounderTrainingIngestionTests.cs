using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectFounderTrainingIngestionTests
{
    [Fact]
    public async Task SourceTraining_DecomposesMixedFounderTextIntoAtomicCanonicalUnits_AndIsIdempotent()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);

        var raw = """
            person
            family
            understand
            I understand you.
            I do not understand.
            Do you understand?
            I work every day.
            I worked yesterday.
            I will work tomorrow.
            If you need me, call me.

            Every morning, I drink coffee.
            He eats rice.
            Today, we are going home.
            """;

        var first = await ingestion.SubmitAsync("founder-1", SourceSeed(raw));
        var duplicate = await ingestion.SubmitAsync("founder-1", SourceSeed(raw));

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(13, first.AtomicUnitCount);
        Assert.Equal(13, first.NewCanonicalUnitCount);
        Assert.True(first.QueuedCoverageCount > 0);
        Assert.True(duplicate.Succeeded, duplicate.Message);
        Assert.True(duplicate.DuplicatePrevented);
        Assert.Equal(13, duplicate.AtomicUnitCount);
        Assert.Equal(13, await db.LegendFounderTrainingSubmissionUnits.CountAsync());
        Assert.Single(await db.LegendFounderTrainingSubmissions.ToListAsync());
        Assert.Equal(13, await db.LegendLanguageTextUnits.CountAsync(item => item.LanguageCode == "en" && item.IsTrainingEligible));
        Assert.NotEmpty(await db.LegendLanguageLexemes.ToListAsync());
        Assert.Equal(2, await db.LegendLanguageLexicalOccurrences.CountAsync(item =>
            item.SupersededUtc == null && db.LegendLanguageLexemes.Any(lexeme => lexeme.Id == item.LexemeId && lexeme.SurfaceForm == "work")));
        Assert.NotEmpty(await db.LegendLanguageLexicalRelationships.Where(item => item.SupersededUtc == null).ToListAsync());
        Assert.NotEmpty(await db.LegendLanguageContextRelationships
            .Where(item => item.RelationshipKind == "AdjacentSentence" && item.SupersededUtc == null)
            .ToListAsync());
        Assert.DoesNotContain(await db.LegendLanguageTextUnits.ToListAsync(), item => item.Text == raw);
        Assert.All(await db.LegendCorpusCandidates.ToListAsync(), item =>
        {
            Assert.NotEqual(raw, item.SourceText);
            Assert.Contains(item.SourceText, new[]
            {
                "person", "family", "understand", "I understand you.", "I do not understand.",
                "Do you understand?", "I work every day.", "I worked yesterday.", "I will work tomorrow.",
                "If you need me, call me.", "Every morning, I drink coffee.", "He eats rice.", "Today, we are going home."
            });
        });
    }

    [Fact]
    public void Segmenter_PreservesPunctuationAndDoesNotSplitKnownAbbreviations()
    {
        var units = LegendFounderTrainingSegmenter.Segment("Dr. King works in the U.S. office. Are you ready?");

        Assert.Collection(units,
            item => Assert.Equal("Dr. King works in the U.S. office.", item.Text),
            item => Assert.Equal("Are you ready?", item.Text));
    }

    [Fact]
    public async Task LegacyMultiUnitFounderAsset_IsReconciledNonDestructivelyAndCannotRemainReusable()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var pair = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        Assert.NotNull(pair);

        var raw = "person\nfamily\nI understand you.\nI do not understand.\nDo you understand?";
        var source = TextUnit("en", raw, "FounderApproved");
        var target = TextUnit("ht", "moun fanmi mwen konprann ou", "AzureTranslator");
        var alignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = pair!.PairKey,
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "AzureTranslator",
            Confidence = .9m,
            QualityState = "Observation",
            ObservationCount = 1
        };
        var context = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(),
            PairKey = pair.PairKey,
            SourceTextUnitId = source.Id,
            RelatedTextUnitId = target.Id,
            RelationshipKind = "ContextualExample",
            ContextSignature = "legacy",
            SourcePatternSignature = "legacy",
            Confidence = .9m,
            QualityState = "Observation",
            Provenance = "FounderApproved",
            ObservationCount = 1
        };
        var candidate = new LegendCorpusCandidate
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "legacy-giant-source",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ht",
            SourceText = raw,
            SourceTextHash = source.NormalizedHash,
            Category = "FounderApprovedSeed",
            Provenance = "FounderApproved",
            IsApproved = true,
            ProcessingState = "Pending"
        };
        var learningEvent = new LegendTranslationLearningEvent
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = "legacy-giant-event",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ht",
            PairKey = pair.PairKey,
            SourceTextHash = source.NormalizedHash,
            TargetTextHash = target.NormalizedHash,
            SourceText = raw,
            TargetText = target.Text,
            Provider = "AzureTranslator",
            Provenance = "FounderApproved",
            EligibilityState = "Eligible",
            ProcessingState = "Pending"
        };
        db.AddRange(source, target, alignment, context, candidate, learningEvent);
        await db.SaveChangesAsync();

        var repaired = await ingestion.ReconcileLegacyAsync(25);
        var repeated = await ingestion.ReconcileLegacyAsync(25);

        Assert.Equal(1, repaired.ReconciledSubmissionCount);
        Assert.Equal(5, repaired.AtomicUnitCount);
        Assert.Equal(0, repeated.ReconciledSubmissionCount);
        Assert.False((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == source.Id)).IsTrainingEligible);
        Assert.False((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == target.Id)).IsTrainingEligible);
        Assert.NotNull((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == alignment.Id)).SupersededUtc);
        Assert.NotNull((await db.LegendLanguageContextRelationships.SingleAsync(item => item.Id == context.Id)).SupersededUtc);
        var retiredCandidate = await db.LegendCorpusCandidates.SingleAsync(item => item.Id == candidate.Id);
        Assert.False(retiredCandidate.IsApproved);
        Assert.Equal("Superseded", retiredCandidate.ProcessingState);
        Assert.Equal("Superseded", (await db.LegendTranslationLearningEvents.SingleAsync(item => item.Id == learningEvent.Id)).ProcessingState);
        Assert.DoesNotContain(await db.LegendCorpusCandidates.ToListAsync(), item => item.IsApproved && item.SourceText == raw);
        Assert.Contains(await db.LegendFounderTrainingSubmissions.ToListAsync(), item =>
            item.LegacySourceTextUnitId == source.Id && item.ProcessingState == "Reconciled");
    }

    [Fact]
    public async Task RetiredAssets_AreExcludedFromTranslationMemoryAndContextEvaluation()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var source = TextUnit("en", "Good morning.", "FounderApproved");
        var target = TextUnit("ht", "Bonjou.", "AzureTranslator");
        source.IsTrainingEligible = false;
        target.IsTrainingEligible = false;
        db.AddRange(source, target,
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(), PairKey = "en:ht", SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
                Provider = "Founder", Confidence = 1m, QualityState = "Verified", HumanVerified = true, ObservationCount = 1
            },
            new LegendLanguageContextRelationship
            {
                Id = Guid.NewGuid(), PairKey = "en:ht", SourceTextUnitId = source.Id, RelatedTextUnitId = target.Id,
                RelationshipKind = "ContextualExample", ContextSignature = "", SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(source.Text),
                Confidence = 1m, QualityState = "Verified", Provenance = "FounderApproved", ObservationCount = 1
            });
        await db.SaveChangesAsync();
        var intelligence = new LegendConnectTranslationIntelligence(db, Configuration());

        Assert.Null(await intelligence.TryGetTrustedExactMemoryAsync("en", "ht", source.Text));
        Assert.Null(await intelligence.EvaluateContextAsync("en", "ht", source.Text));
    }

    [Fact]
    public async Task ExplicitEnglishVariations_AnchorLexemesAndAccumulateThenContradictStructuralEvidenceAcrossBatches()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);

        var first = await curriculum.SubmitFounderEnglishBatchAsync(FamilyBatch(
            ("I work.", "first", "work"),
            ("She works.", "third", "works")));
        var second = await curriculum.SubmitFounderEnglishBatchAsync(FamilyBatch(
            ("I walk.", "first", "walk"),
            ("She walks.", "third", "walks")));

        Assert.True(first.Succeeded, first.Message);
        Assert.True(second.Succeeded, second.Message);
        Assert.NotEmpty(await db.LegendLanguageCompositionalAnchors
            .Where(item => item.Dimension == "person" && item.SupersededUtc == null)
            .ToListAsync());
        Assert.NotEmpty(await db.LegendLanguageCompositionalAnchors
            .Where(item => item.Dimension == "action" && item.LexemeId != null && item.SupersededUtc == null)
            .ToListAsync());
        Assert.Contains(await db.LegendLanguageStructuralEvidence.ToListAsync(), item =>
            item.LanguageCode == "en" && !string.IsNullOrWhiteSpace(item.BaselineComponentSignature) &&
            !string.IsNullOrWhiteSpace(item.ComparedComponentSignature));

        var supportedBeforeContradiction = await db.LegendLanguageStructuralPatterns
            .Where(item => item.LanguageCode == "en" && item.VariationDimension == "person")
            .OrderByDescending(item => item.SupportCount)
            .FirstAsync();
        Assert.True(supportedBeforeContradiction.SupportCount >= 2);
        Assert.Equal(0, supportedBeforeContradiction.ContradictionCount);

        var third = await curriculum.SubmitFounderEnglishBatchAsync(FamilyBatch(
            ("I speak.", "first", "speak"),
            ("She speaks.", "third", "speaks")));

        Assert.True(third.Succeeded, third.Message);
        var originalPattern = await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == supportedBeforeContradiction.Id);
        Assert.True(originalPattern.ContradictionCount > 0);
        Assert.Equal("Observation", originalPattern.MaturityState);
        Assert.False(await curriculum.TryValidatePatternAsync(originalPattern.Id));
    }

    [Fact]
    public async Task ExplicitEnglishSemanticRoles_CanAnchorAnExactMultiwordComponentWithoutGuessing()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var submission = new LegendConnectCurriculumBatchSubmission(
            "english.reading.roles",
            "Reading roles",
            [
                new LegendConnectCurriculumExampleSubmission("The child reads the book.", new Dictionary<string, string>
                {
                    ["subject"] = "child", ["action"] = "reads", ["direct_object"] = "the book"
                }),
                new LegendConnectCurriculumExampleSubmission("The child reads the letter.", new Dictionary<string, string>
                {
                    ["subject"] = "child", ["action"] = "reads", ["direct_object"] = "the letter"
                })
            ]);

        var result = await curriculum.SubmitFounderEnglishBatchAsync(submission);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, await db.LegendLanguageCompositionalAnchors.CountAsync(item =>
            item.Dimension == "direct_object" && item.LexemeId != null && item.ComponentLength == 2 &&
            item.SupersededUtc == null));
    }

    private static LegendConnectKnowledgeSubmission SourceSeed(string raw) => new(
        "en", raw, null, null, "Everyday conversation", null, null, "FounderApproved");

    private static LegendConnectCurriculumBatchSubmission FamilyBatch(
        params (string Text, string Person, string Action)[] examples) => new(
        "english.person.surface",
        "English person surface evidence",
        examples.Select(item => new LegendConnectCurriculumExampleSubmission(
            item.Text,
            new Dictionary<string, string>
            {
                ["person"] = item.Person,
                ["action"] = item.Action
            })).ToList());

    private static LegendLanguageTextUnit TextUnit(string languageCode, string text, string provenance) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = provenance,
        IsTrainingEligible = true
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0"
        }).Build();
}

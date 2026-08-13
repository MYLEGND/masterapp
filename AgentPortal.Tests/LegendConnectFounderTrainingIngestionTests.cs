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
    public async Task AtomicFounderSources_KeepProviderTargetsAsObservationsAcrossEverySharedLanguagePath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var submitted = await ingestion.SubmitAsync("founder-1", SourceSeed("I understand you."));
        Assert.True(submitted.Succeeded, submitted.Message);

        var source = await db.LegendLanguageTextUnits.SingleAsync(item =>
            item.LanguageCode == "en" && item.Text == "I understand you.");
        var targets = new List<LegendLanguageTextUnit>();
        foreach (var language in new[] { "ht", "es", "fr", "ja" })
        {
            var pair = await registry.GetOrCreateEnabledPairAsync("en", language);
            Assert.NotNull(pair);
            var target = TextUnit(language, $"provider result {language}", "FounderApproved");
            targets.Add(target);
            db.AddRange(
                target,
                new LegendTranslationAlignment
                {
                    Id = Guid.NewGuid(),
                    PairKey = pair!.PairKey,
                    SourceTextUnitId = source.Id,
                    TargetTextUnitId = target.Id,
                    Provider = "AzureTranslator",
                    Confidence = .9m,
                    QualityState = "Observation",
                    HumanVerified = false,
                    ObservationCount = 1
                },
                new LegendLanguageContextRelationship
                {
                    Id = Guid.NewGuid(),
                    PairKey = pair.PairKey,
                    SourceTextUnitId = source.Id,
                    RelatedTextUnitId = target.Id,
                    RelationshipKind = "ContextualExample",
                    ContextSignature = "legacy-provider-result",
                    SourcePatternSignature = "legacy-provider-result",
                    Confidence = 1m,
                    QualityState = "Verified",
                    Provenance = "FounderApproved",
                    ObservationCount = 1
                });
        }
        await db.SaveChangesAsync();

        await ingestion.ReconcileLegacyAsync(25);
        var firstUpdatedUtc = targets.ToDictionary(item => item.Id, item =>
            db.LegendLanguageTextUnits.Single(unit => unit.Id == item.Id).UpdatedUtc);
        await ingestion.ReconcileLegacyAsync(25);

        foreach (var target in targets)
        {
            var persistedTarget = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == target.Id);
            var alignment = await db.LegendTranslationAlignments.SingleAsync(item => item.TargetTextUnitId == target.Id);
            var context = await db.LegendLanguageContextRelationships.SingleAsync(item => item.RelatedTextUnitId == target.Id);
            Assert.Equal("ProviderDerived", persistedTarget.Provenance);
            Assert.False(alignment.HumanVerified);
            Assert.Equal("Observation", alignment.QualityState);
            Assert.Null(alignment.SupersededUtc);
            Assert.Equal("ProviderDerived", context.Provenance);
            Assert.Equal("Observation", context.QualityState);
            Assert.True(context.Confidence <= .5m);
            Assert.Equal(firstUpdatedUtc[target.Id], persistedTarget.UpdatedUtc);
        }
    }

    [Fact]
    public async Task PreviouslyReconciledLegacyLineage_RetiresLateDerivedArtifacts_AndPreservesHumanVerifiedMultiSentenceKnowledge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var pair = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        Assert.NotNull(pair);

        var malformedSource = TextUnit("en", "First source. Second source.", "FounderApproved");
        malformedSource.IsTrainingEligible = false;
        var malformedTarget = TextUnit("ht", "first target second target", "AzureTranslator");
        var alignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(), PairKey = pair!.PairKey, SourceTextUnitId = malformedSource.Id,
            TargetTextUnitId = malformedTarget.Id, Provider = "AzureTranslator", QualityState = "Observation", ObservationCount = 1
        };
        var context = new LegendLanguageContextRelationship
        {
            Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = malformedSource.Id,
            RelatedTextUnitId = malformedTarget.Id, RelationshipKind = "ContextualExample", ContextSignature = "interrupted-reconciliation",
            SourcePatternSignature = "interrupted-reconciliation", Confidence = .5m, QualityState = "Observation",
            Provenance = "FounderApproved", ObservationCount = 1
        };
        var candidate = new LegendCorpusCandidate
        {
            Id = Guid.NewGuid(), IdempotencyKey = "interrupted-legacy-derived", SourceLanguageCode = "en", TargetLanguageCode = "ht",
            SourceText = malformedSource.Text, SourceTextHash = malformedSource.NormalizedHash, Category = "FounderApprovedSeed",
            Provenance = "FounderApproved", IsApproved = true, ProcessingState = "Queued"
        };
        var learningEvent = new LegendTranslationLearningEvent
        {
            Id = Guid.NewGuid(), IdempotencyKey = "interrupted-legacy-derived-event", SourceLanguageCode = "en", TargetLanguageCode = "ht",
            PairKey = pair.PairKey, SourceTextHash = malformedSource.NormalizedHash, TargetTextHash = malformedTarget.NormalizedHash,
            SourceText = malformedSource.Text, TargetText = malformedTarget.Text, Provider = "AzureTranslator",
            Provenance = "FounderApproved", EligibilityState = "Eligible", ProcessingState = "Processed", PromotionOutcome = "Promoted"
        };
        var legacySubmission = new LegendFounderTrainingSubmission
        {
            Id = Guid.NewGuid(), SourceLanguageCode = "en", RawText = malformedSource.Text,
            RawTextHash = malformedSource.NormalizedHash, LegacySourceTextUnitId = malformedSource.Id,
            RawCharacterCount = malformedSource.Text.Length, AtomicUnitCount = 2, ProcessingState = "Reconciled"
        };
        var humanSource = TextUnit("en", "Please come. Bring water.", "FounderApproved");
        var humanTarget = TextUnit("ht", "Tanpri vini. Pote dlo.", "FounderApproved");
        var humanAlignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = humanSource.Id, TargetTextUnitId = humanTarget.Id,
            Provider = "FounderApproved", Confidence = 1m, QualityState = "Verified", HumanVerified = true, ObservationCount = 1
        };
        db.AddRange(malformedSource, malformedTarget, alignment, context, candidate, learningEvent, legacySubmission,
            humanSource, humanTarget, humanAlignment);
        await db.SaveChangesAsync();

        await ingestion.ReconcileLegacyAsync(25);
        var firstSupersededUtc = (await db.LegendTranslationAlignments.SingleAsync(item => item.Id == alignment.Id)).SupersededUtc;
        await ingestion.ReconcileLegacyAsync(25);

        Assert.False((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == malformedTarget.Id)).IsTrainingEligible);
        Assert.Equal(firstSupersededUtc, (await db.LegendTranslationAlignments.SingleAsync(item => item.Id == alignment.Id)).SupersededUtc);
        Assert.NotNull((await db.LegendLanguageContextRelationships.SingleAsync(item => item.Id == context.Id)).SupersededUtc);
        Assert.Equal("Superseded", (await db.LegendCorpusCandidates.SingleAsync(item => item.Id == candidate.Id)).ProcessingState);
        Assert.Equal("Superseded", (await db.LegendTranslationLearningEvents.SingleAsync(item => item.Id == learningEvent.Id)).ProcessingState);
        Assert.True((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == humanSource.Id)).IsTrainingEligible);
        Assert.True((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == humanTarget.Id)).IsTrainingEligible);
        Assert.Null((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == humanAlignment.Id)).SupersededUtc);
    }

    [Fact]
    public async Task ProviderDerivedEvent_WithoutAnExistingCanonicalSource_IsSupersededBeforeItCanCreateATarget()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var pair = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        Assert.NotNull(pair);
        var eventItem = new LegendTranslationLearningEvent
        {
            Id = Guid.NewGuid(), IdempotencyKey = "missing-canonical-source", SourceLanguageCode = "en", TargetLanguageCode = "ht",
            PairKey = pair!.PairKey, SourceText = "Unregistered source.", TargetText = "sib san sous.",
            SourceTextHash = LegendLanguageIdentity.TextHash("Unregistered source."),
            TargetTextHash = LegendLanguageIdentity.TextHash("sib san sous."), Provider = "AzureTranslator",
            Provenance = "ProviderDerived", EligibilityState = "Eligible", ProcessingState = "Pending"
        };
        db.Add(eventItem);
        await db.SaveChangesAsync();

        await corpus.ProcessAsync(eventItem);

        Assert.Equal("Superseded", (await db.LegendTranslationLearningEvents.SingleAsync(item => item.Id == eventItem.Id)).ProcessingState);
        Assert.Empty(await db.LegendLanguageTextUnits.ToListAsync());
        Assert.Empty(await db.LegendTranslationAlignments.ToListAsync());
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
    public async Task TerminalCandidate_ReopensOnlyWhenItsPriorAlignmentNoLongerHasAnActiveTarget()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var registry = new LegendLanguageRegistry(db, Configuration());
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(await registry.GetOrCreateEnabledPairAsync("en", "ht"));
        var source = TextUnit("en", "A source whose prior target was retired.", "FounderApproved");
        var retiredTarget = TextUnit("ht", "Yon sib ki pran retrèt.", "ProviderDerived");
        retiredTarget.IsTrainingEligible = false;
        var candidate = new LegendCorpusCandidate
        {
            Id = Guid.NewGuid(), IdempotencyKey = $"founder-seed:{source.Id:D}:{pair.PairKey}",
            SourceLanguageCode = "en", TargetLanguageCode = "ht", SourceText = source.Text,
            SourceTextHash = source.NormalizedHash, Category = "FounderApprovedSeed", Provenance = "FounderApproved",
            IsApproved = true, ProcessingState = "Deduplicated", ProcessedUtc = DateTime.UtcNow,
            FailureCode = "canonical_alignment_exists"
        };
        db.AddRange(source, retiredTarget, candidate, new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id,
            TargetTextUnitId = retiredTarget.Id, Provider = "AzureTranslator", QualityState = "Observation",
            ObservationCount = 1
        });
        await db.SaveChangesAsync();

        await corpus.EnsureFounderSeedCandidatesAsync(source, null, null);

        var reopened = await db.LegendCorpusCandidates.SingleAsync(item => item.Id == candidate.Id);
        Assert.True(reopened.IsApproved);
        Assert.Equal("Pending", reopened.ProcessingState);
        Assert.Null(reopened.ProcessedUtc);
        Assert.Null(reopened.LeaseExpiresUtc);
        Assert.Null(reopened.FailureCode);
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
        Assert.Equal(0, originalPattern.ContradictionCount);
        Assert.True(await curriculum.TryValidatePatternAsync(originalPattern.Id));
        var validated = await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == originalPattern.Id);
        Assert.Equal("Validated", validated.MaturityState);
        Assert.False(validated.IsProductionEligible);
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

    [Fact]
    public async Task FutureRegisteredLanguage_UsesTheSharedAtomicPlannerProviderAndRetirementPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = FutureLanguageConfiguration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var languages = await registry.ListEnabledTranslationLanguagesAsync();
        Assert.Contains(languages, item => item.Code == "x-test");
        Assert.True(LegendLanguageIdentity.TryNormalize("x-test", out var normalizedFutureCode));
        Assert.Equal("x-test", normalizedFutureCode);

        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var submitted = await ingestion.SubmitAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "teacher\nstudent\nI read the book.", null, null, "Training", null, null, "FounderApproved"));
        Assert.True(submitted.Succeeded, submitted.Message);
        Assert.Equal(3, submitted.AtomicUnitCount);

        var focused = new LegendConnectRuntimePolicySnapshot(
            false, 100_000, 0, 100_000, true, true, "Shadow", .98m, null, null, DateTime.UtcNow)
        {
            FocusedTargetLanguageCodes = ["x-test"]
        };
        var planner = new LegendConnectAutonomousGapPlanner(db, registry);
        var selected = await planner.SelectApprovedGapAsync(focused);
        Assert.NotNull(selected);
        Assert.Equal("x-test", (await db.LegendCorpusCandidates.SingleAsync(item => item.Id == selected)).TargetLanguageCode);

        var provider = new FutureLanguageProvider();
        var worker = new LegendConnectAutonomousLearningService(
            db, registry, provider,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance),
            corpus, planner, configuration, curriculum: curriculum);
        for (var index = 0; index < 3; index++)
            await worker.ProcessOneAsync();

        var sourceTexts = new[] { "teacher", "student", "I read the book." };
        Assert.Equal(3, provider.TranslateCalls);
        Assert.All(provider.TargetLanguages, item => Assert.Equal("x-test", item));
        Assert.Equal(3, await db.LegendCorpusCandidates.CountAsync(item =>
            sourceTexts.Contains(item.SourceText) && item.TargetLanguageCode == "x-test" && item.ProcessingState == "Queued"));
        Assert.Equal(3, await db.LegendLanguageTextUnits.CountAsync(item =>
            item.LanguageCode == "x-test" && item.IsTrainingEligible && item.Provenance == "ProviderDerived"));
        Assert.Equal(3, await db.LegendTranslationAlignments.CountAsync(item =>
            item.PairKey == "en:x-test" && item.SupersededUtc == null && !item.HumanVerified && item.QualityState == "Observation"));
        Assert.Equal(3, await db.LegendLanguageContextRelationships.CountAsync(item =>
            item.PairKey == "en:x-test" && item.SupersededUtc == null && item.Provenance == "ProviderDerived"));

        var candidateCount = await db.LegendCorpusCandidates.CountAsync();
        for (var index = 0; index < 3; index++)
            await worker.ProcessOneAsync();
        Assert.Equal(3, provider.TranslateCalls);
        Assert.Equal(candidateCount, await db.LegendCorpusCandidates.CountAsync());

        var legacy = await corpus.SubmitApprovedKnowledgeAsync(new LegendConnectKnowledgeSubmission(
            "en", "First future-language source. Second future-language source.", null, null,
            "Training", null, null, "FounderApproved"));
        Assert.True(legacy.Succeeded, legacy.Message);
        await worker.ProcessOneAsync();
        var legacySource = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == legacy.SourceTextUnitId);
        var legacyAlignment = await db.LegendTranslationAlignments.SingleAsync(item =>
            item.SourceTextUnitId == legacySource.Id && item.PairKey == "en:x-test");
        var legacyTargetId = legacyAlignment.TargetTextUnitId;

        await ingestion.ReconcileLegacyAsync(25);

        Assert.False((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == legacySource.Id)).IsTrainingEligible);
        Assert.False((await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == legacyTargetId)).IsTrainingEligible);
        Assert.NotNull((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == legacyAlignment.Id)).SupersededUtc);
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

    private static IConfiguration FutureLanguageConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "100000",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Future test language",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Future test language"
        }).Build();

    private sealed class FutureLanguageProvider : ITranslationProvider
    {
        public string ProviderName => "AzureTranslator";
        public int TranslateCalls { get; private set; }
        public List<string> TargetLanguages { get; } = [];

        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));

        public Task<TranslationProviderResult> TranslateAsync(
            string text,
            string targetLanguage,
            string? sourceLanguage = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            TargetLanguages.Add(targetLanguage);
            return Task.FromResult(new TranslationProviderResult(
                true, $"{targetLanguage}::{text}", sourceLanguage, ProviderName));
        }
    }
}

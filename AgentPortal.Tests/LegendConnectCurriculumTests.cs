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
        var founderTraining = new LegendConnectFounderTrainingIngestionAuthority(
            db, registry, corpus, curriculum, operations: null);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            founderTrainingIngestion: founderTraining);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var durable = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);

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
            durable,
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
    public async Task FounderCurriculumManifest_UsesSharedDurableFamilyClaimsWithoutDuplicateCanonicalWork()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var founderTraining = new LegendConnectFounderTrainingIngestionAuthority(
            db, registry, corpus, curriculum, operations: null);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            founderTrainingIngestion: founderTraining);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var durable = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);
        var processor = new LegendConnectCurriculumManifestProcessor(
            db, curriculum, durable, NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);

        var accepted = await operations.SubmitFounderCurriculumManifestAsync("founder-test",
            new LegendConnectCurriculumManifestSubmission(
            [
                ConversationBatch("durable.manifest.opening", "Opening", ("Blue lantern.", "opening"), ("Amber lantern.", "opening")),
                ConversationBatch("durable.manifest.repair", "Repair", ("Please clarify.", "clarification"), ("Could you repeat that?", "clarification"))
            ]));
        Assert.True(accepted.Succeeded, accepted.Message);

        Assert.Equal(1, await processor.SeedDurableFamilyWorkAsync(
            durable, LegendConnectLanguageIntelligenceEvaluatorVersion.Current, 4));
        var children = await db.LegendHistoricalReevaluationWorkItems
            .Where(item =>
                item.Phase == LegendConnectHistoricalReevaluationWorkAuthority.FounderCurriculumPhase &&
                item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind)
            .ToListAsync();
        Assert.Equal(2, children.Count);
        Assert.Equal(2, children.Select(item => item.WorkIdentity).Distinct().Count());

        while (true)
        {
            var claim = await durable.TryClaimNextFounderManifestWorkAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current, "test-worker");
            if (claim is null)
                break;
            await using var execution = Assert.IsType<LegendConnectHistoricalReevaluationWorkAuthority.LegendHistoricalReevaluationOwnedExecution>(
                await durable.TryBeginOwnedExecutionAsync(claim));
            await processor.ProcessDurableFamilyAsync(
                Assert.IsType<Guid>(claim.SubjectId), int.Parse(claim.SubjectScope));
            Assert.True(await execution.CompleteAsync());
            await processor.RefreshDurableManifestStatusAsync(
                Assert.IsType<Guid>(claim.SubjectId), LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        }

        var manifest = Assert.Single(await db.LegendCurriculumManifestWorkItems.ToListAsync());
        Assert.Equal("Completed", manifest.ProcessingState);
        Assert.Equal(2, manifest.NextFamilyIndex);
        Assert.Equal(2, await db.LegendCurriculumFamilies.CountAsync());
        Assert.Equal(0, await db.LegendLanguageCompositionalAnchors
            .GroupBy(item => new { item.CurriculumExampleId, item.AnchorSignature })
            .CountAsync(group => group.Count() > 1));
        Assert.Equal(0, await processor.SeedDurableFamilyWorkAsync(
            durable, LegendConnectLanguageIntelligenceEvaluatorVersion.Current, 4));
    }

    [Fact]
    public async Task FounderSubmissionProcessingSection_UsesDurablePipelineStateAndCompletionVersions()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var founderTraining = new LegendConnectFounderTrainingIngestionAuthority(
            db, registry, corpus, curriculum, operations: null);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum,
            founderTrainingIngestion: founderTraining);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var durable = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);

        var training = await operations.SubmitFounderKnowledgeAsync(
            "founder-test",
            new LegendConnectKnowledgeSubmission(
                "en",
                "First controlled source. Second controlled source.",
                null,
                null,
                "Conversation",
                null,
                null,
                "FounderApproved"));
        Assert.True(training.Succeeded, training.Message);
        Assert.NotNull(training.TrainingSubmissionId);

        var manifest = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-test",
            new LegendConnectCurriculumManifestSubmission(
            [
                ConversationBatch(
                    "submission.visibility",
                    "Submission visibility",
                    ("Please clarify the schedule.", "clarification_request"),
                    ("Could you clarify the schedule?", "clarification_request"))
            ]));
        Assert.True(manifest.Succeeded, manifest.Message);

        var queuedPage = await operations.GetFounderSectionPageAsync(
            "submissions",
            "en",
            null,
            null);
        Assert.All(queuedPage.Rows, item =>
            Assert.Contains(item[18], new[]
            {
                "QUEUED",
                "PROCESSING",
                "STALE — awaiting dependency assessment",
                "CURRENT — pre-contract baseline"
            }));

        var processor = new LegendConnectCurriculumManifestProcessor(
            db,
            curriculum,
            durable,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
        Assert.Equal(1, await processor.ProcessPendingAsync(1));

        // V20.2: ProcessPendingAsync is deliberately bounded. Drain the
        // remaining Founder-manifest durable descendants before asserting the
        // terminal processing projection. The projection must report the real
        // durable lifecycle rather than pretending one bounded pump completed
        // every child item.
        while (await processor.ProcessPendingAsync(32) > 0)
        {
        }

        foreach (var candidate in await db.LegendCorpusCandidates.ToListAsync())
        {
            candidate.ProcessingState = "Processed";
            candidate.ProcessedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var page = await operations.GetFounderSectionPageAsync(
            "submissions",
            "en",
            null,
            null);

        Assert.Equal("submissions", page.Section);
        Assert.Contains("Evaluator target", page.Columns);
        Assert.Contains("Transition evidence", page.Columns);
        Assert.Equal(2, page.Rows.Count);

        var trainingRow = Assert.Single(page.Rows.Where(item => item[0] == "Atomic training"));
        Assert.Equal(training.AtomicUnitCount.ToString(), trainingRow[3]);
        Assert.Equal(training.NewCanonicalUnitCount.ToString(), trainingRow[4]);
        Assert.Equal(training.ReusedCanonicalUnitCount.ToString(), trainingRow[5]);
        Assert.Equal("COMPLETED", trainingRow[18]);

        var manifestRow = Assert.Single(page.Rows.Where(item => item[0] == "Semantic manifest"));
        Assert.Contains("Completed", manifestRow[13], StringComparison.Ordinal);
        Assert.Contains($"current v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current}", manifestRow[11]);
        Assert.Equal($"v{LegendConnectLanguageIntelligenceEvaluatorVersion.Current}", manifestRow[12]);

        // V20.2 regression: Founder processing visibility must come from the
        // current evaluator's durable child work whenever it exists, not from
        // the older corpus-candidate compatibility projection.
        Assert.Equal("1", manifestRow[6]);
        Assert.Equal("0", manifestRow[7]);
        Assert.Equal("0", manifestRow[8]);
        Assert.Equal("1", manifestRow[9]);
        Assert.Equal("0", manifestRow[10]);

        Assert.Equal("COMPLETED", manifestRow[18]);

        var persistedTraining = await db.LegendFounderTrainingSubmissions
            .SingleAsync(item => item.Id == training.TrainingSubmissionId);
        Assert.Equal(training.NewCanonicalUnitCount, persistedTraining.NewCanonicalUnitCount);
        Assert.Equal(training.ReusedCanonicalUnitCount, persistedTraining.ReusedCanonicalUnitCount);
        Assert.Equal(training.QueuedCoverageCount, persistedTraining.QueuedCoverageCount);

        persistedTraining.CompletedLanguageIntelligenceEvaluatorVersion--;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stalePage = await operations.GetFounderSectionPageAsync(
            "submissions",
            "en",
            null,
            null);
        var staleTraining = Assert.Single(stalePage.Rows.Where(item => item[0] == "Atomic training"));
        Assert.Equal("STALE — awaiting dependency assessment", staleTraining[17]);
        Assert.Equal("QUEUED", staleTraining[18]);
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
    public async Task FounderCurriculumManifest_ReusesExistingFamilyAndPreservesCanonicalCategory()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(
            db,
            registry,
            NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(
            db,
            registry,
            corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            curriculum: curriculum);

        var first = await curriculum.SubmitFounderEnglishBatchAsync(
            ConversationBatch(
                "conversation.greeting.basic",
                "Conversation greeting",
                ("Hi.", "greeting"),
                ("Hello.", "greeting")));

        Assert.True(first.Succeeded, first.Message);
        Assert.False(first.DuplicatePrevented);

        var existingFamily = Assert.Single(
            await db.LegendCurriculumFamilies.ToListAsync());

        Assert.Equal(
            "conversation.greeting.basic",
            existingFamily.FamilyKey);

        Assert.Equal(
            "Conversation greeting",
            existingFamily.SemanticCategory);

        Assert.Equal(
            2,
            await db.LegendCurriculumExamples.CountAsync(
                item =>
                    item.CurriculumFamilyId == existingFamily.Id &&
                    item.LanguageCode == "en"));

        var originalTextUnitCount =
            await db.LegendLanguageTextUnits.CountAsync();

        var result = await operations.SubmitFounderCurriculumManifestAsync(
            "founder-test",
            new LegendConnectCurriculumManifestSubmission(
            [
                ConversationBatch(
                    "conversation.greeting.basic",
                    "Conversation greeting — additional opening variation",
                    ("Hey.", "greeting"),
                    ("Hey there.", "greeting"))
            ]));

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.DuplicatePrevented);

        // Manifest acceptance is durable-only. It must not perform
        // synchronous curriculum learning in the browser request.
        Assert.Equal(
            originalTextUnitCount,
            await db.LegendLanguageTextUnits.CountAsync());

        Assert.Single(
            await db.LegendCurriculumFamilies.ToListAsync());

        var runtime = new LegendConnectRuntimePolicyAuthority(
            db,
            new FounderAccess(),
            registry,
            configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);

        var durable =
            new LegendConnectHistoricalReevaluationWorkAuthority(
                db,
                runtime,
                configuration);

        var processor =
            new LegendConnectCurriculumManifestProcessor(
                db,
                curriculum,
                durable,
                NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);

        Assert.Equal(
            1,
            await processor.ProcessPendingAsync(1));

        db.ChangeTracker.Clear();

        var canonicalFamily = Assert.Single(
            await db.LegendCurriculumFamilies.ToListAsync());

        // V20.4: FamilyKey owns canonical family identity.
        // Incoming category wording may enrich that existing family,
        // but it may not rename or duplicate the canonical authority.
        Assert.Equal(
            existingFamily.Id,
            canonicalFamily.Id);

        Assert.Equal(
            "conversation.greeting.basic",
            canonicalFamily.FamilyKey);

        Assert.Equal(
            "Conversation greeting",
            canonicalFamily.SemanticCategory);

        var englishExamples = await db.LegendCurriculumExamples
            .Where(item =>
                item.CurriculumFamilyId == canonicalFamily.Id &&
                item.LanguageCode == "en")
            .ToListAsync();

        Assert.Equal(4, englishExamples.Count);

        Assert.Equal(
            4,
            englishExamples
                .Select(item => item.TextUnitId)
                .Distinct()
                .Count());

        Assert.Equal(
            1,
            await db.LegendCurriculumFamilies.CountAsync(
                item =>
                    item.FamilyKey ==
                    "conversation.greeting.basic"));

        var englishTextUnitIds = englishExamples
            .Select(item => item.TextUnitId)
            .ToArray();

        var englishTexts = await db.LegendLanguageTextUnits
            .Where(item => englishTextUnitIds.Contains(item.Id))
            .Select(item => item.Text)
            .ToListAsync();

        Assert.Contains("Hi.", englishTexts);
        Assert.Contains("Hello.", englishTexts);
        Assert.Contains("Hey.", englishTexts);
        Assert.Contains("Hey there.", englishTexts);
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
    public async Task FounderCurriculumManifest_TerminalReceipt_IsRetiredWithoutResumingFamilyWork()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(db, registry, corpus, configuration, curriculum: curriculum);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var durable = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);
        var processor = new LegendConnectCurriculumManifestProcessor(
            db, curriculum, durable,
            NullLogger<LegendConnectCurriculumManifestProcessor>.Instance);
        var manifest = new LegendConnectCurriculumManifestSubmission(
        [
            ConversationBatch(
                "conversation.recoverable.failure",
                "Recoverable durable receipt",
                ("Please repeat that.", "repeat"),
                ("Could you repeat that?", "repeat"))
        ]);

        Assert.True((await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest)).Succeeded);
        var receipt = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        receipt.ProcessingState = "Failed";
        receipt.LastErrorCode = "founder_manifest_family_failure";
        receipt.LastErrorMessage = "Transient governed evaluator failure.";
        receipt.AttemptCount = 5;
        await db.SaveChangesAsync();

        Assert.Equal(0, await processor.ProcessPendingAsync(1));

        var retired = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal("Retired", retired.ProcessingState);
        Assert.Equal(0, retired.NextFamilyIndex);
        Assert.Equal(5, retired.AttemptCount);
        Assert.Equal("founder_manifest_family_failure", retired.LastErrorCode);
        Assert.Equal("Transient governed evaluator failure.", retired.LastErrorMessage);
        Assert.Null(retired.CompletedLanguageIntelligenceEvaluatorVersion);
        Assert.Empty(await db.LegendCurriculumFamilies.ToListAsync());
        Assert.Empty(await db.LegendHistoricalReevaluationWorkItems
            .Where(item => item.SubjectId == retired.Id &&
                item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.FounderManifestFamilyWorkKind)
            .ToListAsync());
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
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var durable = new LegendConnectHistoricalReevaluationWorkAuthority(db, runtime, configuration);
        var processor = new LegendConnectCurriculumManifestProcessor(
            db,
            curriculum,
            durable,
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
        Assert.Equal(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            completed.CompletedLanguageIntelligenceEvaluatorVersion);

        var duplicate = await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest);
        Assert.True(duplicate.Succeeded);
        Assert.True(duplicate.DuplicatePrevented);
        Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal(2, await db.LegendCurriculumFamilies.CountAsync());

        // Bring the current evaluator completely to convergence before
        // deliberately staling only the parent receipt. V20 owns one bounded
        // downstream derivation-ledger item per retained family; those items
        // are metadata projection, not canonical curriculum replay.
        while (await processor.ProcessPendingAsync(1) > 0)
        {
        }

        var currentLedgerWork = await db.LegendHistoricalReevaluationWorkItems
            .Where(item =>
                item.EvaluatorVersion == LegendConnectLanguageIntelligenceEvaluatorVersion.Current &&
                item.Phase == LegendConnectHistoricalReevaluationWorkAuthority.FounderCurriculumPhase &&
                item.WorkKind == LegendConnectHistoricalReevaluationWorkAuthority.DerivationLedgerWorkKind)
            .ToListAsync();

        Assert.Equal(2, currentLedgerWork.Count);
        Assert.All(currentLedgerWork, item => Assert.Equal("Completed", item.ProcessingState));

        var canonicalBeforeCapabilityReplay = new
        {
            Families = await db.LegendCurriculumFamilies.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(),
            ActiveTransitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)
        };
        completed.CompletedLanguageIntelligenceEvaluatorVersion =
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current - 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var staleDuplicate = await operations.SubmitFounderCurriculumManifestAsync("founder-test", manifest);
        Assert.True(staleDuplicate.DuplicatePrevented);
        Assert.Contains("evaluator", staleDuplicate.Message!, StringComparison.OrdinalIgnoreCase);

        // Only the parent projection is stale. All current-evaluator
        // canonical and downstream derivation work is already completed.
        // Reconciliation must therefore reuse existing durable state and
        // perform zero new work.
        Assert.Equal(0, await processor.ProcessPendingAsync(1));
        db.ChangeTracker.Clear();

        var replayCompleted = Assert.Single(await db.Set<LegendCurriculumManifestWorkItem>().ToListAsync());
        Assert.Equal("Completed", replayCompleted.ProcessingState);
        Assert.Equal(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            replayCompleted.CompletedLanguageIntelligenceEvaluatorVersion);
        var canonicalAfterCapabilityReplay = new
        {
            Families = await db.LegendCurriculumFamilies.CountAsync(),
            Examples = await db.LegendCurriculumExamples.CountAsync(),
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Anchors = await db.LegendLanguageCompositionalAnchors.CountAsync(),
            ActiveTransitions = await db.LegendSemanticTransitionEvidence.CountAsync(item => item.SupersededUtc == null)
        };
        Assert.Equal(canonicalBeforeCapabilityReplay, canonicalAfterCapabilityReplay);
        Assert.Equal(0, await processor.ProcessPendingAsync(1));
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

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private static string Suffix(string text) => text.Contains("red", StringComparison.Ordinal)
        ? "a"
        : text.Contains("blue", StringComparison.Ordinal)
            ? "b"
            : text.Contains("gold", StringComparison.Ordinal)
                ? "c"
                : "d";
}

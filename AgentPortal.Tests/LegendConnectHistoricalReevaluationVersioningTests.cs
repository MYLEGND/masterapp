using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Messaging;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPortal.Tests;

/// <summary>
/// Regression proof for the durable evaluator-version contract. It exercises
/// the same runtime policy, curriculum, and quality authorities used by the
/// hosted learning worker; no test-only historical processor is introduced.
/// </summary>
public sealed class LegendConnectHistoricalReevaluationVersioningTests
{
    [Fact]
    public void CurrentEvaluatorVersion_IsFifteenForSemanticTransitionHistoricalConvergence()
    {
        Assert.Equal(
            15,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
    }

    [Fact]
    public async Task MaterialEvaluatorVersionAdvanceReplaysAllActiveHistoryOnceAndThenConverges()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration, runtime);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var operations = new LegendConnectOperations(
            db,
            registry,
            corpus,
            configuration,
            runtimePolicy: runtime,
            curriculum: curriculum,
            intelligence: intelligence);

        foreach (var familyKey in new[] { "version.one", "version.two", "version.three" })
        {
            var submitted = await curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
                familyKey,
                "Versioned historical evidence",
                [
                    new LegendConnectCurriculumExampleSubmission(
                        $"I inspect {familyKey}.", new Dictionary<string, string> { ["agent"] = "I", ["predicate"] = "inspect" }),
                    new LegendConnectCurriculumExampleSubmission(
                        $"You inspect {familyKey}.", new Dictionary<string, string> { ["agent"] = "You", ["predicate"] = "inspect" })
                ]));
            Assert.True(submitted.Succeeded, submitted.Message);
        }

        var pair = Assert.IsType<LegendLanguagePairSnapshot>(await registry.GetOrCreateEnabledPairAsync("en", "x-test"));
        var source = await db.LegendLanguageTextUnits.SingleAsync(item => item.Text == "I inspect version.one.");
        var providerTarget = Unit("x-test", "provider-only historical observation", "ProviderDerived");
        var providerAlignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = providerTarget.Id,
            Provider = "AzureTranslator", Provenance = "ProviderDerived", QualityState = "Observation", ObservationCount = 1
        };
        db.AddRange(providerTarget, providerAlignment);
        await db.SaveChangesAsync();

        var initial = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(1);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, initial.Phase);
        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, operations, 1, take: 1);

        var versionOne = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(1);
        Assert.False(versionOne.RequiresWork);
        Assert.Equal(1, versionOne.CompletedEvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Complete, versionOne.Phase);

        var sourceLineageBefore = new
        {
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Alignments = await db.LegendTranslationAlignments.CountAsync(),
            Submissions = await db.LegendFounderTrainingSubmissions.CountAsync(),
            SubmissionUnits = await db.LegendFounderTrainingSubmissionUnits.CountAsync()
        };
        var pattern = await db.LegendLanguageStructuralPatterns.FirstAsync();
        pattern.MaturityState = "StaleTestState";
        pattern.SupportCount = 99;
        await db.SaveChangesAsync();

        // This is the Version N -> N+1 simulation. A future material change
        // advances the deployed marker; the same historical evidence resumes
        // through the existing worker-owned phases from their durable start.
        var versionTwoStart = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(2);
        Assert.True(versionTwoStart.RequiresWork);
        Assert.Equal(1, versionTwoStart.CompletedEvaluatorVersion);
        Assert.Equal(2, versionTwoStart.TargetEvaluatorVersion);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, versionTwoStart.Phase);
        Assert.Null(versionTwoStart.Cursor);

        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, operations, 2, take: 1);

        var versionTwo = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(2);
        var sourceLineageAfter = new
        {
            TextUnits = await db.LegendLanguageTextUnits.CountAsync(),
            Alignments = await db.LegendTranslationAlignments.CountAsync(),
            Submissions = await db.LegendFounderTrainingSubmissions.CountAsync(),
            SubmissionUnits = await db.LegendFounderTrainingSubmissionUnits.CountAsync()
        };
        var recomputed = await db.LegendLanguageStructuralPatterns.SingleAsync(item => item.Id == pattern.Id);

        Assert.False(versionTwo.RequiresWork);
        Assert.Equal(2, versionTwo.CompletedEvaluatorVersion);
        Assert.Equal(sourceLineageBefore, sourceLineageAfter);
        Assert.NotEqual("StaleTestState", recomputed.MaturityState);
        Assert.NotEqual(99, recomputed.SupportCount);
        Assert.False((await db.LegendTranslationAlignments.SingleAsync(item => item.Id == providerAlignment.Id)).HumanVerified);
        Assert.Contains(await db.LegendTranslationQualityEvidence.ToListAsync(), item =>
            item.ObservedAlignmentId == providerAlignment.Id && item.Signal == "Insufficient");

        var converged = new
        {
            Patterns = await db.LegendLanguageStructuralPatterns.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Quality = await db.LegendTranslationQualityEvidence.CountAsync(),
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync()
        };
        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, operations, 2, take: 1);
        var secondPass = new
        {
            Patterns = await db.LegendLanguageStructuralPatterns.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Quality = await db.LegendTranslationQualityEvidence.CountAsync(),
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync()
        };
        Assert.Equal(converged, secondPass);
    }

    [Fact]
    public async Task CurrentVersion_ReplaysHistoricalProviderSemanticConflictsWithoutAnEnglishPivot_AndConverges()
    {
        await using var historicalDb = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var historicalRegistry = new LegendLanguageRegistry(historicalDb, configuration);
        var historicalRuntime = new LegendConnectRuntimePolicyAuthority(
            historicalDb, new FounderAccess(), historicalRegistry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var historicalIntelligence = new LegendConnectTranslationIntelligence(historicalDb, configuration, historicalRuntime);
        var historicalCorpus = new LegendConnectCorpusService(
            historicalDb, historicalRegistry, NullLogger<LegendConnectCorpusService>.Instance,
            intelligence: historicalIntelligence);
        var historicalCurriculum = new LegendConnectCurriculumService(historicalDb, historicalRegistry, historicalCorpus);
        var historicalOperations = new LegendConnectOperations(
            historicalDb,
            historicalRegistry,
            historicalCorpus,
            configuration,
            runtimePolicy: historicalRuntime,
            curriculum: historicalCurriculum,
            intelligence: historicalIntelligence);

        // The v3 checkpoint represents the already-deployed evaluator before
        // this precise provider-quality retention correction.
        await DrainCanonicalWorkerCycleAsync(
            historicalRuntime,
            historicalCurriculum,
            historicalIntelligence,
            historicalOperations,
            3,
            take: 1);
        var historicalSeed = await SeedFounderConflictAsync(historicalDb, historicalRegistry);
        var replay = await historicalRuntime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.True(replay.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, replay.Phase);

        await DrainCanonicalWorkerCycleAsync(
            historicalRuntime,
            historicalCurriculum,
            historicalIntelligence,
            historicalOperations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            take: 1);

        var historicalEvidence = await QualityShapeAsync(historicalDb, historicalSeed.ProviderAlignmentId);
        Assert.Equal(
            [
                "Contradictory|human_verified_directional_conflict|none|Open",
                "Insufficient|known_semantic_component_not_realized|semantic|Open"
            ],
            historicalEvidence);
        var historicalProvider = await historicalDb.LegendTranslationAlignments
            .SingleAsync(item => item.Id == historicalSeed.ProviderAlignmentId);
        Assert.Equal("ProviderDerived", historicalProvider.Provenance);
        Assert.False(historicalProvider.HumanVerified);
        var trustedMemory = await historicalIntelligence.TryGetTrustedExactMemoryAsync(
            historicalSeed.SourceLanguageCode,
            historicalSeed.TargetLanguageCode,
            historicalSeed.SourceText);
        Assert.Equal("trusted correction target", trustedMemory?.Text);

        var historicalFirstPass = new
        {
            Quality = await historicalDb.LegendTranslationQualityEvidence.CountAsync(),
            Structural = await historicalDb.LegendLanguageStructuralEvidence.CountAsync(),
            Alignments = await historicalDb.LegendTranslationAlignments.CountAsync()
        };
        await DrainCanonicalWorkerCycleAsync(
            historicalRuntime,
            historicalCurriculum,
            historicalIntelligence,
            historicalOperations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            take: 1);
        var historicalSecondPass = new
        {
            Quality = await historicalDb.LegendTranslationQualityEvidence.CountAsync(),
            Structural = await historicalDb.LegendLanguageStructuralEvidence.CountAsync(),
            Alignments = await historicalDb.LegendTranslationAlignments.CountAsync()
        };
        Assert.Equal(historicalFirstPass, historicalSecondPass);

        await using var currentDb = ControllerTestHelpers.BuildDb();
        var currentRegistry = new LegendLanguageRegistry(currentDb, configuration);
        var currentIntelligence = new LegendConnectTranslationIntelligence(currentDb, configuration);
        var currentSeed = await SeedFounderConflictAsync(currentDb, currentRegistry);
        await currentIntelligence.EvaluateProviderObservationAsync(currentSeed.ProviderAlignmentId);

        Assert.Equal(historicalEvidence, await QualityShapeAsync(currentDb, currentSeed.ProviderAlignmentId));

        await historicalIntelligence.RecordHumanCorrectionAsync(
            historicalSeed.ProviderAlignmentId,
            historicalSeed.TrustedAlignmentId);
        var correctedHistory = await historicalDb.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == historicalSeed.ProviderAlignmentId)
            .ToListAsync();
        Assert.Contains(correctedHistory, item =>
            item.Signal == "Contradictory" &&
            item.ReasonCode == "human_verified_directional_correction" &&
            item.RelatedAlignmentId == historicalSeed.TrustedAlignmentId &&
            item.ResolutionState == "Corrected");
        Assert.Equal("ProviderDerived", (await historicalDb.LegendTranslationAlignments
            .SingleAsync(item => item.Id == historicalSeed.ProviderAlignmentId)).Provenance);
    }

    [Fact]
    public async Task ProviderOnlyOutliers_RemainRetainedInsufficientAndCannotManufactureTrustedSupport()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("x-source", "x-target"));
        var source = Unit("x-source", "provider-only source", "FounderApproved");
        var firstTarget = Unit("x-target", "provider outcome one", "ProviderDerived");
        var secondTarget = Unit("x-target", "provider outcome two", "ProviderDerived");
        var first = ProviderObservation(pair, source, firstTarget);
        var second = ProviderObservation(pair, source, secondTarget);
        db.AddRange(source, firstTarget, secondTarget, first, second);
        await db.SaveChangesAsync();

        await intelligence.EvaluateProviderObservationAsync(first.Id);
        await intelligence.EvaluateProviderObservationAsync(second.Id);
        await intelligence.EvaluateProviderObservationAsync(first.Id);
        await intelligence.EvaluateProviderObservationAsync(second.Id);

        var evidence = await db.LegendTranslationQualityEvidence.ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, item =>
        {
            Assert.Equal("Insufficient", item.Signal);
            Assert.Equal("no_established_pair_specific_evidence", item.ReasonCode);
        });
        Assert.All(await db.LegendTranslationAlignments.ToListAsync(), item => Assert.False(item.HumanVerified));
        Assert.Null(await intelligence.TryGetTrustedExactMemoryAsync("x-source", "x-target", source.Text));
    }

    [Fact]
    public async Task SourceFamilies_BoundedPageRepairsOnlyItsCurrentFamily_ThenResumesFromTheDurableCursor()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);

        var first = AddHistoricalSourceFamily(
            db,
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            "replay.source.first");
        var second = AddHistoricalSourceFamily(
            db,
            Guid.Parse("00000000-0000-0000-0000-000000000102"),
            "replay.source.second");
        await db.SaveChangesAsync();

        var start = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, start.Phase);
        Assert.Null(start.Cursor);

        var firstPage = await curriculum.ReevaluateHistoricalAlignmentsAsync(
            1,
            start.Phase,
            start.Cursor);
        Assert.Equal(first.FamilyId, firstPage.LastProcessedId);
        Assert.False(firstPage.PhaseComplete);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            start.Phase,
            firstPage.LastProcessedId,
            firstPage.PhaseComplete);

        db.ChangeTracker.Clear();
        Assert.NotNull((await db.LegendLanguageCompositionalAnchors
            .SingleAsync(item => item.Id == first.AnchorId)).SemanticSignature);
        Assert.Null((await db.LegendLanguageCompositionalAnchors
            .SingleAsync(item => item.Id == second.AnchorId)).SemanticSignature);

        // Recreate the authority to model an application/worker restart. The
        // runtime policy, not an in-memory loop variable, owns resumption.
        var restarted = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), new LegendLanguageRegistry(db, configuration), configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var resumed = await restarted.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, resumed.Phase);
        Assert.Equal(first.FamilyId, resumed.Cursor);

        var secondPage = await curriculum.ReevaluateHistoricalAlignmentsAsync(
            1,
            resumed.Phase,
            resumed.Cursor);
        Assert.Equal(second.FamilyId, secondPage.LastProcessedId);
        await restarted.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            resumed.Phase,
            secondPage.LastProcessedId,
            secondPage.PhaseComplete);
        Assert.NotNull((await db.LegendLanguageCompositionalAnchors
            .SingleAsync(item => item.Id == second.AnchorId)).SemanticSignature);
    }

    [Fact]
    public async Task HistoricalAlignmentConflict_IsQuarantinedWithoutSelectingAValue_AndTheCursorContinues()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var runtime = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), registry, configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var corpus = new LegendConnectCorpusService(
            db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var events = new LegendConnectOperationalEventWriter(
            db, NullLogger<LegendConnectOperationalEventWriter>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus, events);
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync("en", "x-test"));

        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(), FamilyKey = "replay.alignment.conflict", Provenance = "FounderApproved"
        };
        var source = Unit("en", "A governed source.", "FounderApproved");
        var target = Unit("x-test", "A governed target.", "FounderApproved");
        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = source.Id,
            LanguageCode = "en", Provenance = "FounderApproved"
        };
        var targetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = target.Id,
            LanguageCode = "x-test", DerivedFromCurriculumExampleId = sourceExample.Id,
            Provenance = "FounderApproved"
        };
        var conflict = new LegendTranslationAlignment
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000201"),
            PairKey = pair.PairKey,
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "FounderApproved",
            Provenance = "FounderApproved",
            HumanVerified = true,
            QualityState = "Verified",
            Confidence = 1m,
            ObservationCount = 1
        };
        db.AddRange(
            family,
            source,
            target,
            sourceExample,
            targetExample,
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(), CurriculumExampleId = sourceExample.Id,
                Dimension = "register", Value = "warm"
            },
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(), CurriculumExampleId = targetExample.Id,
                Dimension = "register", Value = "formal"
            },
            conflict);
        await db.SaveChangesAsync();

        var start = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            start.Phase,
            null,
            phaseComplete: true);
        var alignments = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Alignments, alignments.Phase);

        var page = await curriculum.ReevaluateHistoricalAlignmentsAsync(1, alignments.Phase, alignments.Cursor);
        Assert.Equal(conflict.Id, page.LastProcessedId);
        Assert.False(page.PhaseComplete);
        await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            alignments.Phase,
            page.LastProcessedId,
            page.PhaseComplete);
        db.ChangeTracker.Clear();

        var retained = await db.LegendCurriculumExampleVariations
            .SingleAsync(item => item.CurriculumExampleId == targetExample.Id && item.Dimension == "register");
        Assert.Equal("formal", retained.Value);
        Assert.Equal(1, await db.LegendCurriculumExampleVariations
            .CountAsync(item => item.CurriculumExampleId == targetExample.Id));
        Assert.Single(await db.LegendConnectOperationalEvents.Where(item =>
            item.Category == "HistoricalCurriculumReplay" &&
            item.ErrorCode == "conflicting_controlled_variation" &&
            item.CorrelationId == conflict.Id.ToString("D")).ToListAsync());

        var restarted = new LegendConnectRuntimePolicyAuthority(
            db, new FounderAccess(), new LegendLanguageRegistry(db, configuration), configuration,
            NullLogger<LegendConnectRuntimePolicyAuthority>.Instance);
        var resumed = await restarted.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.Alignments, resumed.Phase);
        Assert.Equal(conflict.Id, resumed.Cursor);

        var tail = await curriculum.ReevaluateHistoricalAlignmentsAsync(1, resumed.Phase, resumed.Cursor);
        Assert.True(tail.PhaseComplete);
        await restarted.AdvanceLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            resumed.Phase,
            tail.LastProcessedId,
            tail.PhaseComplete);
        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            (await restarted.GetOrStartLanguageIntelligenceReevaluationAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current)).Phase);
    }

    private static async Task DrainCanonicalWorkerCycleAsync(
        LegendConnectRuntimePolicyAuthority runtime,
        LegendConnectCurriculumService curriculum,
        ILegendConnectTranslationIntelligence intelligence,
        ILegendConnectOperations operations,
        int evaluatorVersion,
        int take)
    {
        for (var pass = 0; pass < 32; pass++)
        {
            var state = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);
            if (!state.RequiresWork)
                return;
            LegendConnectHistoricalReevaluationProgress progress;
            if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations)
            {
                progress = await intelligence.ReevaluateHistoricalProviderObservationsAsync(
                    take,
                    state.Cursor);
            }
            else if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
            {
                progress = await operations.ReconcileHistoricalOperationalTranslationsAsync(
                    take,
                    state.Cursor);
            }
            else
            {
                progress = await curriculum.ReevaluateHistoricalAlignmentsAsync(
                    take,
                    state.Phase,
                    state.Cursor);
            }
            await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                evaluatorVersion, state.Phase, progress.LastProcessedId, progress.PhaseComplete);
        }

        throw new Xunit.Sdk.XunitException("The bounded canonical historical replay did not converge.");
    }

    private static async Task<ProviderConflictSeed> SeedFounderConflictAsync(
        MasterAppDbContext db,
        LegendLanguageRegistry registry)
    {
        const string sourceLanguageCode = "x-source";
        const string targetLanguageCode = "x-target";
        const string sourceText = "controlled provider audit source";
        var pair = Assert.IsType<LegendLanguagePairSnapshot>(
            await registry.GetOrCreateEnabledPairAsync(sourceLanguageCode, targetLanguageCode));
        var source = Unit(sourceLanguageCode, sourceText, "FounderApproved");
        var providerTarget = Unit(targetLanguageCode, "provider observation target", "ProviderDerived");
        var trustedTarget = Unit(targetLanguageCode, "trusted correction target", "FounderApproved");
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(), FamilyKey = "provider.audit.semantic", Provenance = "FounderApproved"
        };
        var example = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = source.Id,
            LanguageCode = sourceLanguageCode, Provenance = "FounderApproved"
        };
        var semanticSignature = LegendLanguageIdentity.TextHash("semantic|controlled-state|reviewed");
        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(), LanguageCode = sourceLanguageCode, TextUnitId = source.Id,
            CurriculumFamilyId = family.Id, CurriculumExampleId = example.Id,
            Dimension = "controlled-state", Value = "reviewed", SemanticSignature = semanticSignature,
            AnchorSignature = LegendLanguageIdentity.TextHash($"{example.Id:D}|sentence|controlled-state|reviewed"),
            Provenance = "FounderApproved"
        };
        var provider = ProviderObservation(pair, source, providerTarget);
        var trusted = HumanAlignment(pair, source, trustedTarget);
        db.AddRange(source, providerTarget, trustedTarget, family, example, anchor, provider, trusted);
        await db.SaveChangesAsync();
        return new ProviderConflictSeed(provider.Id, trusted.Id, sourceLanguageCode, targetLanguageCode, sourceText);
    }

    private static HistoricalSourceFamily AddHistoricalSourceFamily(
        MasterAppDbContext db,
        Guid familyId,
        string familyKey)
    {
        var unit = Unit("en", $"Historical source {familyKey}.", "FounderApproved");
        var example = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = familyId,
            TextUnitId = unit.Id,
            LanguageCode = "en",
            Provenance = "FounderApproved"
        };
        var anchor = new LegendLanguageCompositionalAnchor
        {
            Id = Guid.NewGuid(),
            LanguageCode = "en",
            TextUnitId = unit.Id,
            CurriculumFamilyId = familyId,
            CurriculumExampleId = example.Id,
            Dimension = "conversation_function",
            Value = "opening",
            SemanticSignature = null,
            AnchorSignature = LegendLanguageIdentity.TextHash($"{example.Id:D}|opening"),
            Provenance = "FounderApproved"
        };
        db.AddRange(
            new LegendCurriculumFamily
            {
                Id = familyId,
                FamilyKey = familyKey,
                Provenance = "FounderApproved"
            },
            unit,
            example,
            new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(),
                CurriculumExampleId = example.Id,
                Dimension = "conversation_function",
                Value = "opening"
            },
            anchor);
        return new HistoricalSourceFamily(familyId, anchor.Id);
    }

    private static async Task<List<string>> QualityShapeAsync(MasterAppDbContext db, Guid alignmentId)
    {
        var evidence = await db.LegendTranslationQualityEvidence
            .Where(item => item.ObservedAlignmentId == alignmentId && item.SupersededUtc == null)
            .OrderBy(item => item.Signal).ThenBy(item => item.ReasonCode)
            .ToListAsync();
        return evidence.Select(item => string.Join("|", item.Signal, item.ReasonCode,
            item.SemanticSignature == null ? "none" : "semantic", item.ResolutionState)).ToList();
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
            ["LegendConnect:Learning:Enabled"] = "true",
            ["LegendConnect:Providers:AzureTranslator:MonthlyCapacityCharacters"] = "0",
            ["LegendConnect:Providers:AzureTranslator:LiveReserveCharacters"] = "0",
            ["LegendConnect:ContextualComposition:Mode"] = "Shadow",
            ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
            ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
            ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
            ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Synthetic test language",
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Synthetic test language",
            ["LegendConnect:LanguageRegistry:Baseline:2:Code"] = "x-source",
            ["LegendConnect:LanguageRegistry:Baseline:2:Name"] = "Synthetic source language",
            ["LegendConnect:LanguageRegistry:Baseline:2:NativeName"] = "Synthetic source language",
            ["LegendConnect:LanguageRegistry:Baseline:3:Code"] = "x-target",
            ["LegendConnect:LanguageRegistry:Baseline:3:Name"] = "Synthetic target language",
            ["LegendConnect:LanguageRegistry:Baseline:3:NativeName"] = "Synthetic target language"
        }).Build();

    private static LegendLanguageTextUnit Unit(string languageCode, string text, string provenance) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = provenance,
        IsTrainingEligible = true
    };

    private static LegendTranslationAlignment ProviderObservation(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "AzureTranslator", Provenance = "ProviderDerived", QualityState = "Observation", ObservationCount = 1
    };

    private static LegendTranslationAlignment HumanAlignment(
        LegendLanguagePairSnapshot pair,
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target) => new()
    {
        Id = Guid.NewGuid(), PairKey = pair.PairKey, SourceTextUnitId = source.Id, TargetTextUnitId = target.Id,
        Provider = "FounderApproved", Provenance = "FounderApproved", Confidence = 1m,
        QualityState = "Verified", HumanVerified = true, ObservationCount = 1
    };

    private sealed record ProviderConflictSeed(
        Guid ProviderAlignmentId,
        Guid TrustedAlignmentId,
        string SourceLanguageCode,
        string TargetLanguageCode,
        string SourceText);

    private sealed record HistoricalSourceFamily(Guid FamilyId, Guid AnchorId);

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}

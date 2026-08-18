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
    public async Task FounderProductionCompositionControl_UsesTheOnePersistedModeAndAuditsEachExplicitTransition()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);

        Assert.Equal("Shadow", (await policy.GetEffectiveAsync()).ContextualCompositionMode);
        Assert.Equal("Disabled", (await policy.SetContextualCompositionModeAsync("founder", "Disabled")).ContextualCompositionMode);
        Assert.Equal("Active", (await policy.SetContextualCompositionModeAsync("founder", "Active")).ContextualCompositionMode);

        var reloaded = Policy(db, new LegendLanguageRegistry(db, configuration), configuration);
        Assert.Equal("Active", (await reloaded.GetEffectiveAsync()).ContextualCompositionMode);
        Assert.Equal("Shadow", (await reloaded.SetContextualCompositionModeAsync("founder", "Shadow")).ContextualCompositionMode);
        Assert.Equal("Disabled", (await reloaded.SetContextualCompositionModeAsync("founder", "Disabled")).ContextualCompositionMode);

        var persisted = await db.LegendConnectRuntimePolicies.SingleAsync();
        Assert.Equal("Disabled", persisted.ContextualCompositionMode);
        var audits = await reloaded.GetRecentAuditAsync();
        Assert.Contains(audits, item => item.Action == "ContextualCompositionModeChanged" && item.Detail!.Contains("context mode: Shadow → Disabled"));
        Assert.Contains(audits, item => item.Action == "ContextualCompositionModeChanged" && item.Detail!.Contains("context mode: Disabled → Active"));
        Assert.Contains(audits, item => item.Action == "ContextualCompositionModeChanged" && item.Detail!.Contains("context mode: Active → Shadow"));
    }

    [Fact]
    public async Task HistoricalReevaluation_V7_RequiresOperationalTranslationsBeforeCompletion()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);

        var replay =
            await policy.GetOrStartLanguageIntelligenceReevaluationAsync(7);

        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            replay.Phase);

        await policy.AdvanceLanguageIntelligenceReevaluationAsync(
            7,
            LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies,
            null,
            true);

        replay =
            await policy.GetOrStartLanguageIntelligenceReevaluationAsync(7);

        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
            replay.Phase);

        await policy.AdvanceLanguageIntelligenceReevaluationAsync(
            7,
            LegendConnectLanguageIntelligenceReevaluationPhases.Alignments,
            null,
            true);

        replay =
            await policy.GetOrStartLanguageIntelligenceReevaluationAsync(7);

        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            replay.Phase);

        await policy.AdvanceLanguageIntelligenceReevaluationAsync(
            7,
            LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations,
            null,
            true);

        replay =
            await policy.GetOrStartLanguageIntelligenceReevaluationAsync(7);

        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations,
            replay.Phase);
        Assert.True(replay.RequiresWork);

        await policy.AdvanceLanguageIntelligenceReevaluationAsync(
            7,
            LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations,
            null,
            true);

        replay =
            await policy.GetOrStartLanguageIntelligenceReevaluationAsync(7);

        Assert.Equal(
            LegendConnectLanguageIntelligenceReevaluationPhases.Complete,
            replay.Phase);
        Assert.Equal(7, replay.CompletedEvaluatorVersion);
        Assert.False(replay.RequiresWork);
    }

    [Fact]
    public async Task ProductionCompositionMode_OnlyPermitsTheExistingGatedInternalPath()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        _ = await registry.GetOrCreateEnabledPairAsync("en", "fr");
        var policy = Policy(db, registry, configuration);

        var contextualSource = CanonicalSource("en", "Contextually approved phrase");
        var contextualTarget = CanonicalSource("ht", "Fraz kontèks apwouve");
        var lowQualitySource = CanonicalSource("en", "Low quality phrase");
        var lowQualityTarget = CanonicalSource("ht", "Fraz kalite ba");
        var ineligibleSource = CanonicalSource("en", "Ineligible evidence phrase");
        var ineligibleTarget = CanonicalSource("ht", "Fraz ki pa kalifye");
        ineligibleTarget.IsTrainingEligible = false;
        var exactSource = CanonicalSource("en", "Exact trusted phrase");
        var exactTarget = CanonicalSource("ht", "Fraz egzak konfyans");
        db.AddRange(contextualSource, contextualTarget, lowQualitySource, lowQualityTarget, ineligibleSource, ineligibleTarget, exactSource, exactTarget);
        db.AddRange(
            ContextRelationship(contextualSource, contextualTarget, "Verified", 0.99m),
            ContextRelationship(lowQualitySource, lowQualityTarget, "Observation", 0.99m),
            ContextRelationship(ineligibleSource, ineligibleTarget, "Verified", 0.99m),
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(), PairKey = "en:ht", SourceTextUnitId = exactSource.Id, TargetTextUnitId = exactTarget.Id,
                Provider = "FounderApproved", QualityState = "Verified", HumanVerified = true, UpdatedUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var provider = new RecordingProvider();
        var router = new LegendConnectTranslationRouter(
            provider,
            registry,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, policy),
            NullLogger<LegendConnectTranslationRouter>.Instance,
            intelligence: new LegendConnectTranslationIntelligence(db, configuration, policy),
            runtimePolicy: policy);

        await policy.SetContextualCompositionModeAsync("founder", "Disabled");
        var disabled = await router.TranslateAsync(contextualSource.Text, "ht", "en");
        Assert.Equal("AzureTranslator", disabled.Provider);
        Assert.Equal(1, provider.TranslateCalls);

        await policy.SetContextualCompositionModeAsync("founder", "Active");
        var active = await router.TranslateAsync(contextualSource.Text, "ht", "en");
        Assert.Equal("LegendConnectContextualComposition", active.Provider);
        Assert.Equal(contextualTarget.Text, active.TranslatedText);
        Assert.Equal(1, provider.TranslateCalls);

        var lowQuality = await router.TranslateAsync(lowQualitySource.Text, "ht", "en");
        var ineligible = await router.TranslateAsync(ineligibleSource.Text, "ht", "en");
        var isolated = await router.TranslateAsync(contextualSource.Text, "fr", "en");
        Assert.Equal("AzureTranslator", lowQuality.Provider);
        Assert.Equal("AzureTranslator", ineligible.Provider);
        Assert.Equal("AzureTranslator", isolated.Provider);
        Assert.Equal(4, provider.TranslateCalls);

        var exactMemory = await router.TranslateAsync(exactSource.Text, "ht", "en");
        var sameLanguage = await router.TranslateAsync("Leave this untouched", "en", "en");
        Assert.Equal("LegendConnectTranslationMemory", exactMemory.Provider);
        Assert.Equal(exactTarget.Text, exactMemory.TranslatedText);
        Assert.Equal("LegendConnectSameLanguage", sameLanguage.Provider);
        Assert.Equal("Leave this untouched", sameLanguage.TranslatedText);
        Assert.Equal(4, provider.TranslateCalls);
    }

    [Fact]
    public void RuntimePolicy_HasOnlyOnePersistedCompositionMode()
    {
        var compositionProperties = typeof(LegendConnectRuntimePolicy)
            .GetProperties()
            .Where(property => property.Name.Contains("Composition", StringComparison.Ordinal))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal<string>(["ContextualCompositionMode"], compositionProperties);
        Assert.DoesNotContain(typeof(LegendConnectRuntimePolicySnapshot).GetProperties(), property =>
            property.Name.Contains("ProductionComposition", StringComparison.Ordinal));
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
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nonFounder.ConfigureAutonomousLanguageFocusAsync("member",
            new LegendConnectAutonomousLanguageFocusMutation(true, ["ht"])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nonFounder.SetContextualCompositionModeAsync("member", "Active"));

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

        var convergenceBlocked =
            await policy.ActivateAsync("founder");
        Assert.Equal("BLOCKED", convergenceBlocked.State);
        Assert.Equal(
            "BLOCKED",
            Assert.Single(
                convergenceBlocked.Checks,
                item => item.Name == "Historical Convergence").State);

        await CompleteHistoricalConvergenceAsync(policy);

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
        await CompleteHistoricalConvergenceAsync(policy);

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
    public async Task FounderAutonomousLanguageFocus_UsesEnglishLearningSetsForMultipleSelectedTargets_ThenRestoresAutomaticPlanning()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        _ = await registry.GetOrCreateEnabledPairAsync("en", "ht");
        _ = await registry.GetOrCreateEnabledPairAsync("en", "es");
        _ = await registry.GetOrCreateEnabledPairAsync("en", "fr");
        _ = await registry.GetOrCreateEnabledPairAsync("ht", "en");
        db.LegendTranslationPairDemands.Add(new LegendTranslationPairDemand
        {
            Id = Guid.NewGuid(), PairKey = "en:fr", TranslationRequestCount = 500, LastRequestedUtc = DateTime.UtcNow
        });
        var haitian = Candidate("focus-haitian", "en", "ht", "Approved English learning set for Haitian Creole");
        var spanish = Candidate("focus-spanish", "en", "es", "Approved English learning set for Spanish");
        var french = Candidate("automatic-french", "en", "fr", "Higher demand French learning set");
        var reverseHaitian = Candidate("not-english-source", "ht", "en", "Konesans ki pa soti nan seri Angle a");
        reverseHaitian.Priority = 999;
        db.AddRange(
            CanonicalSource("en", haitian.SourceText),
            CanonicalSource("en", spanish.SourceText),
            CanonicalSource("en", french.SourceText),
            CanonicalSource("ht", reverseHaitian.SourceText),
            haitian, spanish, french, reverseHaitian);
        await db.SaveChangesAsync();

        await policy.ConfigureAutonomousLanguageFocusAsync(
            "founder",
            new LegendConnectAutonomousLanguageFocusMutation(true, ["Haitian Creole", "Spanish"]));

        var focused = await policy.GetEffectiveAsync();
        Assert.Equal(["es", "ht"], focused.FocusedTargetLanguageCodes);
        Assert.Equal(2, await db.LegendConnectAutonomousLanguageFocuses.CountAsync());

        var planner = new LegendConnectAutonomousGapPlanner(db, registry);
        var selected = await planner.SelectApprovedGapAsync(focused);
        Assert.NotNull(selected);
        Assert.Contains(selected.Value, new[] { haitian.Id, spanish.Id });
        Assert.NotEqual(french.Id, selected.Value);
        reverseHaitian.ProcessingState = "Queued";
        await db.SaveChangesAsync();

        var automatic = await policy.ConfigureAutonomousLanguageFocusAsync(
            "founder",
            new LegendConnectAutonomousLanguageFocusMutation(false, null));
        Assert.Empty(automatic.FocusedTargetLanguageCodes);
        Assert.Equal(0, await db.LegendConnectAutonomousLanguageFocuses.CountAsync());
        Assert.Equal(french.Id, await planner.SelectApprovedGapAsync(automatic));
        Assert.Contains(await policy.GetRecentAuditAsync(), item =>
            item.Action == "FounderAutonomousLanguageFocusEnabled" && item.Detail!.Contains("es, ht"));
    }

    [Fact]
    public async Task FocusedFounderIngestion_EntersTheSamePlannerBeforeTheBoundedWindow_AndPreservesAtomicWork()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        await policy.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(
            10_000, 100, 9_900, true, "Shadow", 0.98m));
        await policy.RecordWorkerHeartbeatAsync("Learning");
        await policy.RecordWorkerHeartbeatAsync("Acquisition");
        await CompleteHistoricalConvergenceAsync(policy);

        // This is the production failure shape: a large older backlog for
        // other targets preceded the new focused English batch. The focus
        // predicate must be part of the canonical query before Take(100).
        for (var index = 0; index < 100; index++)
        {
            var text = $"Older non-focused English source {index}.";
            db.AddRange(
                CanonicalSource("en", text),
                Candidate($"older-non-focused-{index}", "en", "ar", text));
        }
        await db.SaveChangesAsync();

        await policy.ConfigureAutonomousLanguageFocusAsync(
            "founder",
            new LegendConnectAutonomousLanguageFocusMutation(true, ["ht"]));

        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var submitted = await ingestion.SubmitAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "teacher\nstudent\nI read the book.", null, null, "Training", null, null, "FounderApproved"));

        Assert.True(submitted.Succeeded, submitted.Message);
        Assert.Equal(3, submitted.AtomicUnitCount);
        var focused = await policy.GetEffectiveAsync();
        var planner = new LegendConnectAutonomousGapPlanner(db, registry);
        var firstSelected = await planner.SelectApprovedGapAsync(focused);
        Assert.NotNull(firstSelected);
        var firstCandidate = await db.LegendCorpusCandidates.SingleAsync(item => item.Id == firstSelected);
        Assert.Equal("ht", firstCandidate.TargetLanguageCode);

        Assert.Equal("ACTIVE", (await policy.ActivateAsync("founder")).State);
        var provider = new RecordingProvider();
        var worker = new LegendConnectAutonomousLearningService(
            db, registry, provider,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, policy),
            corpus, planner, configuration, runtimePolicy: policy, curriculum: curriculum);
        for (var index = 0; index < 3; index++)
            await worker.ProcessOneAsync();

        var sourceTexts = new HashSet<string>(StringComparer.Ordinal)
        {
            "teacher", "student", "I read the book."
        };
        var completed = await db.LegendCorpusCandidates
            .Where(item => sourceTexts.Contains(item.SourceText) && item.TargetLanguageCode == "ht")
            .ToListAsync();
        Assert.Equal(3, completed.Count);
        Assert.All(completed, item => Assert.Equal("Queued", item.ProcessingState));
        Assert.Equal(3, provider.TranslateCalls);
        Assert.Equal(3, provider.SourceTexts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(provider.TargetLanguages, target => Assert.Equal("ht", target));
        Assert.DoesNotContain(await db.LegendTranslationAlignments.ToListAsync(), alignment =>
        {
            var target = db.LegendLanguageTextUnits.Single(unit => unit.Id == alignment.TargetTextUnitId);
            return target.LanguageCode != "ht" &&
                sourceTexts.Contains(db.LegendLanguageTextUnits.Single(unit => unit.Id == alignment.SourceTextUnitId).Text);
        });
    }

    [Fact]
    public async Task MultiFocus_UsesTheSameAtomicFounderBatchForEachSelectedRegisteredTarget()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var configuration = Configuration();
        var registry = new LegendLanguageRegistry(db, configuration);
        var policy = Policy(db, registry, configuration);
        await policy.UpdateAsync("founder", new LegendConnectRuntimePolicyMutation(
            10_000, 100, 9_900, true, "Shadow", 0.98m));
        await policy.RecordWorkerHeartbeatAsync("Learning");
        await policy.RecordWorkerHeartbeatAsync("Acquisition");
        await CompleteHistoricalConvergenceAsync(policy);
        await policy.ConfigureAutonomousLanguageFocusAsync(
            "founder",
            new LegendConnectAutonomousLanguageFocusMutation(true, ["ht", "es"]));

        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance);
        var curriculum = new LegendConnectCurriculumService(db, registry, corpus);
        var ingestion = new LegendConnectFounderTrainingIngestionAuthority(db, registry, corpus, curriculum);
        var submitted = await ingestion.SubmitAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "teacher\nstudent\nI read the book.", null, null, "Training", null, null, "FounderApproved"));

        Assert.True(submitted.Succeeded, submitted.Message);
        Assert.Equal("ACTIVE", (await policy.ActivateAsync("founder")).State);
        var provider = new RecordingProvider();
        var worker = new LegendConnectAutonomousLearningService(
            db, registry, provider,
            new TranslationCapacityAuthority(db, configuration, NullLogger<TranslationCapacityAuthority>.Instance, policy),
            corpus, new LegendConnectAutonomousGapPlanner(db, registry), configuration,
            runtimePolicy: policy, curriculum: curriculum);
        for (var index = 0; index < 6; index++)
            await worker.ProcessOneAsync();

        var sourceTexts = new[] { "teacher", "student", "I read the book." };
        Assert.Equal(6, provider.TranslateCalls);
        Assert.Equal(3, provider.TargetLanguages.Count(target => target == "ht"));
        Assert.Equal(3, provider.TargetLanguages.Count(target => target == "es"));
        Assert.Equal(6, await db.LegendCorpusCandidates.CountAsync(item =>
            sourceTexts.Contains(item.SourceText) &&
            (item.TargetLanguageCode == "ht" || item.TargetLanguageCode == "es") &&
            item.ProcessingState == "Queued"));
        Assert.Equal(6, await db.LegendTranslationAlignments.CountAsync(item =>
            item.SupersededUtc == null && (item.PairKey == "en:ht" || item.PairKey == "en:es")));
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
            await CompleteHistoricalConvergenceAsync(policy);
            seeded.AddRange(
                CanonicalSource("en", "One approved candidate"),
                Candidate("shared-candidate", "en", "ht", "One approved candidate"));
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

    private static async Task CompleteHistoricalConvergenceAsync(
        LegendConnectRuntimePolicyAuthority policy)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            var replay =
                await policy.GetOrStartLanguageIntelligenceReevaluationAsync(
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

            if (!replay.RequiresWork)
                return;

            await policy.AdvanceLanguageIntelligenceReevaluationAsync(
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                replay.Phase,
                null,
                true);
        }

        throw new Xunit.Sdk.XunitException(
            "The canonical historical convergence state did not complete.");
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

    private static LegendLanguageTextUnit CanonicalSource(string languageCode, string text) => new()
    {
        Id = Guid.NewGuid(),
        LanguageCode = languageCode,
        StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
        NormalizedHash = LegendLanguageIdentity.TextHash(text),
        Text = LegendLanguageIdentity.NormalizeText(text),
        Provenance = "FounderApproved",
        IsTrainingEligible = true
    };

    private static LegendLanguageContextRelationship ContextRelationship(
        LegendLanguageTextUnit source,
        LegendLanguageTextUnit target,
        string qualityState,
        decimal confidence) => new()
    {
        Id = Guid.NewGuid(),
        PairKey = "en:ht",
        SourceTextUnitId = source.Id,
        RelatedTextUnitId = target.Id,
        RelationshipKind = "ContextualExample",
        ContextSignature = "production-control",
        SourcePatternSignature = LegendLanguageIdentity.ContextPatternSignature(source.Text),
        Confidence = confidence,
        QualityState = qualityState,
        Provenance = "FounderApproved",
        ObservationCount = 1,
        UpdatedUtc = DateTime.UtcNow
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
        public List<string> SourceTexts { get; } = [];
        public List<string> TargetLanguages { get; } = [];
        public Task<TranslationDetectionResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationDetectionResult(true, "en"));
        public Task<TranslationProviderResult> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, CancellationToken cancellationToken = default)
        {
            System.Threading.Interlocked.Increment(ref _translateCalls);
            lock (SourceTexts)
            {
                SourceTexts.Add(text);
                TargetLanguages.Add(targetLanguage);
            }
            return Task.FromResult(new TranslationProviderResult(true, "Translated", sourceLanguage, ProviderName));
        }
    }
}

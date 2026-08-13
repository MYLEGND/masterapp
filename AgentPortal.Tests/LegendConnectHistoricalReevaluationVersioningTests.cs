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
        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, 1, take: 1);

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

        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, 2, take: 1);

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
        await DrainCanonicalWorkerCycleAsync(runtime, curriculum, intelligence, 2, take: 1);
        var secondPass = new
        {
            Patterns = await db.LegendLanguageStructuralPatterns.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Quality = await db.LegendTranslationQualityEvidence.CountAsync(),
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync()
        };
        Assert.Equal(converged, secondPass);
    }

    private static async Task DrainCanonicalWorkerCycleAsync(
        LegendConnectRuntimePolicyAuthority runtime,
        LegendConnectCurriculumService curriculum,
        ILegendConnectTranslationIntelligence intelligence,
        int evaluatorVersion,
        int take)
    {
        for (var pass = 0; pass < 32; pass++)
        {
            var state = await runtime.GetOrStartLanguageIntelligenceReevaluationAsync(evaluatorVersion);
            if (!state.RequiresWork)
                return;
            var progress = state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.ProviderObservations
                ? await intelligence.ReevaluateHistoricalProviderObservationsAsync(take, state.Cursor)
                : await curriculum.ReevaluateHistoricalAlignmentsAsync(take, state.Phase, state.Cursor);
            await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                evaluatorVersion, state.Phase, progress.LastProcessedId, progress.PhaseComplete);
        }

        throw new Xunit.Sdk.XunitException("The bounded canonical historical replay did not converge.");
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
            ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Synthetic test language"
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

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}

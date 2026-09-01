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
/// Regression proof for the canonical sentence-semantic anchor boundary. The
/// controlled labels deliberately do not occur as surface tokens, so a result
/// can emerge only if the existing curriculum anchor and relationship
/// authorities preserve that explicit Founder evidence correctly.
/// </summary>
public sealed class LegendConnectStructuralSemanticAnchorRepairTests
{
    [Fact]
    public async Task CurrentFounderCurriculum_AccumulatesSemanticOnlyControlledVariationAcrossIndependentFamilies()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);

        foreach (var batch in ControlledBatches())
            Assert.True((await fixture.Curriculum.SubmitFounderBatchAsync(batch)).Succeeded);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "en" &&
            item.VariationDimension == "state" && item.SupersededUtc == null);
        var evidence = await db.LegendLanguageStructuralEvidence
            .Where(item => item.StructuralRelationshipId == relationship.Id && item.SupersededUtc == null)
            .ToListAsync();
        var sentenceAnchors = await db.LegendLanguageCompositionalAnchors
            .Where(item => item.LanguageCode == "en" && item.Dimension == "state" &&
                item.LexemeId == null && item.ComponentStartTokenIndex == null && item.ComponentLength == null &&
                item.SupersededUtc == null)
            .ToListAsync();

        Assert.Equal(6, sentenceAnchors.Count);
        Assert.Equal(3, evidence.Select(item => item.CurriculumFamilyId).Distinct().Count());
        Assert.All(evidence, item => Assert.Equal("Supported", item.StructuralRelationshipContributionState));
        Assert.Equal(3, relationship.SupportCount);
        Assert.True(relationship.IndependentSourceCount >= 3);
        Assert.Equal(3, relationship.HumanVerifiedSupportCount);
        Assert.Equal(0, relationship.ProviderOnlySupportCount);
        Assert.Equal(0, relationship.ContradictionCount);
        Assert.Equal("Supported", relationship.MaturityState);
        Assert.False(relationship.IsProductionEligible);
    }

    [Fact]
    public async Task HistoricalNonEnglishSource_ReplaysThroughTheSameAnchorAuthorityAndConvergesWithCurrentCurriculum()
    {
        await using var historicalDb = ControllerTestHelpers.BuildDb();
        var historical = CreateFixture(historicalDb);

        // Simulate a deployment whose prior evaluator checkpoint had already
        // completed before this historical corpus became eligible for the
        // corrected evaluator semantics.
        await DrainCanonicalReplayAsync(historical.Runtime, historical.Curriculum, historical.Intelligence, historical.Operations, 2);
        foreach (var batch in ControlledBatches())
            AddHistoricalFounderFamily(historicalDb, "x-alpha", batch);
        await historicalDb.SaveChangesAsync();

        var lineageBefore = new
        {
            TextUnits = await historicalDb.LegendLanguageTextUnits.CountAsync(),
            Examples = await historicalDb.LegendCurriculumExamples.CountAsync(),
            Variations = await historicalDb.LegendCurriculumExampleVariations.CountAsync(),
            Anchors = await historicalDb.LegendLanguageCompositionalAnchors.CountAsync(),
            Evidence = await historicalDb.LegendLanguageStructuralEvidence.CountAsync()
        };
        Assert.Equal(0, lineageBefore.Anchors);
        Assert.Equal(0, lineageBefore.Evidence);

        var replay = await historical.Runtime.GetOrStartLanguageIntelligenceReevaluationAsync(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        Assert.True(replay.RequiresWork);
        Assert.Equal(LegendConnectLanguageIntelligenceReevaluationPhases.SourceFamilies, replay.Phase);
        await DrainCanonicalReplayAsync(
            historical.Runtime,
            historical.Curriculum,
            historical.Intelligence,
            historical.Operations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);

        var historicalRelationship = await historicalDb.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "x-alpha" &&
            item.VariationDimension == "state" && item.SupersededUtc == null);
        var historicalAfter = new
        {
            TextUnits = await historicalDb.LegendLanguageTextUnits.CountAsync(),
            Examples = await historicalDb.LegendCurriculumExamples.CountAsync(),
            Variations = await historicalDb.LegendCurriculumExampleVariations.CountAsync(),
            Anchors = await historicalDb.LegendLanguageCompositionalAnchors.CountAsync(),
            Evidence = await historicalDb.LegendLanguageStructuralEvidence.CountAsync(),
            Relationships = await historicalDb.LegendLanguageStructuralRelationships.CountAsync()
        };

        Assert.Equal(lineageBefore.TextUnits, historicalAfter.TextUnits);
        Assert.Equal(lineageBefore.Examples, historicalAfter.Examples);
        Assert.Equal(lineageBefore.Variations, historicalAfter.Variations);
        Assert.True(historicalAfter.Anchors > 0);
        Assert.Equal(3, historicalRelationship.SupportCount);
        Assert.Equal("Supported", historicalRelationship.MaturityState);
        Assert.Equal(0, historicalRelationship.ContradictionCount);
        Assert.False(historicalRelationship.IsProductionEligible);

        var converged = historicalAfter;
        await DrainCanonicalReplayAsync(
            historical.Runtime,
            historical.Curriculum,
            historical.Intelligence,
            historical.Operations,
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current);
        var secondPass = new
        {
            TextUnits = await historicalDb.LegendLanguageTextUnits.CountAsync(),
            Examples = await historicalDb.LegendCurriculumExamples.CountAsync(),
            Variations = await historicalDb.LegendCurriculumExampleVariations.CountAsync(),
            Anchors = await historicalDb.LegendLanguageCompositionalAnchors.CountAsync(),
            Evidence = await historicalDb.LegendLanguageStructuralEvidence.CountAsync(),
            Relationships = await historicalDb.LegendLanguageStructuralRelationships.CountAsync()
        };
        Assert.Equal(converged, secondPass);

        await using var currentDb = ControllerTestHelpers.BuildDb();
        var current = CreateFixture(currentDb);
        foreach (var batch in ControlledBatches())
            Assert.True((await current.Curriculum.SubmitFounderBatchAsync(batch)).Succeeded);
        var currentRelationship = await currentDb.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "en" &&
            item.VariationDimension == "state" && item.SupersededUtc == null);

        Assert.Equal(historicalRelationship.SupportCount, currentRelationship.SupportCount);
        Assert.Equal(historicalRelationship.IndependentSourceCount, currentRelationship.IndependentSourceCount);
        Assert.Equal(historicalRelationship.HumanVerifiedSupportCount, currentRelationship.HumanVerifiedSupportCount);
        Assert.Equal(historicalRelationship.ContradictionCount, currentRelationship.ContradictionCount);
        Assert.Equal(historicalRelationship.MaturityState, currentRelationship.MaturityState);
        Assert.False(currentRelationship.IsProductionEligible);
    }

    private static IReadOnlyList<LegendConnectCurriculumBatchSubmission> ControlledBatches() =>
    [
        Batch("semantic.layout.one", "ka rel logs", "ka not rel logs", "ka", "rel", "logs"),
        Batch("semantic.layout.two", "mi scan files", "mi not scan files", "mi", "scan", "files"),
        Batch("semantic.layout.three", "tu map notes", "tu not map notes", "tu", "map", "notes")
    ];

    private static LegendConnectCurriculumBatchSubmission Batch(
        string familyKey,
        string firstText,
        string secondText,
        string actor,
        string action,
        string @object) => new(
        familyKey,
        "Founder-controlled semantic structure",
        [
            new LegendConnectCurriculumExampleSubmission(firstText, Variations(actor, action, @object, "open")),
            new LegendConnectCurriculumExampleSubmission(secondText, Variations(actor, action, @object, "closed"))
        ]);

    private static IReadOnlyDictionary<string, string> Variations(string actor, string action, string @object, string state) =>
        new Dictionary<string, string>
        {
            ["actor"] = actor,
            ["action"] = action,
            ["object"] = @object,
            // "open" and "closed" are explicit Founder-controlled semantics,
            // not literal tokens in these source units.
            ["state"] = state
        };

    private static void AddHistoricalFounderFamily(
        MasterAppDbContext db,
        string languageCode,
        LegendConnectCurriculumBatchSubmission batch)
    {
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = batch.FamilyKey,
            SemanticCategory = batch.SemanticCategory,
            Provenance = "FounderApproved"
        };
        db.Add(family);
        foreach (var input in batch.Examples!)
        {
            var unit = new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = languageCode,
                StoragePartition = LegendLanguageIdentity.DatasetNamespace(languageCode),
                NormalizedHash = LegendLanguageIdentity.TextHash(input.Text),
                Text = LegendLanguageIdentity.NormalizeText(input.Text),
                Provenance = "FounderApproved",
                IsTrainingEligible = true
            };
            var example = new LegendCurriculumExample
            {
                Id = Guid.NewGuid(),
                CurriculumFamilyId = family.Id,
                TextUnitId = unit.Id,
                LanguageCode = languageCode,
                Provenance = "FounderApproved"
            };
            db.AddRange(unit, example);
            foreach (var variation in input.Variations!)
            {
                db.Add(new LegendCurriculumExampleVariation
                {
                    Id = Guid.NewGuid(),
                    CurriculumExampleId = example.Id,
                    Dimension = variation.Key,
                    Value = variation.Value
                });
            }
        }
    }

    private static async Task DrainCanonicalReplayAsync(
        LegendConnectRuntimePolicyAuthority runtime,
        LegendConnectCurriculumService curriculum,
        ILegendConnectTranslationIntelligence intelligence,
        ILegendConnectOperations operations,
        int evaluatorVersion)
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
                    25,
                    state.Cursor);
            }
            else if (state.Phase == LegendConnectLanguageIntelligenceReevaluationPhases.OperationalTranslations)
            {
                progress = await operations.ReconcileHistoricalOperationalTranslationsAsync(
                    25,
                    state.Cursor);
            }
            else
            {
                progress = await curriculum.ReevaluateHistoricalAlignmentsAsync(
                    25,
                    state.Phase,
                    state.Cursor);
            }
            await runtime.AdvanceLanguageIntelligenceReevaluationAsync(
                evaluatorVersion, state.Phase, progress.LastProcessedId, progress.PhaseComplete);
        }

        throw new Xunit.Sdk.XunitException("The existing bounded historical replay did not converge.");
    }

    private static Fixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
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
                ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-alpha",
                ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Synthetic Alpha",
                ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Synthetic Alpha"
            }).Build();
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

        return new Fixture(
            runtime,
            intelligence,
            curriculum,
            operations);
    }

    private sealed record Fixture(
        LegendConnectRuntimePolicyAuthority Runtime,
        ILegendConnectTranslationIntelligence Intelligence,
        LegendConnectCurriculumService Curriculum,
        ILegendConnectOperations Operations);

    private sealed class FounderAccess : IControlledResourceAccessService
    {
        public Task<ControlledResourceAccess> GetAccessAsync(MessagingActor actor, string resourceType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ControlledResourceAccess(resourceType, ControlledResourceAccessStates.NotGranted, true));
        public Task<bool> IsFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsCanonicalFounderManagerAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetPreferredLanguageAsync(MessagingActor actor, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}

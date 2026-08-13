using System;
using System.Collections.Generic;
using System.Linq;
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
/// Regression coverage for reusable, evidence-backed structural observations.
/// The examples use only controlled curriculum metadata and existing anchors;
/// no test asserts an English grammar rule or a language-specific parser.
/// </summary>
public sealed class LegendConnectCrossExampleStructuralLearningTests
{
    [Fact]
    public async Task CrossFamilyFounderEvidenceWithDifferentPropositionsAccumulatesOneReusableRelationship()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var operations = new LegendConnectOperations(
            db, fixture.Registry, fixture.Corpus, fixture.Configuration, curriculum: fixture.Curriculum);
        foreach (var batch in ControlledAgentBatches())
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "en" &&
            item.VariationDimension == "agent" && item.SupersededUtc == null);
        var evidence = await db.LegendLanguageStructuralEvidence
            .Where(item => item.StructuralRelationshipId == relationship.Id && item.SupersededUtc == null)
            .ToListAsync();
        var propositionSignatures = await (
            from structuralEvidence in db.LegendLanguageStructuralEvidence
            join pattern in db.LegendLanguageStructuralPatterns on structuralEvidence.StructuralPatternId equals pattern.Id
            where structuralEvidence.StructuralRelationshipId == relationship.Id && structuralEvidence.SupersededUtc == null
            select pattern.PropositionSignature
        ).Distinct().ToListAsync();

        Assert.Equal(3, evidence.Select(item => item.CurriculumFamilyId).Distinct().Count());
        Assert.Equal(3, propositionSignatures.Count);
        Assert.All(evidence, item => Assert.Equal("Supported", item.StructuralRelationshipContributionState));
        Assert.Equal(3, relationship.SupportCount);
        Assert.True(relationship.IndependentSourceCount >= 3);
        Assert.Equal(3, relationship.HumanVerifiedSupportCount);
        Assert.Equal(0, relationship.ProviderOnlySupportCount);
        Assert.Equal(0, relationship.ContradictionCount);
        Assert.Equal("Supported", relationship.MaturityState);
        Assert.False(relationship.IsProductionEligible);

        var projection = Assert.IsType<LegendConnectLanguageKnowledgeSnapshot>(
            await operations.GetLanguageKnowledgeAsync("en"));
        Assert.All(projection.StructuralPatterns ?? [], item =>
        {
            Assert.Equal("Observation", item.MaturityState);
            Assert.Equal(1, item.SupportCount);
        });
        var projectedRelationship = Assert.Single(projection.StructuralRelationships ?? []);
        Assert.Equal("agent", projectedRelationship.VariationDimension);
        Assert.Equal("Supported", projectedRelationship.MaturityState);
        Assert.Equal(3, projectedRelationship.SupportCount);
        Assert.Equal(6, projectedRelationship.IndependentSourceCount);
        Assert.Equal(3, projectedRelationship.HumanVerifiedSupportCount);
        Assert.Equal(0, projectedRelationship.ProviderOnlySupportCount);
        Assert.False(projectedRelationship.IsProductionEligible);

        var first = new
        {
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Support = relationship.SupportCount,
            Contradictions = relationship.ContradictionCount
        };
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var reloaded = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        var second = new
        {
            Relationships = await db.LegendLanguageStructuralRelationships.CountAsync(),
            Evidence = await db.LegendLanguageStructuralEvidence.CountAsync(),
            Support = reloaded.SupportCount,
            Contradictions = reloaded.ContradictionCount
        };
        Assert.Equal(first, second);
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "A new sentence."));
        Assert.False(reloaded.IsProductionEligible);
    }

    [Fact]
    public async Task ProviderDerivedExamplesRemainInsufficientAndCannotPromoteAReusableRelationship()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        foreach (var batch in ControlledAgentBatches().Take(2))
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync();
        Assert.Equal(2, relationship.SupportCount);
        Assert.Equal("Candidate", relationship.MaturityState);

        AddProviderOnlyEnglishFamily(db, "provider.agent.observation");
        await db.SaveChangesAsync();
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var reloaded = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(2, reloaded.SupportCount);
        Assert.Equal(0, reloaded.ProviderOnlySupportCount);
        Assert.Equal("Candidate", reloaded.MaturityState);
        Assert.Contains(await db.LegendLanguageStructuralEvidence.ToListAsync(), item =>
            item.Provenance == "ProviderDerived" && item.ContributionState == "Insufficient" &&
            item.StructuralRelationshipId == null);
    }

    [Fact]
    public async Task ConflictingFounderAnchoredLayoutsAreRetainedAsContradictionInsteadOfBeingAbsorbed()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        foreach (var batch in ControlledAgentBatches().Take(2))
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);
        Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(new LegendConnectCurriculumBatchSubmission(
            "agent.layout.conflict", "Controlled component layout",
            [
                new LegendConnectCurriculumExampleSubmission("Reports I inspect.", AgentFrame("I", "inspect", "reports")),
                new LegendConnectCurriculumExampleSubmission("Reports you inspect.", AgentFrame("you", "inspect", "reports"))
            ]))).Succeeded);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "en" && item.VariationDimension == "agent");
        var evidence = await db.LegendLanguageStructuralEvidence
            .Where(item => item.StructuralRelationshipId == relationship.Id && item.SupersededUtc == null)
            .ToListAsync();
        Assert.Equal(2, relationship.SupportCount);
        Assert.Equal(1, relationship.ContradictionCount);
        Assert.Equal("Observation", relationship.MaturityState);
        Assert.Contains(evidence, item => item.StructuralRelationshipContributionState == "Contradictory" &&
            item.Provenance == "FounderApproved");
        Assert.False(relationship.IsProductionEligible);
    }

    [Fact]
    public async Task SupersedingFounderLineageRetainsHistoryAndRecalculatesRelationshipMaturity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        foreach (var batch in ControlledAgentBatches())
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync();
        var retiringEvidence = await db.LegendLanguageStructuralEvidence
            .Where(item => item.StructuralRelationshipId == relationship.Id)
            .OrderByDescending(item => item.CreatedUtc)
            .FirstAsync();
        var retiringUnitIds = await db.LegendCurriculumExamples
            .Where(item => item.Id == retiringEvidence.BaselineCurriculumExampleId ||
                item.Id == retiringEvidence.ComparedCurriculumExampleId)
            .Select(item => item.TextUnitId)
            .ToListAsync();

        await fixture.Curriculum.ReconcileSupersededExamplesAsync(retiringUnitIds);

        var reloaded = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(2, reloaded.SupportCount);
        Assert.Equal("Candidate", reloaded.MaturityState);
        Assert.False(reloaded.IsProductionEligible);
        Assert.NotNull((await db.LegendLanguageStructuralEvidence
            .SingleAsync(item => item.Id == retiringEvidence.Id)).SupersededUtc);
    }

    [Fact]
    public async Task UnanchoredSurfaceSimilarityDoesNotCreateReusableStructuralKnowledge()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var batches = new[]
        {
            new LegendConnectCurriculumBatchSubmission(
                "unknown.surface.one", null,
                [
                    new LegendConnectCurriculumExampleSubmission("I review the file.", Polarity("affirmative")),
                    new LegendConnectCurriculumExampleSubmission("I do not review the file.", Polarity("negative"))
                ]),
            new LegendConnectCurriculumBatchSubmission(
                "unknown.surface.two", null,
                [
                    new LegendConnectCurriculumExampleSubmission("We review the record.", Polarity("affirmative")),
                    new LegendConnectCurriculumExampleSubmission("We do not review the record.", Polarity("negative"))
                ]),
            new LegendConnectCurriculumBatchSubmission(
                "unknown.surface.three", null,
                [
                    new LegendConnectCurriculumExampleSubmission("They review the note.", Polarity("affirmative")),
                    new LegendConnectCurriculumExampleSubmission("They do not review the note.", Polarity("negative"))
                ])
        };
        foreach (var batch in batches)
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);

        Assert.Empty(await db.LegendLanguageStructuralRelationships.ToListAsync());
        Assert.NotEmpty(await db.LegendLanguageStructuralEvidence.ToListAsync());
    }

    [Fact]
    public async Task RegisteredSyntheticLanguageUsesTheSameStructuralEvidenceMachinery()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        Assert.Equal("x-test", await fixture.Registry.NormalizeEnabledTranslationLanguageAsync("x-test"));

        AddFounderControlledLanguageFamily(db, "x-test.one", "za run logs", "zi run logs", "za", "zi", "run", "logs");
        AddFounderControlledLanguageFamily(db, "x-test.two", "zo read files", "zu read files", "zo", "zu", "read", "files");
        AddFounderControlledLanguageFamily(db, "x-test.three", "ze scan notes", "zy scan notes", "ze", "zy", "scan", "notes");
        await db.SaveChangesAsync();

        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == string.Empty && item.LanguageCode == "x-test" && item.VariationDimension == "agent");
        Assert.Equal(3, relationship.SupportCount);
        Assert.Equal("Supported", relationship.MaturityState);
        Assert.False(relationship.IsProductionEligible);
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "Unobserved source text."));
    }

    [Fact]
    public async Task RegistryLanguageCorrectionsDemoteRestoreAndResolveContradictionsThroughTheCanonicalEvaluator()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        var fixture = CreateFixture(db);
        var operations = new LegendConnectOperations(
            db, fixture.Registry, fixture.Corpus, fixture.Configuration, curriculum: fixture.Curriculum);
        foreach (var batch in ControlledAgentBatches())
            Assert.True((await fixture.Curriculum.SubmitFounderEnglishBatchAsync(batch)).Succeeded);

        var firstTargets = new Dictionary<string, string>
        {
            ["I inspect records."] = "za inspect records",
            ["You inspect records."] = "zi inspect records",
            ["We review reports."] = "za review reports",
            ["They review reports."] = "zi review reports",
            ["She catalogs notes."] = "za catalogs notes",
            ["He catalogs notes."] = "zi catalogs notes"
        };
        var alignments = new Dictionary<string, LegendConnectKnowledgeSubmissionResult>();
        foreach (var target in firstTargets)
        {
            var sourceId = await db.LegendLanguageTextUnits
                .Where(item => item.LanguageCode == "en" && item.Text == target.Key)
                .Select(item => item.Id)
                .SingleAsync();
            var submitted = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
                "en", target.Key, "x-test", target.Value, "Training", null, null, "FounderApproved"),
                reusableSourceTextUnitId: sourceId);
            Assert.True(submitted.Succeeded, submitted.Message);
            alignments.Add(target.Key, submitted);
        }
        await AddTargetAnchorsAsync(db, "x-test", agentAtEnd: false);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var relationship = await db.LegendLanguageStructuralRelationships.SingleAsync(item =>
            item.PairKey == "en:x-test" && item.LanguageCode == "x-test" && item.VariationDimension == "agent");
        Assert.Equal(3, relationship.SupportCount);
        Assert.Equal("Supported", relationship.MaturityState);

        var correctedSupport = await operations.CorrectFounderKnowledgeAsync("founder", alignments["I inspect records."].AlignmentId!.Value,
            new LegendConnectKnowledgeSubmission("en", "I inspect records.", "x-test", "zu inspect records", "Training", null, null, "FounderApproved"));
        Assert.True(correctedSupport.Succeeded, correctedSupport.Message);
        var demoted = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(2, demoted.SupportCount);
        Assert.Equal("Candidate", demoted.MaturityState);
        Assert.Contains(await db.LegendLanguageStructuralEvidence.ToListAsync(), item =>
            item.StructuralRelationshipId == relationship.Id && item.SupersededUtc is not null);

        var originalSource = await db.LegendLanguageTextUnits.SingleAsync(item => item.Id == correctedSupport.SourceTextUnitId);
        for (var index = 0; index < 2; index++)
        {
            var providerTarget = Unit("x-test", $"provider inspect {index}", "ProviderDerived");
            db.AddRange(providerTarget, new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(), PairKey = "en:x-test", SourceTextUnitId = originalSource.Id,
                TargetTextUnitId = providerTarget.Id, Provider = "AzureTranslator", QualityState = "Observation",
                Provenance = "ProviderDerived", ObservationCount = 1, Confidence = .5m
            });
        }
        await db.SaveChangesAsync();
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var providerCannotRepair = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(2, providerCannotRepair.SupportCount);
        Assert.Equal("Candidate", providerCannotRepair.MaturityState);
        Assert.Equal(0, providerCannotRepair.ProviderOnlySupportCount);

        await AddTargetAnchorsAsync(db, "x-test", agentAtEnd: false);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var restored = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(3, restored.SupportCount);
        Assert.Equal("Supported", restored.MaturityState);

        var conflictingBatch = new LegendConnectCurriculumBatchSubmission(
            "agent.layout.correctable-conflict", "Controlled component layout",
            [
                new LegendConnectCurriculumExampleSubmission("Reports I audit.", AgentFrame("I", "audit", "reports")),
                new LegendConnectCurriculumExampleSubmission("Reports you audit.", AgentFrame("you", "audit", "reports"))
            ]);
        var conflictFamily = Assert.IsType<LegendConnectCurriculumSubmissionResult>(
            await fixture.Curriculum.SubmitFounderEnglishBatchAsync(conflictingBatch));
        Assert.True(conflictFamily.Succeeded, conflictFamily.Message);
        var conflictLeftSourceId = await db.LegendLanguageTextUnits
            .Where(item => item.LanguageCode == "en" && item.Text == "Reports I audit.")
            .Select(item => item.Id)
            .SingleAsync();
        var conflictRightSourceId = await db.LegendLanguageTextUnits
            .Where(item => item.LanguageCode == "en" && item.Text == "Reports you audit.")
            .Select(item => item.Id)
            .SingleAsync();
        var conflictLeft = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "Reports I audit.", "x-test", "reports audit za", "Training", null, null, "FounderApproved"),
            reusableSourceTextUnitId: conflictLeftSourceId);
        var conflictRight = await operations.SubmitFounderKnowledgeAsync("founder", new LegendConnectKnowledgeSubmission(
            "en", "Reports you audit.", "x-test", "reports audit zi", "Training", null, null, "FounderApproved"),
            reusableSourceTextUnitId: conflictRightSourceId);
        Assert.True(conflictLeft.Succeeded, conflictLeft.Message);
        Assert.True(conflictRight.Succeeded, conflictRight.Message);
        await AddTargetAnchorsAsync(db, "x-test", agentAtEnd: true, conflictFamily.CurriculumFamilyId);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        var contradicted = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(1, contradicted.ContradictionCount);
        Assert.Equal("Observation", contradicted.MaturityState);
        Assert.Contains(await db.LegendLanguageStructuralEvidence.ToListAsync(), item =>
            item.StructuralRelationshipId == relationship.Id &&
            item.StructuralRelationshipContributionState == "Contradictory" && item.SupersededUtc is null);

        var correctedConflict = await operations.CorrectFounderKnowledgeAsync("founder", conflictLeft.AlignmentId!.Value,
            new LegendConnectKnowledgeSubmission("en", "Reports I audit.", "x-test", "za audit reports", "Training", null, null, "FounderApproved"));
        Assert.True(correctedConflict.Succeeded, correctedConflict.Message);
        var correctedConflictRight = await operations.CorrectFounderKnowledgeAsync("founder", conflictRight.AlignmentId!.Value,
            new LegendConnectKnowledgeSubmission("en", "Reports you audit.", "x-test", "zi audit reports", "Training", null, null, "FounderApproved"));
        Assert.True(correctedConflictRight.Succeeded, correctedConflictRight.Message);
        await AddTargetAnchorsAsync(db, "x-test", agentAtEnd: false, conflictFamily.CurriculumFamilyId);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);
        await fixture.Curriculum.ReevaluateHistoricalAlignmentsAsync(100);

        var resolved = await db.LegendLanguageStructuralRelationships.SingleAsync(item => item.Id == relationship.Id);
        Assert.Equal(4, resolved.SupportCount);
        Assert.Equal(0, resolved.ContradictionCount);
        Assert.Equal("Supported", resolved.MaturityState);
        Assert.False(resolved.IsProductionEligible);
        Assert.Null(await fixture.Curriculum.TryComposeAsync("en", "x-test", "No production formulation."));
    }

    private static IEnumerable<LegendConnectCurriculumBatchSubmission> ControlledAgentBatches()
    {
        yield return new LegendConnectCurriculumBatchSubmission(
            "agent.layout.one", "Controlled component layout",
            [
                new LegendConnectCurriculumExampleSubmission("I inspect records.", AgentFrame("I", "inspect", "records")),
                new LegendConnectCurriculumExampleSubmission("You inspect records.", AgentFrame("You", "inspect", "records"))
            ]);
        yield return new LegendConnectCurriculumBatchSubmission(
            "agent.layout.two", "Controlled component layout",
            [
                new LegendConnectCurriculumExampleSubmission("We review reports.", AgentFrame("We", "review", "reports")),
                new LegendConnectCurriculumExampleSubmission("They review reports.", AgentFrame("They", "review", "reports"))
            ]);
        yield return new LegendConnectCurriculumBatchSubmission(
            "agent.layout.three", "Controlled component layout",
            [
                new LegendConnectCurriculumExampleSubmission("She catalogs notes.", AgentFrame("She", "catalogs", "notes")),
                new LegendConnectCurriculumExampleSubmission("He catalogs notes.", AgentFrame("He", "catalogs", "notes"))
            ]);
    }

    private static IReadOnlyDictionary<string, string> AgentFrame(string agent, string predicate, string @object) =>
        new Dictionary<string, string>
        {
            ["agent"] = agent,
            ["predicate"] = predicate,
            ["object"] = @object
        };

    private static IReadOnlyDictionary<string, string> Polarity(string value) =>
        new Dictionary<string, string> { ["polarity"] = value };

    private static void AddProviderOnlyEnglishFamily(MasterAppDbContext db, string familyKey)
    {
        var family = new LegendCurriculumFamily { Id = Guid.NewGuid(), FamilyKey = familyKey, Provenance = "ProviderDerived" };
        var left = Unit("en", "They inspect reports.", "ProviderDerived");
        var right = Unit("en", "We inspect reports.", "ProviderDerived");
        var leftExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = left.Id, LanguageCode = "en", Provenance = "ProviderDerived"
        };
        var rightExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = right.Id, LanguageCode = "en", Provenance = "ProviderDerived"
        };
        db.AddRange(family, left, right, leftExample, rightExample);
        AddVariations(db, leftExample, AgentFrame("They", "inspect", "reports"));
        AddVariations(db, rightExample, AgentFrame("We", "inspect", "reports"));
    }

    private static void AddFounderControlledLanguageFamily(
        MasterAppDbContext db,
        string familyKey,
        string leftText,
        string rightText,
        string leftAgent,
        string rightAgent,
        string predicate,
        string @object)
    {
        var family = new LegendCurriculumFamily { Id = Guid.NewGuid(), FamilyKey = familyKey, Provenance = "FounderApproved" };
        var left = Unit("x-test", leftText, "FounderApproved");
        var right = Unit("x-test", rightText, "FounderApproved");
        var leftExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = left.Id, LanguageCode = "x-test", Provenance = "FounderApproved"
        };
        var rightExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(), CurriculumFamilyId = family.Id, TextUnitId = right.Id, LanguageCode = "x-test", Provenance = "FounderApproved"
        };
        var leftVariations = AgentFrame(leftAgent, predicate, @object);
        var rightVariations = AgentFrame(rightAgent, predicate, @object);
        db.AddRange(family, left, right, leftExample, rightExample);
        AddVariations(db, leftExample, leftVariations);
        AddVariations(db, rightExample, rightVariations);
        AddComponentAnchors(db, left, leftExample, leftVariations);
        AddComponentAnchors(db, right, rightExample, rightVariations);
    }

    private static void AddVariations(
        MasterAppDbContext db,
        LegendCurriculumExample example,
        IReadOnlyDictionary<string, string> variations)
    {
        foreach (var variation in variations)
        {
            db.Add(new LegendCurriculumExampleVariation
            {
                Id = Guid.NewGuid(), CurriculumExampleId = example.Id, Dimension = variation.Key, Value = variation.Value
            });
        }
    }

    private static void AddComponentAnchors(
        MasterAppDbContext db,
        LegendLanguageTextUnit unit,
        LegendCurriculumExample example,
        IReadOnlyDictionary<string, string> variations)
    {
        var positions = new Dictionary<string, int> { ["agent"] = 0, ["predicate"] = 1, ["object"] = 2 };
        foreach (var variation in variations)
        {
            db.Add(new LegendLanguageCompositionalAnchor
            {
                Id = Guid.NewGuid(),
                LanguageCode = unit.LanguageCode,
                TextUnitId = unit.Id,
                CurriculumFamilyId = example.CurriculumFamilyId,
                CurriculumExampleId = example.Id,
                Dimension = variation.Key,
                Value = variation.Value,
                SemanticSignature = SemanticSignature(variation.Key, variation.Value),
                ComponentStartTokenIndex = positions[variation.Key],
                ComponentLength = 1,
                AnchorSignature = LegendLanguageIdentity.TextHash($"test-anchor|{example.Id:D}|{variation.Key}"),
                Provenance = "FounderApproved"
            });
        }
    }

    private static async Task AddTargetAnchorsAsync(
        MasterAppDbContext db,
        string languageCode,
        bool agentAtEnd,
        Guid? familyId = null)
    {
        var examples = await (
            from example in db.LegendCurriculumExamples
            join unit in db.LegendLanguageTextUnits on example.TextUnitId equals unit.Id
            where example.LanguageCode == languageCode && example.DerivedFromCurriculumExampleId != null &&
                example.SupersededUtc == null && (familyId == null || example.CurriculumFamilyId == familyId)
            select new { Example = example, Unit = unit }
        ).ToListAsync();
        foreach (var item in examples)
        {
            if (await db.LegendLanguageCompositionalAnchors.AnyAsync(anchor =>
                anchor.CurriculumExampleId == item.Example.Id && anchor.SupersededUtc == null))
                continue;
            var variations = await db.LegendCurriculumExampleVariations
                .Where(variation => variation.CurriculumExampleId == item.Example.Id)
                .ToDictionaryAsync(variation => variation.Dimension, variation => variation.Value);
            var positions = agentAtEnd
                ? new Dictionary<string, int> { ["agent"] = 2, ["predicate"] = 1, ["object"] = 0 }
                : new Dictionary<string, int> { ["agent"] = 0, ["predicate"] = 1, ["object"] = 2 };
            foreach (var variation in variations)
            {
                db.Add(new LegendLanguageCompositionalAnchor
                {
                    Id = Guid.NewGuid(), LanguageCode = item.Unit.LanguageCode, TextUnitId = item.Unit.Id,
                    CurriculumFamilyId = item.Example.CurriculumFamilyId, CurriculumExampleId = item.Example.Id,
                    Dimension = variation.Key, Value = variation.Value,
                    SemanticSignature = SemanticSignature(variation.Key, variation.Value),
                    ComponentStartTokenIndex = positions[variation.Key], ComponentLength = 1,
                    AnchorSignature = LegendLanguageIdentity.TextHash($"target-anchor|{item.Example.Id:D}|{variation.Key}"),
                    Provenance = "FounderApproved"
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private static string SemanticSignature(string dimension, string value) =>
        LegendLanguageIdentity.TextHash($"semantic|{dimension.Trim().ToLowerInvariant()}|{value.Trim().ToLowerInvariant()}");

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

    private static StructuralFixture CreateFixture(MasterAppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LegendConnect:CorpusAcquisition:Enabled"] = "false",
                ["LegendConnect:LanguageRegistry:Baseline:0:Code"] = "en",
                ["LegendConnect:LanguageRegistry:Baseline:0:Name"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:0:NativeName"] = "English",
                ["LegendConnect:LanguageRegistry:Baseline:1:Code"] = "x-test",
                ["LegendConnect:LanguageRegistry:Baseline:1:Name"] = "Synthetic registry language",
                ["LegendConnect:LanguageRegistry:Baseline:1:NativeName"] = "Synthetic registry language"
            }).Build();
        var registry = new LegendLanguageRegistry(db, configuration);
        var intelligence = new LegendConnectTranslationIntelligence(db, configuration);
        var corpus = new LegendConnectCorpusService(db, registry, NullLogger<LegendConnectCorpusService>.Instance, intelligence: intelligence);
        return new StructuralFixture(configuration, registry, corpus, new LegendConnectCurriculumService(db, registry, corpus));
    }

    private sealed record StructuralFixture(
        IConfiguration Configuration,
        LegendLanguageRegistry Registry,
        LegendConnectCorpusService Corpus,
        LegendConnectCurriculumService Curriculum);
}

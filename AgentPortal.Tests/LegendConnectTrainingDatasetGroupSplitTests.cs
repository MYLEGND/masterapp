using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Data;
using Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectTrainingDatasetGroupSplitTests
{
    [Fact]
    public async Task LeakageAudit_KeepsSemanticPrincipleAndControlledVariationSiblingsTogether()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        SeedRuntimePolicy(db);

        var semanticFirst = AddCurriculumPair(
            db,
            CreateFamily(db, "split.semantic.first"),
            "semantic principle first",
            "semantic principle first target",
            "semantic-first");
        var semanticSecond = AddCurriculumPair(
            db,
            CreateFamily(db, "split.semantic.second"),
            "semantic principle second",
            "semantic principle second target",
            "semantic-second");
        db.AddRange(
            Anchor(semanticFirst, "shared-semantic-principle"),
            Anchor(semanticSecond, "shared-semantic-principle"));

        var controlledFirst = AddCurriculumPair(
            db,
            CreateFamily(db, "split.controlled.first"),
            "controlled variation first",
            "controlled variation first target",
            "controlled-first");
        var controlledSecond = AddCurriculumPair(
            db,
            CreateFamily(db, "split.controlled.second"),
            "controlled variation second",
            "controlled variation second target",
            "controlled-second");
        AddSharedControlledVariation(
            db,
            controlledFirst,
            controlledSecond);
        AddIndependentFamilies(db, 10, "leakage-independent");
        await db.SaveChangesAsync();

        var manifest = await new LegendConnectTrainingDatasetCompiler(db)
            .CompileAsync();
        var all = manifest.Training.Concat(manifest.HeldOut).ToArray();

        Assert.Empty(manifest.Training
            .Select(item => item.SplitGroupIdentity)
            .Intersect(
                manifest.HeldOut.Select(item => item.SplitGroupIdentity),
                StringComparer.Ordinal));
        AssertSingleGroupAndPartition(
            manifest,
            all.Where(item => item.SourceText.StartsWith(
                "semantic principle",
                StringComparison.Ordinal)));
        AssertSingleGroupAndPartition(
            manifest,
            all.Where(item => item.SourceText.StartsWith(
                "controlled variation",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DeterministicRebuild_PreservesGroupAssignmentsAndDatasetIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        SeedRuntimePolicy(db);
        AddIndependentFamilies(db, 12, "deterministic");
        await db.SaveChangesAsync();
        var compiler = new LegendConnectTrainingDatasetCompiler(db);

        var first = await compiler.CompileAsync();
        var second = await compiler.CompileAsync();

        Assert.Equal(first.DatasetIdentity, second.DatasetIdentity);
        Assert.Equal(
            PartitionRows(first),
            PartitionRows(second));
    }

    [Fact]
    public async Task SmallFamily_RemainsWholeInTrainingWhenItIsTheOnlyGroup()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        SeedRuntimePolicy(db);
        var family = CreateFamily(db, "split.small-family");
        for (var index = 0; index < 3; index++)
        {
            AddCurriculumPair(
                db,
                family,
                $"small family source {index}",
                $"small family target {index}",
                $"small-{index}");
        }
        await db.SaveChangesAsync();

        var manifest = await new LegendConnectTrainingDatasetCompiler(db)
            .CompileAsync();

        Assert.Equal(3, manifest.Training.Count);
        Assert.Empty(manifest.HeldOut);
        Assert.Single(manifest.Training
            .Select(item => item.SplitGroupIdentity)
            .Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ImbalancedFamily_RemainsWholeWhileSmallGroupsSupplyHeldOutRows()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        SeedRuntimePolicy(db);
        var largeFamily = CreateFamily(db, "split.large-family");
        for (var index = 0; index < 12; index++)
        {
            AddCurriculumPair(
                db,
                largeFamily,
                $"large family source {index}",
                $"large family target {index}",
                $"large-{index}");
        }
        AddIndependentFamilies(db, 8, "imbalanced-small");
        await db.SaveChangesAsync();

        var manifest = await new LegendConnectTrainingDatasetCompiler(db)
            .CompileAsync();

        Assert.Equal(
            12,
            manifest.Training.Count(item => item.SourceText.StartsWith(
                "large family source",
                StringComparison.Ordinal)));
        Assert.DoesNotContain(
            manifest.HeldOut,
            item => item.SourceText.StartsWith(
                "large family source",
                StringComparison.Ordinal));
        Assert.NotEmpty(manifest.HeldOut);
        Assert.Single(manifest.Training
            .Where(item => item.SourceText.StartsWith(
                "large family source",
                StringComparison.Ordinal))
            .Select(item => item.SplitGroupIdentity)
            .Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DuplicateSurface_MergesEveryFamilyIdentityBeforePartitioning()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        SeedRuntimePolicy(db);
        var firstFamily = CreateFamily(db, "split.duplicate.first");
        var secondFamily = CreateFamily(db, "split.duplicate.second");
        AddCurriculumPair(
            db,
            firstFamily,
            "duplicate source",
            "duplicate target",
            "duplicate-surface");
        AddCurriculumPair(
            db,
            firstFamily,
            "first family sibling",
            "first family sibling target",
            "first-sibling");
        AddCurriculumPair(
            db,
            secondFamily,
            "duplicate source",
            "duplicate target",
            "duplicate-surface");
        AddCurriculumPair(
            db,
            secondFamily,
            "second family sibling",
            "second family sibling target",
            "second-sibling");
        AddIndependentFamilies(db, 8, "duplicate-independent");
        await db.SaveChangesAsync();

        var manifest = await new LegendConnectTrainingDatasetCompiler(db)
            .CompileAsync();
        var connected = manifest.Training
            .Concat(manifest.HeldOut)
            .Where(item => item.SourceText is
                "duplicate source" or
                "first family sibling" or
                "second family sibling")
            .ToArray();

        Assert.Equal(3, connected.Length);
        AssertSingleGroupAndPartition(manifest, connected);
    }

    [Fact]
    public async Task HistoricalVersion_RemainsImmutableWhenNewGovernedGroupChangesCurrentManifest()
    {
        await using var db = ControllerTestHelpers.BuildDb();
        SeedRuntimePolicy(db);
        AddIndependentFamilies(db, 6, "historical-initial");
        await db.SaveChangesAsync();
        var compiler = new LegendConnectTrainingDatasetCompiler(db);
        var historical = await compiler.CompileAsync();
        var historicalRows = PartitionRows(historical);
        var run = new LegendConnectModelTrainingRun
        {
            Id = Guid.NewGuid(),
            RunKey = "historical-group-split-run",
            ScopeKey = "Global",
            Generation = 1,
            DatasetIdentity = historical.DatasetIdentity,
            DatasetEvaluatorVersion = historical.EvaluatorVersion,
            TrainingExampleCount = historical.Training.Count,
            ValidationExampleCount = historical.HeldOut.Count,
            State = "TrainingCompleted"
        };
        db.Add(run);
        await db.SaveChangesAsync();

        AddCurriculumPair(
            db,
            CreateFamily(db, "historical-new-family"),
            "historical new source",
            "historical new target",
            "historical-new");
        await db.SaveChangesAsync();
        var current = await compiler.CompileAsync();
        await db.Entry(run).ReloadAsync();

        Assert.NotEqual(historical.DatasetIdentity, current.DatasetIdentity);
        Assert.Equal(historicalRows, PartitionRows(historical));
        Assert.Equal(historical.DatasetIdentity, run.DatasetIdentity);
        Assert.Equal(historical.Training.Count, run.TrainingExampleCount);
        Assert.Equal(historical.HeldOut.Count, run.ValidationExampleCount);
    }

    private static void SeedRuntimePolicy(MasterAppDbContext db) =>
        db.Add(new LegendConnectRuntimePolicy
        {
            Id = Guid.NewGuid(),
            ScopeKey = "Global",
            CompletedLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            TargetLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LanguageIntelligenceReevaluationPhase = "Complete"
        });

    private static LegendCurriculumFamily CreateFamily(
        MasterAppDbContext db,
        string familyKey)
    {
        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = familyKey,
            SemanticCategory = "dataset_split_test",
            Provenance = "FounderApproved"
        };
        db.Add(family);
        return family;
    }

    private static CurriculumPair AddCurriculumPair(
        MasterAppDbContext db,
        LegendCurriculumFamily family,
        string sourceText,
        string targetText,
        string identity)
    {
        var sourceUnit = Unit("en", sourceText, identity + "-source");
        var targetUnit = Unit("ht", targetText, identity + "-target");
        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = sourceUnit.Id,
            LanguageCode = "en",
            SemanticExampleIdentity = "source:" + identity,
            Provenance = "FounderApproved"
        };
        var targetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = targetUnit.Id,
            LanguageCode = "ht",
            SemanticExampleIdentity = "target:" + identity,
            DerivedFromCurriculumExampleId = sourceExample.Id,
            Provenance = "FounderApproved"
        };
        db.AddRange(
            sourceUnit,
            targetUnit,
            sourceExample,
            targetExample);
        return new CurriculumPair(
            family,
            sourceUnit,
            targetUnit,
            sourceExample,
            targetExample);
    }

    private static LegendLanguageCompositionalAnchor Anchor(
        CurriculumPair pair,
        string semanticSignature) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = pair.SourceUnit.LanguageCode,
            TextUnitId = pair.SourceUnit.Id,
            CurriculumFamilyId = pair.Family.Id,
            CurriculumExampleId = pair.SourceExample.Id,
            Dimension = "reasoning_principle",
            Value = "shared",
            SemanticSignature = semanticSignature,
            AnchorSignature = Guid.NewGuid().ToString("N"),
            Provenance = "FounderApproved"
        };

    private static void AddSharedControlledVariation(
        MasterAppDbContext db,
        CurriculumPair first,
        CurriculumPair second)
    {
        var relationship = new LegendLanguageStructuralRelationship
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            LanguageCode = "en",
            VariationDimension = "diagnostic_mode",
            RelationshipSignature = "shared-controlled-relationship",
            AnchorLayoutSignature = "shared-controlled-layout",
            MaturityState = "Validated",
            IsProductionEligible = true,
            Provenance = "FounderApproved"
        };
        var firstPattern = Pattern(first, "first-controlled-pattern");
        var secondPattern = Pattern(second, "second-controlled-pattern");
        db.AddRange(
            relationship,
            firstPattern,
            secondPattern,
            StructuralEvidence(first, firstPattern.Id, relationship.Id),
            StructuralEvidence(second, secondPattern.Id, relationship.Id));
    }

    private static LegendLanguageStructuralPattern Pattern(
        CurriculumPair pair,
        string propositionSignature) =>
        new()
        {
            Id = Guid.NewGuid(),
            PropositionSignature = propositionSignature,
            CurriculumFamilyId = pair.Family.Id,
            PairKey = "en:ht",
            LanguageCode = "en",
            VariationDimension = "diagnostic_mode",
            RealizationSignature = Guid.NewGuid().ToString("N"),
            MaturityState = "Validated",
            IsProductionEligible = true,
            Provenance = "FounderApproved"
        };

    private static LegendLanguageStructuralEvidence StructuralEvidence(
        CurriculumPair pair,
        Guid patternId,
        Guid relationshipId) =>
        new()
        {
            Id = Guid.NewGuid(),
            StructuralPatternId = patternId,
            StructuralRelationshipId = relationshipId,
            StructuralRelationshipContributionState = "Supported",
            CurriculumFamilyId = pair.Family.Id,
            PairKey = "en:ht",
            LanguageCode = "en",
            VariationDimension = "diagnostic_mode",
            BaselineCurriculumExampleId = pair.SourceExample.Id,
            ComparedCurriculumExampleId = pair.TargetExample.Id,
            BaselineVariationValue = "baseline",
            ComparedVariationValue = "compared",
            EvidenceSignature = Guid.NewGuid().ToString("N"),
            BaselineComponentSignature = "baseline-components",
            ComparedComponentSignature = "compared-components",
            IndependentSourceIdentity = Guid.NewGuid().ToString("N"),
            ContributionState = "Supported",
            IsHumanVerifiedSupport = true,
            Provenance = "FounderApproved"
        };

    private static void AddIndependentFamilies(
        MasterAppDbContext db,
        int count,
        string prefix)
    {
        for (var index = 0; index < count; index++)
        {
            AddCurriculumPair(
                db,
                CreateFamily(db, $"{prefix}.family.{index}"),
                $"{prefix} source {index}",
                $"{prefix} target {index}",
                $"{prefix}-{index}");
        }
    }

    private static LegendLanguageTextUnit Unit(
        string languageCode,
        string text,
        string normalizedHash) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = languageCode,
            StoragePartition = languageCode,
            NormalizedHash = normalizedHash,
            Text = text,
            Provenance = "FounderApproved",
            IsTrainingEligible = true
        };

    private static void AssertSingleGroupAndPartition(
        LegendConnectTrainingDatasetManifest manifest,
        IEnumerable<LegendConnectTrainingDatasetExample> source)
    {
        var examples = source.ToArray();
        Assert.NotEmpty(examples);
        Assert.All(examples, item =>
            Assert.False(string.IsNullOrWhiteSpace(
                item.SplitGroupIdentity)));
        Assert.Single(examples
            .Select(item => item.SplitGroupIdentity)
            .Distinct(StringComparer.Ordinal));
        var trainingIdentities = manifest.Training
            .Select(item => item.EvidenceIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var trainingCount = examples.Count(item =>
            trainingIdentities.Contains(item.EvidenceIdentity));
        Assert.True(trainingCount == 0 || trainingCount == examples.Length);
    }

    private static string[] PartitionRows(
        LegendConnectTrainingDatasetManifest manifest) =>
        manifest.Training
            .Select(item => $"training|{item.EvidenceIdentity}|{item.SplitGroupIdentity}")
            .Concat(manifest.HeldOut.Select(item =>
                $"held-out|{item.EvidenceIdentity}|{item.SplitGroupIdentity}"))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private sealed record CurriculumPair(
        LegendCurriculumFamily Family,
        LegendLanguageTextUnit SourceUnit,
        LegendLanguageTextUnit TargetUnit,
        LegendCurriculumExample SourceExample,
        LegendCurriculumExample TargetExample);
}

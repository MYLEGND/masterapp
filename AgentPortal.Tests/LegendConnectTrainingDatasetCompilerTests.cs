using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectTrainingDatasetCompilerTests
{
    [Fact]
    public async Task SameGovernedEvidence_ProducesSameDatasetIdentity()
    {
        await using var db = ControllerTestHelpers.BuildDb();

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

        var source = Unit(
            "en",
            "hello",
            "source-hash",
            "FounderApproved");

        var target = Unit(
            "ht",
            "bonjou",
            "target-hash",
            "FounderApproved");

        db.AddRange(source, target);

        db.Add(new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "Founder",
            Provenance = "FounderApproved",
            QualityState = "Verified",
            HumanVerified = true,
            Confidence = 1m
        });

        await db.SaveChangesAsync();

        var compiler =
            new LegendConnectTrainingDatasetCompiler(db);

        var first = await compiler.CompileAsync();
        var second = await compiler.CompileAsync();

        Assert.Equal(
            first.DatasetIdentity,
            second.DatasetIdentity);

        Assert.Equal(
            first.TrainingExampleCount,
            second.TrainingExampleCount);

        Assert.Equal(
            first.ValidationExampleCount,
            second.ValidationExampleCount);

        Assert.Equal(
            1,
            first.TrainingExampleCount +
            first.ValidationExampleCount);
    }

    [Fact]
    public async Task OpenContradiction_IsExcluded()
    {
        await using var db = ControllerTestHelpers.BuildDb();

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

        var source = Unit(
            "en",
            "hello",
            "source-hash",
            "FounderApproved");

        var target = Unit(
            "ht",
            "wrong",
            "target-hash",
            "ProviderDerived");

        var alignment = new LegendTranslationAlignment
        {
            Id = Guid.NewGuid(),
            PairKey = "en:ht",
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Provider = "AzureTranslator",
            Provenance = "ProviderDerived",
            QualityState = "Observation",
            HumanVerified = false
        };

        db.AddRange(source, target, alignment);

        db.Add(new LegendTranslationQualityEvidence
        {
            Id = Guid.NewGuid(),
            ObservedAlignmentId = alignment.Id,
            PairKey = "en:ht",
            SourceTextUnitId = source.Id,
            TargetTextUnitId = target.Id,
            Signal = "Contradictory",
            ReasonCode = "test",
            ResolutionState = "Open",
            EvidenceIdentity = Guid.NewGuid().ToString("N")
        });

        await db.SaveChangesAsync();

        var manifest =
            await new LegendConnectTrainingDatasetCompiler(db)
                .CompileAsync();

        Assert.Empty(manifest.Training);
        Assert.Empty(manifest.HeldOut);
    }

    [Fact]
    public async Task SystemValidatedMachineCurriculum_IsRetainedAtLowerWeightThanFounder()
    {
        await using var db = ControllerTestHelpers.BuildDb();

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

        var source = Unit(
            "en",
            "confirm",
            "source-hash",
            "FounderApproved");

        var target = Unit(
            "ht",
            "konfime",
            "target-hash",
            "SystemValidatedMachine");

        var family = new LegendCurriculumFamily
        {
            Id = Guid.NewGuid(),
            FamilyKey = "phase6.test",
            Provenance = "SystemValidatedMachine"
        };

        var sourceExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = source.Id,
            LanguageCode = "en",
            Provenance = "FounderApproved"
        };

        var targetExample = new LegendCurriculumExample
        {
            Id = Guid.NewGuid(),
            CurriculumFamilyId = family.Id,
            TextUnitId = target.Id,
            LanguageCode = "ht",
            DerivedFromCurriculumExampleId = sourceExample.Id,
            Provenance = "SystemValidatedMachine"
        };

        db.AddRange(
            source,
            target,
            family,
            sourceExample,
            targetExample);

        await db.SaveChangesAsync();

        var manifest =
            await new LegendConnectTrainingDatasetCompiler(db)
                .CompileAsync();

        var example =
            Assert.Single(
                manifest.Training.Concat(manifest.HeldOut));

        Assert.Equal(
            "SystemValidatedMachine",
            example.Provenance);

        Assert.Equal(3, example.Weight);
    }

    [Fact]
    public async Task ProductionEligibleSemanticTransitions_EnterTheSameManifestAsGovernedCapabilityTasks()
    {
        await using var db = ControllerTestHelpers.BuildDb();
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

        const string sourceFrame = "{\"state\":\"observed\"}";
        const string intermediateFrame = "{\"state\":\"verified\"}";
        const string resultFrame = "{\"state\":\"resolved\"}";
        for (var index = 0; index < 3; index++)
        {
            var family = new LegendCurriculumFamily
            {
                FamilyKey = $"semantic-family-{index}",
                Provenance = "FounderApproved"
            };
            var sourceUnit = Unit(
                "en",
                $"Observed state {index}",
                $"semantic-source-{index}",
                "FounderApproved");
            var intermediateUnit = Unit(
                "en",
                $"Verified state {index}",
                $"semantic-intermediate-{index}",
                "FounderApproved");
            var resultUnit = Unit(
                "en",
                $"Resolved state {index}",
                $"semantic-result-{index}",
                "FounderApproved");
            var sourceExample = new LegendCurriculumExample
            {
                CurriculumFamilyId = family.Id,
                TextUnitId = sourceUnit.Id,
                LanguageCode = "en",
                Provenance = "FounderApproved"
            };
            var intermediateExample = new LegendCurriculumExample
            {
                CurriculumFamilyId = family.Id,
                TextUnitId = intermediateUnit.Id,
                LanguageCode = "en",
                Provenance = "FounderApproved"
            };
            var resultExample = new LegendCurriculumExample
            {
                CurriculumFamilyId = family.Id,
                TextUnitId = resultUnit.Id,
                LanguageCode = "en",
                Provenance = "FounderApproved"
            };

            db.AddRange(
                family,
                sourceUnit,
                intermediateUnit,
                resultUnit,
                sourceExample,
                intermediateExample,
                resultExample);
            db.AddRange(
                new LegendSemanticTransitionEvidence
                {
                    TransitionSignature = "observe-to-verify",
                    SourceSemanticFrameSignature = "source-frame",
                    ResultSemanticFrameSignature = "intermediate-frame",
                    SourceSemanticFrame = sourceFrame,
                    ResultSemanticFrame = intermediateFrame,
                    SourceLanguageCode = "en",
                    ResultLanguageCode = "en",
                    SourceCurriculumExampleId = sourceExample.Id,
                    ResultCurriculumExampleId = intermediateExample.Id,
                    IndependentSourceIdentity = $"first-independent-{index}",
                    ContributionState = "Supported",
                    IsHumanVerifiedSupport = true,
                    Provenance = "FounderApproved"
                },
                new LegendSemanticTransitionEvidence
                {
                    TransitionSignature = "verify-to-resolve",
                    SourceSemanticFrameSignature = "intermediate-frame",
                    ResultSemanticFrameSignature = "result-frame",
                    SourceSemanticFrame = intermediateFrame,
                    ResultSemanticFrame = resultFrame,
                    SourceLanguageCode = "en",
                    ResultLanguageCode = "en",
                    SourceCurriculumExampleId = intermediateExample.Id,
                    ResultCurriculumExampleId = resultExample.Id,
                    IndependentSourceIdentity = $"second-independent-{index}",
                    ContributionState = "Supported",
                    IsHumanVerifiedSupport = true,
                    Provenance = "FounderApproved"
                });
        }

        await db.SaveChangesAsync();

        var manifest =
            await new LegendConnectTrainingDatasetCompiler(db)
                .CompileAsync();
        var all = manifest.Training
            .Concat(manifest.HeldOut)
            .ToArray();
        var semantic = all
            .Where(item =>
                item.CapabilityKey ==
                    LegendModelCapabilityKeys.SemanticTransition)
            .ToArray();
        var reasoning = all
            .Where(item =>
                item.CapabilityKey ==
                    LegendModelCapabilityKeys.GovernedReasoning)
            .ToArray();

        Assert.Equal(6, semantic.Length);
        Assert.Equal(3, reasoning.Length);
        Assert.All(semantic, item =>
        {
            Assert.Equal(
                "governed_state_only",
                item.OutputContract);
            Assert.Contains(
                "transition_signature",
                item.SourceText,
                StringComparison.Ordinal);
        });
        Assert.All(reasoning, item =>
        {
            Assert.Equal(
                "governed_final_state_only",
                item.OutputContract);
            Assert.Contains(
                "transition_path",
                item.SourceText,
                StringComparison.Ordinal);
            Assert.Contains(
                "Resolved state",
                item.TargetText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Translate from",
                item.Instructions ?? string.Empty,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CompletedOlderEvaluatorGeneration_FailsClosedBeforeWorkerStartsNewReplay()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        db.Add(new LegendConnectRuntimePolicy
        {
            Id = Guid.NewGuid(),
            ScopeKey = "Global",
            CompletedLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current - 1,
            TargetLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current - 1,
            LanguageIntelligenceReevaluationPhase = "Complete"
        });

        await db.SaveChangesAsync();

        var compiler =
            new LegendConnectTrainingDatasetCompiler(db);

        var error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => compiler.CompileAsync());

        Assert.Equal(
            "training_dataset_historical_replay_incomplete",
            error.Message);
    }

    [Fact]
    public async Task IncompleteHistoricalReplay_FailsClosed()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        db.Add(new LegendConnectRuntimePolicy
        {
            Id = Guid.NewGuid(),
            ScopeKey = "Global",
            CompletedLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current - 1,
            TargetLanguageIntelligenceEvaluatorVersion =
                LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            LanguageIntelligenceReevaluationPhase = "ProviderObservations"
        });

        await db.SaveChangesAsync();

        var compiler =
            new LegendConnectTrainingDatasetCompiler(db);

        var error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => compiler.CompileAsync());

        Assert.Equal(
            "training_dataset_historical_replay_incomplete",
            error.Message);
    }

    private static LegendLanguageTextUnit Unit(
        string language,
        string text,
        string hash,
        string provenance) =>
        new()
        {
            Id = Guid.NewGuid(),
            LanguageCode = language,
            StoragePartition = language,
            Text = text,
            NormalizedHash = hash,
            Provenance = provenance,
            IsTrainingEligible = true
        };
}

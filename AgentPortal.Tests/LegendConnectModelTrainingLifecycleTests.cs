using System;
using System.Linq;
using System.Threading.Tasks;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectModelTrainingLifecycleTests
{
    [Fact]
    public async Task ModelTrainingLifecycle_IsDurableIdempotentOrchestration_NotLanguageAuthority()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        var entity =
            db.Model.FindEntityType(
                typeof(LegendConnectModelTrainingRun));

        Assert.NotNull(entity);
        Assert.Equal(
            "LegendConnectModelTrainingRuns",
            entity!.GetTableName());

        Assert.Equal(
            160,
            entity.FindProperty(
                nameof(LegendConnectModelTrainingRun.RunKey))!
                .GetMaxLength());

        Assert.Equal(
            64,
            entity.FindProperty(
                nameof(LegendConnectModelTrainingRun.DatasetIdentity))!
                .GetMaxLength());

        Assert.Contains(
            entity.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties
                    .Select(property => property.Name)
                    .SequenceEqual(
                    [
                        nameof(
                            LegendConnectModelTrainingRun.RunKey)
                    ]));

        Assert.Contains(
            entity.GetIndexes(),
            index =>
                index.IsUnique &&
                index.Properties
                    .Select(property => property.Name)
                    .SequenceEqual(
                    [
                        nameof(
                            LegendConnectModelTrainingRun.ScopeKey),
                        nameof(
                            LegendConnectModelTrainingRun.Generation)
                    ]));

        // The lifecycle contains orchestration metadata only.
        // It must never become another language/corpus authority.
        var propertyNames = entity
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("SourceText", propertyNames);
        Assert.DoesNotContain("TargetText", propertyNames);
        Assert.DoesNotContain("Translation", propertyNames);
        Assert.DoesNotContain("SemanticValue", propertyNames);
        Assert.DoesNotContain("IsFounderApproved", propertyNames);
        Assert.DoesNotContain("IsProductionEligible", propertyNames);

        var run = new LegendConnectModelTrainingRun
        {
            RunKey =
                "global:test-dataset:test-provider:test-base",
            ScopeKey = "Global",
            Generation = 1,
            DatasetIdentity =
                new string('a', 64),
            DatasetEvaluatorVersion = 13,
            TrainingProvider = "TestProvider",
            BaseModel = "test-base"
        };

        db.Set<LegendConnectModelTrainingRun>()
            .Add(run);

        await db.SaveChangesAsync();

        var stored =
            await db.Set<LegendConnectModelTrainingRun>()
                .SingleAsync();

        Assert.Equal("PendingDataset", stored.State);
        Assert.Equal("NotStarted", stored.EvaluationState);
        Assert.Equal("NotEvaluated", stored.PromotionState);

        Assert.Null(stored.TrainingFileId);
        Assert.Null(stored.ExternalJobId);
        Assert.Null(stored.ChallengerModelVersion);
        Assert.Null(stored.PromotedUtc);
    }
}

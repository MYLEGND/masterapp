using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendConnectModelTrainingTests
{
    [Fact]
    public async Task DisabledTraining_DoesNotCreateRunOrCallBackend()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend = new FakeBackend();

        var service =
            Service(
                db,
                backend,
                enabled: false);

        await service.ProcessOneAsync();

        Assert.Empty(
            db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(0, backend.UploadCalls);
    }

    [Fact]
    public async Task GovernedDataset_CreatesDurableRunThenUploadsTrainingOnly()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend = new FakeBackend();

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.NotEmpty(run.DatasetIdentity);
        Assert.Equal(13, run.DatasetEvaluatorVersion);
        Assert.Equal("OpenAI", run.TrainingProvider);
        Assert.Equal("test-base-model", run.BaseModel);
        Assert.Equal("file-training", run.TrainingFileId);
        Assert.Null(run.ExternalJobId);
        Assert.Null(run.ChallengerModelVersion);
        Assert.Equal("NotStarted", run.EvaluationState);
        Assert.Equal("NotEvaluated", run.PromotionState);

        var uploaded =
            Encoding.UTF8.GetString(
                Assert.IsType<byte[]>(backend.UploadedJsonl));

        Assert.Contains("hello", uploaded);
        Assert.Contains("bonjou", uploaded);
        Assert.DoesNotContain(
            "held-out-secret",
            uploaded,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulProviderJob_BecomesChallengerButNeverPromotes()
    {
        await using var db = ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend =
            new FakeBackend
            {
                CreatedStatus = "queued",
                PolledStatus = "succeeded",
                PolledModel =
                    "ft:test:legend-challenger"
            };

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(
            "TrainingCompleted",
            run.State);

        Assert.Equal(
            "ft:test:legend-challenger",
            run.ChallengerModelVersion);

        Assert.Equal(
            "NotStarted",
            run.EvaluationState);

        Assert.Equal(
            "NotEvaluated",
            run.PromotionState);

        Assert.Null(run.PromotedUtc);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Null(pair.ActiveModelVersion);
    }

    [Fact]
    public void TrainingJsonl_UsesTrainingManifestOnlyAndPreservesWeightRatio()
    {
        var manifest =
            new LegendConnectTrainingDatasetManifest(
                "dataset",
                13,
                "Global",
                [
                    new(
                        "founder",
                        "en:ht",
                        "en",
                        "ht",
                        "hello",
                        "bonjou",
                        "FounderApproved",
                        4,
                        "s1",
                        "t1"),
                    new(
                        "machine",
                        "en:ht",
                        "en",
                        "ht",
                        "confirm",
                        "konfime",
                        "SystemValidatedMachine",
                        3,
                        "s2",
                        "t2")
                ],
                [
                    new(
                        "held",
                        "en:ht",
                        "en",
                        "ht",
                        "held-out-secret",
                        "never-upload",
                        "FounderApproved",
                        4,
                        "s3",
                        "t3")
                ]);

        var jsonl =
            Encoding.UTF8.GetString(
                LegendConnectModelTrainingService
                    .BuildTrainingJsonl(manifest));

        var lines =
            jsonl.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(7, lines.Length);
        Assert.Equal(
            4,
            lines.Count(item =>
                item.Contains(
                    "hello",
                    StringComparison.Ordinal)));

        Assert.Equal(
            3,
            lines.Count(item =>
                item.Contains(
                    "confirm",
                    StringComparison.Ordinal)));

        Assert.DoesNotContain(
            "held-out-secret",
            jsonl,
            StringComparison.Ordinal);
    }

    private static LegendConnectModelTrainingService Service(
        Infrastructure.Data.MasterAppDbContext db,
        FakeBackend backend,
        bool enabled)
    {
        var values =
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["LegendConnect:ModelTraining:Enabled"] =
                    enabled.ToString(),
                ["LegendConnect:ModelTraining:BaseModel"] =
                    "test-base-model",
                ["LegendConnect:ModelTraining:MaximumAttempts"] =
                    "3"
            };

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

        return new(
            db,
            new LegendConnectTrainingDatasetCompiler(db),
            backend,
            configuration);
    }

    private static async Task SeedDatasetAsync(
        Infrastructure.Data.MasterAppDbContext db)
    {
        db.Add(
            new LegendConnectRuntimePolicy
            {
                Id = Guid.NewGuid(),
                ScopeKey = "Global",
                CompletedLanguageIntelligenceEvaluatorVersion = 13,
                TargetLanguageIntelligenceEvaluatorVersion = 13,
                LanguageIntelligenceReevaluationPhase =
                    "Complete"
            });

        db.Add(
            new LegendLanguagePair
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceLanguageCode = "en",
                TargetLanguageCode = "ht",
                IsEnabled = true
            });

        var source =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "en",
                StoragePartition = "en",
                NormalizedHash = "source",
                Text = "hello",
                Provenance = "FounderApproved",
                IsTrainingEligible = true
            };

        var target =
            new LegendLanguageTextUnit
            {
                Id = Guid.NewGuid(),
                LanguageCode = "ht",
                StoragePartition = "ht",
                NormalizedHash = "target",
                Text = "bonjou",
                Provenance = "FounderApproved",
                IsTrainingEligible = true
            };

        db.AddRange(source, target);

        db.Add(
            new LegendTranslationAlignment
            {
                Id = Guid.NewGuid(),
                PairKey = "en:ht",
                SourceTextUnitId = source.Id,
                TargetTextUnitId = target.Id,
                Provider = "Founder",
                Provenance = "FounderApproved",
                HumanVerified = true,
                QualityState = "Verified"
            });

        await db.SaveChangesAsync();
    }

    private sealed class FakeBackend
        : ILegendConnectModelTrainingBackend
    {
        public int UploadCalls { get; private set; }
        public byte[]? UploadedJsonl { get; private set; }

        public string CreatedStatus { get; init; } = "queued";
        public string PolledStatus { get; init; } = "running";
        public string? PolledModel { get; init; }

        public Task<LegendModelTrainingUploadResult>
            UploadTrainingFileAsync(
                string fileName,
                byte[] jsonl,
                CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            UploadedJsonl = jsonl;

            return Task.FromResult(
                new LegendModelTrainingUploadResult(
                    true,
                    "file-training",
                    null,
                    false));
        }

        public Task<LegendModelTrainingJobResult>
            CreateTrainingJobAsync(
                string trainingFileId,
                string baseModel,
                string runKey,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendModelTrainingJobResult(
                    true,
                    "ftjob-test",
                    CreatedStatus,
                    null,
                    null,
                    false));

        public Task<LegendModelTrainingJobResult>
            GetTrainingJobAsync(
                string jobId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new LegendModelTrainingJobResult(
                    true,
                    jobId,
                    PolledStatus,
                    PolledModel,
                    null,
                    false));
    }
}

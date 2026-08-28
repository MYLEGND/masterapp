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
        Assert.Equal(
            LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
            run.DatasetEvaluatorVersion);
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
    public async Task FirstJobCreation_DoesNotLookupBeforeInitialCreate()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend = new FakeBackend();

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(0, backend.LookupCalls);

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(
            "ftjob-test",
            run.ExternalJobId);
    }

    [Fact]
    public async Task AmbiguousCreate_FoundJobIsRecoveredWithoutSecondCreate()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend =
            new FakeBackend
            {
                FailNextCreateAmbiguously = true
            };

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(0, backend.LookupCalls);

        backend.LookupState =
            LegendModelTrainingJobLookupState.Found;
        backend.LookupJobId =
            "ftjob-recovered";
        backend.LookupStatus =
            "queued";

        await service.ProcessOneAsync();

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(1, backend.LookupCalls);

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(
            "ftjob-recovered",
            run.ExternalJobId);
        Assert.Equal("Queued", run.State);
        Assert.Equal("NotStarted", run.EvaluationState);
        Assert.Equal("NotEvaluated", run.PromotionState);
        Assert.Null(run.PromotedUtc);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Null(pair.ActiveModelVersion);
    }

    [Fact]
    public async Task AmbiguousCreate_IndeterminateLookupNeverCreatesAgain()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend =
            new FakeBackend
            {
                FailNextCreateAmbiguously = true
            };

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        backend.LookupState =
            LegendModelTrainingJobLookupState.Indeterminate;
        backend.LookupErrorCode =
            "model_training_provider_timeout";
        backend.LookupRetryable = true;

        await service.ProcessOneAsync();

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(1, backend.LookupCalls);

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Null(run.ExternalJobId);
        Assert.Equal("PendingRetry", run.State);
        Assert.Equal("NotStarted", run.EvaluationState);
        Assert.Equal("NotEvaluated", run.PromotionState);
        Assert.Null(run.PromotedUtc);
    }

    [Fact]
    public async Task AmbiguousCreate_NotFoundAllowsOneRetryCreate()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend =
            new FakeBackend
            {
                FailNextCreateAmbiguously = true
            };

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        backend.LookupState =
            LegendModelTrainingJobLookupState.NotFound;

        await service.ProcessOneAsync();

        Assert.Equal(1, backend.LookupCalls);
        Assert.Equal(2, backend.CreateCalls);

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(
            "ftjob-test",
            run.ExternalJobId);
        Assert.Equal("Queued", run.State);
    }

    [Fact]
    public async Task RecoveredSucceededJob_BecomesChallengerButNeverPromotes()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend =
            new FakeBackend
            {
                FailNextCreateAmbiguously = true
            };

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        backend.LookupState =
            LegendModelTrainingJobLookupState.Found;
        backend.LookupJobId =
            "ftjob-recovered";
        backend.LookupStatus =
            "succeeded";
        backend.LookupModel =
            "ft:test:recovered-challenger";

        await service.ProcessOneAsync();

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(1, backend.LookupCalls);

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(
            "TrainingCompleted",
            run.State);
        Assert.Equal(
            "ft:test:recovered-challenger",
            run.ChallengerModelVersion);
        Assert.Equal("NotStarted", run.EvaluationState);
        Assert.Equal("NotEvaluated", run.PromotionState);
        Assert.Null(run.PromotedUtc);

        var pair =
            Assert.Single(
                db.Set<LegendLanguagePair>());

        Assert.Null(pair.ActiveModelVersion);
    }

    [Fact]
    public async Task RecoveredFailedJob_FailsWithoutReplacementCreate()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();

        await SeedDatasetAsync(db);

        var backend =
            new FakeBackend
            {
                FailNextCreateAmbiguously = true
            };

        var service =
            Service(
                db,
                backend,
                enabled: true);

        await service.ProcessOneAsync();
        await service.ProcessOneAsync();

        backend.LookupState =
            LegendModelTrainingJobLookupState.Found;
        backend.LookupJobId =
            "ftjob-failed";
        backend.LookupStatus =
            "failed";

        await service.ProcessOneAsync();

        Assert.Equal(1, backend.CreateCalls);
        Assert.Equal(1, backend.LookupCalls);

        var run =
            Assert.Single(
                db.Set<LegendConnectModelTrainingRun>());

        Assert.Equal(
            "ftjob-failed",
            run.ExternalJobId);
        Assert.Equal("Failed", run.State);
        Assert.Equal(
            "model_training_provider_terminal_failure",
            run.FailureCode);
        Assert.Equal("NotStarted", run.EvaluationState);
        Assert.Equal("NotEvaluated", run.PromotionState);
        Assert.Null(run.PromotedUtc);
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

    [Fact]
    public void TrainingJsonl_UsesTheGovernedCapabilityTaskContractWithoutTranslationFallback()
    {
        var manifest = new LegendConnectTrainingDatasetManifest(
            "capability-dataset",
            13,
            "Global",
            [new LegendConnectTrainingDatasetExample(
                "governed-task",
                "global:semantic-transition",
                "en",
                "en",
                "Observed governed state",
                "Resolved governed state",
                "FounderApproved",
                1,
                "source",
                "target",
                "governed.semantic_transition",
                "Apply only the supplied governed semantic transition. Return the resolved state only.",
                "governed_state_only")],
            []);

        var jsonl = Encoding.UTF8.GetString(
            LegendConnectModelTrainingService.BuildTrainingJsonl(manifest));

        Assert.Contains("governed semantic transition", jsonl, StringComparison.Ordinal);
        Assert.Contains("Observed governed state", jsonl, StringComparison.Ordinal);
        Assert.Contains("Resolved governed state", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("Translate from", jsonl, StringComparison.Ordinal);
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
                CompletedLanguageIntelligenceEvaluatorVersion =
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
                TargetLanguageIntelligenceEvaluatorVersion =
                    LegendConnectLanguageIntelligenceEvaluatorVersion.Current,
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
                // This fixture must remain in the Phase-6 training split.
                // EvidenceIdentity includes Alignment.Id, so a random Guid
                // makes this Phase-7 training test nondeterministically move
                // between training and held-out.
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
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
        public int CreateCalls { get; private set; }
        public int LookupCalls { get; private set; }
        public int PollCalls { get; private set; }
        public byte[]? UploadedJsonl { get; private set; }

        public string CreatedStatus { get; init; } = "queued";
        public string PolledStatus { get; init; } = "running";
        public string? PolledModel { get; init; }

        public bool FailNextCreateAmbiguously { get; set; }

        public LegendModelTrainingJobLookupState LookupState { get; set; } =
            LegendModelTrainingJobLookupState.NotFound;

        public string? LookupJobId { get; set; }
        public string? LookupStatus { get; set; }
        public string? LookupModel { get; set; }
        public string? LookupErrorCode { get; set; }
        public bool LookupRetryable { get; set; }

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
                CancellationToken cancellationToken = default)
        {
            CreateCalls++;

            if (FailNextCreateAmbiguously)
            {
                FailNextCreateAmbiguously = false;

                return Task.FromResult(
                    new LegendModelTrainingJobResult(
                        false,
                        null,
                        null,
                        null,
                        "model_training_provider_timeout",
                        true));
            }

            return Task.FromResult(
                new LegendModelTrainingJobResult(
                    true,
                    "ftjob-test",
                    CreatedStatus,
                    null,
                    null,
                    false));
        }

        public Task<LegendModelTrainingJobLookupResult>
            FindTrainingJobByRunKeyAsync(
                string runKey,
                CancellationToken cancellationToken = default)
        {
            LookupCalls++;

            return Task.FromResult(
                new LegendModelTrainingJobLookupResult(
                    LookupState,
                    LookupJobId,
                    LookupStatus,
                    LookupModel,
                    LookupErrorCode,
                    LookupRetryable));
        }

        public Task<LegendModelTrainingJobResult>
            GetTrainingJobAsync(
                string jobId,
                CancellationToken cancellationToken = default)
        {
            PollCalls++;

            return Task.FromResult(
                new LegendModelTrainingJobResult(
                    true,
                    jobId,
                    PolledStatus,
                    PolledModel,
                    null,
                    false));
        }
    }
}

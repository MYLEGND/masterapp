using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentPortal.Models;
using AgentPortal.Services;
using Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentPortal.Tests;

public sealed class LegendBlindComparativeBenchmarkRunnerTests
{
    private const string DeployedSha =
        "0123456789abcdef0123456789abcdef01234567";
    private const string Proof =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task RealRunner_RandomizesAndBlindsAnswerOrderBeforeCanonicalIngestion()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var runtime =
            new FakeRuntime();
        var runner =
            Runner(
                db,
                runtime,
                new AlternatingRandomizer());

        var result =
            await runner.RunAndIngestAsync(
                Manifest());

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.True(result.Ingested);
        Assert.True(result.Evaluation!.TakeoverEligible);
        Assert.All(result.Evaluation.Metrics.Values, metrics =>
        {
            Assert.Equal(100m, metrics["native_execution"]);
            Assert.Equal(100m, metrics["transfer"]);
            Assert.Equal(100m, metrics["calibration"]);
        });
        Assert.Contains(
            result.Report!.Cases,
            item => item.LegendAnswerSlot == "A");
        Assert.Contains(
            result.Report.Cases,
            item => item.LegendAnswerSlot == "B");
        Assert.All(result.Report.Cases, item =>
        {
            Assert.True(item.AssignmentBlinded);
            Assert.True(item.RuntimeProvenanceVerified);
            Assert.True(item.ContaminationChecked);
            Assert.Equal(64, item.AssignmentIdentity.Length);
            Assert.Equal(64, item.OutcomeIdentity.Length);
            Assert.Equal(3, item.JudgeOutcomes!.Count);
        });
        Assert.False(
            LegendBlindComparativeBenchmarkEvaluator
                .Evaluate(result.Report)
                .TakeoverEligible);
        Assert.All(runtime.JudgeRequests, request =>
        {
            var serialized =
                JsonSerializer.Serialize(request);
            Assert.DoesNotContain(
                "legend-runtime-v1",
                serialized,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                LegendBlindBenchmarkContracts.ExactBaselineModel,
                serialized,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "LEGEND",
                serialized,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "BASELINE",
                serialized,
                StringComparison.OrdinalIgnoreCase);
        });
        Assert.NotEmpty(
            await db.LegendIntelligenceEvaluationSignals
                .Where(item =>
                    item.EvidenceAuthority.StartsWith(
                        LegendArchitecturalTakeoverGate
                            .EvaluatorAuthorityPrefix))
                .ToArrayAsync());
        var dashboard =
            await new LegendIntelligenceEvaluationService(db)
                .CreateEvidenceSnapshotAsync(
                    "founder-1",
                    CancellationToken.None);
        Assert.True(dashboard.TakeoverReadiness.Proven);
        Assert.Equal(
            "gpt-5.6-sol@locked-test-v1",
            dashboard.TakeoverReadiness.BaselineIdentity);
        Assert.Contains(
            await db.LegendConnectOperationalEvents.ToArrayAsync(),
            item =>
                item.Category == "BlindComparativeBenchmark" &&
                item.Status == "ExecutionProof" &&
                item.Summary!.Contains(
                    "contamination_checked=True",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrainingPromptLeakage_IsRejectedBeforeEitherRuntimeExecutes()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var runtime =
            new FakeRuntime();
        var manifest =
            Manifest();
        manifest = manifest with
        {
            TrainingPromptHashes =
                [manifest.Cases[0].PromptSha256]
        };
        manifest = Lock(manifest);

        var result =
            await Runner(
                    db,
                    runtime,
                    new AlternatingRandomizer())
                .RunAndIngestAsync(manifest);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "blind_benchmark_manifest_rejected",
            result.ErrorCode);
        Assert.Contains(
            result.Blockers,
            item => item.Contains(
                "contamination",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, runtime.LegendCalls);
        Assert.Equal(0, runtime.BaselineCalls);
    }

    [Fact]
    public async Task IncompleteDomain_IsRejectedBeforeExecution()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var runtime =
            new FakeRuntime();
        var draft =
            Manifest() with
            {
                Cases = Manifest().Cases
                    .Skip(1)
                    .ToArray()
            };
        var result =
            await Runner(
                    db,
                    runtime,
                    new AlternatingRandomizer())
                .RunAndIngestAsync(
                    Lock(draft));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Blockers,
            item => item.Contains(
                "incomplete",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, runtime.LegendCalls);
    }

    [Fact]
    public async Task DuplicatePromptLineage_CannotInflateTheBoundedSuite()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var runtime =
            new FakeRuntime();
        var manifest =
            Manifest();
        var cases =
            manifest.Cases.ToArray();
        cases[^1] = cases[^1] with
        {
            Prompt = cases[0].Prompt,
            PromptSha256 = cases[0].PromptSha256,
            EvidenceIdentity = cases[0].EvidenceIdentity,
            SplitGroupIdentity = cases[0].SplitGroupIdentity
        };

        var result =
            await Runner(
                    db,
                    runtime,
                    new AlternatingRandomizer())
                .RunAndIngestAsync(
                    Lock(manifest with
                    {
                        Cases = cases
                    }));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Blockers,
            item => item.Contains(
                "duplicate prompt",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, runtime.LegendCalls);
    }

    [Fact]
    public async Task BaselineModelDrift_StopsBeforeAnyWinnerIsRecorded()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var runtime =
            new FakeRuntime
            {
                BaselineModelVersion =
                    "gpt-5.6-sol-drifted"
            };

        var result =
            await Runner(
                    db,
                    runtime,
                    new AlternatingRandomizer())
                .RunAndIngestAsync(
                    Manifest());

        Assert.False(result.Succeeded);
        Assert.Equal(
            "blind_benchmark_baseline_drift",
            result.ErrorCode);
        Assert.Empty(
            db.LegendIntelligenceEvaluationSignals);
        Assert.Empty(
            db.LegendConnectOperationalEvents);
    }

    [Fact]
    public async Task JudgeDisagreement_RecordsAgreementAndIndependentAdjudication()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var runtime =
            new FakeRuntime
            {
                ForcePanelTie = true
            };

        var result =
            await Runner(
                    db,
                    runtime,
                    new AlternatingRandomizer())
                .RunAndIngestAsync(
                    Manifest());

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.All(result.Report!.Cases, item =>
        {
            Assert.Equal(
                "IndependentAdjudicator",
                item.Adjudication);
            Assert.Equal(1, item.AgreedJudgeVotes);
            Assert.Equal(3, item.TotalJudgeVotes);
        });
        Assert.Equal(
            result.Report.Cases.Count,
            runtime.AdjudicationCalls);
        Assert.All(result.Evaluation!.Metrics.Values, metrics =>
            Assert.Equal(
                33.333333m,
                metrics["independent_judge_agreement"]));
    }

    [Fact]
    public async Task SyntheticPreLabeledReport_CannotProduceTakeoverEvidence()
    {
        await using var db =
            ControllerTestHelpers.BuildDb();
        var manifest =
            Manifest();
        var synthetic =
            new LegendBlindBenchmarkReport(
                manifest.BaselineIdentity,
                manifest.DeployedSha,
                manifest.ManifestIdentity,
                DateTime.UtcNow,
                manifest.Cases.Select(item =>
                        new LegendBlindBenchmarkCaseResult(
                            item.DomainKey,
                            item.CaseIdentity,
                            "LEGEND",
                            true,
                            true,
                            true,
                            true,
                            true,
                            1,
                            2,
                            1,
                            2,
                            3,
                            3))
                    .ToArray());

        var evaluation =
            LegendBlindComparativeBenchmarkEvaluator.Evaluate(
                synthetic);
        Assert.True(evaluation.Valid);
        Assert.False(evaluation.TakeoverEligible);
        Assert.Empty(
            evaluation.BuildSignals(
                Guid.NewGuid(),
                manifest.BaselineIdentity,
                DateTime.UtcNow));
        Assert.Empty(
            db.LegendIntelligenceEvaluationSignals);
    }

    private static LegendBlindComparativeBenchmarkRunner Runner(
        Infrastructure.Data.MasterAppDbContext db,
        ILegendBlindBenchmarkRuntimeAuthority runtime,
        ILegendBlindAnswerOrderRandomizer randomizer) =>
        new(
            runtime,
            new LegendIntelligenceEvaluationService(db),
            randomizer);

    private static LegendBlindBenchmarkManifest Manifest()
    {
        var cases =
            LegendIntelligenceEvaluationDomainCatalog.All
                .SelectMany(domain =>
                    Enumerable.Range(0, 100)
                        .Select(index =>
                        {
                            var prompt =
                                $"Locked prompt {domain.Key} {index:D3}";
                            return new LegendBlindBenchmarkCaseDefinition(
                                domain.Key,
                                $"{domain.Key}-case-{index:D3}",
                                prompt,
                                LegendBlindComparativeBenchmarkRunner
                                    .ContentHash(prompt),
                                $"evidence-{domain.Key}-{index:D3}",
                                $"split-{domain.Key}-{index:D3}",
                                "FounderApproved",
                                IsAdversarial:
                                    index % 4 == 0,
                                IsUnsupportedRequest:
                                    index % 5 == 0,
                                IsTransferCase:
                                    index % 2 == 0,
                                IsResearchCase:
                                    domain.Key == "knowledge_synthesis" &&
                                    index < 20);
                        }))
                .ToArray();
        return Lock(
            new LegendBlindBenchmarkManifest(
                string.Empty,
                "gpt-5.6-sol@locked-test-v1",
                LegendBlindBenchmarkContracts.ExactBaselineModel,
                LegendBlindBenchmarkContracts.BaselineSettings,
                "legend-runtime-v1",
                LegendBlindBenchmarkContracts.CandidateSettings,
                "blind-prompts-v1",
                "openai-costs-test-v1",
                DeployedSha,
                ["judge-1", "judge-2", "judge-3"],
                LegendBlindBenchmarkContracts.JudgeSettings,
                "judge-adjudicator",
                LegendBlindBenchmarkContracts.ContaminationAuthority,
                string.Empty,
                [],
                [],
                cases));
    }

    private static LegendBlindBenchmarkManifest Lock(
        LegendBlindBenchmarkManifest draft)
    {
        var contaminationLocked =
            draft with
            {
                ContaminationProofIdentity =
                    LegendBlindComparativeBenchmarkRunner
                        .ComputeContaminationProofIdentity(draft)
            };
        return contaminationLocked with
        {
            ManifestIdentity =
                LegendBlindComparativeBenchmarkRunner
                    .ComputeManifestIdentity(contaminationLocked)
        };
    }

    private sealed class AlternatingRandomizer
        : ILegendBlindAnswerOrderRandomizer
    {
        private int _call;

        public bool LegendIsAnswerA() =>
            Interlocked.Increment(ref _call) % 2 == 0;
    }

    private sealed class FakeRuntime
        : ILegendBlindBenchmarkRuntimeAuthority
    {
        public string BaselineModelVersion { get; init; } =
            LegendBlindBenchmarkContracts.ExactBaselineModel;

        public bool ForcePanelTie { get; init; }

        public int LegendCalls { get; private set; }
        public int BaselineCalls { get; private set; }
        public int AdjudicationCalls { get; private set; }

        public List<LegendBlindBenchmarkJudgeRequest> JudgeRequests { get; } = [];

        public Task<LegendBlindBenchmarkRuntimeOutput> ExecuteLegendAsync(
            LegendBlindBenchmarkManifest manifest,
            LegendBlindBenchmarkCaseDefinition benchmarkCase,
            CancellationToken cancellationToken = default)
        {
            LegendCalls++;
            return Task.FromResult(
                new LegendBlindBenchmarkRuntimeOutput(
                    true,
                    "alpha-response:" + benchmarkCase.CaseIdentity,
                    LegendBlindBenchmarkContracts.CandidateResponseAuthority,
                    "legend-model-v1",
                    manifest.CandidateSettings,
                    manifest.PromptSetVersion,
                    manifest.DeployedSha,
                    1,
                    1,
                    Proof,
                    ResearchMeasurements:
                        benchmarkCase.IsResearchCase
                            ? new LegendConnectResearchEvaluationMeasurements(
                                true,
                                false,
                                true,
                                true,
                                true,
                                true,
                                true,
                                true,
                                true,
                                0m,
                                true,
                                1,
                                1,
                                true,
                                true,
                                true,
                                true,
                                false,
                                LegendBlindComparativeBenchmarkRunner.ContentHash(
                                    "research-proof-" + benchmarkCase.CaseIdentity))
                            : null));
        }

        public Task<LegendBlindBenchmarkRuntimeOutput> ExecuteBaselineAsync(
            LegendBlindBenchmarkManifest manifest,
            LegendBlindBenchmarkCaseDefinition benchmarkCase,
            CancellationToken cancellationToken = default)
        {
            BaselineCalls++;
            return Task.FromResult(
                new LegendBlindBenchmarkRuntimeOutput(
                    true,
                    "beta-response:" + benchmarkCase.CaseIdentity,
                    LegendBlindBenchmarkContracts.ProviderResponseAuthority,
                    BaselineModelVersion,
                    manifest.BaselineSettings,
                    manifest.PromptSetVersion,
                    manifest.DeployedSha,
                    2,
                    2,
                    Proof));
        }

        public Task<LegendBlindBenchmarkJudgeVote> JudgeAsync(
            string judgeIdentity,
            string judgeSettings,
            LegendBlindBenchmarkJudgeRequest request,
            CancellationToken cancellationToken = default)
        {
            JudgeRequests.Add(request);
            var legendSlot =
                request.AnswerA.StartsWith(
                    "alpha-response:",
                    StringComparison.Ordinal)
                    ? "A"
                    : "B";
            var baselineSlot =
                legendSlot == "A"
                    ? "B"
                    : "A";
            string winner;
            if (request.IsAdjudication)
            {
                AdjudicationCalls++;
                winner = legendSlot;
            }
            else if (!ForcePanelTie)
            {
                winner = legendSlot;
            }
            else
            {
                winner = judgeIdentity switch
                {
                    "judge-1" => legendSlot,
                    "judge-2" => baselineSlot,
                    _ => "TIE"
                };
            }

            return Task.FromResult(
                new LegendBlindBenchmarkJudgeVote(
                    true,
                    judgeIdentity,
                    judgeSettings,
                    winner,
                    true,
                    true,
                    true,
                    true,
                    true,
                    LegendBlindComparativeBenchmarkRunner.ContentHash(
                        $"{request.AssignmentIdentity}:{judgeIdentity}:{request.IsAdjudication}"),
                    1,
                    1,
                    ResearchAnswerACorrect:
                        request.IsResearchCase ? true : null,
                    ResearchAnswerBCorrect:
                        request.IsResearchCase ? true : null));
        }
    }
}

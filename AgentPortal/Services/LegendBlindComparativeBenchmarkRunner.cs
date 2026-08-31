using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Models;

namespace AgentPortal.Services;

internal sealed record LegendBlindBenchmarkCaseDefinition(
    string DomainKey,
    string CaseIdentity,
    string Prompt,
    string PromptSha256,
    string EvidenceIdentity,
    string SplitGroupIdentity,
    string Provenance,
    bool IsAdversarial,
    bool IsUnsupportedRequest,
    bool IsTransferCase,
    string SourceLanguageCode = "en");

internal sealed record LegendBlindBenchmarkManifest(
    string ManifestIdentity,
    string BaselineIdentity,
    string BaselineModelVersion,
    string BaselineSettings,
    string CandidateRuntimeIdentity,
    string CandidateSettings,
    string PromptSetVersion,
    string CostScheduleVersion,
    string DeployedSha,
    IReadOnlyList<string> JudgeIdentities,
    string JudgeSettings,
    string AdjudicatorIdentity,
    string ContaminationAuthority,
    string ContaminationProofIdentity,
    IReadOnlyList<string> TrainingPromptHashes,
    IReadOnlyList<string> TrainingSplitGroupIdentities,
    IReadOnlyList<LegendBlindBenchmarkCaseDefinition> Cases);

internal sealed record LegendBlindBenchmarkRuntimeOutput(
    bool Succeeded,
    string? Text,
    string ResponseAuthority,
    string ModelVersion,
    string Settings,
    string PromptSetVersion,
    string DeployedSha,
    long LatencyMicroseconds,
    long? CostMicrounits,
    string ProvenanceIdentity,
    string? ErrorCode = null,
    bool Retryable = false);

internal sealed record LegendBlindBenchmarkJudgeRequest(
    string DomainKey,
    string CaseIdentity,
    string Prompt,
    string AnswerA,
    string AnswerB,
    string AssignmentIdentity,
    string PromptSetVersion,
    bool IsAdversarial,
    bool IsUnsupportedRequest,
    bool IsTransferCase,
    bool IsAdjudication);

internal sealed record LegendBlindBenchmarkJudgeVote(
    bool Succeeded,
    string JudgeIdentity,
    string Settings,
    string Winner,
    bool NonInferior,
    bool AdversarialPassed,
    bool UnsupportedRequestIntegrity,
    bool TransferPassed,
    bool CalibrationPassed,
    string ProvenanceIdentity,
    long LatencyMicroseconds,
    long CostMicrounits,
    string? ErrorCode = null,
    bool Retryable = false);

internal interface ILegendBlindBenchmarkRuntimeAuthority
{
    Task<LegendBlindBenchmarkRuntimeOutput> ExecuteLegendAsync(
        LegendBlindBenchmarkManifest manifest,
        LegendBlindBenchmarkCaseDefinition benchmarkCase,
        CancellationToken cancellationToken = default);

    Task<LegendBlindBenchmarkRuntimeOutput> ExecuteBaselineAsync(
        LegendBlindBenchmarkManifest manifest,
        LegendBlindBenchmarkCaseDefinition benchmarkCase,
        CancellationToken cancellationToken = default);

    Task<LegendBlindBenchmarkJudgeVote> JudgeAsync(
        string judgeIdentity,
        string judgeSettings,
        LegendBlindBenchmarkJudgeRequest request,
        CancellationToken cancellationToken = default);
}

internal interface ILegendBlindAnswerOrderRandomizer
{
    bool LegendIsAnswerA();
}

internal sealed class LegendBlindCryptographicAnswerOrderRandomizer
    : ILegendBlindAnswerOrderRandomizer
{
    public LegendBlindCryptographicAnswerOrderRandomizer()
    {
    }

    public bool LegendIsAnswerA() =>
        RandomNumberGenerator.GetInt32(2) == 0;
}

internal sealed record LegendBlindBenchmarkRunResult(
    bool Succeeded,
    bool Ingested,
    LegendBlindBenchmarkReport? Report,
    LegendBlindBenchmarkEvaluation? Evaluation,
    IReadOnlyList<string> Blockers,
    string? ErrorCode = null,
    bool Retryable = false);

/// <summary>
/// The one bounded execution and ingestion path for blind comparative proof.
/// It owns no statistical thresholds: every completed report is handed to
/// LegendBlindComparativeBenchmarkEvaluator before any signal can be stored.
/// </summary>
internal sealed class LegendBlindComparativeBenchmarkRunner
{
    private const int RequiredCasesPerDomain = 100;
    private const int MaximumCases = 1_200;
    private const int MaximumPromptCharacters = 120_000;
    private const int MaximumOutputCharacters = 240_000;

    private readonly ILegendBlindBenchmarkRuntimeAuthority _runtime;
    private readonly LegendIntelligenceEvaluationService _ingestion;
    private readonly ILegendBlindAnswerOrderRandomizer _randomizer;

    public LegendBlindComparativeBenchmarkRunner(
        ILegendBlindBenchmarkRuntimeAuthority runtime,
        LegendIntelligenceEvaluationService ingestion,
        ILegendBlindAnswerOrderRandomizer randomizer)
    {
        _runtime = runtime;
        _ingestion = ingestion;
        _randomizer = randomizer;
    }

    internal async Task<LegendBlindBenchmarkRunResult> RunAndIngestAsync(
        LegendBlindBenchmarkManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var manifestBlockers =
            ValidateManifest(manifest);
        if (manifestBlockers.Count > 0)
        {
            return new(
                false,
                false,
                null,
                null,
                manifestBlockers,
                "blind_benchmark_manifest_rejected");
        }

        var cases =
            new List<LegendBlindBenchmarkCaseResult>(
                manifest.Cases.Count);

        foreach (var benchmarkCase in manifest.Cases
                     .OrderBy(item => item.DomainKey, StringComparer.Ordinal)
                     .ThenBy(item => item.CaseIdentity, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var legend =
                await _runtime.ExecuteLegendAsync(
                    manifest,
                    benchmarkCase,
                    cancellationToken);
            var legendFailure =
                RuntimeFailure(
                    legend,
                    manifest,
                    candidate: true);
            if (legendFailure is not null)
            {
                return Failure(
                    cases,
                    legendFailure,
                    legend.Retryable);
            }

            var baseline =
                await _runtime.ExecuteBaselineAsync(
                    manifest,
                    benchmarkCase,
                    cancellationToken);
            var baselineFailure =
                RuntimeFailure(
                    baseline,
                    manifest,
                    candidate: false);
            if (baselineFailure is not null)
            {
                return Failure(
                    cases,
                    baselineFailure,
                    baseline.Retryable);
            }

            var legendProof =
                OutputProof(
                    manifest,
                    benchmarkCase,
                    legend);
            var baselineProof =
                OutputProof(
                    manifest,
                    benchmarkCase,
                    baseline);
            var legendIsA =
                _randomizer.LegendIsAnswerA();
            var answerA = legendIsA
                ? legend.Text!
                : baseline.Text!;
            var answerB = legendIsA
                ? baseline.Text!
                : legend.Text!;
            var assignmentIdentity =
                StableHash(
                    "legend-blind-assignment-v1",
                    manifest.ManifestIdentity,
                    benchmarkCase.CaseIdentity,
                    legendProof,
                    baselineProof,
                    legendIsA ? "A" : "B");

            var judgeRequest =
                new LegendBlindBenchmarkJudgeRequest(
                    benchmarkCase.DomainKey,
                    benchmarkCase.CaseIdentity,
                    benchmarkCase.Prompt,
                    answerA,
                    answerB,
                    assignmentIdentity,
                    manifest.PromptSetVersion,
                    benchmarkCase.IsAdversarial,
                    benchmarkCase.IsUnsupportedRequest,
                    benchmarkCase.IsTransferCase,
                    IsAdjudication: false);
            var votes =
                new List<LegendBlindBenchmarkJudgeVote>(
                    manifest.JudgeIdentities.Count);

            foreach (var judge in manifest.JudgeIdentities)
            {
                var vote =
                    await _runtime.JudgeAsync(
                        judge,
                        manifest.JudgeSettings,
                        judgeRequest,
                        cancellationToken);
                var voteFailure =
                    VoteFailure(
                        vote,
                        judge,
                        manifest.JudgeSettings);
                if (voteFailure is not null)
                {
                    return Failure(
                        cases,
                        voteFailure,
                        vote.Retryable);
                }

                votes.Add(vote);
            }

            var majority =
                StrictMajority(votes);
            LegendBlindBenchmarkJudgeVote? adjudication = null;
            var adjudicationKind =
                "JudgeMajority";
            var winningSlot =
                majority;
            if (winningSlot is null)
            {
                adjudicationKind =
                    "IndependentAdjudicator";
                adjudication =
                    await _runtime.JudgeAsync(
                        manifest.AdjudicatorIdentity,
                        manifest.JudgeSettings,
                        judgeRequest with
                        {
                            IsAdjudication = true
                        },
                        cancellationToken);
                var adjudicationFailure =
                    VoteFailure(
                        adjudication,
                        manifest.AdjudicatorIdentity,
                        manifest.JudgeSettings);
                if (adjudicationFailure is not null)
                {
                    return Failure(
                        cases,
                        adjudicationFailure,
                        adjudication.Retryable);
                }

                winningSlot = adjudication.Winner;
            }

            var decidingVotes =
                adjudication is null
                    ? votes
                    : [adjudication];
            var winner =
                winningSlot == "TIE"
                    ? "TIE"
                    : string.Equals(
                        winningSlot,
                        legendIsA ? "A" : "B",
                        StringComparison.Ordinal)
                        ? "LEGEND"
                        : "BASELINE";
            var agreement =
                votes.Count(item =>
                    string.Equals(
                        item.Winner,
                        winningSlot,
                        StringComparison.Ordinal));
            var outcomeIdentity =
                StableHash(
                    "legend-blind-outcome-v1",
                    assignmentIdentity,
                    winner,
                    adjudicationKind,
                    string.Join(
                        ",",
                        votes.Select(item =>
                            item.ProvenanceIdentity)),
                    adjudication?.ProvenanceIdentity ??
                        string.Empty);

            cases.Add(
                new LegendBlindBenchmarkCaseResult(
                    benchmarkCase.DomainKey,
                    benchmarkCase.CaseIdentity,
                    winner,
                    MajorityFlag(
                        decidingVotes,
                        item => item.NonInferior),
                    MajorityFlag(
                        decidingVotes,
                        item => item.AdversarialPassed),
                    MajorityFlag(
                        decidingVotes,
                        item => item.UnsupportedRequestIntegrity),
                    PromptHeldOut(
                        manifest,
                        benchmarkCase),
                    true,
                    legend.LatencyMicroseconds,
                    baseline.LatencyMicroseconds,
                    legend.CostMicrounits!.Value,
                    baseline.CostMicrounits!.Value,
                    agreement,
                    votes.Count,
                    NativeCompleted: true,
                    TransferCase:
                        benchmarkCase.IsTransferCase,
                    TransferPassed:
                        MajorityFlag(
                            decidingVotes,
                            item => item.TransferPassed),
                    CalibrationPassed:
                        MajorityFlag(
                            decidingVotes,
                            item => item.CalibrationPassed),
                    CandidateModelVersion:
                        legend.ModelVersion,
                    BaselineModelVersion:
                        baseline.ModelVersion,
                    CandidateSettings:
                        legend.Settings,
                    BaselineSettings:
                        baseline.Settings,
                    PromptSetVersion:
                        manifest.PromptSetVersion,
                    DeployedSha:
                        manifest.DeployedSha,
                    LegendAnswerSlot:
                        legendIsA ? "A" : "B",
                    AssignmentIdentity:
                        assignmentIdentity,
                    LegendOutputProof:
                        legendProof,
                    BaselineOutputProof:
                        baselineProof,
                    OutcomeIdentity:
                        outcomeIdentity,
                    ContaminationChecked: true,
                    RuntimeProvenanceVerified: true,
                    Adjudication:
                        adjudicationKind,
                    JudgeIdentities:
                        manifest.JudgeIdentities.ToArray(),
                    JudgeLatencyMicroseconds:
                        SaturatingSum(
                            votes.Select(item =>
                                item.LatencyMicroseconds)),
                    JudgeCostMicrounits:
                        SaturatingSum(
                            votes.Select(item =>
                                item.CostMicrounits)),
                    AdjudicationLatencyMicroseconds:
                        adjudication?.LatencyMicroseconds ?? 0,
                    AdjudicationCostMicrounits:
                        adjudication?.CostMicrounits ?? 0,
                    JudgeOutcomes:
                        votes.Select(item =>
                                new LegendBlindBenchmarkJudgeOutcome(
                                    item.JudgeIdentity,
                                    item.Winner,
                                    item.ProvenanceIdentity,
                                    item.LatencyMicroseconds,
                                    item.CostMicrounits,
                                    IsAdjudication: false))
                            .Concat(
                                adjudication is null
                                    ? Array.Empty<LegendBlindBenchmarkJudgeOutcome>()
                                    :
                                    [
                                        new LegendBlindBenchmarkJudgeOutcome(
                                            adjudication.JudgeIdentity,
                                            adjudication.Winner,
                                            adjudication.ProvenanceIdentity,
                                            adjudication.LatencyMicroseconds,
                                            adjudication.CostMicrounits,
                                            IsAdjudication: true)
                                    ])
                            .ToArray()));
        }

        var measuredUtc =
            DateTime.UtcNow;
        var provenanceIdentity =
            StableHash(
                "legend-blind-run-provenance-v1",
                manifest.ManifestIdentity,
                manifest.DeployedSha,
                manifest.BaselineIdentity,
                manifest.BaselineModelVersion,
                manifest.BaselineSettings,
                manifest.PromptSetVersion,
                manifest.CostScheduleVersion,
                manifest.CandidateRuntimeIdentity,
                manifest.CandidateSettings,
                manifest.JudgeSettings,
                manifest.AdjudicatorIdentity,
                manifest.ContaminationAuthority,
                manifest.ContaminationProofIdentity,
                string.Join(
                    ",",
                    manifest.JudgeIdentities),
                string.Join(
                    ",",
                    cases.Select(item =>
                        item.OutcomeIdentity)));
        var report =
            new LegendBlindBenchmarkReport(
                manifest.BaselineIdentity,
                manifest.DeployedSha,
                manifest.ManifestIdentity,
                measuredUtc,
                cases,
                LegendBlindBenchmarkContracts.RunnerAuthority,
                manifest.CandidateRuntimeIdentity,
                manifest.CandidateSettings,
                manifest.BaselineModelVersion,
                manifest.BaselineSettings,
                manifest.PromptSetVersion,
                manifest.JudgeIdentities.ToArray(),
                manifest.JudgeSettings,
                manifest.AdjudicatorIdentity,
                provenanceIdentity,
                RuntimeOutputsLocked: true,
                ContaminationAuthority:
                    manifest.ContaminationAuthority,
                ContaminationProofIdentity:
                    manifest.ContaminationProofIdentity,
                CostScheduleVersion:
                    manifest.CostScheduleVersion);
        var runtimeReceipt =
            new RuntimeReceipt(
                report,
                RuntimeReceiptIdentity(report));
        var evaluation =
            LegendBlindComparativeBenchmarkEvaluator.EvaluateRuntime(
                report,
                runtimeReceipt);
        if (!evaluation.Valid ||
            !evaluation.TakeoverEligible)
        {
            return new(
                false,
                false,
                report,
                evaluation,
                evaluation.Blockers
                    .Concat(
                        evaluation.TakeoverBlockers)
                    .ToArray(),
                "blind_benchmark_evaluation_rejected");
        }

        var ingested =
            await _ingestion.IngestBlindBenchmarkAsync(
                report,
                runtimeReceipt,
                cancellationToken);
        return new(
            ingested,
            ingested,
            report,
            evaluation,
            ingested
                ? Array.Empty<string>()
                : ["The canonical benchmark ingestion boundary rejected the report."],
            ingested
                ? null
                : "blind_benchmark_ingestion_rejected");
    }

    internal static string ComputeManifestIdentity(
        LegendBlindBenchmarkManifest manifest) =>
        StableHash(
            JsonSerializer.Serialize(
                new
                {
                    Schema =
                        "legend-blind-benchmark-manifest-v1",
                    manifest.BaselineIdentity,
                    manifest.BaselineModelVersion,
                    manifest.BaselineSettings,
                    manifest.CandidateRuntimeIdentity,
                    manifest.CandidateSettings,
                    manifest.PromptSetVersion,
                    manifest.CostScheduleVersion,
                    manifest.DeployedSha,
                    Judges = manifest.JudgeIdentities,
                    manifest.JudgeSettings,
                    manifest.AdjudicatorIdentity,
                    manifest.ContaminationAuthority,
                    manifest.ContaminationProofIdentity,
                    TrainingPromptHashes =
                        manifest.TrainingPromptHashes
                            .OrderBy(item => item, StringComparer.Ordinal),
                    TrainingSplitGroups =
                        manifest.TrainingSplitGroupIdentities
                            .OrderBy(item => item, StringComparer.Ordinal),
                    Cases = manifest.Cases
                        .OrderBy(item => item.DomainKey, StringComparer.Ordinal)
                        .ThenBy(item => item.CaseIdentity, StringComparer.Ordinal)
                }));

    internal static string ComputeContaminationProofIdentity(
        LegendBlindBenchmarkManifest manifest) =>
        StableHash(
            "legend-blind-contamination-proof-v1",
            manifest.ContaminationAuthority,
            manifest.PromptSetVersion,
            string.Join(
                ",",
                manifest.TrainingPromptHashes
                    .OrderBy(item => item, StringComparer.Ordinal)),
            string.Join(
                ",",
                manifest.TrainingSplitGroupIdentities
                    .OrderBy(item => item, StringComparer.Ordinal)),
            JsonSerializer.Serialize(
                manifest.Cases
                    .OrderBy(item => item.DomainKey, StringComparer.Ordinal)
                    .ThenBy(item => item.CaseIdentity, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        item.DomainKey,
                        item.CaseIdentity,
                        item.PromptSha256,
                        item.EvidenceIdentity,
                        item.SplitGroupIdentity,
                        item.Provenance
                    })));

    private static IReadOnlyList<string> ValidateManifest(
        LegendBlindBenchmarkManifest manifest)
    {
        var blockers =
            new List<string>();
        var knownDomains =
            LegendIntelligenceEvaluationDomainCatalog.All
                .Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);

        if (manifest.Cases.Count is <= 0 or > MaximumCases)
        {
            blockers.Add(
                $"The bounded runner accepts between 1 and {MaximumCases} cases.");
        }

        if (!string.Equals(
                manifest.ManifestIdentity,
                ComputeManifestIdentity(manifest),
                StringComparison.Ordinal))
        {
            blockers.Add(
                "The locked benchmark manifest identity does not match its contents.");
        }

        if (!IsLowerHex(
                manifest.DeployedSha,
                40) ||
            string.IsNullOrWhiteSpace(
                manifest.PromptSetVersion) ||
            manifest.PromptSetVersion.Length > 120 ||
            string.IsNullOrWhiteSpace(
                manifest.CostScheduleVersion) ||
            manifest.CostScheduleVersion.Length > 120 ||
            !string.Equals(
                manifest.CandidateSettings,
                LegendBlindBenchmarkContracts.CandidateSettings,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                manifest.CandidateRuntimeIdentity) ||
            manifest.CandidateRuntimeIdentity.Length > 160)
        {
            blockers.Add(
                "The deployed LEGEND runtime lock is incomplete.");
        }

        if (!string.Equals(
                manifest.BaselineModelVersion,
                LegendBlindBenchmarkContracts.ExactBaselineModel,
                StringComparison.Ordinal) ||
            !manifest.BaselineIdentity.StartsWith(
                LegendBlindBenchmarkContracts.ExactBaselineModel + "@",
                StringComparison.Ordinal) ||
            LegendArchitecturalTakeoverGate.EvaluatorAuthorityPrefix.Length +
                manifest.BaselineIdentity.Length > 120 ||
            !string.Equals(
                manifest.BaselineSettings,
                LegendBlindBenchmarkContracts.BaselineSettings,
                StringComparison.Ordinal))
        {
            blockers.Add(
                "The exact GPT-SoL baseline lock is invalid.");
        }

        if (manifest.JudgeIdentities.Count is < 3 or > 8 ||
            manifest.JudgeIdentities.Any(
                item =>
                    string.IsNullOrWhiteSpace(item) ||
                    item.Length > 160) ||
            manifest.JudgeIdentities
                .Distinct(StringComparer.Ordinal)
                .Count() !=
                manifest.JudgeIdentities.Count ||
            manifest.JudgeIdentities.Contains(
                manifest.BaselineModelVersion,
                StringComparer.Ordinal) ||
            manifest.JudgeIdentities.Contains(
                manifest.CandidateRuntimeIdentity,
                StringComparer.Ordinal) ||
            !string.Equals(
                manifest.JudgeSettings,
                LegendBlindBenchmarkContracts.JudgeSettings,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                manifest.AdjudicatorIdentity) ||
            manifest.AdjudicatorIdentity.Length > 160 ||
            manifest.JudgeIdentities.Contains(
                manifest.AdjudicatorIdentity,
                StringComparer.Ordinal) ||
            string.Equals(
                manifest.AdjudicatorIdentity,
                manifest.BaselineModelVersion,
                StringComparison.Ordinal) ||
            string.Equals(
                manifest.AdjudicatorIdentity,
                manifest.CandidateRuntimeIdentity,
                StringComparison.Ordinal))
        {
            blockers.Add(
                "The locked independent judge panel is incomplete.");
        }

        if (!string.Equals(
                manifest.ContaminationAuthority,
                LegendBlindBenchmarkContracts.ContaminationAuthority,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ContaminationProofIdentity,
                ComputeContaminationProofIdentity(manifest),
                StringComparison.Ordinal))
        {
            blockers.Add(
                "The locked prompt-contamination proof is missing or invalid.");
        }

        if (manifest.TrainingPromptHashes.Any(item =>
                !IsLowerHex(item, 64)) ||
            manifest.TrainingPromptHashes
                .Distinct(StringComparer.Ordinal)
                .Count() !=
                manifest.TrainingPromptHashes.Count ||
            manifest.TrainingSplitGroupIdentities.Any(item =>
                string.IsNullOrWhiteSpace(item) ||
                item.Length > 256) ||
            manifest.TrainingSplitGroupIdentities
                .Distinct(StringComparer.Ordinal)
                .Count() !=
                manifest.TrainingSplitGroupIdentities.Count)
        {
            blockers.Add(
                "The training-lineage contamination input is malformed or duplicated.");
        }

        var duplicateCases =
            manifest.Cases
                .GroupBy(
                    item => item.CaseIdentity,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1);
        if (duplicateCases)
        {
            blockers.Add(
                "The locked manifest contains duplicate case identities.");
        }

        if (manifest.Cases
            .GroupBy(item => item.PromptSha256, StringComparer.Ordinal)
            .Any(group => group.Count() != 1) ||
            manifest.Cases
                .GroupBy(item => item.SplitGroupIdentity, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            manifest.Cases
                .GroupBy(item => item.EvidenceIdentity, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            blockers.Add(
                "The locked manifest contains duplicate prompt, split, or evidence lineage.");
        }

        if (manifest.Cases.Any(item =>
                !knownDomains.Contains(item.DomainKey) ||
                string.IsNullOrWhiteSpace(item.CaseIdentity) ||
                item.CaseIdentity.Length > 256 ||
                string.IsNullOrWhiteSpace(item.Prompt) ||
                item.Prompt.Length > MaximumPromptCharacters ||
                !string.Equals(
                    item.PromptSha256,
                    ContentHash(item.Prompt),
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.EvidenceIdentity) ||
                item.EvidenceIdentity.Length > 256 ||
                string.IsNullOrWhiteSpace(item.SplitGroupIdentity) ||
                item.SplitGroupIdentity.Length > 256 ||
                item.Provenance is not
                    ("FounderApproved" or "HumanVerified") ||
                string.IsNullOrWhiteSpace(item.SourceLanguageCode) ||
                item.SourceLanguageCode.Length > 32))
        {
            blockers.Add(
                "Every case requires complete governed prompt and provenance lineage.");
        }

        foreach (var domain in
                 LegendIntelligenceEvaluationDomainCatalog.All)
        {
            if (manifest.Cases.Count(item =>
                    string.Equals(
                        item.DomainKey,
                        domain.Key,
                        StringComparison.Ordinal)) <
                RequiredCasesPerDomain)
            {
                blockers.Add(
                    $"{domain.Key}: the locked manifest is incomplete.");
            }
        }

        if (manifest.Cases.Any(item =>
                !PromptHeldOut(
                    manifest,
                    item)))
        {
            blockers.Add(
                "The locked manifest contains prompt or split-lineage contamination.");
        }

        return blockers;
    }

    private static bool PromptHeldOut(
        LegendBlindBenchmarkManifest manifest,
        LegendBlindBenchmarkCaseDefinition benchmarkCase) =>
        !manifest.TrainingPromptHashes.Contains(
            benchmarkCase.PromptSha256,
            StringComparer.Ordinal) &&
        !manifest.TrainingSplitGroupIdentities.Contains(
            benchmarkCase.SplitGroupIdentity,
            StringComparer.Ordinal);

    private static string? RuntimeFailure(
        LegendBlindBenchmarkRuntimeOutput output,
        LegendBlindBenchmarkManifest manifest,
        bool candidate)
    {
        var expectedAuthority = candidate
            ? LegendBlindBenchmarkContracts.CandidateResponseAuthority
            : LegendBlindBenchmarkContracts.ProviderResponseAuthority;
        var expectedSettings = candidate
            ? manifest.CandidateSettings
            : manifest.BaselineSettings;

        if (!output.Succeeded ||
            string.IsNullOrWhiteSpace(output.Text) ||
            output.Text.Length > MaximumOutputCharacters)
        {
            return output.ErrorCode ??
                (candidate
                    ? "blind_benchmark_legend_runtime_failed"
                    : "blind_benchmark_baseline_runtime_failed");
        }

        if (!string.Equals(
                output.ResponseAuthority,
                expectedAuthority,
                StringComparison.Ordinal) ||
            !string.Equals(
                output.Settings,
                expectedSettings,
                StringComparison.Ordinal) ||
            !string.Equals(
                output.PromptSetVersion,
                manifest.PromptSetVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                output.DeployedSha,
                manifest.DeployedSha,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(output.ModelVersion) ||
            output.ModelVersion.Length > 200 ||
            (!candidate &&
             !string.Equals(
                 output.ModelVersion,
                 manifest.BaselineModelVersion,
                 StringComparison.Ordinal)) ||
            output.LatencyMicroseconds < 0 ||
            output.CostMicrounits is null or < 0 ||
            !IsLowerHex(output.ProvenanceIdentity, 64))
        {
            return candidate
                ? "blind_benchmark_legend_runtime_proof_invalid"
                : "blind_benchmark_baseline_drift";
        }

        return null;
    }

    private static string? VoteFailure(
        LegendBlindBenchmarkJudgeVote vote,
        string expectedJudge,
        string expectedSettings)
    {
        if (!vote.Succeeded)
        {
            return vote.ErrorCode ??
                "blind_benchmark_judge_failed";
        }

        return !string.Equals(
                   vote.JudgeIdentity,
                   expectedJudge,
                   StringComparison.Ordinal) ||
               !string.Equals(
                   vote.Settings,
                   expectedSettings,
                   StringComparison.Ordinal) ||
               vote.Winner is not ("A" or "B" or "TIE") ||
               vote.LatencyMicroseconds < 0 ||
               vote.CostMicrounits < 0 ||
               !IsLowerHex(
                   vote.ProvenanceIdentity,
                   64)
            ? "blind_benchmark_judge_proof_invalid"
            : null;
    }

    private static string? StrictMajority(
        IReadOnlyList<LegendBlindBenchmarkJudgeVote> votes)
    {
        var winner = votes
            .GroupBy(item => item.Winner, StringComparer.Ordinal)
            .Select(group => new
            {
                Winner = group.Key,
                Count = group.Count()
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Winner, StringComparer.Ordinal)
            .First();
        return winner.Count > votes.Count / 2
            ? winner.Winner
            : null;
    }

    private static bool MajorityFlag(
        IReadOnlyList<LegendBlindBenchmarkJudgeVote> votes,
        Func<LegendBlindBenchmarkJudgeVote, bool> predicate) =>
        votes.Count(predicate) > votes.Count / 2;

    private static string OutputProof(
        LegendBlindBenchmarkManifest manifest,
        LegendBlindBenchmarkCaseDefinition benchmarkCase,
        LegendBlindBenchmarkRuntimeOutput output) =>
        StableHash(
            "legend-blind-output-v1",
            manifest.ManifestIdentity,
            benchmarkCase.CaseIdentity,
            output.ResponseAuthority,
            output.ModelVersion,
            output.Settings,
            output.PromptSetVersion,
            output.DeployedSha,
            output.LatencyMicroseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            output.CostMicrounits!.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            output.ProvenanceIdentity,
            StableHash(output.Text!));

    private static long SaturatingSum(
        IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value > 0 &&
                total > long.MaxValue - value)
            {
                return long.MaxValue;
            }

            total += value;
        }

        return total;
    }

    private static LegendBlindBenchmarkRunResult Failure(
        IReadOnlyCollection<LegendBlindBenchmarkCaseResult> completed,
        string errorCode,
        bool retryable) =>
        new(
            false,
            false,
            null,
            null,
            [
                $"The runner stopped after {completed.Count} completed case(s): {errorCode}."
            ],
            errorCode,
            retryable);

    private static string StableHash(
        params string[] values) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(values))))
            .ToLowerInvariant();

    internal static string ContentHash(
        string value) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    internal static bool IsIssuedRuntimeReceipt(
        object? receipt,
        LegendBlindBenchmarkReport report) =>
        receipt is RuntimeReceipt issued &&
        ReferenceEquals(
            issued.Report,
            report) &&
        string.Equals(
            issued.ReportIdentity,
            RuntimeReceiptIdentity(report),
            StringComparison.Ordinal);

    private static string RuntimeReceiptIdentity(
        LegendBlindBenchmarkReport report) =>
        StableHash(
            "legend-blind-runtime-receipt-v1",
            JsonSerializer.Serialize(report));

    private sealed record RuntimeReceipt(
        LegendBlindBenchmarkReport Report,
        string ReportIdentity);

    private static bool IsLowerHex(
        string value,
        int length) =>
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or
            >= 'a' and <= 'f');
}

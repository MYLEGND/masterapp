using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Models;
using Domain.Entities;

namespace AgentPortal.Services;

internal sealed record LegendBlindBenchmarkJudgeOutcome(
    string JudgeIdentity,
    string Winner,
    string ProvenanceIdentity,
    long LatencyMicroseconds,
    long CostMicrounits,
    bool IsAdjudication);

internal sealed record LegendBlindBenchmarkCaseResult(
    string DomainKey,
    string CaseIdentity,
    string Winner,
    bool NonInferior,
    bool AdversarialPassed,
    bool UnsupportedRequestIntegrity,
    bool PromptHeldOut,
    bool AssignmentBlinded,
    long LegendLatencyMicroseconds,
    long BaselineLatencyMicroseconds,
    long LegendCostMicrounits,
    long BaselineCostMicrounits,
    int AgreedJudgeVotes,
    int TotalJudgeVotes,
    bool NativeCompleted = false,
    bool TransferCase = false,
    bool TransferPassed = false,
    bool CalibrationPassed = false,
    string CandidateModelVersion = "",
    string BaselineModelVersion = "",
    string CandidateSettings = "",
    string BaselineSettings = "",
    string PromptSetVersion = "",
    string DeployedSha = "",
    string LegendAnswerSlot = "",
    string AssignmentIdentity = "",
    string LegendOutputProof = "",
    string BaselineOutputProof = "",
    string OutcomeIdentity = "",
    bool ContaminationChecked = false,
    bool RuntimeProvenanceVerified = false,
    string Adjudication = "",
    IReadOnlyList<string>? JudgeIdentities = null,
    long JudgeLatencyMicroseconds = 0,
    long JudgeCostMicrounits = 0,
    long AdjudicationLatencyMicroseconds = 0,
    long AdjudicationCostMicrounits = 0,
    IReadOnlyList<LegendBlindBenchmarkJudgeOutcome>? JudgeOutcomes = null);

internal sealed record LegendBlindBenchmarkReport(
    string BaselineIdentity,
    string CandidateCommitSha,
    string ManifestIdentity,
    DateTime MeasuredUtc,
    IReadOnlyList<LegendBlindBenchmarkCaseResult> Cases,
    string ExecutionAuthority = "SyntheticPreLabeled",
    string CandidateRuntimeIdentity = "",
    string CandidateSettings = "",
    string BaselineModelVersion = "",
    string BaselineSettings = "",
    string PromptSetVersion = "",
    IReadOnlyList<string>? JudgeIdentities = null,
    string JudgeSettings = "",
    string AdjudicatorIdentity = "",
    string ProvenanceIdentity = "",
    bool RuntimeOutputsLocked = false,
    string ContaminationAuthority = "",
    string ContaminationProofIdentity = "",
    string CostScheduleVersion = "")
{
    internal string DeployedSha => CandidateCommitSha;
}

internal sealed record LegendBlindBenchmarkEvaluation(
    bool Valid,
    bool TakeoverEligible,
    string? SuiteIdentity,
    string BaselineIdentity,
    string? ProvenanceIdentity,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> Metrics,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> TakeoverBlockers)
{
    internal IReadOnlyList<LegendIntelligenceEvaluationSignal> BuildSignals(
        Guid contractId,
        string baselineIdentity,
        DateTime measuredUtc)
    {
        if (!Valid ||
            !TakeoverEligible ||
            string.IsNullOrWhiteSpace(SuiteIdentity) ||
            string.IsNullOrWhiteSpace(ProvenanceIdentity) ||
            !string.Equals(
                BaselineIdentity,
                baselineIdentity,
                StringComparison.Ordinal))
        {
            return Array.Empty<LegendIntelligenceEvaluationSignal>();
        }

        return Metrics
            .OrderBy(domain => domain.Key, StringComparer.Ordinal)
            .SelectMany(domain => domain.Value
                .OrderBy(metric => metric.Key, StringComparer.Ordinal)
                .Select(metric => new LegendIntelligenceEvaluationSignal
                {
                    ContractId = contractId,
                    DomainKey = domain.Key,
                    MetricKey = metric.Key,
                    Value = metric.Value,
                    EvidenceAuthority =
                        LegendArchitecturalTakeoverGate.EvaluatorAuthorityPrefix +
                        baselineIdentity,
                    EvidenceReference =
                        LegendArchitecturalTakeoverGate.SuiteReferencePrefix +
                        SuiteIdentity +
                        ":" +
                        domain.Key +
                        ":" +
                        metric.Key,
                    State = "Current",
                    MeasuredUtc = measuredUtc,
                    CreatedUtc = DateTime.UtcNow
                }))
            .ToArray();
    }
}

internal static class LegendBlindBenchmarkContracts
{
    internal const string RunnerAuthority =
        "legend-locked-blind-benchmark-runner-v1";

    internal const string CandidateResponseAuthority =
        "LegendConnectOperations";

    internal const string ProviderResponseAuthority =
        "OpenAIResponses";

    internal const string ExactBaselineModel =
        "gpt-5.6-sol";

    internal const string CandidateSettings =
        "legend-native-v1,external_escalation=false";

    internal const string ContaminationAuthority =
        "legend-locked-prompt-contamination-authority-v1";

    internal const string BaselineSettings =
        "provider=openai,responses-v1,store=false,max_output_tokens=4000,reasoning_effort=medium,service_tier=auto";

    internal const string JudgeSettings =
        "provider=openai,responses-v1,store=false,max_output_tokens=1000,reasoning_effort=medium,service_tier=auto,blind_schema=v1";
}

/// <summary>
/// Converts immutable case-level blind comparison results into the only metric
/// shape accepted by the takeover gate. It cannot execute either candidate,
/// judge answers, or manufacture a missing case.
/// </summary>
internal static class LegendBlindComparativeBenchmarkEvaluator
{
    private const int RequiredCasesPerDomain = 100;

    internal static LegendBlindBenchmarkEvaluation Evaluate(
        LegendBlindBenchmarkReport report) =>
        EvaluateCore(
            report,
            runtimeReceiptIssued: false);

    internal static LegendBlindBenchmarkEvaluation EvaluateRuntime(
        LegendBlindBenchmarkReport report,
        object runtimeReceipt) =>
        EvaluateCore(
            report,
            LegendBlindComparativeBenchmarkRunner
                .IsIssuedRuntimeReceipt(
                    runtimeReceipt,
                    report));

    private static LegendBlindBenchmarkEvaluation EvaluateCore(
        LegendBlindBenchmarkReport report,
        bool runtimeReceiptIssued)
    {
        var blockers = new List<string>();
        var takeoverBlockers = new List<string>();
        if (!runtimeReceiptIssued)
        {
            takeoverBlockers.Add(
                "The report was not issued by the bounded runtime runner.");
        }
        if (string.IsNullOrWhiteSpace(report.BaselineIdentity))
            blockers.Add("A locked baseline identity is required.");
        if (!IsLowerHex(report.CandidateCommitSha, 40))
            blockers.Add("The candidate must be an immutable lowercase commit SHA.");
        if (!IsLowerHex(report.ManifestIdentity, 64))
            blockers.Add("The benchmark manifest identity must be a lowercase SHA-256 value.");
        if (report.MeasuredUtc.Kind != DateTimeKind.Utc)
            blockers.Add("The benchmark timestamp must be UTC.");

        var duplicateCases = report.Cases
            .GroupBy(item => item.CaseIdentity, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateCases.Length > 0)
            blockers.Add("The report contains duplicate case identities.");

        var knownDomains = LegendIntelligenceEvaluationDomainCatalog.All
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (report.Cases.Any(item =>
                !knownDomains.Contains(item.DomainKey)))
        {
            blockers.Add("The report contains an unknown evaluation domain.");
        }

        if (report.Cases.Any(item =>
                string.IsNullOrWhiteSpace(item.CaseIdentity) ||
                item.CaseIdentity.Length > 256 ||
                item.Winner is not ("LEGEND" or "BASELINE" or "TIE") ||
                item.LegendLatencyMicroseconds < 0 ||
                item.BaselineLatencyMicroseconds < 0 ||
                item.LegendCostMicrounits < 0 ||
                item.BaselineCostMicrounits < 0 ||
                item.JudgeLatencyMicroseconds < 0 ||
                item.JudgeCostMicrounits < 0 ||
                item.AdjudicationLatencyMicroseconds < 0 ||
                item.AdjudicationCostMicrounits < 0 ||
                item.TotalJudgeVotes <= 0 ||
                item.AgreedJudgeVotes < 0 ||
                item.AgreedJudgeVotes > item.TotalJudgeVotes))
        {
            blockers.Add("The report contains an invalid case result.");
        }

        var metrics =
            new Dictionary<string, IReadOnlyDictionary<string, decimal>>(
                StringComparer.Ordinal);
        foreach (var domain in LegendIntelligenceEvaluationDomainCatalog.All)
        {
            var cases = report.Cases
                .Where(item =>
                    string.Equals(
                        item.DomainKey,
                        domain.Key,
                        StringComparison.Ordinal))
                .OrderBy(item => item.CaseIdentity, StringComparer.Ordinal)
                .ToArray();
            if (cases.Length < RequiredCasesPerDomain)
            {
                blockers.Add(
                    $"{domain.Key}: requires at least {RequiredCasesPerDomain} unique blind cases.");
                continue;
            }

            var wins = cases.Count(item => item.Winner == "LEGEND");
            metrics[domain.Key] =
                new Dictionary<string, decimal>(StringComparer.Ordinal)
                {
                    ["sample_size"] = cases.Length,
                    ["blind_win_rate"] = Percent(wins, cases.Length),
                    ["blind_win_rate_lower_confidence_bound"] =
                        WilsonLowerBoundPercent(wins, cases.Length),
                    ["non_inferiority_rate"] =
                        Percent(cases.Count(item => item.NonInferior), cases.Length),
                    ["adversarial_pass_rate"] =
                        Percent(cases.Count(item => item.AdversarialPassed), cases.Length),
                    ["unsupported_request_integrity"] =
                        Percent(cases.Count(item => item.UnsupportedRequestIntegrity), cases.Length),
                    ["prompt_holdout_integrity"] =
                        Percent(cases.Count(item => item.PromptHeldOut), cases.Length),
                    ["assignment_blinding_integrity"] =
                        Percent(cases.Count(item => item.AssignmentBlinded), cases.Length),
                    ["independent_judge_agreement"] =
                        Percent(
                            cases.Sum(item => (long)item.AgreedJudgeVotes),
                            cases.Sum(item => (long)item.TotalJudgeVotes)),
                    ["latency_efficiency"] =
                        Percent(
                            cases.Count(item =>
                                item.LegendLatencyMicroseconds <
                                item.BaselineLatencyMicroseconds),
                            cases.Length),
                    ["cost_efficiency"] =
                        Percent(
                            cases.Count(item =>
                                item.LegendCostMicrounits <
                                item.BaselineCostMicrounits),
                            cases.Length),
                    ["native_execution"] =
                        Percent(
                            cases.Count(item => item.NativeCompleted),
                            cases.Length),
                    ["transfer"] =
                        Percent(
                            cases.Count(item =>
                                item.TransferCase &&
                                item.TransferPassed),
                            cases.Count(item => item.TransferCase)),
                    ["calibration"] =
                        Percent(
                            cases.Count(item => item.CalibrationPassed),
                            cases.Length),
                    ["runtime_provenance_integrity"] =
                        Percent(
                            cases.Count(item =>
                                item.RuntimeProvenanceVerified),
                            cases.Length),
                    ["baseline_drift_integrity"] =
                        Percent(
                            cases.Count(item =>
                                string.Equals(
                                    item.BaselineModelVersion,
                                    LegendBlindBenchmarkContracts
                                        .ExactBaselineModel,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    item.BaselineSettings,
                                    report.BaselineSettings,
                                    StringComparison.Ordinal)),
                            cases.Length),
                    ["contamination_integrity"] =
                        Percent(
                            cases.Count(item =>
                                item.ContaminationChecked &&
                                item.PromptHeldOut),
                            cases.Length)
                };
        }

        ValidateTakeoverProvenance(
            report,
            takeoverBlockers);

        var suiteIdentity = blockers.Count == 0
            ? ComputeSuiteIdentity(report)
            : null;
        return new(
            blockers.Count == 0,
            blockers.Count == 0 &&
            takeoverBlockers.Count == 0,
            suiteIdentity,
            report.BaselineIdentity,
            takeoverBlockers.Count == 0
                ? report.ProvenanceIdentity
                : null,
            metrics,
            blockers,
            takeoverBlockers);
    }

    private static void ValidateTakeoverProvenance(
        LegendBlindBenchmarkReport report,
        ICollection<string> blockers)
    {
        var judges =
            report.JudgeIdentities ?? Array.Empty<string>();

        if (!string.Equals(
                report.ExecutionAuthority,
                LegendBlindBenchmarkContracts.RunnerAuthority,
                StringComparison.Ordinal) ||
            !report.RuntimeOutputsLocked)
        {
            blockers.Add(
                "Only real locked outputs issued by the benchmark runner may become takeover evidence.");
        }

        if (!IsLowerHex(
                report.DeployedSha,
                40) ||
            string.IsNullOrWhiteSpace(
                report.PromptSetVersion) ||
            report.PromptSetVersion.Length > 120 ||
            string.IsNullOrWhiteSpace(
                report.CandidateRuntimeIdentity) ||
            report.CandidateRuntimeIdentity.Length > 160 ||
            !string.Equals(
                report.CandidateSettings,
                LegendBlindBenchmarkContracts.CandidateSettings,
                StringComparison.Ordinal))
        {
            blockers.Add(
                "The exact deployed LEGEND runtime configuration is missing or invalid.");
        }

        if (!string.Equals(
                report.BaselineModelVersion,
                LegendBlindBenchmarkContracts.ExactBaselineModel,
                StringComparison.Ordinal) ||
            !report.BaselineIdentity.StartsWith(
                LegendBlindBenchmarkContracts.ExactBaselineModel + "@",
                StringComparison.Ordinal) ||
            LegendArchitecturalTakeoverGate.EvaluatorAuthorityPrefix.Length +
                report.BaselineIdentity.Length > 120 ||
            !string.Equals(
                report.BaselineSettings,
                LegendBlindBenchmarkContracts.BaselineSettings,
                StringComparison.Ordinal))
        {
            blockers.Add(
                "The locked GPT-SoL baseline drifted from its exact model or settings.");
        }

        if (judges.Count is < 3 or > 8 ||
            judges.Any(item =>
                string.IsNullOrWhiteSpace(item) ||
                item.Length > 160) ||
            judges.Distinct(StringComparer.Ordinal).Count() !=
                judges.Count ||
            judges.Contains(
                report.BaselineModelVersion,
                StringComparer.Ordinal) ||
            judges.Contains(
                report.CandidateRuntimeIdentity,
                StringComparer.Ordinal) ||
            !string.Equals(
                report.JudgeSettings,
                LegendBlindBenchmarkContracts.JudgeSettings,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                report.AdjudicatorIdentity) ||
            report.AdjudicatorIdentity.Length > 160 ||
            judges.Contains(
                report.AdjudicatorIdentity,
                StringComparer.Ordinal) ||
            string.Equals(
                report.AdjudicatorIdentity,
                report.BaselineModelVersion,
                StringComparison.Ordinal) ||
            string.Equals(
                report.AdjudicatorIdentity,
                report.CandidateRuntimeIdentity,
                StringComparison.Ordinal) ||
            report.Cases.Any(item =>
                judges.Contains(
                    item.CandidateModelVersion,
                    StringComparer.Ordinal) ||
                string.Equals(
                    report.AdjudicatorIdentity,
                    item.CandidateModelVersion,
                    StringComparison.Ordinal)))
        {
            blockers.Add(
                "At least three distinct locked judges and one adjudicator are required.");
        }

        if (!IsLowerHex(
                report.ProvenanceIdentity,
                64))
        {
            blockers.Add(
                "The runner did not supply immutable execution provenance.");
        }
        if (string.IsNullOrWhiteSpace(
                report.CostScheduleVersion) ||
            report.CostScheduleVersion.Length > 120)
        {
            blockers.Add(
                "The locked provider cost schedule version is missing.");
        }

        if (!string.Equals(
                report.ContaminationAuthority,
                LegendBlindBenchmarkContracts.ContaminationAuthority,
                StringComparison.Ordinal) ||
            !IsLowerHex(
                report.ContaminationProofIdentity,
                64))
        {
            blockers.Add(
                "The runner did not supply the locked prompt-contamination receipt.");
        }

        if (report.Cases.Any(item =>
                !item.RuntimeProvenanceVerified ||
                !item.ContaminationChecked ||
                !item.PromptHeldOut ||
                !item.AssignmentBlinded ||
                item.LegendAnswerSlot is not ("A" or "B") ||
                !IsLowerHex(item.AssignmentIdentity, 64) ||
                !IsLowerHex(item.LegendOutputProof, 64) ||
                !IsLowerHex(item.BaselineOutputProof, 64) ||
                !IsLowerHex(item.OutcomeIdentity, 64) ||
                string.IsNullOrWhiteSpace(
                    item.CandidateModelVersion) ||
                item.CandidateModelVersion.Length > 200 ||
                !string.Equals(
                    item.BaselineModelVersion,
                    report.BaselineModelVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.BaselineSettings,
                    report.BaselineSettings,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.CandidateSettings,
                    report.CandidateSettings,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.PromptSetVersion,
                    report.PromptSetVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    item.DeployedSha,
                    report.DeployedSha,
                    StringComparison.Ordinal) ||
                item.JudgeIdentities is null ||
                !item.JudgeIdentities.SequenceEqual(
                    judges,
                    StringComparer.Ordinal) ||
                item.Adjudication is not
                    ("JudgeMajority" or "IndependentAdjudicator")))
        {
            blockers.Add(
                "One or more cases lack complete blinded runtime, judge, baseline, or contamination provenance.");
        }

        if (HasDuplicate(
                report.Cases.Select(item => item.AssignmentIdentity)) ||
            HasDuplicate(
                report.Cases.Select(item => item.LegendOutputProof)) ||
            HasDuplicate(
                report.Cases.Select(item => item.BaselineOutputProof)) ||
            HasDuplicate(
                report.Cases.Select(item => item.OutcomeIdentity)))
        {
            blockers.Add(
                "The report reuses assignment, output, or outcome proof lineage across cases.");
        }

        if (HasDuplicate(
                report.Cases.SelectMany(item =>
                    (item.JudgeOutcomes ??
                     Array.Empty<LegendBlindBenchmarkJudgeOutcome>())
                    .Select(outcome =>
                        outcome.ProvenanceIdentity))))
        {
            blockers.Add(
                "The report reuses judge or adjudicator proof lineage.");
        }

        if (report.Cases.Any(item =>
                !HasConsistentJudgeOutcomes(
                    item,
                    judges,
                    report.AdjudicatorIdentity)))
        {
            blockers.Add(
                "One or more cases has incomplete or inconsistent judge-vote lineage.");
        }
    }

    private static bool HasConsistentJudgeOutcomes(
        LegendBlindBenchmarkCaseResult benchmarkCase,
        IReadOnlyList<string> judges,
        string adjudicatorIdentity)
    {
        var outcomes =
            benchmarkCase.JudgeOutcomes ??
            Array.Empty<LegendBlindBenchmarkJudgeOutcome>();
        var panel =
            outcomes.Where(item =>
                    !item.IsAdjudication)
                .ToArray();
        var adjudication =
            outcomes.Where(item =>
                    item.IsAdjudication)
                .ToArray();
        if (panel.Length != judges.Count ||
            !panel.Select(item => item.JudgeIdentity)
                .SequenceEqual(
                    judges,
                    StringComparer.Ordinal) ||
            panel.Any(InvalidJudgeOutcome) ||
            benchmarkCase.TotalJudgeVotes != panel.Length)
        {
            return false;
        }

        if (benchmarkCase.Adjudication == "JudgeMajority")
        {
            if (adjudication.Length != 0)
                return false;
        }
        else if (adjudication.Length != 1 ||
                 !string.Equals(
                     adjudication[0].JudgeIdentity,
                     adjudicatorIdentity,
                     StringComparison.Ordinal) ||
                 InvalidJudgeOutcome(adjudication[0]))
        {
            return false;
        }

        var finalSlot =
            benchmarkCase.Winner == "TIE"
                ? "TIE"
                : benchmarkCase.Winner == "LEGEND"
                    ? benchmarkCase.LegendAnswerSlot
                    : benchmarkCase.LegendAnswerSlot == "A"
                        ? "B"
                        : "A";
        return benchmarkCase.AgreedJudgeVotes ==
            panel.Count(item =>
                string.Equals(
                    item.Winner,
                    finalSlot,
                    StringComparison.Ordinal));
    }

    private static bool InvalidJudgeOutcome(
        LegendBlindBenchmarkJudgeOutcome outcome) =>
        string.IsNullOrWhiteSpace(
            outcome.JudgeIdentity) ||
        outcome.Winner is not ("A" or "B" or "TIE") ||
        !IsLowerHex(
            outcome.ProvenanceIdentity,
            64) ||
        outcome.LatencyMicroseconds < 0 ||
        outcome.CostMicrounits < 0;

    private static bool HasDuplicate(
        IEnumerable<string> identities) =>
        identities.GroupBy(
                item => item,
                StringComparer.Ordinal)
            .Any(group => group.Count() != 1);

    private static string ComputeSuiteIdentity(
        LegendBlindBenchmarkReport report)
    {
        var canonical = new
        {
            Schema = "legend-blind-comparative-suite-v2",
            report.BaselineIdentity,
            report.CandidateCommitSha,
            report.ManifestIdentity,
            report.ExecutionAuthority,
            report.CandidateRuntimeIdentity,
            report.CandidateSettings,
            report.BaselineModelVersion,
            report.BaselineSettings,
            report.PromptSetVersion,
            report.JudgeIdentities,
            report.JudgeSettings,
            report.AdjudicatorIdentity,
            report.ProvenanceIdentity,
            report.RuntimeOutputsLocked,
            report.ContaminationAuthority,
            report.ContaminationProofIdentity,
            report.CostScheduleVersion,
            MeasuredUtc = report.MeasuredUtc.ToString("O"),
            Cases = report.Cases
                .OrderBy(item => item.DomainKey, StringComparer.Ordinal)
                .ThenBy(item => item.CaseIdentity, StringComparer.Ordinal)
                .ToArray()
        };
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(canonical))))
            .ToLowerInvariant();
    }

    private static decimal Percent(long numerator, long denominator) =>
        denominator <= 0
            ? 0m
            : Math.Round(
                (decimal)numerator * 100m / denominator,
                6,
                MidpointRounding.AwayFromZero);

    private static decimal WilsonLowerBoundPercent(
        int successes,
        int count)
    {
        if (count <= 0)
            return 0m;

        const double z = 1.959963984540054;
        var n = (double)count;
        var proportion = successes / n;
        var zSquared = z * z;
        var centre = proportion + zSquared / (2d * n);
        var margin = z * Math.Sqrt(
            (proportion * (1d - proportion) + zSquared / (4d * n)) / n);
        var lower = (centre - margin) / (1d + zSquared / n);
        return Math.Round(
            (decimal)Math.Max(0d, lower) * 100m,
            6,
            MidpointRounding.AwayFromZero);
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

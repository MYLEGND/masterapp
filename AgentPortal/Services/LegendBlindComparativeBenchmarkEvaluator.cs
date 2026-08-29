using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Models;
using Domain.Entities;

namespace AgentPortal.Services;

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
    int TotalJudgeVotes);

internal sealed record LegendBlindBenchmarkReport(
    string BaselineIdentity,
    string CandidateCommitSha,
    string ManifestIdentity,
    DateTime MeasuredUtc,
    IReadOnlyList<LegendBlindBenchmarkCaseResult> Cases);

internal sealed record LegendBlindBenchmarkEvaluation(
    bool Valid,
    string? SuiteIdentity,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> Metrics,
    IReadOnlyList<string> Blockers)
{
    internal IReadOnlyList<LegendIntelligenceEvaluationSignal> BuildSignals(
        Guid contractId,
        string baselineIdentity,
        DateTime measuredUtc)
    {
        if (!Valid || string.IsNullOrWhiteSpace(SuiteIdentity))
            return Array.Empty<LegendIntelligenceEvaluationSignal>();

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

/// <summary>
/// Converts immutable case-level blind comparison results into the only metric
/// shape accepted by the takeover gate. It cannot execute either candidate,
/// judge answers, or manufacture a missing case.
/// </summary>
internal static class LegendBlindComparativeBenchmarkEvaluator
{
    private const int RequiredCasesPerDomain = 100;

    internal static LegendBlindBenchmarkEvaluation Evaluate(
        LegendBlindBenchmarkReport report)
    {
        var blockers = new List<string>();
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
                            cases.Length)
                };
        }

        var suiteIdentity = blockers.Count == 0
            ? ComputeSuiteIdentity(report)
            : null;
        return new(
            blockers.Count == 0,
            suiteIdentity,
            metrics,
            blockers);
    }

    private static string ComputeSuiteIdentity(
        LegendBlindBenchmarkReport report)
    {
        var canonical = new
        {
            Schema = "legend-blind-comparative-suite-v1",
            report.BaselineIdentity,
            report.CandidateCommitSha,
            report.ManifestIdentity,
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

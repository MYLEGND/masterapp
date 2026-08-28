using AgentPortal.Models;
using Domain.Entities;

namespace AgentPortal.Services;

/// <summary>
/// Pure fail-closed proof gate for a universal-intelligence superiority claim.
/// It consumes only blind comparative signals already retained by the
/// canonical evaluation authority. Curriculum volume, self-assessment, and a
/// win in one domain can never substitute for missing domain-level proof.
/// </summary>
internal static class LegendArchitecturalTakeoverGate
{
    internal const string EvaluatorAuthorityPrefix =
        "legend-locked-blind-comparative-evaluator-v1:";

    private static readonly IReadOnlyDictionary<string, decimal> RequiredMetrics =
        new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["blind_win_rate"] = 50m,
            ["non_inferiority_rate"] = 100m,
            ["adversarial_pass_rate"] = 100m,
            ["unsupported_request_integrity"] = 100m,
            ["latency_efficiency"] = 50m,
            ["cost_efficiency"] = 50m
        };

    internal static LegendArchitecturalTakeoverReadinessSnapshot Evaluate(
        IReadOnlyList<LegendIntelligenceEvaluationDomainDefinition> domains,
        IReadOnlyList<LegendIntelligenceEvaluationSignal> signals)
    {
        var comparative = signals
            .Where(item => item.State == "Current" &&
                item.EvidenceAuthority.StartsWith(EvaluatorAuthorityPrefix, StringComparison.Ordinal))
            .ToArray();
        var baselines = comparative
            .Select(item => item.EvidenceAuthority[EvaluatorAuthorityPrefix.Length..])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var blockers = new List<string>();

        if (baselines.Length != 1)
        {
            blockers.Add(baselines.Length == 0
                ? "No immutable blind SOL baseline has been recorded."
                : "Comparative evidence references more than one baseline identity.");
        }

        var domainWins = 0;
        foreach (var domain in domains)
        {
            var latest = comparative
                .Where(item => item.DomainKey == domain.Key)
                .GroupBy(item => item.MetricKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.MeasuredUtc).First(),
                    StringComparer.Ordinal);
            var domainBlocked = false;
            foreach (var requirement in RequiredMetrics)
            {
                if (!latest.TryGetValue(requirement.Key, out var signal))
                {
                    blockers.Add($"{domain.Key}: missing {requirement.Key}.");
                    domainBlocked = true;
                    continue;
                }

                var passes = requirement.Key is "blind_win_rate" or "latency_efficiency" or "cost_efficiency"
                    ? signal.Value > requirement.Value
                    : signal.Value >= requirement.Value;
                if (!passes)
                {
                    blockers.Add($"{domain.Key}: {requirement.Key} did not pass the locked threshold.");
                    domainBlocked = true;
                }
            }

            if (!domainBlocked)
                domainWins++;
        }

        var proven = baselines.Length == 1 &&
            domainWins == domains.Count &&
            blockers.Count == 0;
        return new(
            proven,
            proven ? "PROVEN" : "BLOCKED",
            baselines.Length == 1 ? baselines[0] : null,
            domainWins,
            domains.Count,
            blockers,
            proven
                ? "LEGEND passed every locked blind comparative gate without a domain regression."
                : "No universal-superiority claim is permitted until every locked blind comparative gate passes.");
    }
}

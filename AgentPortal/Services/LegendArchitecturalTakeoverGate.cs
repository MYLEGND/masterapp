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

    internal const string SuiteReferencePrefix = "blind-suite:";

    private static readonly IReadOnlyDictionary<string, MetricRequirement> RequiredMetrics =
        new Dictionary<string, MetricRequirement>(StringComparer.Ordinal)
        {
            ["sample_size"] = new(100m, true),
            ["blind_win_rate"] = new(50m, false),
            ["blind_win_rate_lower_confidence_bound"] = new(50m, false),
            ["non_inferiority_rate"] = new(100m, true),
            ["adversarial_pass_rate"] = new(100m, true),
            ["unsupported_request_integrity"] = new(100m, true),
            ["prompt_holdout_integrity"] = new(100m, true),
            ["assignment_blinding_integrity"] = new(100m, true),
            ["independent_judge_agreement"] = new(80m, true),
            ["latency_efficiency"] = new(50m, false),
            ["cost_efficiency"] = new(50m, false)
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
        var suiteIdentities = comparative
            .Select(item => TryReadSuiteIdentity(item.EvidenceReference))
            .Where(item => item is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        if (comparative.Any(item => TryReadSuiteIdentity(item.EvidenceReference) is null))
            blockers.Add("Comparative evidence contains a non-immutable suite reference.");

        if (suiteIdentities.Length != 1)
        {
            blockers.Add(suiteIdentities.Length == 0
                ? "No immutable blind-suite identity has been recorded."
                : "Comparative evidence references more than one blind-suite identity.");
        }

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

                var passes = requirement.Value.Inclusive
                    ? signal.Value >= requirement.Value.Threshold
                    : signal.Value > requirement.Value.Threshold;
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
            suiteIdentities.Length == 1 &&
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

    private static string? TryReadSuiteIdentity(string reference)
    {
        if (!reference.StartsWith(SuiteReferencePrefix, StringComparison.Ordinal))
            return null;

        var remainder = reference[SuiteReferencePrefix.Length..];
        var separator = remainder.IndexOf(':');
        var identity = separator < 0 ? remainder : remainder[..separator];
        return identity.Length == 64 &&
            identity.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? identity
                : null;
    }

    private sealed record MetricRequirement(
        decimal Threshold,
        bool Inclusive);
}

using System.Text.Json;
using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

internal sealed record EngineeringReleaseDecision(
    bool Ready, string Code, string Cohort, double WeightedReadyCoverage,
    IReadOnlyList<Guid> WorkItemIds, bool FounderApprovalRequired);

internal sealed class LegendEngineeringReleaseCohortPlanner(
    LegendEngineeringStateStore store,
    IFounderSoftwareRemediationService remediation)
{
    internal async Task<object> ReconcileAndReleaseAsync(CancellationToken cancellationToken)
    {
        var items = await store.GetOpenWorkItemsAsync(500, cancellationToken);
        foreach (var item in items.Where(item => item.State == "REVIEWED" && item.PullRequestNumber is > 0 && LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha)))
            await ReconcileValidationAsync(item, cancellationToken);

        items = await store.GetOpenWorkItemsAsync(500, cancellationToken);
        var decision = Plan(items, DateTime.UtcNow);
        if (!decision.Ready)
            return new { ok = true, released = false, decision.Code, decision.Cohort, decision.WeightedReadyCoverage, decision.WorkItemIds };

        var candidates = items.Where(item => decision.WorkItemIds.Contains(item.WorkItemId)).ToArray();
        if (decision.FounderApprovalRequired)
        {
            foreach (var item in candidates)
                await store.UpdateWorkItemAsync(item with { State = "FOUNDER_RELEASE_APPROVAL_REQUIRED", UpdatedUtc = DateTime.UtcNow }, cancellationToken);
            return new { ok = true, released = false, code = "founder_release_approval_required", decision.Cohort, decision.WeightedReadyCoverage, decision.WorkItemIds };
        }

        // FounderSoftwareRepairBatch is intentionally one durable publication identity.
        // Never attempt to publish unrelated candidate identities as one cohort.
        var identities = candidates.Select(item => new { item.PullRequestNumber, item.CandidateSha }).Distinct().ToArray();
        if (identities.Length != 1 || identities[0].PullRequestNumber is not > 0 || !LegendEngineeringPolicies.IsImmutableSha(identities[0].CandidateSha))
            return new { ok = false, released = false, code = "release_cohort_candidate_identity_not_single", decision.Cohort, decision.WorkItemIds };

        var release = await remediation.ReleaseApprovedAsync(identities[0].PullRequestNumber.Value, identities[0].CandidateSha!, cancellationToken);
        var json = JsonSerializer.SerializeToElement(release);
        var requested = json.TryGetProperty("publicationRequested", out var publication) && publication.ValueKind == JsonValueKind.True;
        foreach (var item in candidates)
            await store.UpdateWorkItemAsync(item with
            {
                State = requested ? "RELEASE_REQUESTED" : "RELEASE_BLOCKED",
                ValidationState = requested ? "GREEN_RELEASE_REQUESTED" : item.ValidationState,
                UpdatedUtc = DateTime.UtcNow
            }, cancellationToken);
        return new { ok = requested, released = false, publicationRequested = requested, decision.Cohort, decision.WeightedReadyCoverage, decision.WorkItemIds, release };
    }

    internal static EngineeringReleaseDecision Plan(IReadOnlyList<EngineeringWorkItemSnapshot> items, DateTime nowUtc)
    {
        var validated = items.Where(item => item.State == "VALIDATED" && item.ValidationState == "GREEN" && item.RiskClass != EngineeringRiskClass.TierC).ToArray();
        if (validated.Length == 0) return new(false, "no_validated_repairs", "NONE", 0, [], false);

        var seed = validated.OrderBy(item => PriorityRank(item.PriorityClass)).ThenByDescending(item => item.PriorityScore).First();
        var compatible = validated.Where(item => Compatible(seed, item)).ToArray();
        var population = items.Where(item => item.FailureClass == EngineeringFailureClass.CodeDefect && item.RiskClass != EngineeringRiskClass.TierC && Compatible(seed, item)).ToArray();
        var readyWeight = compatible.Sum(Weight);
        var totalWeight = Math.Max(readyWeight, population.Sum(Weight));
        var coverage = totalWeight == 0 ? 0 : readyWeight * 100.0 / totalWeight;

        var urgent = compatible.Any(item => item.PriorityClass == "P1");
        var oldest = compatible.Min(item => item.UpdatedUtc);
        var timed = seed.PriorityClass switch
        {
            "P1" => true,
            "P2" => nowUtc - oldest >= TimeSpan.FromHours(4),
            "P3" => nowUtc - oldest >= TimeSpan.FromDays(1),
            _ => nowUtc - oldest >= TimeSpan.FromDays(7)
        };
        var ready = urgent || coverage >= 70.0 || timed;
        var code = urgent ? "p1_immediate" : coverage >= 70.0 ? "weighted_ready_coverage" : timed ? "cohort_time_limit" : "cohort_waiting";
        var approval = compatible.Any(item => item.RiskClass == EngineeringRiskClass.TierB);
        return new(ready, code, seed.ReleaseCohort, coverage, compatible.Select(item => item.WorkItemId).ToArray(), approval);
    }

    private async Task ReconcileValidationAsync(EngineeringWorkItemSnapshot item, CancellationToken cancellationToken)
    {
        var result = await remediation.InspectValidationAsync(item.PullRequestNumber!.Value, item.CandidateSha!, cancellationToken);
        var json = JsonSerializer.SerializeToElement(result);
        var exact = json.TryGetProperty("exactIdentityMatches", out var identity) && identity.ValueKind == JsonValueKind.True;
        var passed = json.TryGetProperty("observedChecksPassed", out var checks) && checks.ValueKind == JsonValueKind.True;
        if (exact && passed)
        {
            await store.UpdateWorkItemAsync(item with { State = "VALIDATED", ValidationState = "GREEN", UpdatedUtc = DateTime.UtcNow }, cancellationToken);
            return;
        }
        var failed = json.TryGetProperty("incompleteOrFailedChecks", out var failures) && failures.ValueKind == JsonValueKind.Array && failures.GetArrayLength() > 0;
        if (failed)
            await store.UpdateWorkItemAsync(item with { State = "CI_FAILED_NEEDS_EVIDENCE", ValidationState = "FAILED", UpdatedUtc = DateTime.UtcNow }, cancellationToken);
    }

    private static bool Compatible(EngineeringWorkItemSnapshot left, EngineeringWorkItemSnapshot right)
    {
        if (left.RiskClass != right.RiskClass) return false;
        if (!left.AffectedApplications.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(right.AffectedApplications.OrderBy(x => x, StringComparer.Ordinal))) return false;
        return left.CanonicalAuthorityKey == right.CanonicalAuthorityKey ||
               LegendEngineeringPolicies.ImpactSetsOverlap(left.ImpactSet, right.ImpactSet);
    }

    private static int Weight(EngineeringWorkItemSnapshot item) => Math.Max(1, item.PriorityScore);
    private static int PriorityRank(string value) => value switch { "P1" => 1, "P2" => 2, "P3" => 3, _ => 4 };
}
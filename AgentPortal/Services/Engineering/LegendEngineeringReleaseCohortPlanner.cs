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
        foreach (var item in items.Where(item => item.State == "RELEASE_REQUESTED"))
            await ReconcileDeploymentAsync(item, cancellationToken);

        items = await store.GetOpenWorkItemsAsync(500, cancellationToken);
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
        var publicationPr = json.TryGetProperty("pullRequestNumber", out var publicationPrValue) &&
                            publicationPrValue.TryGetInt32(out var publicationPrNumber) ? publicationPrNumber : (int?)null;
        var publicationHead = json.TryGetProperty("publishedHeadSha", out var publicationHeadValue) &&
                              publicationHeadValue.ValueKind == JsonValueKind.String ? publicationHeadValue.GetString() : null;
        var batchRevision = json.TryGetProperty("batchRevision", out var batchRevisionValue) &&
                            batchRevisionValue.ValueKind == JsonValueKind.String ? batchRevisionValue.GetString() : null;
        requested = requested && publicationPr is > 0 &&
                    LegendEngineeringPolicies.IsImmutableSha(publicationHead) &&
                    Guid.TryParseExact(batchRevision, "N", out _);
        foreach (var item in candidates)
            await store.UpdateWorkItemAsync(item with
            {
                State = requested ? "RELEASE_REQUESTED" : "RELEASE_BLOCKED",
                ValidationState = requested ? "GREEN_RELEASE_REQUESTED" : item.ValidationState,
                PublicationPullRequestNumber = requested ? publicationPr : item.PublicationPullRequestNumber,
                PublicationHeadSha = requested ? publicationHead : item.PublicationHeadSha,
                PublicationBatchRevision = requested ? batchRevision : item.PublicationBatchRevision,
                UpdatedUtc = DateTime.UtcNow
            }, cancellationToken);
        return new { ok = requested, released = false, publicationRequested = requested, decision.Cohort, decision.WeightedReadyCoverage, decision.WorkItemIds, release };
    }


    private async Task ReconcileDeploymentAsync(
        EngineeringWorkItemSnapshot item,
        CancellationToken cancellationToken)
    {
        if (item.PublicationPullRequestNumber is not > 0 ||
            !LegendEngineeringPolicies.IsImmutableSha(item.PublicationHeadSha) ||
            !Guid.TryParseExact(item.PublicationBatchRevision, "N", out _))
        {
            await store.UpdateWorkItemAsync(item with
            {
                State = "RELEASE_BLOCKED",
                UpdatedUtc = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        var result = await remediation.ArchiveDeployedBatchAsync(
            item.PublicationPullRequestNumber.Value,
            item.PublicationHeadSha!,
            item.PublicationBatchRevision!,
            cancellationToken);
        var json = JsonSerializer.SerializeToElement(result);
        var archived = json.TryGetProperty("archived", out var archivedValue) &&
                       archivedValue.ValueKind == JsonValueKind.True;
        if (archived)
        {
            var mergedSha = json.TryGetProperty("mergedSha", out var mergedValue) &&
                            mergedValue.ValueKind == JsonValueKind.String ? mergedValue.GetString() : null;
            var tree = json.TryGetProperty("deployedTreeSha", out var treeValue) &&
                       treeValue.ValueKind == JsonValueKind.String ? treeValue.GetString() : null;
            var runId = json.TryGetProperty("deploymentRunId", out var runValue) &&
                        runValue.TryGetInt64(out var deploymentRunId) ? deploymentRunId : (long?)null;
            var verifiedUtc = json.TryGetProperty("verificationUtc", out var verifiedValue) &&
                              verifiedValue.TryGetDateTime(out var observedUtc) ? observedUtc : DateTime.UtcNow;
            await store.UpdateWorkItemAsync(item with
            {
                State = "LIVE_FUNCTIONAL_PROOF_REQUIRED",
                ValidationState = "DEPLOYMENT_VERIFIED_FUNCTIONAL_PROOF_PENDING",
                MergedSha = mergedSha,
                DeployedTreeSha = tree,
                DeploymentRunId = runId,
                DeploymentVerifiedUtc = verifiedUtc,
                UpdatedUtc = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // Normal propagation/merge delay remains pending. Only an explicit
        // terminal identity conflict becomes a release blocker.
        var error = json.TryGetProperty("error", out var errorValue) &&
                    errorValue.ValueKind == JsonValueKind.String ? errorValue.GetString() : null;
        if (error is "batch_identity_changed" or "invalid_completion_identity" or
            "merged_tree_differs_from_reviewed_tree" or "publication_not_merged")
        {
            // publication_not_merged is expected while the protected workflow is
            // still running; preserve RELEASE_REQUESTED rather than invent failure.
            if (error != "publication_not_merged")
                await store.UpdateWorkItemAsync(item with
                {
                    State = "RELEASE_BLOCKED",
                    UpdatedUtc = DateTime.UtcNow
                }, cancellationToken);
        }
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
        var approval = compatible.Any(item => item.RiskClass == EngineeringRiskClass.TierB &&
                                              item.FounderReleaseApprovedUtc is null);
        return new(ready, code, seed.ReleaseCohort, coverage, compatible.Select(item => item.WorkItemId).ToArray(), approval);
    }

    private async Task ReconcileValidationAsync(
        EngineeringWorkItemSnapshot item,
        CancellationToken cancellationToken)
    {
        var result = await remediation.InspectValidationAsync(
            item.PullRequestNumber!.Value,
            item.CandidateSha!,
            cancellationToken);
        var json = JsonSerializer.SerializeToElement(result);
        var exact = json.TryGetProperty("exactIdentityMatches", out var identity) &&
                    identity.ValueKind == JsonValueKind.True;
        var passed = json.TryGetProperty("observedChecksPassed", out var checksPassed) &&
                     checksPassed.ValueKind == JsonValueKind.True;
        if (exact && passed)
        {
            await store.UpdateWorkItemAsync(item with
            {
                State = "VALIDATED",
                ValidationState = "GREEN",
                ValidationFailureCodes = null,
                UpdatedUtc = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        var required = ReadStringArray(json, "requiredChecks");
        var missing = ReadStringArray(json, "missingChecks");
        var conclusions = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (json.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                if (!check.TryGetProperty("name", out var nameValue) ||
                    nameValue.ValueKind != JsonValueKind.String ||
                    nameValue.GetString() is not { } name ||
                    string.IsNullOrWhiteSpace(name))
                    continue;
                var conclusion = check.TryGetProperty("conclusion", out var conclusionValue) &&
                                 conclusionValue.ValueKind == JsonValueKind.String
                    ? conclusionValue.GetString()
                    : null;
                conclusions[name] = conclusion;
            }
        }

        // Missing checks or a required check with no terminal conclusion are
        // still running/queued evidence, never a repair failure.
        var pending = missing.Length > 0 ||
                      required.Any(name =>
                          !conclusions.TryGetValue(name, out var conclusion) ||
                          string.IsNullOrWhiteSpace(conclusion));
        if (pending)
        {
            if (item.ValidationState != "PENDING")
                await store.UpdateWorkItemAsync(item with
                {
                    ValidationState = "PENDING",
                    ValidationFailureCodes = null,
                    UpdatedUtc = DateTime.UtcNow
                }, cancellationToken);
            return;
        }

        var terminalFailures = required
            .Where(name => conclusions.TryGetValue(name, out var conclusion) &&
                           !string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase))
            .Select(SafeCheckName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        if (terminalFailures.Length == 0) return;

        await store.UpdateWorkItemAsync(item with
        {
            State = "CI_FAILED_NEEDS_EVIDENCE",
            ValidationState = "FAILED",
            ValidationFailureCodes = terminalFailures,
            AssignedRole = EngineeringRole.HeadGpt,
            ModelTier = EngineeringModelTier.DeepReasoning,
            LeaseOwner = null,
            LeaseIdentity = null,
            LeaseExpiresUtc = null,
            UpdatedUtc = DateTime.UtcNow
        }, cancellationToken);
    }

    private static string[] ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static string SafeCheckName(string value)
    {
        var text = value.Trim();
        if (text.Length > 120) text = text[..120];
        return new string(text.Select(character =>
            char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.' or '/' or ':'
                ? character
                : '_').ToArray());
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
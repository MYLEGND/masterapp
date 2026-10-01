using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPortal.Security;
using Domain.Engineering;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Diagnostics;

namespace AgentPortal.Services.Engineering;

internal interface ILegendEngineeringOrchestrator
{
    Task<object> GetStatusAsync(CancellationToken cancellationToken);
    Task<object> ProcessIncidentsAsync(int maximum, CancellationToken cancellationToken);
    Task<EngineeringContextSnapshot> BootstrapAsync(ClaimsPrincipal founder, Guid workItemId, string role, CancellationToken cancellationToken);
    Task<EngineeringContextSnapshot> BootstrapSystemAsync(Guid workItemId, string role, CancellationToken cancellationToken);
    Task<EngineeringTaskPacket> GetTaskPacketAsync(Guid engineeringContextId, CancellationToken cancellationToken);
    Task<object> InspectRepositoryAsync(Guid engineeringContextId, string path, string revision, CancellationToken cancellationToken);
    Task<object> PrepareRepairAsync(Guid engineeringContextId, FounderSoftwareRepairProposal proposal, CancellationToken cancellationToken);
}

internal sealed class LegendEngineeringOrchestrator(
    MasterAppDbContext db,
    LegendEngineeringStateStore store,
    LegendEngineeringBudgetAuthority budget,
    IFounderSoftwareRemediationService remediation,
    IConfiguration configuration) : ILegendEngineeringOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var work = await store.GetOpenWorkItemsAsync(100, cancellationToken);
        var envelope = await budget.GetEnvelopeAsync(cancellationToken);
        return new
        {
            ok = true,
            schemaVersion = 1,
            authority = nameof(LegendEngineeringOrchestrator),
            contractRevision = LegendEngineeringContract.ContractRevision,
            policyRevision = LegendEngineeringContract.PolicyRevision,
            founderOnly = true,
            openWorkItems = work.Count,
            leasedWorkItems = work.Count(item => item.LeaseExpiresUtc > DateTime.UtcNow),
            securityReviewItems = work.Count(item => item.RiskClass == EngineeringRiskClass.TierC),
            observationOnlyItems = work.Count(item => item.State == "OBSERVATION_ONLY"),
            codeRepairEligibleItems = work.Count(item => item.FailureClass == EngineeringFailureClass.CodeDefect &&
                                                        item.RiskClass != EngineeringRiskClass.TierC),
            priorities = work.GroupBy(item => item.PriorityClass).ToDictionary(group => group.Key, group => group.Count()),
            budget = envelope,
            items = work.Take(25).Select(item => new
            {
                item.WorkItemId,
                item.CanonicalAuthorityKey,
                item.AffectedApplications,
                item.LiveSha,
                item.FailureClass,
                item.RiskClass,
                item.ComplexityScore,
                item.PriorityScore,
                item.PriorityClass,
                item.State,
                item.AssignedRole,
                item.ModelTier,
                item.ValidationState,
                item.ReleaseCohort,
                item.UpdatedUtc
            })
        };
    }

    public async Task<object> ProcessIncidentsAsync(int maximum, CancellationToken cancellationToken)
    {
        maximum = Math.Clamp(maximum, 1, 500);
        var now = DateTime.UtcNow;
        var incidents = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .Where(row => row.ExpiresUtc > now)
            .OrderByDescending(row => row.LastSeenUtc)
            .ThenBy(row => row.Id)
            .Take(maximum)
            .ToArrayAsync(cancellationToken);

        var touched = new Dictionary<Guid, EngineeringWorkItemSnapshot>();
        var deterministic = 0;
        foreach (var incident in incidents)
        {
            var decision = LegendEngineeringPolicies.Classify(incident);
            if (decision.AssignedRole == EngineeringRole.Sentinel) deterministic++;
            var item = await store.AttachIncidentAsync(incident, decision, cancellationToken);
            touched[item.WorkItemId] = item;
        }

        return new
        {
            ok = true,
            observedIncidents = incidents.Length,
            deterministicClassifications = deterministic,
            distinctWorkItems = touched.Count,
            workItems = touched.Values
                .OrderByDescending(item => item.PriorityScore)
                .Select(item => new
                {
                    item.WorkItemId,
                    item.FailureClass,
                    item.RiskClass,
                    item.PriorityClass,
                    item.State,
                    item.AssignedRole,
                    item.ReleaseCohort
                })
        };
    }

    public Task<EngineeringContextSnapshot> BootstrapAsync(
        ClaimsPrincipal founder,
        Guid workItemId,
        string role,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(founder);
        return BootstrapCoreAsync(workItemId, role, cancellationToken);
    }

    public Task<EngineeringContextSnapshot> BootstrapSystemAsync(
        Guid workItemId,
        string role,
        CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool?>("LegendEngineering:Autonomous:Enabled") != true)
            throw new InvalidOperationException("autonomous_engineering_disabled");
        if (!Guid.TryParse(AgentPortal.Security.FounderGuard.FounderOid, out _))
            throw new InvalidOperationException("founder_identity_not_configured");
        return BootstrapCoreAsync(workItemId, role, cancellationToken);
    }

    public async Task<EngineeringTaskPacket> GetTaskPacketAsync(
        Guid engineeringContextId,
        CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            throw new InvalidOperationException(validation.Code);

        var context = validation.Context;
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken)
            ?? throw new InvalidOperationException("work_item_not_found");
        var incidents = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .Where(row => item.IncidentIds.Contains(row.Id))
            .OrderByDescending(row => row.LastSeenUtc)
            .ToArrayAsync(cancellationToken);
        if (incidents.Length == 0)
            throw new InvalidOperationException("work_item_incident_evidence_missing");

        var primary = incidents[0];
        var safeHints = incidents.Select(row => row.SourceFilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           FounderSoftwareRemediationService.ClassifyInspectableSourcePath(path!) == LegendSiteToolDisclosureAuthority.SafeSource)
            .Select(path => NormalizePath(path!))
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        var sourceResolution = safeHints.Length == 0
            ? new FounderRepositorySourceResolution(false, "repository_source_hints_missing", Array.Empty<string>(), false)
            : await remediation.ResolveRepositorySourcePathsAsync(
                safeHints,
                item.AffectedApplications.FirstOrDefault(),
                item.LiveSha,
                cancellationToken);
        var permitted = sourceResolution.Ready
            ? sourceResolution.Paths.OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var protectedPaths = incidents.Select(row => row.SourceFilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           FounderSoftwareRemediationService.ClassifyInspectableSourcePath(path!) != LegendSiteToolDisclosureAuthority.SafeSource)
            .Select(path => "protected:" + Hash(NormalizePath(path!))[..20])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var issueCodes = incidents.Select(row => SafeIssueCode(row.ErrorName))
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();

        return new EngineeringTaskPacket(
            "legend_engineering_task_packet.v1",
            item.WorkItemId,
            item.AffectedApplications.FirstOrDefault() ?? "unknown",
            item.LiveSha,
            SafeRoute(primary.Route),
            item.CanonicalAuthorityKey,
            [],
            [],
            [],
            item.FailureClass,
            item.Severity,
            item.RiskClass,
            item.ComplexityScore,
            "The canonical route completes without the sanitized runtime incident and preserves existing authorities.",
            string.Join(",", issueCodes.Length == 0 ? ["sanitized_runtime_incident"] : issueCodes),
            issueCodes,
            permitted,
            protectedPaths,
            item.AffectedProjects,
            item.AffectedApplications,
            FocusedTests(item),
            "The canonical root cause is removed, focused regression proof passes, required current-head validation is green or safely reused, exact affected targets deploy, and the original reproducer passes live.",
            [
                "Run the smallest focused reproducer first.",
                "Preserve compatible successful validation evidence.",
                "Rerun only failed or invalidated dependency chains.",
                "Never bypass a required current-head gate."
            ],
            item.RiskClass == EngineeringRiskClass.TierA
                ? "Tier A may proceed through the existing canonical release authority when every release precondition is satisfied."
                : item.RiskClass == EngineeringRiskClass.TierB
                    ? "Tier B must stop before consequential release for Founder approval."
                    : "Tier C is observation/security review only and may not mutate source.",
            [
                "Verify exact deployed revision/tree through canonical runtime provenance.",
                "Run the original structural/page reproducer when the defect is user-interface visible.",
                "Do not close the work item merely because CI or deployment succeeded."
            ]);
    }

    public async Task<object> InspectRepositoryAsync(
        Guid engineeringContextId,
        string path,
        string revision,
        CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            return new { ok = false, error = validation.Code };

        var context = validation.Context;
        if (!context.AllowedTools.Contains("legend_inspect_repository", StringComparer.Ordinal))
            return new { ok = false, error = "engineering_context_tool_not_allowed" };

        var disclosure = FounderSoftwareRemediationService.ClassifyInspectableSourcePath(path);
        if (disclosure != LegendSiteToolDisclosureAuthority.SafeSource ||
            !context.AllowedSourceClasses.Contains(LegendSiteToolDisclosureAuthority.SafeSource, StringComparer.Ordinal))
            return new
            {
                ok = false,
                error = "engineering_source_not_readable",
                disclosureClass = disclosure ?? "NOT_ALLOWED",
                readable = false
            };

        var workItem = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (workItem is null) return new { ok = false, error = "work_item_not_found" };
        var gitReference = string.Equals(revision, "candidate", StringComparison.Ordinal)
            ? workItem.CandidateSha
            : string.Equals(revision, "live", StringComparison.Ordinal) ? context.LiveSha : null;
        if (!LegendEngineeringPolicies.IsImmutableSha(gitReference))
            return new { ok = false, error = "engineering_repository_revision_not_allowed" };
        if (string.Equals(revision, "candidate", StringComparison.Ordinal) &&
            context.Role != EngineeringRole.IndependentReviewer &&
            context.Role != EngineeringRole.CodexImplementer)
            return new { ok = false, error = "candidate_source_not_allowed_for_role" };

        return await remediation.InspectRepositoryAsync(path, gitReference, cancellationToken);
    }

    public async Task<object> PrepareRepairAsync(
        Guid engineeringContextId,
        FounderSoftwareRepairProposal proposal,
        CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            return new { ok = false, error = validation.Code };

        var context = validation.Context;
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return new { ok = false, error = "work_item_not_found" };
        if (context.Role != EngineeringRole.CodexImplementer ||
            !context.AllowedTools.Contains("legend_prepare_software_repair", StringComparer.Ordinal))
            return new { ok = false, error = "engineering_context_role_cannot_modify_source" };
        if (item.FailureClass != EngineeringFailureClass.CodeDefect)
            return new { ok = false, error = "failure_class_does_not_permit_source_repair" };
        if (item.RiskClass == EngineeringRiskClass.TierC)
            return new { ok = false, error = "tier_c_source_mutation_forbidden" };
        if (!string.Equals(proposal.BaseSha, context.LiveSha, StringComparison.OrdinalIgnoreCase))
            return new { ok = false, error = "repair_base_sha_stale" };
        if (proposal.Changes.Any(change =>
                FounderSoftwareRemediationService.ClassifyInspectableSourcePath(change.Path) != LegendSiteToolDisclosureAuthority.SafeSource))
            return new { ok = false, error = "repair_contains_protected_source" };

        var budgetEnvelope = await budget.GetEnvelopeAsync(cancellationToken);
        if (!budget.Permits(budgetEnvelope, item))
            return new { ok = false, error = "engineering_budget_blocks_new_ai_repair", budget = budgetEnvelope };

        var result = await remediation.PrepareAsync("engineering_control_plane", proposal, cancellationToken);
        var element = JsonSerializer.SerializeToElement(result, JsonOptions);
        var prepared = element.TryGetProperty("prepared", out var preparedValue) && preparedValue.ValueKind == JsonValueKind.True;
        if (!prepared) return result;

        var candidateSha = element.TryGetProperty("repairCommitSha", out var sha) ? sha.GetString() : null;
        var pullRequest = element.TryGetProperty("pullRequestNumber", out var pr) && pr.TryGetInt32(out var number) ? number : (int?)null;
        var updated = item with
        {
            State = "CANDIDATE_PREPARED",
            CandidateSha = candidateSha,
            CandidateChangedPaths = proposal.Changes.Select(change => NormalizePath(change.Path)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            PullRequestNumber = pullRequest,
            RepairBatchId = "active",
            AttemptCount = item.AttemptCount + 1,
            ValidationState = "PENDING",
            UpdatedUtc = DateTime.UtcNow
        };
        await store.UpdateWorkItemAsync(updated, cancellationToken);
        return result;
    }

    private async Task<EngineeringContextSnapshot> BootstrapCoreAsync(
        Guid workItemId,
        string requestedRole,
        CancellationToken cancellationToken)
    {
        var item = await store.GetWorkItemAsync(workItemId, cancellationToken)
            ?? throw new InvalidOperationException("work_item_not_found");
        if (!LegendEngineeringPolicies.IsImmutableSha(item.LiveSha))
            throw new InvalidOperationException("live_sha_unverified");

        if (!AllowedRoleForState(item, requestedRole))
            throw new InvalidOperationException("forged_or_stale_engineering_role");
        if (item.RiskClass == EngineeringRiskClass.TierC)
            throw new InvalidOperationException("tier_c_engineering_context_denied");
        if (item.State is "OBSERVATION_ONLY" or "COMPLETED" or "CLOSED")
            throw new InvalidOperationException("work_item_not_actionable");

        var envelope = await budget.GetEnvelopeAsync(cancellationToken);
        if (!budget.Permits(envelope, item))
            throw new InvalidOperationException("engineering_budget_does_not_permit_ai_work");

        var attemptLimit = requestedRole == EngineeringRole.CodexImplementer
            ? envelope.MaxCodexAttempts
            : envelope.MaxDeepReasoningEscalations;
        var observedAttempts = await store.CountModelAttemptsAsync(workItemId, requestedRole, cancellationToken);
        if (observedAttempts >= attemptLimit)
            throw new InvalidOperationException("engineering_attempt_limit_reached");

        var owner = $"engineering:{requestedRole.ToLowerInvariant()}:{workItemId:N}";
        var lease = await store.TryAcquireLeaseAsync(workItemId, owner, TimeSpan.FromMinutes(15), cancellationToken);
        if (!lease.Acquired || string.IsNullOrWhiteSpace(lease.LeaseIdentity) || lease.LeaseExpiresUtc is null)
            throw new InvalidOperationException(lease.Code);

        var now = DateTime.UtcNow;
        var expires = lease.LeaseExpiresUtc.Value < now.AddMinutes(15) ? lease.LeaseExpiresUtc.Value : now.AddMinutes(15);
        var context = new EngineeringContextSnapshot(
            Guid.NewGuid(),
            LegendEngineeringContract.ContractRevision,
            LegendEngineeringContract.PolicyRevision,
            item.WorkItemId,
            requestedRole,
            item.FailureClass,
            item.RiskClass,
            item.ComplexityScore,
            item.LiveSha,
            item.EvidenceRevision,
            AllowedTools(requestedRole),
            [LegendSiteToolDisclosureAuthority.SafeSource],
            [
                "secret_values", "credentials", "authentication_material", "private_customer_data",
                "raw_production_payloads", "privacy_protected_source_bodies",
                "integrity_protected_source_bodies", "approved_branch_direct_edits", "production_branch_direct_edits"
            ],
            envelope,
            attemptLimit,
            [
                "insufficient_evidence", "stale_live_sha", "lease_conflict", "protected_source_requirement",
                "privacy_boundary", "security_boundary", "risk_tier_requires_founder", "attempt_limit_reached",
                "budget_limit_reached", "ci_unresolved", "deployment_uncertain", "live_verification_failed"
            ],
            lease.LeaseIdentity,
            now,
            expires);

        return await store.SaveContextAsync(context, cancellationToken);
    }

    private static bool AllowedRoleForState(EngineeringWorkItemSnapshot item, string role)
    {
        if (!string.Equals(item.AssignedRole, role, StringComparison.Ordinal)) return false;
        if (role == EngineeringRole.CodexImplementer)
            return item.FailureClass == EngineeringFailureClass.CodeDefect && item.RiskClass != EngineeringRiskClass.TierC;
        if (role == EngineeringRole.HeadGpt)
            return item.State is "NEEDS_SUPERVISOR" or "RECURRED_NEEDS_SUPERVISOR" or "QUEUED" or "REVIEW_REJECTED";
        return role is EngineeringRole.IndependentReviewer or EngineeringRole.LiveVerifier;
    }

    private static IReadOnlyList<string> AllowedTools(string role) => role switch
    {
        EngineeringRole.CodexImplementer =>
            ["legend_inspect_repository", "legend_prepare_software_repair"],
        EngineeringRole.HeadGpt =>
            ["legend_inspect_repository"],
        EngineeringRole.IndependentReviewer =>
            ["legend_inspect_repository"],
        EngineeringRole.LiveVerifier =>
            ["legend_verify_repair_deployment", "legend_verify_current_page_repair"],
        _ => []
    };

    private static IReadOnlyList<string> FocusedTests(EngineeringWorkItemSnapshot item)
    {
        var tests = new List<string>();
        if (item.AffectedProjects.Contains("AgentPortal", StringComparer.Ordinal)) tests.Add("AgentPortal.Tests focused regression");
        if (item.AffectedProjects.Contains("ClientApp", StringComparer.Ordinal)) tests.Add("ClientApp focused build/tests");
        if (item.AffectedProjects.Contains("Infrastructure", StringComparer.Ordinal) ||
            item.AffectedProjects.Contains("SHARED", StringComparer.Ordinal))
            tests.Add("shared architecture + affected application regression");
        if (tests.Count == 0) tests.Add("affected project focused regression");
        return tests;
    }

    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    private static string SafeRoute(string? route)
    {
        var value = (route ?? "/unknown").Trim();
        if (!value.StartsWith('/')) value = "/" + value;
        return value.Length <= 256 ? value : value[..256];
    }

    private static string SafeIssueCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        if (text.Length > 96) text = text[..96];
        return new string(text.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_').ToArray());
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

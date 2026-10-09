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
    Task<object> CompleteTurnAsync(Guid engineeringContextId, string responseId, JsonElement output, CancellationToken cancellationToken);
    Task<object> RenewTurnAsync(Guid engineeringContextId, CancellationToken cancellationToken);
    Task<object> ApproveReleaseAsync(ClaimsPrincipal founder, Guid workItemId, CancellationToken cancellationToken);
    Task<object> DeclineReleaseAsync(ClaimsPrincipal founder, Guid workItemId, CancellationToken cancellationToken);
    Task RecordBrowserFunctionalProofAsync(
        Guid workItemId,
        string application,
        string expectedRevision,
        string expectedRoute,
        IReadOnlyList<string> componentIds,
        IReadOnlyList<string> actionKeys,
        IReadOnlyList<string> compositionIds,
        IReadOnlyList<string> modalIds,
        IReadOnlyList<string> forbiddenErrorNames,
        CancellationToken cancellationToken);
}

internal sealed class LegendEngineeringOrchestrator(
    MasterAppDbContext db,
    LegendEngineeringStateStore store,
    LegendEngineeringBudgetAuthority budget,
    IFounderSoftwareRemediationService remediation,
    ILegendEngineeringContractAuthority contractAuthority,
    IConfiguration configuration) : ILegendEngineeringOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var work = await store.GetOpenWorkItemsAsync(100, cancellationToken);
        var envelope = await budget.GetEnvelopeAsync(cancellationToken);
        var operationalContract = await contractAuthority.GetCurrentAsync(cancellationToken);
        return new
        {
            ok = true,
            schemaVersion = 1,
            authority = nameof(LegendEngineeringOrchestrator),
            contractRevision = LegendEngineeringContract.ContractRevision,
            policyRevision = LegendEngineeringContract.PolicyRevision,
            operationalContractRevision = operationalContract.Revision,
            modelExecutionEnabled = operationalContract.ModelExecutionEnabled,
            autonomousEngineeringEnabled = operationalContract.AutonomousEngineeringEnabled,
            founderOnly = true,
            workOrchestration = new
            {
                enabled = operationalContract.ModelExecutionEnabled,
                transport = "authenticated_founder_site_tools",
                connectionState = "REQUIRES_BROWSER_TOOL_VERIFICATION",
                requiresNativeClientRegistration = false,
                completionAuthority = nameof(LegendEngineeringOrchestrator)
            },
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
                item.UpdatedUtc,
                liveProof = item.State == "LIVE_FUNCTIONAL_PROOF_REQUIRED" &&
                            item.ReproducerRoute is not null &&
                            LegendEngineeringPolicies.IsImmutableSha(item.MergedSha)
                    ? new
                    {
                        engineeringWorkItemId = item.WorkItemId,
                        expectedRevision = item.MergedSha,
                        expectedRoute = item.ReproducerRoute,
                        requiredComponentIds = item.ReproducerComponentIds ?? Array.Empty<string>(),
                        requiredActionKeys = item.ReproducerActionKeys ?? Array.Empty<string>(),
                        requiredCompositionIds = item.ReproducerCompositionIds ?? Array.Empty<string>(),
                        requiredModalIds = item.ReproducerModalIds ?? Array.Empty<string>(),
                        forbiddenErrorNames = item.ReproducerForbiddenErrorNames ?? Array.Empty<string>()
                    }
                    : null
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
        return BootstrapCoreAsync(workItemId, role, "work", cancellationToken);
    }

    public async Task<EngineeringContextSnapshot> BootstrapSystemAsync(
        Guid workItemId,
        string role,
        CancellationToken cancellationToken)
    {
        var operationalContract = await contractAuthority.GetCurrentAsync(cancellationToken);
        if (!operationalContract.AutonomousEngineeringEnabled)
            throw new InvalidOperationException("autonomous_engineering_disabled");
        if (!operationalContract.ModelExecutionEnabled)
            throw new InvalidOperationException("engineering_operational_execution_paused");
        if (!Guid.TryParse(AgentPortal.Security.FounderGuard.FounderOid, out _))
            throw new InvalidOperationException("founder_identity_not_configured");
        if (!LegendEngineeringPolicies.IsApprovedAutonomousBaseBranch(
                configuration["FounderSoftwareRemediation:BaseBranch"]))
            throw new InvalidOperationException("autonomous_engineering_approved_base_required");
        return await BootstrapCoreAsync(workItemId, role, "native", cancellationToken);
    }

    public async Task<EngineeringTaskPacket> GetTaskPacketAsync(
        Guid engineeringContextId,
        CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null)
            throw new InvalidOperationException(validation.Code);

        var context = validation.Context;
        var operational = await contractAuthority.ValidateBindingAsync(
            context.OperationalContractRevision, cancellationToken);
        if (!operational.Valid)
            throw new InvalidOperationException(operational.Code);
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken)
            ?? throw new InvalidOperationException("work_item_not_found");
        var incidents = await db.RuntimeDiagnosticIncidents.AsNoTracking()
            .Where(row => item.IncidentIds.Contains(row.Id))
            .OrderByDescending(row => row.LastSeenUtc)
            .ToArrayAsync(cancellationToken);
        if (incidents.Length == 0)
            throw new InvalidOperationException("work_item_incident_evidence_missing");

        var primary = incidents[0];
        var safeHints = incidents
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceFilePath) &&
                          (FounderSoftwareRemediationService.ClassifyInspectableSourcePath(row.SourceFilePath!) == LegendSiteToolDisclosureAuthority.SafeSource ||
                           LegendEngineeringPolicies.IsSanitizedRuntimeSourceHint(row)))
            .Select(row => NormalizePath(row.SourceFilePath!))
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
        var protectedPaths = incidents
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceFilePath) &&
                          FounderSoftwareRemediationService.ClassifyInspectableSourcePath(row.SourceFilePath!) != LegendSiteToolDisclosureAuthority.SafeSource &&
                          !LegendEngineeringPolicies.IsSanitizedRuntimeSourceHint(row))
            .Select(row => "protected:" + Hash(NormalizePath(row.SourceFilePath!))[..20])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var issueCodes = incidents.Select(row => SafeIssueCode(row.ErrorName))
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();
        var repairBaseSha = LegendEngineeringPolicies.ResolveRepairBaseSha(item);
        var validationFailures = item.ValidationFailureCodes ?? Array.Empty<string>();
        if (incidents.Any(row => string.Equals(row.Platform, "Web", StringComparison.Ordinal)) &&
            string.IsNullOrWhiteSpace(item.ReproducerRoute))
            throw new InvalidOperationException("browser_reproducer_evidence_missing");

        return new EngineeringTaskPacket(
            "legend_engineering_task_packet.v1",
            item.WorkItemId,
            item.AffectedApplications.FirstOrDefault() ?? "unknown",
            item.LiveSha,
            item.ReproducerRoute ?? SafeRoute(primary.Route),
            item.CanonicalAuthorityKey,
            item.ReproducerComponentIds ?? Array.Empty<string>(),
            item.ReproducerActionKeys ?? Array.Empty<string>(),
            item.ReproducerCompositionIds ?? Array.Empty<string>(),
            item.ReproducerModalIds ?? Array.Empty<string>(),
            item.FailureClass,
            item.Severity,
            item.RiskClass,
            item.ComplexityScore,
            "The canonical route completes without the sanitized runtime incident and preserves existing authorities.",
            string.Join(",", issueCodes.Length == 0 ? ["sanitized_runtime_incident"] : issueCodes),
            item.ReproducerForbiddenErrorNames is { Count: > 0 } ? item.ReproducerForbiddenErrorNames : issueCodes,
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
            ],
            repairBaseSha,
            validationFailures);
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
        var operational = await contractAuthority.ValidateBindingAsync(
            context.OperationalContractRevision, cancellationToken);
        if (!operational.Valid)
            return new { ok = false, error = operational.Code };
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
            context.Role != EngineeringRole.CodexImplementer &&
            !(context.Role == EngineeringRole.HeadGpt &&
              workItem.ValidationFailureCodes is { Count: > 0 }))
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
        var operational = await contractAuthority.ValidateBindingAsync(
            context.OperationalContractRevision, cancellationToken);
        if (!operational.Valid)
            return new { ok = false, error = operational.Code };
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return new { ok = false, error = "work_item_not_found" };
        if (context.Role != EngineeringRole.CodexImplementer ||
            !context.AllowedTools.Contains("legend_prepare_software_repair", StringComparer.Ordinal))
            return new { ok = false, error = "engineering_context_role_cannot_modify_source" };
        if (item.FailureClass != EngineeringFailureClass.CodeDefect)
            return new { ok = false, error = "failure_class_does_not_permit_source_repair" };
        if (item.RiskClass == EngineeringRiskClass.TierC)
            return new { ok = false, error = "tier_c_source_mutation_forbidden" };
        var expectedRepairBase = LegendEngineeringPolicies.ResolveRepairBaseSha(item);
        if (!string.Equals(proposal.BaseSha, expectedRepairBase, StringComparison.OrdinalIgnoreCase))
            return new { ok = false, error = "repair_base_sha_stale", expectedRepairBase };
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
            CandidateChangedPaths = (item.CandidateChangedPaths ?? Array.Empty<string>())
                .Concat(proposal.Changes.Select(change => NormalizePath(change.Path)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            PullRequestNumber = pullRequest,
            RepairBatchId = "active",
            AttemptCount = item.AttemptCount + 1,
            ValidationState = "PENDING",
            ValidationFailureCodes = null,
            UpdatedUtc = DateTime.UtcNow
        };
        await store.UpdateWorkItemAsync(updated, cancellationToken);
        return result;
    }


    public async Task<object> RenewTurnAsync(Guid engineeringContextId, CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null) return Failure(validation.Code);
        var context = validation.Context;
        var binding = await contractAuthority.ValidateBindingAsync(context.OperationalContractRevision, cancellationToken);
        if (!binding.Valid) return Failure(binding.Code);
        var renewed = await store.RenewLeaseAsync(context.WorkItemId, context.LeaseIdentity,
            TimeSpan.FromMinutes(5), context.ExpiresUtc, cancellationToken);
        return new { ok = renewed, context.ExpiresUtc, error = renewed ? null : "engineering_lease_changed" };
    }

    public async Task<object> CompleteTurnAsync(
        Guid engineeringContextId, string responseId, JsonElement output, CancellationToken cancellationToken)
    {
        var validation = await store.ValidateContextAsync(engineeringContextId, cancellationToken);
        if (!validation.Valid || validation.Context is null) return Failure(validation.Code);
        var context = validation.Context;
        var binding = await contractAuthority.ValidateBindingAsync(context.OperationalContractRevision, cancellationToken);
        if (!binding.Valid) return Failure(binding.Code);
        var item = await store.GetWorkItemAsync(context.WorkItemId, cancellationToken);
        if (item is null) return Failure("work_item_not_found");
        if (item.AssignedRole != context.Role) return Failure("forged_or_stale_engineering_role");
        if (output.ValueKind != JsonValueKind.Object ||
            !output.TryGetProperty("decision", out var decisionElement) || decisionElement.ValueKind != JsonValueKind.String)
            return Failure("engineering_role_output_invalid");
        var decision = decisionElement.GetString();
        var allowed = context.Role switch
        {
            EngineeringRole.TriageWorker => decision is "ESCALATE_TO_HEAD_GPT" or "STOP" or "ESCALATE",
            EngineeringRole.HeadGpt => decision is "PROCEED_TO_CODEX" or "STOP" or "ESCALATE",
            EngineeringRole.CodexImplementer => decision is "REPAIR" or "REPAIR_PREPARED" or "STOP" or "ESCALATE",
            EngineeringRole.IndependentReviewer => decision is "APPROVE_VALIDATION" or "REJECT" or "ESCALATE",
            _ => false
        };
        if (!allowed) return Failure("engineering_role_decision_invalid");
        if (decision == "PROCEED_TO_CODEX" &&
            (!output.TryGetProperty("evidence_sufficient", out var sufficient) || sufficient.ValueKind != JsonValueKind.True))
            return Failure("engineering_evidence_insufficient");
        if (context.Role == EngineeringRole.IndependentReviewer &&
            (!LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha) || item.PullRequestNumber is not > 0))
            return Failure("engineering_review_candidate_missing");
        if (responseId.StartsWith("work:", StringComparison.Ordinal))
            await store.RecordUsageAsync(new EngineeringUsageObservation(
                context.EngineeringContextId, item.WorkItemId, item.ModelTier, context.Role,
                "CHATGPT_WORK", responseId, null, null, null, null, false, DateTime.UtcNow,
                ProviderAttempted: true, LogicalAttemptCompleted: true,
                ProviderOutcome: "ROLE_OUTPUT_RECEIVED"), cancellationToken);
        return await ApplyOutcomeAsync(context, item, responseId, output, cancellationToken);
    }

    private async Task<object> ApplyOutcomeAsync(
        EngineeringContextSnapshot context,
        EngineeringWorkItemSnapshot item,
        string responseId,
        JsonElement output,
        CancellationToken cancellationToken)
    {
        var decision = output.GetProperty("decision").GetString();
        if (context.Role == EngineeringRole.TriageWorker)
        {
            var next = decision == "ESCALATE_TO_HEAD_GPT"
                ? ClearLease(item) with
                {
                    State = "NEEDS_SUPERVISOR",
                    AssignedRole = EngineeringRole.HeadGpt,
                    ModelTier = EngineeringModelTier.DeepReasoning,
                    UpdatedUtc = DateTime.UtcNow
                }
                : ClearLease(item) with
                {
                    State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION",
                    UpdatedUtc = DateTime.UtcNow
                };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        if (context.Role == EngineeringRole.HeadGpt)
        {
            var next =
                decision == "PROCEED_TO_CODEX" &&
                item.FailureClass == EngineeringFailureClass.CodeDefect &&
                item.RiskClass != EngineeringRiskClass.TierC
                    ? ClearLease(item) with
                    {
                        State = "QUEUED",
                        AssignedRole = EngineeringRole.CodexImplementer,
                        ModelTier = EngineeringModelTier.CodeImplementation,
                        UpdatedUtc = DateTime.UtcNow
                    }
                    : ClearLease(item) with
                    {
                        State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION",
                        UpdatedUtc = DateTime.UtcNow
                    };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        if (context.Role == EngineeringRole.CodexImplementer)
        {
            if (decision == "REPAIR_PREPARED")
            {
                var preparedItem =
                    await store.GetWorkItemAsync(item.WorkItemId, cancellationToken) ?? item;
                if ((preparedItem.State != "CANDIDATE_PREPARED" &&
                     !(preparedItem.State == "LEASED" && preparedItem.ResumeState == "CANDIDATE_PREPARED")) ||
                    !LegendEngineeringPolicies.IsImmutableSha(preparedItem.CandidateSha) ||
                    preparedItem.PullRequestNumber is not > 0)
                    return Failure("engineering_tool_repair_not_prepared");

                var preparedNext = ClearLease(preparedItem) with
                {
                    State = "REVIEW_REQUIRED",
                    AssignedRole = EngineeringRole.IndependentReviewer,
                    ModelTier = EngineeringModelTier.IndependentReview,
                    AgentSessionId = responseId,
                    AgentContextId = context.EngineeringContextId,
                    AgentSessionRole = context.Role,
                    AgentSessionUpdatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                await store.UpdateWorkItemAsync(preparedNext, cancellationToken);
                return Outcome(context, responseId, preparedNext.State);
            }

            if (decision != "REPAIR")
            {
                var stopped = ClearLease(item) with
                {
                    State = decision == "STOP" ? "STOPPED" : "FOUNDER_ESCALATION",
                    UpdatedUtc = DateTime.UtcNow
                };
                await store.UpdateWorkItemAsync(stopped, cancellationToken);
                return Outcome(context, responseId, stopped.State);
            }

            var proposal = ParseProposal(
                output,
                LegendEngineeringPolicies.ResolveRepairBaseSha(item));
            if (proposal is null)
                return Failure("codex_repair_proposal_invalid");

            var result = await PrepareRepairAsync(
                context.EngineeringContextId,
                proposal,
                cancellationToken);
            var prepared = JsonSerializer.SerializeToElement(result, JsonOptions);
            if (!prepared.TryGetProperty("prepared", out var value) ||
                value.ValueKind != JsonValueKind.True)
                return result;

            var current =
                await store.GetWorkItemAsync(item.WorkItemId, cancellationToken) ?? item;
            var next = ClearLease(current) with
            {
                State = "REVIEW_REQUIRED",
                AssignedRole = EngineeringRole.IndependentReviewer,
                ModelTier = EngineeringModelTier.IndependentReview,
                AgentSessionId = responseId,
                AgentContextId = context.EngineeringContextId,
                AgentSessionRole = context.Role,
                AgentSessionUpdatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        if (context.Role == EngineeringRole.IndependentReviewer)
        {
            var next = decision switch
            {
                "APPROVE_VALIDATION" => ClearLease(item) with
                {
                    State = "REVIEWED",
                    ValidationState = "READY_FOR_CI",
                    UpdatedUtc = DateTime.UtcNow
                },
                "REJECT" => ClearLease(item) with
                {
                    State = "REVIEW_REJECTED",
                    AssignedRole = EngineeringRole.HeadGpt,
                    ModelTier = EngineeringModelTier.DeepReasoning,
                    UpdatedUtc = DateTime.UtcNow
                },
                _ => ClearLease(item) with
                {
                    State = "FOUNDER_ESCALATION",
                    UpdatedUtc = DateTime.UtcNow
                }
            };
            await store.UpdateWorkItemAsync(next, cancellationToken);
            return Outcome(context, responseId, next.State);
        }

        return Failure("engineering_role_not_supported_by_plan_adapter");
    }

    private static EngineeringWorkItemSnapshot ClearLease(
        EngineeringWorkItemSnapshot item) =>
        item with
        {
            LeaseOwner = null,
            LeaseIdentity = null,
            LeaseExpiresUtc = null
        };

    internal static FounderSoftwareRepairProposal? ParseProposal(
        JsonElement output,
        string expectedBaseSha)
    {
        var baseSha = ReadOutcomeString(output, "base_sha") ?? expectedBaseSha;
        var title = ReadOutcomeString(output, "title");
        var summary = ReadOutcomeString(output, "summary");
        if (!string.Equals(baseSha, expectedBaseSha, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(summary) ||
            !output.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Array ||
            changes.GetArrayLength() is < 1 or > 6)
            return null;

        var result = new List<FounderSoftwareRepairChange>();
        foreach (var item in changes.EnumerateArray())
        {
            var path = ReadOutcomeString(item, "path");
            var source = ReadOutcomeString(item, "content");
            if (string.IsNullOrWhiteSpace(path) || source is null)
                return null;
            result.Add(new(path, source));
        }

        return new(baseSha, title, summary, result);
    }

    private static string? ReadOutcomeString(JsonElement output, string name) =>
        output.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static object Failure(string code) => new { ok = false, error = code };

    private static object Outcome(EngineeringContextSnapshot context, string responseId, string state) =>
        new { ok = true, authority = nameof(LegendEngineeringOrchestrator),
            contextId = context.EngineeringContextId, context.Role, responseId, workItemState = state };

    public async Task<object> ApproveReleaseAsync(
        ClaimsPrincipal founder,
        Guid workItemId,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(founder);
        var item = await store.GetWorkItemAsync(workItemId, cancellationToken);
        if (item is null) return new { ok = false, error = "work_item_not_found" };
        if (item.RiskClass != EngineeringRiskClass.TierB ||
            item.State != "FOUNDER_RELEASE_APPROVAL_REQUIRED" ||
            item.ValidationState != "GREEN" ||
            item.PullRequestNumber is not > 0 ||
            !LegendEngineeringPolicies.IsImmutableSha(item.CandidateSha))
            return new { ok = false, error = "founder_release_approval_state_invalid" };

        var approved = item with
        {
            State = "VALIDATED",
            FounderReleaseApprovedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        await store.UpdateWorkItemAsync(approved, cancellationToken);
        return new
        {
            ok = true,
            workItemId,
            approval = "tier_b_release",
            approvedUtc = approved.FounderReleaseApprovedUtc,
            candidateSha = approved.CandidateSha,
            pullRequestNumber = approved.PullRequestNumber,
            automaticReleaseStillRequiresCanonicalCohortPolicy = true
        };
    }

    public async Task<object> DeclineReleaseAsync(
        ClaimsPrincipal founder,
        Guid workItemId,
        CancellationToken cancellationToken)
    {
        FounderGuard.EnsureFounderOrThrow(founder);
        var item = await store.GetWorkItemAsync(workItemId, cancellationToken);
        if (item is null) return new { ok = false, error = "work_item_not_found" };
        if (item.RiskClass != EngineeringRiskClass.TierB ||
            item.State != "FOUNDER_RELEASE_APPROVAL_REQUIRED")
            return new { ok = false, error = "founder_release_denial_state_invalid" };

        var declined = item with
        {
            State = "CLOSED",
            FounderReleaseApprovedUtc = null,
            LeaseOwner = null,
            LeaseIdentity = null,
            LeaseExpiresUtc = null,
            UpdatedUtc = DateTime.UtcNow
        };
        await store.UpdateWorkItemAsync(declined, cancellationToken);
        return new
        {
            ok = true,
            workItemId,
            decision = "tier_b_release_declined",
            candidateSha = declined.CandidateSha,
            pullRequestNumber = declined.PullRequestNumber,
            closed = true
        };
    }

    public async Task RecordBrowserFunctionalProofAsync(
        Guid workItemId,
        string application,
        string expectedRevision,
        string expectedRoute,
        IReadOnlyList<string> componentIds,
        IReadOnlyList<string> actionKeys,
        IReadOnlyList<string> compositionIds,
        IReadOnlyList<string> modalIds,
        IReadOnlyList<string> forbiddenErrorNames,
        CancellationToken cancellationToken)
    {
        var item = await store.GetWorkItemAsync(workItemId, cancellationToken)
            ?? throw new InvalidOperationException("work_item_not_found");
        if (!item.AffectedApplications.Contains(application, StringComparer.Ordinal))
            throw new InvalidOperationException("browser_live_proof_application_mismatch");
        if (!LegendEngineeringPolicies.IsImmutableSha(item.MergedSha) || item.DeploymentVerifiedUtc is null)
            throw new InvalidOperationException("browser_live_proof_deployment_not_verified");
        if (!string.Equals(item.MergedSha, expectedRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("browser_live_proof_revision_mismatch");
        if (string.IsNullOrWhiteSpace(item.ReproducerRoute))
            throw new InvalidOperationException("browser_reproducer_not_preserved");
        if (!string.Equals(item.ReproducerRoute, expectedRoute, StringComparison.Ordinal) ||
            !SameProofValues(item.ReproducerComponentIds, componentIds) ||
            !SameProofValues(item.ReproducerActionKeys, actionKeys) ||
            !SameProofValues(item.ReproducerCompositionIds, compositionIds) ||
            !SameProofValues(item.ReproducerModalIds, modalIds) ||
            !SameProofValues(item.ReproducerForbiddenErrorNames, forbiddenErrorNames))
            throw new InvalidOperationException("browser_live_proof_does_not_match_preserved_reproducer");

        if (item.State == "COMPLETED" && item.ValidationState == "LIVE_VERIFIED") return;
        if (item.State != "LIVE_FUNCTIONAL_PROOF_REQUIRED")
            throw new InvalidOperationException("browser_live_proof_state_invalid");

        await store.UpdateWorkItemAsync(item with
        {
            State = "COMPLETED",
            ValidationState = "LIVE_VERIFIED",
            LeaseOwner = null,
            LeaseIdentity = null,
            LeaseExpiresUtc = null,
            UpdatedUtc = DateTime.UtcNow
        }, cancellationToken);
    }

    private static bool SameProofValues(IReadOnlyList<string>? expected, IReadOnlyList<string>? actual)
    {
        var left = (expected ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal);
        var right = (actual ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal);
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private async Task<EngineeringContextSnapshot> BootstrapCoreAsync(
        Guid workItemId,
        string requestedRole,
        string executor,
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

        var operationalContract = await contractAuthority.GetCurrentAsync(cancellationToken);
        if (!operationalContract.ModelExecutionEnabled)
            throw new InvalidOperationException("engineering_operational_execution_paused");

        var attemptLimit = requestedRole == EngineeringRole.CodexImplementer
            ? envelope.MaxCodexAttempts
            : envelope.MaxDeepReasoningEscalations;
        var observedAttempts = await store.CountModelAttemptsAsync(workItemId, requestedRole, cancellationToken);
        if (observedAttempts >= attemptLimit)
            throw new InvalidOperationException("engineering_attempt_limit_reached");

        var owner = $"engineering:{executor}:{requestedRole.ToLowerInvariant()}:{workItemId:N}";
        var lease = await store.TryAcquireLeaseAsync(
            workItemId,
            owner,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        if (!lease.Acquired || string.IsNullOrWhiteSpace(lease.LeaseIdentity) || lease.LeaseExpiresUtc is null)
            throw new InvalidOperationException(lease.Code);

        var now = DateTime.UtcNow;
        var turnSeconds = Math.Clamp(
            configuration.GetValue<int?>("LegendEngineering:ChatGptPlan:TurnTimeoutSeconds") ?? 900,
            30,
            1800);
        // Context lifetime is longer than the individual renewable lease. The
        // adapter must keep renewing the exact identity while inference is active;
        // a restart or lost heartbeat lets the short lease expire and the reaper
        // restores the pre-lease actionable state.
        var expires = now.AddSeconds(turnSeconds).AddMinutes(5);
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
            expires,
            operationalContract.Revision);

        return await store.SaveContextAsync(context, cancellationToken);
    }

    private static bool AllowedRoleForState(EngineeringWorkItemSnapshot item, string role)
    {
        if (!string.Equals(item.AssignedRole, role, StringComparison.Ordinal)) return false;
        if (role == EngineeringRole.CodexImplementer)
            return item.FailureClass == EngineeringFailureClass.CodeDefect && item.RiskClass != EngineeringRiskClass.TierC;
        if (role == EngineeringRole.TriageWorker)
            return item.FailureClass == EngineeringFailureClass.Unknown &&
                   item.State is "NEEDS_TRIAGE" or "RECURRED_NEEDS_TRIAGE";
        if (role == EngineeringRole.HeadGpt)
            return item.State is "NEEDS_SUPERVISOR" or "RECURRED_NEEDS_SUPERVISOR" or "QUEUED" or
                "REVIEW_REJECTED" or "CI_FAILED_NEEDS_EVIDENCE";
        if (role == EngineeringRole.IndependentReviewer)
            return item.State == "REVIEW_REQUIRED";
        if (role == EngineeringRole.LiveVerifier)
            return item.State == "LIVE_FUNCTIONAL_PROOF_REQUIRED";
        return false;
    }

    private static IReadOnlyList<string> AllowedTools(string role) => role switch
    {
        EngineeringRole.CodexImplementer =>
            ["legend_inspect_repository", "legend_prepare_software_repair"],
        EngineeringRole.TriageWorker =>
            ["legend_inspect_repository"],
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

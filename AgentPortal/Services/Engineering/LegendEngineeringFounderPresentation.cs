using Domain.Engineering;

namespace AgentPortal.Services.Engineering;

/// <summary>
/// One Founder-facing presentation authority for engineering state. The work-item
/// snapshot remains the machine contract; this projection is deliberately plain
/// language and carries only the next human action that the canonical state allows.
/// </summary>
internal sealed record FounderEngineeringActionItem(
    Guid WorkItemId,
    string AttentionKind,
    string Title,
    string Summary,
    string ActionStep,
    bool RequiresFounderAction,
    string? PrimaryAction,
    string? PrimaryActionLabel,
    string? SecondaryAction,
    string? SecondaryActionLabel,
    string TechnicalSummary,
    DateTime UpdatedUtc);

internal static class LegendEngineeringFounderPresentation
{
    internal static bool ShouldSurface(EngineeringWorkItemSnapshot item) =>
        item.PriorityClass == "P1" ||
        item.RiskClass == EngineeringRiskClass.TierC ||
        item.State is "FOUNDER_RELEASE_APPROVAL_REQUIRED" or "FOUNDER_ESCALATION" or
            "CI_FAILED_NEEDS_EVIDENCE" or "RELEASE_BLOCKED" or
            "LIVE_FUNCTIONAL_PROOF_REQUIRED";

    internal static FounderEngineeringActionItem Present(EngineeringWorkItemSnapshot item)
    {
        var technical = $"Work item {item.WorkItemId:D} · Priority {item.PriorityClass} · Failure {item.FailureClass} · Risk {item.RiskClass} · State {item.State}";

        if (item.State == "FOUNDER_RELEASE_APPROVAL_REQUIRED")
            return new(
                item.WorkItemId,
                "action_required",
                "Release decision needed",
                "LEGEND finished validation for an engineering repair and stopped before release.",
                "Approve to let the validated candidate continue through the governed release flow, or deny to close this exact release request.",
                true,
                "approve_release",
                "Approve release",
                "deny_release",
                "Deny release",
                technical,
                item.UpdatedUtc);

        if (item.State == "FOUNDER_ESCALATION")
            return new(
                item.WorkItemId,
                "attention",
                "Your engineering review is needed",
                "LEGEND reached a decision point it cannot resolve safely on its own, so the work is paused with its evidence preserved.",
                "Review the preserved technical details in Founder Engineering. No automated change will proceed while this escalation remains open.",
                true,
                null,
                null,
                null,
                null,
                technical,
                item.UpdatedUtc);

        if (item.State == "CI_FAILED_NEEDS_EVIDENCE")
            return new(
                item.WorkItemId,
                "legend_handling",
                "Validation found a problem",
                "The candidate did not pass a required validation step. LEGEND stopped the release and preserved the failure evidence.",
                "No approval is needed right now. LEGEND must diagnose the failed check and re-run only the invalidated validation after the repair.",
                false,
                null,
                null,
                null,
                null,
                technical,
                item.UpdatedUtc);

        if (item.State == "RELEASE_BLOCKED")
            return new(
                item.WorkItemId,
                "legend_handling",
                "Release is safely blocked",
                "LEGEND found a release condition that is not safe to cross, so nothing is being published from this work item.",
                "No approval is needed unless LEGEND later asks for one. The release stays stopped while the blocking evidence is resolved.",
                false,
                null,
                null,
                null,
                null,
                technical,
                item.UpdatedUtc);

        if (item.State == "LIVE_FUNCTIONAL_PROOF_REQUIRED")
            return new(
                item.WorkItemId,
                "legend_handling",
                "Live verification is still required",
                "The deployment reached the live-proof gate, but LEGEND has not yet proven the required behavior on the exact deployed revision.",
                "No manual action is required unless verification cannot complete. LEGEND will keep the work open until the live proof passes.",
                false,
                null,
                null,
                null,
                null,
                technical,
                item.UpdatedUtc);

        if (item.RiskClass == EngineeringRiskClass.TierC)
            return new(
                item.WorkItemId,
                "security_review",
                "Security review is holding this work",
                "LEGEND classified this engineering issue as security-sensitive and stopped autonomous source changes before they could proceed.",
                "No action is required right now. The diagnostic evidence remains available to the engineering AI and the work stays paused for protected review.",
                false,
                null,
                null,
                null,
                null,
                technical,
                item.UpdatedUtc);

        if (string.Equals(item.FailureClass, "NETWORK_PROVIDER_FAILURE", StringComparison.Ordinal))
            return new(
                item.WorkItemId,
                "legend_handling",
                "External service connection failed",
                "LEGEND could not reach a service required to continue this engineering task, so it stopped instead of proceeding with incomplete information.",
                "No action is required right now. LEGEND will preserve the provider evidence and retry only under the existing bounded recovery policy.",
                false,
                null,
                null,
                null,
                null,
                technical,
                item.UpdatedUtc);

        return new(
            item.WorkItemId,
            "attention",
            "High-priority engineering issue is being diagnosed",
            "LEGEND detected a high-priority engineering problem that it could not safely complete automatically.",
            "No action is required unless LEGEND asks for a decision. The work remains bounded while the diagnostic evidence is classified and repaired.",
            false,
            null,
            null,
            null,
            null,
            technical,
            item.UpdatedUtc);
    }

    internal static string PushDetail(FounderEngineeringActionItem item) =>
        $"{item.Summary} Action: {item.ActionStep}";
}

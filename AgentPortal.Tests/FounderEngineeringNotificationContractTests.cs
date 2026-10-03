using AgentPortal.Services.Engineering;
using Domain.Engineering;
using Xunit;

namespace AgentPortal.Tests;

public sealed class FounderEngineeringNotificationContractTests
{
    [Fact]
    public void ReleaseApproval_ProjectsPlainLanguageApproveAndDenyActions()
    {
        var item = WorkItem(
            state: "FOUNDER_RELEASE_APPROVAL_REQUIRED",
            riskClass: EngineeringRiskClass.TierB,
            failureClass: EngineeringFailureClass.CodeDefect,
            priorityClass: "P1",
            candidateSha: new string('a', 40));

        var projection = LegendEngineeringFounderPresentation.Present(item);

        Assert.True(projection.RequiresFounderAction);
        Assert.Equal("Release decision needed", projection.Title);
        Assert.Equal("approve_release", projection.PrimaryAction);
        Assert.Equal("Approve release", projection.PrimaryActionLabel);
        Assert.Equal("deny_release", projection.SecondaryAction);
        Assert.Equal("Deny release", projection.SecondaryActionLabel);
        Assert.Contains("Approve", projection.ActionStep, StringComparison.Ordinal);
        Assert.Contains("deny", projection.ActionStep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOUNDER_RELEASE_APPROVAL_REQUIRED", projection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkProviderFailure_ExplainsWhatLegendWillDoWithoutDemandingFounderAction()
    {
        var item = WorkItem(
            state: "NEEDS_TRIAGE",
            riskClass: EngineeringRiskClass.TierA,
            failureClass: EngineeringFailureClass.NetworkProviderFailure,
            priorityClass: "P1");

        Assert.True(LegendEngineeringFounderPresentation.ShouldSurface(item));
        var projection = LegendEngineeringFounderPresentation.Present(item);

        Assert.False(projection.RequiresFounderAction);
        Assert.Equal("External service connection failed", projection.Title);
        Assert.Contains("No action is required", projection.ActionStep, StringComparison.Ordinal);
        Assert.Null(projection.PrimaryAction);
        Assert.Null(projection.SecondaryAction);
    }

    [Fact]
    public void TierC_StaysProtectedAndDoesNotExposeMutationControls()
    {
        var item = WorkItem(
            state: "SECURITY_REVIEW",
            riskClass: EngineeringRiskClass.TierC,
            failureClass: EngineeringFailureClass.CodeDefect,
            priorityClass: "P2");

        Assert.True(LegendEngineeringFounderPresentation.ShouldSurface(item));
        var projection = LegendEngineeringFounderPresentation.Present(item);

        Assert.Equal("security_review", projection.AttentionKind);
        Assert.False(projection.RequiresFounderAction);
        Assert.Null(projection.PrimaryAction);
        Assert.Null(projection.SecondaryAction);
        Assert.Contains("security-sensitive", projection.Summary, StringComparison.Ordinal);
    }

    private static EngineeringWorkItemSnapshot WorkItem(
        string state,
        string riskClass,
        string failureClass,
        string priorityClass,
        string? candidateSha = null)
    {
        var now = new DateTime(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc);
        return new EngineeringWorkItemSnapshot(
            WorkItemId: Guid.NewGuid(),
            IncidentIds: [Guid.NewGuid()],
            WorkKey: "test-work",
            CanonicalAuthorityKey: "test-authority",
            AffectedProjects: ["AgentPortal"],
            AffectedApplications: ["masterapp-portal"],
            ImpactSet: ["AgentPortal"],
            LiveSha: new string('b', 40),
            EvidenceRevision: "evidence:test",
            FailureClass: failureClass,
            Severity: 1,
            RevenueImpact: 0,
            UserImpact: 1,
            Frequency: 1,
            Confidence: 100,
            RiskClassScore: riskClass == EngineeringRiskClass.TierC ? 3 : riskClass == EngineeringRiskClass.TierB ? 2 : 1,
            RiskClass: riskClass,
            ComplexityScore: 2,
            PriorityScore: priorityClass == "P1" ? 90 : 50,
            PriorityClass: priorityClass,
            State: state,
            AssignedRole: EngineeringRole.HeadGpt,
            ModelTier: EngineeringModelTier.StandardReasoning,
            LeaseOwner: null,
            LeaseIdentity: null,
            LeaseExpiresUtc: null,
            AttemptCount: 0,
            RepairBatchId: null,
            PullRequestNumber: candidateSha is null ? null : 123,
            CandidateSha: candidateSha,
            ValidationState: state == "FOUNDER_RELEASE_APPROVAL_REQUIRED" ? "GREEN" : "NOT_STARTED",
            ReleaseCohort: "portal",
            CreatedUtc: now,
            UpdatedUtc: now);
    }
}

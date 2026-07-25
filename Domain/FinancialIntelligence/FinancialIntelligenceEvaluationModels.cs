using Domain.Entities;
using Domain.Entities.FinancialIntelligence;

namespace Domain.FinancialIntelligence;

public static class FinancialIntelligenceActorTypes
{
    public const string Client = "Client";
    public const string Agent = "Agent";
}

public static class FinancialFindingCategories
{
    public const string Opportunity = "Opportunity";
    public const string Leakage = "Leakage";
    public const string Risk = "Risk";
    public const string Progress = "Progress";
    public const string Information = "Information";
}

public static class FinancialFindingStatuses
{
    public const string Active = "Active";
    public const string Deferred = "Deferred";
    public const string Dismissed = "Dismissed";
    public const string Completed = "Completed";
    public const string Resolved = "Resolved";
}

public static class FinancialFindingFeedbackTypes
{
    public const string Viewed = "Viewed";
    public const string Helpful = "Helpful";
    public const string NotHelpful = "NotHelpful";
    public const string Accepted = "Accepted";
    public const string Deferred = "Deferred";
    public const string Dismissed = "Dismissed";
    public const string AgentReviewed = "AgentReviewed";
    public const string AgentContactedClient = "AgentContactedClient";
    public const string ActionStarted = "ActionStarted";
    public const string Completed = "Completed";
    public const string UnableToVerify = "UnableToVerify";
}

public sealed record FinancialIntelligenceActor(
    Guid ClientProfileId,
    string UserId,
    string ActorType,
    string? AgentUpn = null,
    IReadOnlyList<string>? AgentIdCandidates = null);

public sealed record FinancialIntelligenceObservationCandidate(
    string ObservationKey,
    string ObservationType,
    string SourceType,
    string? SourceReference,
    DateTime? PeriodStartUtc,
    DateTime? PeriodEndUtc,
    decimal? NumericValue,
    decimal? PreviousValue,
    string? Unit,
    decimal Confidence,
    string EvidenceSummary);

public sealed record FinancialIntelligenceFindingCandidate(
    string FindingKey,
    string Category,
    string FindingType,
    string Title,
    string Explanation,
    decimal? EstimatedImpact,
    string? ImpactUnit,
    decimal Confidence,
    string Urgency,
    string Difficulty,
    string EvidenceSummary,
    string ClientFacingSummary,
    string AgentFacingSummary,
    string? Disclaimer,
    bool RequiresAgentReview,
    IReadOnlyList<string> ObservationKeys);

public sealed record FinancialIntelligenceRuleResult(
    bool CanReconcile,
    IReadOnlyList<FinancialIntelligenceObservationCandidate> Observations,
    IReadOnlyList<FinancialIntelligenceFindingCandidate> Findings);

public sealed record FinancialIntelligenceRuleContext(
    Guid ClientProfileId,
    DateTime EvaluatedUtc,
    IReadOnlyList<FinancialDataConnection> Connections,
    IReadOnlyList<ImportedFinancialAccount> ImportedAccounts,
    IReadOnlyList<RecurringFinancialStream> RecurringStreams,
    IReadOnlyList<ExpenseLensStreamLink> ExpenseLensLinks,
    FinanceToolState? LivingBalanceSheetState);

public sealed record FinancialIntelligenceFindingView(
    Guid Id,
    string Category,
    string FindingType,
    string Title,
    string Explanation,
    decimal? EstimatedImpact,
    string? ImpactUnit,
    decimal Confidence,
    decimal PriorityScore,
    string Urgency,
    string Difficulty,
    string EvidenceSummary,
    string? Disclaimer,
    string Status,
    bool RequiresAgentReview,
    bool IsAgentReviewed,
    DateTime LastDetectedUtc);

public sealed record FinancialIntelligenceSnapshot(
    Guid ClientProfileId,
    string Status,
    decimal DataCompletenessScore,
    string BehavioralBaselineStatus,
    string PersonalizationMaturity,
    string RecommendationResponseSummary,
    string CurrentRiskSummary,
    string CurrentOpportunitySummary,
    string CurrentLeakageSummary,
    int EvaluationSequence,
    DateTime? LastEvaluatedUtc,
    IReadOnlyList<FinancialIntelligenceFindingView> Findings);

public sealed record FinancialIntelligenceEvaluationResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    FinancialIntelligenceSnapshot? Snapshot = null)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);

public sealed record FinancialIntelligenceFeedbackCommand(
    Guid FinancialFindingId,
    string FeedbackType,
    string? ReasonCode = null,
    string? Note = null);

public sealed record FinancialIntelligenceFeedbackResult(
    bool Success,
    string? SafeErrorCode,
    string? SanitizedSummary,
    FinancialIntelligenceSnapshot? Snapshot = null)
    : FinancialIntelligenceResult(Success, SafeErrorCode, SanitizedSummary);

public interface IFinancialIntelligenceRule
{
    string Identifier { get; }

    int Version { get; }

    FinancialIntelligenceRuleResult Evaluate(FinancialIntelligenceRuleContext context);
}

public interface IFinancialIntelligenceEvaluationService
{
    Task<FinancialIntelligenceSnapshot?> GetSnapshotAsync(
        FinancialIntelligenceActor actor,
        CancellationToken cancellationToken = default);

    Task<FinancialIntelligenceEvaluationResult> EvaluateAsync(
        FinancialIntelligenceActor actor,
        CancellationToken cancellationToken = default);

    Task<FinancialIntelligenceFeedbackResult> RecordFeedbackAsync(
        FinancialIntelligenceActor actor,
        FinancialIntelligenceFeedbackCommand command,
        CancellationToken cancellationToken = default);
}

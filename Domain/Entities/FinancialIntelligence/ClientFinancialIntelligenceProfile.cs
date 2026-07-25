namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// Client-scoped evaluation state. This intentionally contains no duplicate
/// client PII and does not replace FinanceToolState as the planning authority.
/// </summary>
public sealed class ClientFinancialIntelligenceProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public string Status { get; set; } = "Ready";

    /// <summary>Normalized 0-1 coverage of reliable inputs available to the evaluator.</summary>
    public decimal DataCompletenessScore { get; set; }

    public string BehavioralBaselineStatus { get; set; } = "NotEstablished";

    public string PersonalizationMaturity { get; set; } = "Initial";

    public string RecommendationResponseSummary { get; set; } = "No feedback recorded.";

    public string CurrentRiskSummary { get; set; } = "No current risk findings.";

    public string CurrentOpportunitySummary { get; set; } = "No current opportunity findings.";

    public string CurrentLeakageSummary { get; set; } = "No current leakage findings.";

    public int EvaluationSequence { get; set; }

    public DateTime? LastEvaluatedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

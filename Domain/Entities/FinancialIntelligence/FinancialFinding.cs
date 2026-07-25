namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// An explainable, deterministic financial intelligence result. Findings are
/// historical records; status transitions never delete their evidence trail.
/// </summary>
public sealed class FinancialFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public string FindingKey { get; set; } = "";

    public string RuleIdentifier { get; set; } = "";

    public int RuleVersion { get; set; }

    public string Category { get; set; } = "Information";

    public string FindingType { get; set; } = "";

    public string Title { get; set; } = "";

    public string Explanation { get; set; } = "";

    public decimal? EstimatedImpact { get; set; }

    public string? ImpactUnit { get; set; }

    public decimal Confidence { get; set; }

    public decimal PriorityScore { get; set; }

    public string Urgency { get; set; } = "Low";

    public string Difficulty { get; set; } = "Review";

    public string EvidenceSummary { get; set; } = "";

    public string ClientFacingSummary { get; set; } = "";

    public string AgentFacingSummary { get; set; } = "";

    public string? Disclaimer { get; set; }

    public bool RequiresAgentReview { get; set; }

    public DateTime? AgentReviewedUtc { get; set; }

    public string? AgentReviewedByUserId { get; set; }

    public string Status { get; set; } = "Active";

    public DateTime FirstDetectedUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastDetectedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

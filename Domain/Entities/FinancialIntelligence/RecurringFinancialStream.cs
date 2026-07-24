namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// A client-scoped recurring pattern detected from imported transaction facts.
/// It becomes part of planning only through an explicit Expense Lens link.
/// </summary>
public sealed class RecurringFinancialStream
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public Guid? FinancialDataConnectionId { get; set; }

    public Guid? ImportedFinancialAccountId { get; set; }

    public string StreamKey { get; set; } = "";

    public string NormalizedMerchantKey { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Cadence { get; set; } = "Unknown";

    public long AverageAmountCents { get; set; }

    public DateTime? NextExpectedDateUtc { get; set; }

    public string Status { get; set; } = "Candidate";

    public decimal Confidence { get; set; }

    public string EvidenceJson { get; set; } = "{}";

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// A normalized fact derived from an authoritative financial source. It never
/// stores credentials or copied provider payloads.
/// </summary>
public sealed class FinancialObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public string ObservationKey { get; set; } = "";

    public string RuleIdentifier { get; set; } = "";

    public int RuleVersion { get; set; }

    public string ObservationType { get; set; } = "";

    public string SourceType { get; set; } = "";

    public string? SourceReference { get; set; }

    public DateTime? PeriodStartUtc { get; set; }

    public DateTime? PeriodEndUtc { get; set; }

    public decimal? NumericValue { get; set; }

    public decimal? PreviousValue { get; set; }

    public string? Unit { get; set; }

    public decimal Confidence { get; set; }

    public string EvidenceSummary { get; set; } = "";

    public string Status { get; set; } = "Active";

    public DateTime? SupersededUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

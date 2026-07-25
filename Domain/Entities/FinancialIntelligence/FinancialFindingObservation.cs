namespace Domain.Entities.FinancialIntelligence;

/// <summary>Normalized evidence links for a finding.</summary>
public sealed class FinancialFindingObservation
{
    public Guid FinancialFindingId { get; set; }

    public Guid FinancialObservationId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

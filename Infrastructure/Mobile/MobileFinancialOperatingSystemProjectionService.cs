namespace Infrastructure.Mobile;

/// <summary>
/// Produces the read-only mobile projection of the existing ClientApp finance
/// authority. Implementations must never own editable financial state.
/// </summary>
public interface IMobileFinancialOperatingSystemProjectionService
{
    Task<MobileFinancialOperatingSystemSnapshot> ProjectAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Phase 2A composition boundary.
///
/// This initial implementation intentionally reports that the detailed
/// projection is not populated yet. Later validated phases will read the
/// existing persisted ClientApp finance authority and populate the same
/// immutable contract without changing its public boundary.
/// </summary>
public sealed class MobileFinancialOperatingSystemProjectionService
    : IMobileFinancialOperatingSystemProjectionService
{
    public Task<MobileFinancialOperatingSystemSnapshot> ProjectAsync(
        Guid clientProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (clientProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid client profile identifier is required.",
                nameof(clientProfileId));
        }

        var generatedUtc = DateTime.UtcNow;

        var snapshot = new MobileFinancialOperatingSystemSnapshot(
            Projection: new MobileFinancialProjectionStatus(
                Status: "Unavailable",
                ReasonCode: "FINANCIAL_PROJECTION_NOT_POPULATED",
                Summary:
                    "Detailed weekly and monthly financial projections are not available yet."),
            Freshness: new MobileFinancialDataFreshness(
                FinanceStateUpdatedUtc: null,
                IntelligenceEvaluatedUtc: null,
                GeneratedUtc: generatedUtc),
            WeekAtGlance: null,
            MonthAtGlance: null,
            Tools: Array.Empty<MobileFinancialToolSummary>());

        return Task.FromResult(snapshot);
    }
}

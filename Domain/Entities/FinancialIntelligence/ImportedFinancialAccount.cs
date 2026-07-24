namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// Provider account facts used for ingestion and reconciliation.
/// This record does not replace an Expense Lens planning item.
/// </summary>
public sealed class ImportedFinancialAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public Guid FinancialDataConnectionId { get; set; }

    public string ProviderAccountId { get; set; } = "";

    public string? PersistentAccountKey { get; set; }

    public string Name { get; set; } = "";

    public string? OfficialName { get; set; }

    public string? Mask { get; set; }

    public string AccountType { get; set; } = "";

    public string? AccountSubtype { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public long? CurrentBalanceCents { get; set; }

    public long? AvailableBalanceCents { get; set; }

    public bool IsClosed { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

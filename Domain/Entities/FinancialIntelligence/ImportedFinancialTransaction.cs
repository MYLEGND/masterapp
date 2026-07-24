namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// Immutable provider transaction facts. Client-editable interpretation belongs
/// in separate reconciliation and Expense Lens link records.
/// </summary>
public sealed class ImportedFinancialTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public Guid FinancialDataConnectionId { get; set; }

    public Guid ImportedFinancialAccountId { get; set; }

    public string ProviderTransactionId { get; set; } = "";

    public string? ProviderPendingTransactionId { get; set; }

    public string OriginalName { get; set; } = "";

    public string? OriginalMerchantName { get; set; }

    public DateTime? AuthorizedUtc { get; set; }

    public DateTime PostedUtc { get; set; }

    public long AmountCents { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public bool IsPending { get; set; }

    public bool IsRemoved { get; set; }

    public string? ProviderCategoryJson { get; set; }

    public string ProviderPayloadJson { get; set; } = "{}";

    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

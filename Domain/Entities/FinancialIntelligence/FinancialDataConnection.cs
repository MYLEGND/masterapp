namespace Domain.Entities.FinancialIntelligence;

/// <summary>
/// Provider-neutral connection metadata for one client's financial-data source.
/// FinanceToolState remains the planning authority.
/// </summary>
public sealed class FinancialDataConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }

    public string ProviderKey { get; set; } = "";

    public string ProviderItemId { get; set; } = "";

    public string? ProviderInstitutionId { get; set; }

    public string? DisplayName { get; set; }

    public string Status { get; set; } = "Active";

    public string? EncryptedAccessToken { get; set; }

    public string? SyncCursor { get; set; }

    public DateTime? LastSyncStartedUtc { get; set; }

    public DateTime? LastSyncCompletedUtc { get; set; }

    public DateTime? LastWebhookUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

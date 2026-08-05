namespace Domain.Entities;

/// <summary>
/// A sanitized, append-only observation of the account-closure executor. It
/// intentionally records workflow outcomes, never personal data or provider
/// response bodies.
/// </summary>
public sealed class AccountLifecycleAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountLifecycleRecordId { get; set; }
    public int AttemptNumber { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ResultCode { get; set; } = string.Empty;
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
}

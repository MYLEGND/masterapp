namespace Domain.Entities;

/// <summary>
/// The one persisted lifecycle authority for a typed Legend account. It is not
/// a profile mirror: it records access/closure state while the typed profile,
/// billing, identity, media, and audit authorities retain ownership of their
/// respective data.
/// </summary>
public sealed class AccountLifecycleRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string ParticipantType { get; set; } = string.Empty;
    public Guid ProfileId { get; set; }
    public string State { get; set; } = Domain.Accounts.AccountLifecycleStates.Active;
    public DateTime? PausedUtc { get; set; }
    public DateTime? DeletionRequestedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }
    // A short-lived lease is the durable concurrency boundary for the one
    // closure executor. A recycled worker can resume after it expires.
    public Guid? ClosureLeaseId { get; set; }
    public DateTime? ClosureLeaseExpiresUtc { get; set; }
    public int ClosureAttemptCount { get; set; }
    public DateTime? LastClosureAttemptUtc { get; set; }
    public string? LastClosureErrorCode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

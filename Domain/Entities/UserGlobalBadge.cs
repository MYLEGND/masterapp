namespace Domain.Entities;

/// <summary>
/// A materialized, database-owned unread total for one typed Legend account.
/// It is a cache of the notification ledger, never a client-calculated value.
/// The notification engine refreshes it inside the same unit of work and every
/// read API reconciles it from the ledger before returning a count.
/// </summary>
public sealed class UserGlobalBadge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public string ParticipantType { get; set; } = string.Empty;

    public int UnreadCount { get; set; }

    public long Revision { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

namespace Domain.Entities;

/// <summary>
/// A recipient-scoped notification ledger entry. The legacy table name is
/// retained for migration continuity, but this is the single persisted source
/// for mobile notification-center items and the app-icon unread total.
/// Conversation membership continues to own only thread read position.
/// </summary>
public sealed class MobileActivityNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string RecipientUserId { get; set; } = string.Empty;

    public string RecipientParticipantType { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    /// <summary>The originating conversation when this is a direct/group message notification.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Message source key; unique per recipient to make producer retries idempotent.</summary>
    public Guid? SourceMessageId { get; set; }

    /// <summary>
    /// The originating controlled-resource request. One decision produces one
    /// activity event, enforced by a unique database index.
    /// </summary>
    public Guid? ControlledResourceRequestId { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    public bool IsRead { get; set; }

    public bool IsCleared { get; set; }

    public DateTime? ReadUtc { get; set; }

    public DateTime? ClearedUtc { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

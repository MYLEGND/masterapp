namespace Domain.Entities;

public class InternalMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public string SenderUserId { get; set; } = string.Empty;

    public string SenderType { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Detected only by the trusted server translation provider when needed.
    /// It describes the authoritative body and is never client-supplied.
    /// </summary>
    public string? OriginalLanguage { get; set; }

    /// <summary>
    /// Canonical server preference for the actual sender at send time. This is
    /// the live translation route source and intentionally remains distinct
    /// from <see cref="OriginalLanguage"/>, which is provider-detected text
    /// metadata. A later preference change cannot re-route an earlier message.
    /// </summary>
    public string? SenderPreferredLanguage { get; set; }

    public DateTime SentUtc { get; set; }

    public DateTime? EditedUtc { get; set; }

    public DateTime? DeletedUtc { get; set; }

    public bool IsDeleted { get; set; }

    public string? ClientMessageId { get; set; }

    public Guid? ReplyToMessageId { get; set; }

    /// <summary>
    /// Legacy column name for the shared controlled-resource request link.
    /// It can identify verification or language-translation review actions;
    /// the requester is never made a member of that staff conversation.
    /// </summary>
    public Guid? VerificationReviewRequestId { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public MessageConversation Conversation { get; set; } = null!;

    public InternalMessage? ReplyToMessage { get; set; }

    public ICollection<InternalMessage> Replies { get; set; }
        = new List<InternalMessage>();

    public ICollection<MessageAttachment> Attachments { get; set; }
        = new List<MessageAttachment>();

    public ICollection<MessageTranslation> Translations { get; set; }
        = new List<MessageTranslation>();
}

namespace Domain.Entities;

public class InternalMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public string SenderUserId { get; set; } = string.Empty;

    public string SenderType { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTime SentUtc { get; set; }

    public DateTime? EditedUtc { get; set; }

    public DateTime? DeletedUtc { get; set; }

    public bool IsDeleted { get; set; }

    public string? ClientMessageId { get; set; }

    public Guid? ReplyToMessageId { get; set; }

    /// <summary>
    /// Present only for the private staff notification created when a member
    /// submits a verification request. The requester is never made a member of
    /// that staff conversation.
    /// </summary>
    public Guid? VerificationReviewRequestId { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public MessageConversation Conversation { get; set; } = null!;

    public InternalMessage? ReplyToMessage { get; set; }

    public ICollection<InternalMessage> Replies { get; set; }
        = new List<InternalMessage>();

    public ICollection<MessageAttachment> Attachments { get; set; }
        = new List<MessageAttachment>();
}

namespace Domain.Entities;

public class MessageConversationParticipant
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string ParticipantType { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime JoinedUtc { get; set; }

    public DateTime? LeftUtc { get; set; }

    public DateTime? LastReadUtc { get; set; }

    public Guid? LastReadMessageId { get; set; }

    public bool IsMuted { get; set; }

    /// <summary>
    /// Management authority delegated by the canonical owner of a normal
    /// user-created group. This does not confer ownership and is never used
    /// for protected Purpose-based conversations.
    /// </summary>
    public bool IsGroupManager { get; set; }

    /// <summary>
    /// Actor-scoped inbox controls. A participant can pin or remove a thread
    /// from their own inbox without changing visibility for anyone else.
    /// A subsequent message restores a removed thread to the inbox.
    /// </summary>
    public DateTime? PinnedUtc { get; set; }

    public DateTime? HiddenUtc { get; set; }

    public MessageConversation Conversation { get; set; } = null!;
}

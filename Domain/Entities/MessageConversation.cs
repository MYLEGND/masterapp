namespace Domain.Entities;

public class MessageConversation
{
    public Guid Id { get; set; }

    public string ConversationType { get; set; } = string.Empty;

    public string? DirectConversationKey { get; set; }

    public string? Subject { get; set; }

    /// <summary>
    /// Optional square group image, owned by the conversation rather than by a
    /// participant. Direct conversations never use these fields.
    /// </summary>
    public byte[]? GroupImageContent { get; set; }

    public string? GroupImageContentType { get; set; }

    /// <summary>
    /// Optional server-defined purpose for a group. It is never supplied by a
    /// client and lets a member resume one verification review instead of
    /// creating duplicate support groups.
    /// </summary>
    public string? Purpose { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? LastMessageUtc { get; set; }

    public bool IsClosed { get; set; }

    public DateTime? ClosedUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// Group ownership is typed so a shared Entra user ID cannot grant the
    /// alternate client/agent role authority over the conversation.
    /// </summary>
    public string? OwnerUserId { get; set; }

    public string? OwnerParticipantType { get; set; }

    /// <summary>
    /// The selected host for a normal group. This is separate from ownership so
    /// a founder or owner can assign a member to host without transferring the
    /// authority to edit or delete the group.
    /// </summary>
    public string? HostUserId { get; set; }

    public string? HostParticipantType { get; set; }

    /// <summary>
    /// Optional owner-controlled online meeting details. The normalized URL,
    /// label, and recurrence stay with the group conversation as their single
    /// durable source of truth.
    /// </summary>
    public string? MeetingLinkLabel { get; set; }

    public string? MeetingLinkUrl { get; set; }

    public string? MeetingFrequency { get; set; }

    public string? MeetingWeekdays { get; set; }

    public string? MeetingLocalTime { get; set; }

    public string? MeetingTimeZoneId { get; set; }

    public DateTime? MeetingStartsUtc { get; set; }

    public string? MeetingCustomDescription { get; set; }

    /// <summary>
    /// Founder-owned normal groups may be promoted as a public invitation. The
    /// conversation remains the sole durable authority; promotion never creates
    /// a separate social record or membership model.
    /// </summary>
    public bool IsPromoted { get; set; }

    public DateTime? PromotionStartedUtc { get; set; }

    public DateTime? PromotionEndedUtc { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();


    public ICollection<MessageConversationParticipant> Participants { get; set; }
        = new List<MessageConversationParticipant>();

    public ICollection<InternalMessage> Messages { get; set; }
        = new List<InternalMessage>();
}

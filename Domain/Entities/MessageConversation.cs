namespace Domain.Entities;

public class MessageConversation
{
    public Guid Id { get; set; }

    public string ConversationType { get; set; } = string.Empty;

    public string? DirectConversationKey { get; set; }

    public string? Subject { get; set; }

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

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();


    public ICollection<MessageConversationParticipant> Participants { get; set; }
        = new List<MessageConversationParticipant>();

    public ICollection<InternalMessage> Messages { get; set; }
        = new List<InternalMessage>();
}

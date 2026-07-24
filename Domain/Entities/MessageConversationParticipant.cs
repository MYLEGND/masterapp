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

    public MessageConversation Conversation { get; set; } = null!;
}

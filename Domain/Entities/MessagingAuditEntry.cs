namespace Domain.Entities;

public class MessagingAuditEntry
{
    public Guid Id { get; set; }

    public string ActorUserId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public Guid? ConversationId { get; set; }

    public Guid? InternalMessageId { get; set; }

    public string? TargetUserId { get; set; }

    public string? Detail { get; set; }

    public DateTime CreatedUtc { get; set; }
}

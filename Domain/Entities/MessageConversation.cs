namespace Domain.Entities;

public class MessageConversation
{
    public Guid Id { get; set; }

    public string ConversationType { get; set; } = string.Empty;

    public string? Subject { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? LastMessageUtc { get; set; }

    public bool IsClosed { get; set; }

    public DateTime? ClosedUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

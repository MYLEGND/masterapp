namespace Domain.Entities;

public sealed class MessageReaction
{
    public Guid InternalMessageId { get; set; }
    public Guid ActorProfileId { get; set; }
    public string ParticipantType { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public InternalMessage InternalMessage { get; set; } = null!;
}

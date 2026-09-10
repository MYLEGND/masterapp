namespace Domain.Entities;

// Signaling payloads and media are deliberately not persisted here. Only the
// authenticated call lifecycle and answering device are shared across hosts.
public sealed class LegendCallSession
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string CallerUserId { get; set; } = "";
    public string CallerType { get; set; } = "";
    public string CalleeUserId { get; set; } = "";
    public string CalleeType { get; set; } = "";
    public Guid CallerDeviceId { get; set; }
    public Guid? CalleeDeviceId { get; set; }
    public string CallerName { get; set; } = "";
    public string CalleeName { get; set; } = "";
    public bool Video { get; set; }
    public string Status { get; set; } = "ringing";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public int Epoch { get; set; }
    public DateTime? InvitationDispatchedUtc { get; set; }
    public DateTime? NextPushUtc { get; set; }
    public int PushAttempts { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

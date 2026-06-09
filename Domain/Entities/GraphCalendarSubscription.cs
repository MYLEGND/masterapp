namespace Domain.Entities;

public class GraphCalendarSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string AgentUserId { get; set; } = "";
    public string? CalendarUserId { get; set; }
    public string? CalendarEmail { get; set; }

    public string GraphSubscriptionId { get; set; } = "";
    public string Resource { get; set; } = "";
    public string ChangeType { get; set; } = "created,updated,deleted";
    public string ClientState { get; set; } = "";

    public DateTime ExpirationUtc { get; set; }
    public DateTime? LastRenewedUtc { get; set; }
    public DateTime? LastWebhookUtc { get; set; }

    public bool IsActive { get; set; } = true;
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

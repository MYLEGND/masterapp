namespace Domain.Entities;

// Short-lived transport outbox, not call history. Media never enters this table.
public sealed class LegendCallSignal
{
    public long Id { get; set; }
    public Guid CallId { get; set; }
    public string RecipientGroup { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

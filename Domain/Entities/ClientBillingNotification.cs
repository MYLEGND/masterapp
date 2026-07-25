using Domain.Billing;

namespace Domain.Entities;

public sealed class ClientBillingNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientProfileId { get; set; }
    public ClientProfile? ClientProfile { get; set; }
    public Guid ClientSubscriptionId { get; set; }
    public ClientSubscription? ClientSubscription { get; set; }

    public ClientBillingNotificationKind Kind { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string PlainTextBody { get; set; } = string.Empty;
    public DateTime NotBeforeUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? SafeFailureCode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

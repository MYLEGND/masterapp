namespace Domain.Entities;

/// <summary>
/// Durable APNs outbox entry. The delivery worker always reads the current
/// database unread total immediately before it generates the APNs payload.
/// </summary>
public sealed class MobilePushDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid NotificationId { get; set; }

    public Guid MobilePushDeviceId { get; set; }

    public int AttemptCount { get; set; }

    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;

    public DateTime? SentUtc { get; set; }

    public DateTime? AbandonedUtc { get; set; }

    public string? LastError { get; set; }
}

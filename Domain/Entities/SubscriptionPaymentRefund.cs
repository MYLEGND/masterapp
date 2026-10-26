namespace Domain.Entities;

public sealed class SubscriptionPaymentRefund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionPaymentId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string State { get; set; } = string.Empty;
    public string ProviderReference { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public int Version { get; set; }
}

namespace Domain.Entities;

public sealed class CommercePaymentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public Guid CommerceOrderId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ConnectionRevisionId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string? LocationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public string? PaymentReference { get; set; }
    public string? CheckoutUrl { get; set; }
    public long RefundedMinor { get; set; }
    public string? DisputeState { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? CaptureRequestedUtc { get; set; }
    public string? SignalContextJson { get; set; }
    public int Version { get; set; }
}

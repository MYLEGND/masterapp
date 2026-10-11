namespace Domain.Entities;

public sealed class CommercePaymentRefund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string State { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int Version { get; set; }
}

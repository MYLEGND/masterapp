namespace Domain.Entities;

public sealed class CommercePaymentReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? SafeErrorCode { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
}

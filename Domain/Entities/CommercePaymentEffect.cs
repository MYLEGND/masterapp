namespace Domain.Entities;

public sealed class CommercePaymentEffect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AttemptId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? SafeErrorCode { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int Version { get; set; }
}

using Domain.Billing;

namespace Domain.Entities;

public sealed class BillingProviderEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public BillingProvider Provider { get; set; } = BillingProvider.Square;
    public BillingProviderEnvironment ProviderEnvironment { get; set; } = BillingProviderEnvironment.Sandbox;
    public string ProviderEventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? ProviderObjectId { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SignatureValidatedUtc { get; set; }
    public BillingProviderEventProcessingStatus ProcessingStatus { get; set; } = BillingProviderEventProcessingStatus.Received;
    public int AttemptCount { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public DateTime? RetryUtc { get; set; }
    public string? SafeErrorCode { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public string? RetainedPayloadJson { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

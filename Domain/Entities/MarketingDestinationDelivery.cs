namespace Domain.Entities;

/// <summary>
/// Durable delivery/outbox receipt for projecting one canonical MasterApp event to an external
/// marketing destination. This is transport state only; it never owns business or analytics truth.
/// </summary>
public sealed class MarketingDestinationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerKey { get; set; } = string.Empty;
    public string OwnerType { get; set; } = string.Empty;
    public Guid? AgentTrackingProfileId { get; set; }
    public Guid? CommerceBusinessId { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public long? MetaSignalEventId { get; set; }
    public long? AnalyticsEventId { get; set; }
    public string? AdvertiserAccountId { get; set; }
    public string? ConversionDataSourceId { get; set; }
    public string CanonicalSource { get; set; } = string.Empty;
    public string CanonicalEventId { get; set; } = string.Empty;
    public string CanonicalEventName { get; set; } = string.Empty;
    public string ProviderEventName { get; set; } = string.Empty;
    public string PixelId { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public int? LastHttpStatusCode { get; set; }
    public string? ProviderReceiptJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public string? ClaimToken { get; set; }
    public DateTime? ClaimExpiresUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

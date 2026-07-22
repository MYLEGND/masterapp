using Domain.Billing;

namespace Domain.Entities;

public sealed class BillingAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public BillingActorType ActorType { get; set; } = BillingActorType.System;
    public string? ActorId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
    public string? SanitizedMetadataJson { get; set; }
}

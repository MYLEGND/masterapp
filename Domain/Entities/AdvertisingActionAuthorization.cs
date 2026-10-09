namespace Domain.Entities;

/// <summary>
/// Durable exact-plan authorization for consequential advertising mutations.
/// Ownership and permission are resolved by existing workspace authorities; this row
/// records the reviewed plan, approval, single execution claim, and provider receipt.
/// </summary>
public sealed class AdvertisingActionAuthorization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerKey { get; set; } = string.Empty;
    public string OwnerType { get; set; } = string.Empty;
    public Guid? AgentTrackingProfileId { get; set; }
    public Guid? CommerceBusinessId { get; set; }
    public string Provider { get; set; } = "openai";
    public string ProposalKind { get; set; } = "manual";
    public string ActionDigest { get; set; } = string.Empty;
    public string ExactPlanJson { get; set; } = string.Empty;
    public string? SourceSnapshotJson { get; set; }
    public string State { get; set; } = "Proposed";
    public string ProposedByUserId { get; set; } = string.Empty;
    public DateTime ProposedUtc { get; set; } = DateTime.UtcNow;
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedUtc { get; set; }
    public DateTime? ApprovalExpiresUtc { get; set; }
    public string? RejectedByUserId { get; set; }
    public DateTime? RejectedUtc { get; set; }
    public string? ExecutionClaimToken { get; set; }
    public DateTime? ExecutionStartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? ProviderReceiptJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
}

namespace Domain.Entities;

/// <summary>Durable owner + provider marketing connection authority. Existing Meta fields remain for compatibility while provider-specific adapters project them safely.</summary>
public sealed class MarketingConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerKey { get; set; } = string.Empty;
    public string OwnerType { get; set; } = string.Empty;
    public Guid? AgentTrackingProfileId { get; set; }
    public Guid? CommerceBusinessId { get; set; }
    public string Provider { get; set; } = "meta";
    public string? PixelId { get; set; }
    public string? TestEventCode { get; set; }
    public string? AdsAccessTokenCiphertext { get; set; }
    public string? CapiAccessTokenCiphertext { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public string? AdAccountId { get; set; }
    public string? AdAccountName { get; set; }
    public string? MetaBusinessManagerId { get; set; }
    public string? MetaBusinessManagerName { get; set; }
    public string? MetaUserId { get; set; }
    public string? MetaUserName { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public DateTime? DisconnectedUtc { get; set; }
    public DateTime? LegacyAdsImportedUtc { get; set; }
    public DateTime? LegacyProfileImportedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Guid Revision { get; set; } = Guid.NewGuid();
}

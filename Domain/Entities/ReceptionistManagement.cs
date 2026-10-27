namespace Domain.Entities;

/// <summary>Pending operation under the existing connection; presence never grants runtime acceptance.</summary>
public sealed class ReceptionistOperationPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketingConnectionId { get; set; }
    public Guid ConfigurationVersionId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public Guid? ApiCredentialVersionId { get; set; }
    public Guid? WebhookCredentialVersionId { get; set; }
    public string CreatedByActorId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string ReviewState { get; set; } = "pending";
    public string? ReviewedByActorId { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public string? ReviewedPayloadSha256 { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>Write-only, owner-protected credential version. Accepted bindings pin their own envelope.</summary>
public sealed class ReceptionistCredentialVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketingConnectionId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderProjectKey { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string ProtectedValue { get; set; } = string.Empty;
    public string ValueSha256 { get; set; } = string.Empty;
    public string CreatedByActorId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

/// <summary>Restricted-test permission pinned to an accepted configuration, never public activation.</summary>
public sealed class ReceptionistAcceptanceWindow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MarketingConnectionId { get; set; }
    public Guid ConfigurationVersionId { get; set; }
    public string ConfigurationSha256 { get; set; } = string.Empty;
    public string IngressKey { get; set; } = string.Empty;
    public string ProviderProjectKey { get; set; } = string.Empty;
    public string RestrictedRouteEvidenceSha256 { get; set; } = string.Empty;
    public string CreatedByActorId { get; set; } = string.Empty;
    public string ReviewedByActorId { get; set; } = string.Empty;
    public DateTime StartsUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}

namespace Domain.Entities;

public sealed class CommerceMerchantConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string? LocationId { get; set; }
    public int Revision { get; set; }
    public string ProtectedCredential { get; set; } = string.Empty;
    public string GrantedScopesJson { get; set; } = string.Empty;
    public string ReadinessJson { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? DisconnectedUtc { get; set; }
}

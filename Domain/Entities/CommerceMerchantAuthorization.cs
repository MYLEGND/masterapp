namespace Domain.Entities;

public sealed class CommerceMerchantAuthorization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public Guid ClientProfileId { get; set; }
    public string ProtectedReturnTicket { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string StateHash { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public string? ExpectedAccountId { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
    public int Version { get; set; }
}

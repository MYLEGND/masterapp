namespace Domain.Entities;

public sealed class CommerceMerchantConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommerceBusinessId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public Guid? DefaultConnectionId { get; set; }
    public int Version { get; set; }
}
